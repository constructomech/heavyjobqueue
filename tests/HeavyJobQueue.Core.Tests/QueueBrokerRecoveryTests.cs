using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using HeavyJobQueue.Core;

namespace HeavyJobQueue.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class QueueBrokerRecoveryTests
{
    [TestMethod]
    public async Task BrokerGrantsSharedJobsTogetherBeforeExclusiveWaiter()
    {
        var pipeName = $"{Protocol.PipeName}.Tests.{Guid.NewGuid():N}";
        var coordinator = new QueueCoordinator();
        var first = CreateRequest("first", JobAccessMode.Shared);
        var second = CreateRequest("second", JobAccessMode.Shared);
        var exclusive = CreateRequest("benchmark");
        using var firstLease = new LeaseHolder(first.LeaseName);
        using var secondLease = new LeaseHolder(second.LeaseName);
        using var exclusiveLease = new LeaseHolder(exclusive.LeaseName);
        await using var broker = new QueueBroker(coordinator, pipeName);
        broker.Start();

        await using var firstClient = await BrokerClient.ConnectAsync(pipeName);
        await firstClient.SendAsync(CreateEnqueue(first));
        Assert.AreEqual("queued", (await firstClient.ReadAsync()).GetProperty("type").GetString());
        Assert.AreEqual("grant", (await firstClient.ReadAsync()).GetProperty("type").GetString());

        await using var secondClient = await BrokerClient.ConnectAsync(pipeName);
        await secondClient.SendAsync(CreateEnqueue(second));
        Assert.AreEqual("queued", (await secondClient.ReadAsync()).GetProperty("type").GetString());
        Assert.AreEqual("grant", (await secondClient.ReadAsync()).GetProperty("type").GetString());

        await using var exclusiveClient = await BrokerClient.ConnectAsync(pipeName);
        await exclusiveClient.SendAsync(CreateEnqueue(exclusive));
        Assert.AreEqual(
            "queued",
            (await exclusiveClient.ReadAsync()).GetProperty("type").GetString());
        Assert.HasCount(2, coordinator.Snapshot().ActiveJobs);
        Assert.AreEqual(
            exclusive.RequestId,
            coordinator.Snapshot().Waiting.Single().RequestId);

        await firstClient.SendAsync(CreateCompletion(first));
        Assert.AreEqual("ack", (await firstClient.ReadAsync()).GetProperty("type").GetString());
        Assert.HasCount(1, coordinator.Snapshot().ActiveJobs);

        await secondClient.SendAsync(CreateCompletion(second));
        Assert.AreEqual("ack", (await secondClient.ReadAsync()).GetProperty("type").GetString());
        Assert.AreEqual(
            "grant",
            (await exclusiveClient.ReadAsync()).GetProperty("type").GetString());
    }

