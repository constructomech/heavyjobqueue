using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;

namespace HeavyJobQueue.Core;

public sealed class QueueBroker : IAsyncDisposable
{
    private readonly QueueCoordinator _coordinator;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _schedulerSignal = new(0);
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private Task? _acceptTask;
    private Task? _schedulerTask;
    private Task? _leaseMonitorTask;
    private int _clientSequence;

    public QueueBroker(QueueCoordinator coordinator, string? pipeName = null)
    {
        _coordinator = coordinator;
        _pipeName = pipeName ?? Protocol.PipeName;
        _coordinator.Changed += CoordinatorChanged;
    }

    public void Start()
    {
        if (_acceptTask is not null)
        {
            throw new InvalidOperationException("The broker is already running.");
        }

        _acceptTask = Task.Run(() => RunAcceptLoopAsync(_shutdown.Token));
        _schedulerTask = Task.Run(() => RunSchedulerAsync(_shutdown.Token));
        _leaseMonitorTask = Task.Run(() => RunLeaseMonitorAsync(_shutdown.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _coordinator.Changed -= CoordinatorChanged;
        _shutdown.Cancel();
        _schedulerSignal.Release();

        var tasks = new[] { _acceptTask, _schedulerTask, _leaseMonitorTask }
            .Where(task => task is not null)
            .Cast<Task>()
            .Concat(_clients.Values)
            .ToArray();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _shutdown.Dispose();
        _schedulerSignal.Dispose();
    }

    private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                try
                {
                    await pipe.WaitForConnectionAsync(cancellationToken);
                }
                catch
                {
                    await pipe.DisposeAsync();
                    throw;
                }

                var clientId = Interlocked.Increment(ref _clientSequence);
                var clientTask = HandleClientAsync(pipe, cancellationToken);
                _clients[clientId] = clientTask;
                _ = clientTask.ContinueWith(
                    completedTask => _clients.TryRemove(clientId, out _),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        Guid? attachedRequestId = null;
        await using (pipe)
        using (var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true))
        using (var writer = new SafeStreamWriter(
            pipe,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        })
        {
            try
            {
                ClientMessage first;
                try
                {
                    first = Protocol.ParseClientMessage(
                        await reader.ReadLineAsync(cancellationToken));
                }
                catch (ProtocolException exception)
                {
                    await WriteErrorAsync(writer, exception.Code, exception.Message);
                    return;
                }

                if (first.Type == "complete")
                {
                    await CompleteAsync(first, writer);
                    return;
                }

                if (first.Type == "cancel")
                {
                    await CancelAsync(first, writer);
                    return;
                }

                if (first.Type != "enqueue")
                {
                    await WriteErrorAsync(
                        writer,
                        "expected_enqueue",
                        "The first message must enqueue, complete, or cancel a request.");
                    return;
                }

                attachedRequestId = first.RequestId;
                QueueAttachment attachment;
                try
                {
                    attachment = _coordinator.AttachOrEnqueue(new JobRequest(
                        first.RequestId,
                        first.Label!,
                        first.CallerPid!.Value,
                        first.Cwd!,
                        first.EnqueuedAt!.Value,
                        first.WaitTimeout!.Value,
                        first.Command,
                        first.LeaseName!,
                        first.AccessMode!.Value));
                }
                catch (RequestCompletedException)
                {
                    attachedRequestId = null;
                    await WriteErrorAsync(
                        writer,
                        "request_completed",
                        "This request has already completed.");
                    return;
                }
                catch (InvalidOperationException exception)
                {
                    attachedRequestId = null;
                    await WriteErrorAsync(writer, "duplicate_request", exception.Message);
                    return;
                }

                await ServeAttachedClientAsync(
                    attachment.Registration,
                    reader,
                    writer,
                    cancellationToken);
            }
            catch (ProtocolException exception)
            {
                await TryWriteErrorAsync(writer, exception.Code, exception.Message);
            }
            catch (IOException)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                if (attachedRequestId is not null)
                {
                    _coordinator.MarkDisconnected(attachedRequestId.Value);
                }
            }
        }
    }

