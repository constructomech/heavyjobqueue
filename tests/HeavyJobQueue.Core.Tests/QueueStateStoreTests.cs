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

    [TestMethod]
    public void QueuePauseSurvivesRestart()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new QueueStateStore(Path.Combine(directory, "queue-state.json"));
            var coordinator = new QueueCoordinator(store);
            var individual = coordinator.Enqueue(CreateRequest("individual"));
            var held = coordinator.Enqueue(CreateRequest("held"));
            coordinator.Pause(individual.Request.RequestId);
            Assert.IsTrue(coordinator.PauseAll());

            var restored = new QueueCoordinator(store);

            Assert.IsTrue(restored.IsQueuePaused);
            Assert.IsTrue(restored.Snapshot().Waiting.All(job =>
                job.Status == JobStatus.Paused));
            Assert.IsNull(restored.PeekNext());

            var arrival = restored.Enqueue(CreateRequest("arrival"));
            Assert.IsTrue(arrival.IsPaused);
            Assert.IsNull(restored.PeekNext());

            Assert.IsTrue(restored.ResumeAll());
            var state = restored.Snapshot();
            Assert.AreEqual(
                JobStatus.Paused,
                state.Waiting.Single(job => job.RequestId == individual.Request.RequestId).Status);
            Assert.AreEqual(
                JobStatus.Waiting,
                state.Waiting.Single(job => job.RequestId == held.Request.RequestId).Status);
            Assert.AreEqual(
                arrival.Request.RequestId,
                state.Waiting.Single(job => job.RequestId == arrival.Request.RequestId).RequestId);
            Assert.IsFalse(arrival.IsPaused);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void LoadsStateWrittenBeforeQueuePauseExisted()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var statePath = Path.Combine(directory, "queue-state.json");
            var waitingId = Guid.NewGuid();
            var pausedId = Guid.NewGuid();
            var waitingLease = JsonEscape(RequestLease.GetName(waitingId));
            var pausedLease = JsonEscape(RequestLease.GetName(pausedId));
            File.WriteAllText(statePath, $$"""
                {
                  "version": 1,
                  "jobs": [
                    {
                      "requestId": "{{waitingId}}",
                      "label": "waiting",
                      "callerPid": 1234,
                      "cwd": "C:\\src",
                      "enqueuedAt": "2026-01-01T00:00:00+00:00",
                      "waitTimeout": "00:05:00",
                      "command": "Write-Output 'waiting'",
                      "leaseName": "{{waitingLease}}",
                      "activatedAt": null,
                      "status": 0,
                      "isManualOverride": false,
                      "pausedAt": null,
                      "totalPausedDuration": "00:00:00"
                    },
                    {
                      "requestId": "{{pausedId}}",
                      "label": "paused",
                      "callerPid": 1235,
                      "cwd": "C:\\src",
                      "enqueuedAt": "2026-01-01T00:00:00+00:00",
                      "waitTimeout": "00:05:00",
                      "command": "Write-Output 'paused'",
                      "leaseName": "{{pausedLease}}",
                      "activatedAt": null,
                      "status": 1,
                      "isManualOverride": false,
                      "pausedAt": "2026-01-01T00:01:00+00:00",
                      "totalPausedDuration": "00:00:30"
                    }
                  ],
                  "completions": []
                }
                """);

            var coordinator = new QueueCoordinator(new QueueStateStore(statePath));

            Assert.IsFalse(coordinator.IsQueuePaused);
            var state = coordinator.Snapshot();
            CollectionAssert.AreEqual(
                new[] { waitingId, pausedId },
                state.Waiting.Select(job => job.RequestId).ToArray());
            Assert.AreEqual(JobStatus.Waiting, state.Waiting[0].Status);
            Assert.AreEqual(JobStatus.Paused, state.Waiting[1].Status);
            Assert.IsFalse(state.Waiting[1].IsPausedByQueue);

            Assert.IsTrue(coordinator.PauseAll());
            Assert.IsTrue(coordinator.ResumeAll());
            var resumed = coordinator.Snapshot();
            Assert.AreEqual(
                JobStatus.Waiting,
                resumed.Waiting.Single(job => job.RequestId == waitingId).Status);
            Assert.AreEqual(
                JobStatus.Paused,
                resumed.Waiting.Single(job => job.RequestId == pausedId).Status);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string JsonEscape(string value) => value.Replace("\\", "\\\\");

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