    [TestMethod]
    public async Task WaitingClientReclaimsPositionBehindRestoredActiveLease()
    {
        var directory = CreateTemporaryDirectory();
        var activeRequest = CreateRequest("active");
        var waitingRequest = CreateRequest("waiting");
        using var activeLease = new LeaseHolder(activeRequest.LeaseName);
        using var waitingLease = new LeaseHolder(waitingRequest.LeaseName);
        try
        {
            var pipeName = $"{Protocol.PipeName}.Tests.{Guid.NewGuid():N}";
            var store = new QueueStateStore(Path.Combine(directory, "queue-state.json"));
            var firstCoordinator = new QueueCoordinator(store);
            await using (var firstBroker = new QueueBroker(firstCoordinator, pipeName))
            {
                firstBroker.Start();
                await using var activeClient = await BrokerClient.ConnectAsync(pipeName);
                await activeClient.SendAsync(CreateEnqueue(activeRequest));
                Assert.AreEqual("queued", (await activeClient.ReadAsync()).GetProperty("type").GetString());
                Assert.AreEqual("grant", (await activeClient.ReadAsync()).GetProperty("type").GetString());

                await using var waitingClient = await BrokerClient.ConnectAsync(pipeName);
                await waitingClient.SendAsync(CreateEnqueue(waitingRequest));
                Assert.AreEqual("queued", (await waitingClient.ReadAsync()).GetProperty("type").GetString());
            }

            var restoredCoordinator = new QueueCoordinator(store);
            await using var restoredBroker = new QueueBroker(restoredCoordinator, pipeName);
            restoredBroker.Start();
            await using var reclaimedWaitingClient = await BrokerClient.ConnectAsync(pipeName);
            await reclaimedWaitingClient.SendAsync(CreateEnqueue(waitingRequest));
            Assert.AreEqual(
                "queued",
                (await reclaimedWaitingClient.ReadAsync()).GetProperty("type").GetString());
            Assert.HasCount(1, restoredCoordinator.Snapshot().ActiveJobs);

            await using var completionClient = await BrokerClient.ConnectAsync(pipeName);
            await completionClient.SendAsync(Protocol.Serialize(new
            {
                version = Protocol.Version,
                type = "complete",
                requestId = activeRequest.RequestId,
                leaseName = activeRequest.LeaseName,
                succeeded = true,
                exitCode = 0,
                error = (string?)null
            }));
            Assert.AreEqual(
                "ack",
                (await completionClient.ReadAsync()).GetProperty("type").GetString());
            Assert.AreEqual(
                "grant",
                (await reclaimedWaitingClient.ReadAsync()).GetProperty("type").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task JobEnqueuedWhileQueuePausedIsNotifiedAndGrantedAfterResumeAll()
    {
        var directory = CreateTemporaryDirectory();
        var request = CreateRequest("held");
        using var lease = new LeaseHolder(request.LeaseName);
        try
        {
            var pipeName = $"{Protocol.PipeName}.Tests.{Guid.NewGuid():N}";
            var store = new QueueStateStore(Path.Combine(directory, "queue-state.json"));
            var coordinator = new QueueCoordinator(store);
            await using var broker = new QueueBroker(coordinator, pipeName);
            broker.Start();
            Assert.IsTrue(coordinator.PauseAll());

            await using var client = await BrokerClient.ConnectAsync(pipeName);
            await client.SendAsync(CreateEnqueue(request));
            Assert.AreEqual("queued", (await client.ReadAsync()).GetProperty("type").GetString());
            Assert.AreEqual("paused", (await client.ReadAsync()).GetProperty("type").GetString());
            Assert.IsNull(coordinator.PeekNext());

            Assert.IsTrue(coordinator.ResumeAll());

            var received = new List<string>();
            string? type;
            do
            {
                type = (await client.ReadAsync()).GetProperty("type").GetString();
                received.Add(type!);
            }
            while (type != "grant");

            CollectionAssert.Contains(received, "resumed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task ReconnectingClientIsToldTheQueueIsStillPausedAfterRestart()
    {
        var directory = CreateTemporaryDirectory();
        var request = CreateRequest("held");
        using var lease = new LeaseHolder(request.LeaseName);
        try
        {
            var pipeName = $"{Protocol.PipeName}.Tests.{Guid.NewGuid():N}";
            var store = new QueueStateStore(Path.Combine(directory, "queue-state.json"));
            var firstCoordinator = new QueueCoordinator(store);
            Assert.IsTrue(firstCoordinator.PauseAll());
            await using (var firstBroker = new QueueBroker(firstCoordinator, pipeName))
            {
                firstBroker.Start();
                await using var client = await BrokerClient.ConnectAsync(pipeName);
                await client.SendAsync(CreateEnqueue(request));
                Assert.AreEqual(
                    "queued",
                    (await client.ReadAsync()).GetProperty("type").GetString());
                Assert.AreEqual(
                    "paused",
                    (await client.ReadAsync()).GetProperty("type").GetString());
            }

            var restoredCoordinator = new QueueCoordinator(store);
            Assert.IsTrue(restoredCoordinator.IsQueuePaused);
            await using var restoredBroker = new QueueBroker(restoredCoordinator, pipeName);
            restoredBroker.Start();

            await using var reconnected = await BrokerClient.ConnectAsync(pipeName);
            await reconnected.SendAsync(CreateEnqueue(request));
            Assert.AreEqual(
                "queued",
                (await reconnected.ReadAsync()).GetProperty("type").GetString());
            Assert.AreEqual(
                "paused",
                (await reconnected.ReadAsync()).GetProperty("type").GetString());

            Assert.IsTrue(restoredCoordinator.ResumeAll());

            var received = new List<string>();
            string? type;
            do
            {
                type = (await reconnected.ReadAsync()).GetProperty("type").GetString();
                received.Add(type!);
            }
            while (type != "grant");

            CollectionAssert.Contains(received, "resumed");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public async Task OperatorKillNotifiesPausedClientWithoutChangingActiveJob()
    {
        var pipeName = $"{Protocol.PipeName}.Tests.{Guid.NewGuid():N}";
        var coordinator = new QueueCoordinator();
        var active = coordinator.Enqueue(CreateRequest("active"));
        var paused = CreateRequest("paused");
        coordinator.TryActivateNext(active.Request.RequestId);
        await using var broker = new QueueBroker(coordinator, pipeName);
        broker.Start();

        await using var client = await BrokerClient.ConnectAsync(pipeName);
        await client.SendAsync(CreateEnqueue(paused));
        Assert.AreEqual("queued", (await client.ReadAsync()).GetProperty("type").GetString());
        Assert.IsTrue(coordinator.Pause(paused.RequestId));
        Assert.AreEqual("paused", (await client.ReadAsync()).GetProperty("type").GetString());

        Assert.IsTrue(coordinator.RemoveWaiting(paused.RequestId));

        var cancellation = await client.ReadAsync();
        Assert.AreEqual("error", cancellation.GetProperty("type").GetString());
        Assert.AreEqual("job_cancelled", cancellation.GetProperty("code").GetString());
        Assert.AreEqual(active.Request.RequestId, coordinator.Snapshot().ActiveJobs.Single().RequestId);
        Assert.IsEmpty(coordinator.Snapshot().Waiting);
    }

    private static string CreateEnqueue(JobRequest request) =>
        Protocol.Serialize(new
        {
            version = Protocol.Version,
            type = "enqueue",
            requestId = request.RequestId,
            request.Label,
            callerPid = request.CallerPid,
            request.Cwd,
            command = request.Command,
            accessMode = request.AccessMode == JobAccessMode.Shared ? "shared" : "exclusive",
            enqueuedAt = request.EnqueuedAt.ToString("o"),
            waitTimeoutSeconds = (int)request.WaitTimeout.TotalSeconds,
            leaseName = request.LeaseName
        });

    private static string CreateCompletion(JobRequest request) =>
        Protocol.Serialize(new
        {
            version = Protocol.Version,
            type = "complete",
            requestId = request.RequestId,
            leaseName = request.LeaseName,
            succeeded = true,
            exitCode = 0,
            error = (string?)null
        });

    private static JobRequest CreateRequest(
        string label,
        JobAccessMode accessMode = JobAccessMode.Exclusive)
    {
        var requestId = Guid.NewGuid();
        return new JobRequest(
            requestId,
            label,
            Environment.ProcessId,
            Environment.CurrentDirectory,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            $"Write-Output '{label}'",
            RequestLease.GetName(requestId),
            accessMode);
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"HeavyJobQueue.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class LeaseHolder : IDisposable
    {
        private readonly ManualResetEventSlim _acquired = new();
        private readonly ManualResetEventSlim _release = new();
        private readonly Thread _thread;

        public LeaseHolder(string leaseName)
        {
            _thread = new Thread(() =>
            {
                using var lease = new Mutex(false, leaseName);
                lease.WaitOne();
                _acquired.Set();
                _release.Wait();
                lease.ReleaseMutex();
            });
            _thread.Start();
            if (!_acquired.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("The test request lease was not acquired.");
            }
        }

        public void Dispose()
        {
            _release.Set();
            _thread.Join(TimeSpan.FromSeconds(2));
            _acquired.Dispose();
            _release.Dispose();
        }
    }

    private sealed class BrokerClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private BrokerClient(
            NamedPipeClientStream pipe,
            StreamReader reader,
            StreamWriter writer)
        {
            _pipe = pipe;
            _reader = reader;
            _writer = writer;
        }

        public static async Task<BrokerClient> ConnectAsync(string pipeName)
        {
            var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await pipe.ConnectAsync(timeout.Token);
            var reader = new StreamReader(
                pipe,
                new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            var writer = new StreamWriter(
                pipe,
                new UTF8Encoding(false),
                leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\n"
            };
            return new BrokerClient(pipe, reader, writer);
        }

        public Task SendAsync(string message) => _writer.WriteLineAsync(message);

        public async Task<JsonElement> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var line = await _reader.ReadLineAsync(timeout.Token);
            Assert.IsNotNull(line);
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }

        public async ValueTask DisposeAsync()
        {
            _writer.Dispose();
            _reader.Dispose();
            await _pipe.DisposeAsync();
        }
    }
}
