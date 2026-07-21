using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace HeavyJobQueue.Core;

public sealed class QueueBroker : IAsyncDisposable
{
    private readonly QueueCoordinator _coordinator;
    private readonly LegacyLock _legacyLock;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _schedulerSignal = new(0);
    private readonly ConcurrentDictionary<int, Task> _clients = new();
    private Task? _acceptTask;
    private Task? _schedulerTask;
    private int _clientSequence;

    public QueueBroker(QueueCoordinator coordinator, LegacyLock legacyLock)
    {
        _coordinator = coordinator;
        _legacyLock = legacyLock;
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
    }

    public async ValueTask DisposeAsync()
    {
        _coordinator.Changed -= CoordinatorChanged;
        _shutdown.Cancel();
        _schedulerSignal.Release();
        _coordinator.DisconnectAll();

        var tasks = new[] { _acceptTask, _schedulerTask }
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
                    Protocol.PipeName,
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
        Guid? requestId = null;
        await using (pipe)
        using (var reader = new StreamReader(
            pipe,
            new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false,
            leaveOpen: true))
        using (var writer = new StreamWriter(
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
                ClientMessage enqueue;
                try
                {
                    enqueue = Protocol.ParseClientMessage(
                        await reader.ReadLineAsync(cancellationToken));
                    if (enqueue.Type != "enqueue")
                    {
                        throw new ProtocolException(
                            "expected_enqueue",
                            "The first message must be an enqueue request.");
                    }
                }
                catch (ProtocolException exception)
                {
                    await WriteErrorAsync(writer, exception.Code, exception.Message);
                    return;
                }

                requestId = enqueue.RequestId;
                QueueRegistration registration;
                try
                {
                    registration = _coordinator.Enqueue(new JobRequest(
                        enqueue.RequestId,
                        enqueue.Label!,
                        enqueue.CallerPid!.Value,
                        enqueue.Cwd!,
                        enqueue.EnqueuedAt!.Value,
                        enqueue.WaitTimeout!.Value));
                }
                catch (InvalidOperationException)
                {
                    requestId = null;
                    await WriteErrorAsync(
                        writer,
                        "duplicate_request",
                        "A job with this request ID is already queued.");
                    return;
                }

                await writer.WriteLineAsync(Protocol.Serialize(new
                {
                    version = Protocol.Version,
                    type = "queued",
                    requestId = enqueue.RequestId,
                    position = _coordinator.GetWaitingPosition(enqueue.RequestId)
                }));

                using var readCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                using var timeoutCancellation = new CancellationTokenSource();
                var clientMessageTask =
                    reader.ReadLineAsync(readCancellation.Token).AsTask();
                var timeoutTask = Task.Delay(
                    enqueue.WaitTimeout.Value,
                    timeoutCancellation.Token);
                var first = await Task.WhenAny(
                    registration.Granted,
                    clientMessageTask,
                    timeoutTask);
                cancellationToken.ThrowIfCancellationRequested();

                if (first == timeoutTask)
                {
                    _coordinator.Disconnect(enqueue.RequestId);
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
                    timeoutCancellation.Cancel();
                    await HandlePreGrantMessageAsync(
                        clientMessageTask.Result,
                        enqueue.RequestId,
                        writer);
                    return;
                }

                timeoutCancellation.Cancel();
                await writer.WriteLineAsync(Protocol.Serialize(new
                {
                    version = Protocol.Version,
                    type = "grant",
                    requestId = enqueue.RequestId,
                    grantedAt = DateTimeOffset.UtcNow
                }));

                var completion = Protocol.ParseClientMessage(await clientMessageTask);
                if (completion.RequestId != enqueue.RequestId)
                {
                    throw new ProtocolException(
                        "request_id_mismatch",
                        "The completion request ID does not match the active job.");
                }

                if (completion.Type == "cancel")
                {
                    _coordinator.Disconnect(enqueue.RequestId);
                    return;
                }

                if (completion.Type != "complete")
                {
                    throw new ProtocolException(
                        "expected_completion",
                        "The active client must send a complete or cancel message.");
                }

                _coordinator.Complete(enqueue.RequestId);
                requestId = null;
                await writer.WriteLineAsync(Protocol.Serialize(new
                {
                    version = Protocol.Version,
                    type = "ack",
                    requestId = enqueue.RequestId
                }));
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
                if (requestId is not null)
                {
                    _coordinator.Disconnect(requestId.Value);
                }
            }
        }
    }

    private async Task HandlePreGrantMessageAsync(
        string? line,
        Guid requestId,
        StreamWriter writer)
    {
        if (line is null)
        {
            _coordinator.Disconnect(requestId);
            return;
        }

        try
        {
            var message = Protocol.ParseClientMessage(line);
            if (message.Type != "cancel" || message.RequestId != requestId)
            {
                throw new ProtocolException(
                    "expected_cancel",
                    "A waiting client may only cancel its own request.");
            }

            _coordinator.Disconnect(requestId);
        }
        catch (ProtocolException exception)
        {
            _coordinator.Disconnect(requestId);
            await WriteErrorAsync(writer, exception.Code, exception.Message);
        }
    }

    private async Task RunSchedulerAsync(CancellationToken cancellationToken)
    {
        LegacyLockLease? activeLease = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_coordinator.Snapshot().Active is not null)
                {
                    await _schedulerSignal.WaitAsync(cancellationToken);
                    continue;
                }

                activeLease?.Dispose();
                activeLease = null;

                var candidate = _coordinator.PeekNext();
                if (candidate is null)
                {
                    await _schedulerSignal.WaitAsync(cancellationToken);
                    continue;
                }

                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    candidate.Removed);

                LegacyLockLease lease;
                try
                {
                    lease = await _legacyLock.AcquireAsync(
                        candidate.Request,
                        linkedCancellation.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    continue;
                }

                if (_coordinator.TryActivateNext(candidate.Request.RequestId))
                {
                    activeLease = lease;
                }
                else
                {
                    lease.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            activeLease?.Dispose();
        }
    }

    private void CoordinatorChanged(object? sender, EventArgs eventArgs) =>
        _schedulerSignal.Release();

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
}
