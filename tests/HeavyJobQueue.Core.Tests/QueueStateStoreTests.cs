using HeavyJobQueue.Core;

namespace HeavyJobQueue.Core.Tests;

[TestClass]
public sealed class QueueStateStoreTests
{
    [TestMethod]
    public void RestoresActiveWaitingAndPausedOrder()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new QueueStateStore(Path.Combine(directory, "queue-state.json"));
            var coordinator = new QueueCoordinator(store);
            var active = coordinator.Enqueue(CreateRequest("active"));
            var waiting = coordinator.Enqueue(CreateRequest("waiting"));
            var paused = coordinator.Enqueue(CreateRequest("paused"));
            coordinator.TryActivateNext(active.Request.RequestId);
            coordinator.Pause(paused.Request.RequestId);

            var restored = new QueueCoordinator(store);
            var state = restored.Snapshot();

            Assert.AreEqual(active.Request.RequestId, state.ActiveJobs.Single().RequestId);
            CollectionAssert.AreEqual(
                new[] { waiting.Request.RequestId, paused.Request.RequestId },
                state.Waiting.Select(item => item.RequestId).ToArray());
            Assert.AreEqual(JobStatus.Paused, state.Waiting[1].Status);

            restored.AttachOrEnqueue(waiting.Request);
            Assert.IsTrue(restored.Complete(
                active.Request.RequestId,
                active.Request.LeaseName));
            Assert.AreEqual(
                waiting.Request.RequestId,
                restored.PeekNext()!.Request.RequestId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void CompletionAcknowledgementSurvivesRestart()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new QueueStateStore(Path.Combine(directory, "queue-state.json"));
            var coordinator = new QueueCoordinator(store);
            var active = coordinator.Enqueue(CreateRequest("active"));
            coordinator.TryActivateNext(active.Request.RequestId);
            coordinator.Complete(active.Request.RequestId, active.Request.LeaseName);

            var restored = new QueueCoordinator(store);

            Assert.IsTrue(restored.Complete(
                active.Request.RequestId,
                active.Request.LeaseName));
            Assert.ThrowsExactly<RequestCompletedException>(
                () => restored.AttachOrEnqueue(active.Request));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void RestoredActiveJobRemainsUntilItsWrapperLeaseEnds()
    {
        var directory = CreateTemporaryDirectory();
        var request = CreateRequest("active");
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var leaseThread = new Thread(() =>
        {
            using var lease = new Mutex(false, request.LeaseName);
            lease.WaitOne();
            acquired.Set();
            release.Wait();
            lease.ReleaseMutex();
        });
        leaseThread.Start();
        Assert.IsTrue(acquired.Wait(TimeSpan.FromSeconds(2)));
        try
        {
            var store = new QueueStateStore(Path.Combine(directory, "queue-state.json"));
            var coordinator = new QueueCoordinator(store);
            coordinator.Enqueue(request);
            coordinator.TryActivateNext(request.RequestId);

            var restored = new QueueCoordinator(store);
            Assert.AreEqual(0, restored.RemoveExpiredDisconnectedLeases());
            Assert.HasCount(1, restored.Snapshot().ActiveJobs);

            release.Set();
            Assert.IsTrue(leaseThread.Join(TimeSpan.FromSeconds(2)));

            Assert.AreEqual(1, restored.RemoveExpiredDisconnectedLeases());
            Assert.IsEmpty(restored.Snapshot().ActiveJobs);
        }

        finally
        {
            release.Set();
            leaseThread.Join(TimeSpan.FromSeconds(2));
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void CompletionRecoversWhenBackupPredatesActivation()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var statePath = Path.Combine(directory, "queue-state.json");
            var store = new QueueStateStore(statePath);
            var coordinator = new QueueCoordinator(store);
            var active = coordinator.Enqueue(CreateRequest("active"));
            coordinator.TryActivateNext(active.Request.RequestId);
            File.WriteAllText(statePath, "{corrupt");

            var restored = new QueueCoordinator(store);

            Assert.IsEmpty(restored.Snapshot().ActiveJobs);
            Assert.HasCount(1, restored.Snapshot().Waiting);
            Assert.IsTrue(restored.Complete(
                active.Request.RequestId,
                active.Request.LeaseName));
            Assert.IsEmpty(restored.Snapshot().Waiting);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"HeavyJobQueue.Tests.{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static JobRequest CreateRequest(string label)
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
            RequestLease.GetName(requestId));
    }
}