    private async Task ServeAttachedClientAsync(
        QueueRegistration registration,
        StreamReader reader,
        StreamWriter writer,
        CancellationToken cancellationToken)
    {
        var requestId = registration.Request.RequestId;
        await writer.WriteLineAsync(Protocol.Serialize(new
        {
            version = Protocol.Version,
            type = "queued",
            requestId,
            position = _coordinator.GetWaitingPosition(requestId),
            restored = !registration.Granted.IsCompleted
        }));

        if (registration.IsPaused)
        {
            await WriteStateChangeAsync(writer, requestId, isPaused: true);
        }

        using var readCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var clientMessageTask = reader.ReadLineAsync(readCancellation.Token).AsTask();
        var removedTask = Task.Delay(Timeout.InfiniteTimeSpan, registration.Removed);

        while (!registration.Granted.IsCompleted)
        {
            using var waitCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var stateChangeTask =
                registration.ReadStateChangeAsync(waitCancellation.Token).AsTask();
            var remainingWait = registration.GetRemainingWait(DateTimeOffset.UtcNow);
            var timeoutTask = Task.Delay(
                registration.IsPaused
                    ? Timeout.InfiniteTimeSpan
                    : remainingWait > TimeSpan.Zero
                        ? remainingWait
                        : TimeSpan.Zero,
                waitCancellation.Token);
            var first = await Task.WhenAny(
                removedTask,
                registration.Granted,
                clientMessageTask,
                stateChangeTask,
                timeoutTask);

            waitCancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();

            if (registration.Removed.IsCancellationRequested)
            {
                readCancellation.Cancel();
                await ObserveCanceledReadAsync(clientMessageTask);
                await ObserveCanceledStateChangeAsync(stateChangeTask);
                await WriteErrorAsync(
                    writer,
                    "job_cancelled",
                    "The queued job was killed by the queue operator.");
                return;
            }

            if (first == timeoutTask)
            {
                _coordinator.Remove(requestId, registration.Request.LeaseName);
                readCancellation.Cancel();
                await ObserveCanceledReadAsync(clientMessageTask);
                await WriteErrorAsync(
                    writer,
                    "wait_timeout",
                    "Timed out waiting for a heavy-job grant.");
                return;
            }

            if (first == clientMessageTask)
            {
                await HandlePreGrantMessageAsync(
                    clientMessageTask.Result,
                    registration,
                    writer);
                return;
            }

            if (first == stateChangeTask)
            {
                var stateChange = await stateChangeTask;
                await WriteStateChangeAsync(
                    writer,
                    requestId,
                    stateChange == QueueRegistrationState.Paused);
            }
        }

        await writer.WriteLineAsync(Protocol.Serialize(new
        {
            version = Protocol.Version,
            type = "grant",
            requestId,
            grantedAt = registration.ActivatedAt ?? DateTimeOffset.UtcNow
        }));

        var completion = Protocol.ParseClientMessage(await clientMessageTask);
        if (completion.RequestId != requestId ||
            !string.Equals(
                completion.LeaseName,
                registration.Request.LeaseName,
                StringComparison.Ordinal))
        {
            throw new ProtocolException(
                "request_id_mismatch",
                "The completion does not match the active job lease.");
        }

        if (completion.Type == "cancel")
        {
            _coordinator.Remove(requestId, completion.LeaseName);
            return;
        }

        if (completion.Type != "complete")
        {
            throw new ProtocolException(
                "expected_completion",
                "The active client must send a complete or cancel message.");
        }

        if (!_coordinator.Complete(requestId, completion.LeaseName!))
        {
            throw new ProtocolException(
                "unknown_active_request",
                "The active request could not be completed.");
        }

        await WriteAckAsync(writer, requestId);
    }

    private async Task HandlePreGrantMessageAsync(
        string? line,
        QueueRegistration registration,
        StreamWriter writer)
    {
        if (line is null)
        {
            return;
        }

        var message = Protocol.ParseClientMessage(line);
        if (message.Type != "cancel" ||
            message.RequestId != registration.Request.RequestId ||
            !string.Equals(
                message.LeaseName,
                registration.Request.LeaseName,
                StringComparison.Ordinal))
        {
            throw new ProtocolException(
                "expected_cancel",
                "A waiting client may only cancel its own request lease.");
        }

        _coordinator.Remove(message.RequestId, message.LeaseName);
        await WriteAckAsync(writer, message.RequestId);
    }

    private async Task CompleteAsync(ClientMessage completion, StreamWriter writer)
    {
        if (!_coordinator.Complete(completion.RequestId, completion.LeaseName!))
        {
            await WriteErrorAsync(
                writer,
                "unknown_active_request",
                "No active or recently completed request matches this lease.");
            return;
        }

        await WriteAckAsync(writer, completion.RequestId);
    }

    private async Task CancelAsync(ClientMessage cancellation, StreamWriter writer)
    {
        if (!_coordinator.Remove(cancellation.RequestId, cancellation.LeaseName))
        {
            await WriteErrorAsync(
                writer,
                "unknown_request",
                "No queued request matches this lease.");
            return;
        }

        await WriteAckAsync(writer, cancellation.RequestId);
    }

    private async Task RunSchedulerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var candidate = _coordinator.PeekNext();
                if (candidate is null)
                {
                    await _schedulerSignal.WaitAsync(cancellationToken);
                    continue;
                }

                try
                {
                    _coordinator.TryActivateNext(candidate.Request.RequestId);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunLeaseMonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _coordinator.RemoveExpiredDisconnectedLeases();
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                }
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void CoordinatorChanged(object? sender, EventArgs eventArgs) =>
        _schedulerSignal.Release();

    private static Task WriteStateChangeAsync(
        StreamWriter writer,
        Guid requestId,
        bool isPaused) =>
        writer.WriteLineAsync(Protocol.Serialize(new
        {
            version = Protocol.Version,
            type = isPaused ? "paused" : "resumed",
            requestId,
            changedAt = DateTimeOffset.UtcNow
        }));

    private static Task WriteAckAsync(StreamWriter writer, Guid requestId) =>
        writer.WriteLineAsync(Protocol.Serialize(new
        {
            version = Protocol.Version,
            type = "ack",
            requestId
        }));

    private static Task WriteErrorAsync(StreamWriter writer, string code, string message) =>
        writer.WriteLineAsync(Protocol.Serialize(new
        {
            version = Protocol.Version,
            type = "error",
            code,
            message
        }));

    private static async Task TryWriteErrorAsync(
        StreamWriter writer,
        string code,
        string message)
    {
        try
        {
            await WriteErrorAsync(writer, code, message);
        }
        catch (IOException)
        {
        }
    }

    private static async Task ObserveCanceledReadAsync(Task<string?> readTask)
    {
        try
        {
            await readTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task ObserveCanceledStateChangeAsync(
        Task<QueueRegistrationState> stateChangeTask)
    {
        try
        {
            await stateChangeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private sealed class SafeStreamWriter(
        Stream stream,
        Encoding encoding,
        bool leaveOpen)
        : StreamWriter(stream, encoding, bufferSize: 1024, leaveOpen)
    {
        protected override void Dispose(bool disposing)
        {
            try
            {
                base.Dispose(disposing);
            }
            catch (IOException)
            {
            }
        }
    }
}
