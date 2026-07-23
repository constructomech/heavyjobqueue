using HeavyJobQueue.Core;

namespace HeavyJobQueue.Core.Tests;

[TestClass]
public sealed class QueueCoordinatorTests
{
    [TestMethod]
    public void ActivatesJobsInFifoOrder()
    {
        var coordinator = new QueueCoordinator();
        var first = coordinator.Enqueue(CreateRequest("first"));
        var second = coordinator.Enqueue(CreateRequest("second"));

        Assert.AreEqual(first.Request.RequestId, coordinator.PeekNext()!.Request.RequestId);
        Assert.IsTrue(coordinator.TryActivateNext(first.Request.RequestId));
        Assert.IsTrue(first.Granted.IsCompletedSuccessfully);
        Assert.IsFalse(second.Granted.IsCompleted);

        Assert.IsTrue(coordinator.Complete(
            first.Request.RequestId,
            first.Request.LeaseName));
        Assert.AreEqual(second.Request.RequestId, coordinator.PeekNext()!.Request.RequestId);
        Assert.IsTrue(coordinator.TryActivateNext(second.Request.RequestId));
        Assert.IsTrue(second.Granted.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void ReordersOnlyWaitingJobs()
    {
        var coordinator = new QueueCoordinator();
        var active = coordinator.Enqueue(CreateRequest("active"));
        var second = coordinator.Enqueue(CreateRequest("second"));
        var third = coordinator.Enqueue(CreateRequest("third"));
        coordinator.TryActivateNext(active.Request.RequestId);

        Assert.IsTrue(coordinator.MoveUp(third.Request.RequestId));
        Assert.IsFalse(coordinator.MoveUp(active.Request.RequestId));

        var state = coordinator.Snapshot();
        Assert.AreEqual(active.Request.RequestId, state.ActiveJobs.Single().RequestId);
        CollectionAssert.AreEqual(
            new[] { third.Request.RequestId, second.Request.RequestId },
            state.Waiting.Select(job => job.RequestId).ToArray());
    }

    [TestMethod]
    public void ExplicitRemovalRemovesWaiterAndReleasesActiveJob()
    {
        var coordinator = new QueueCoordinator();
        var active = coordinator.Enqueue(CreateRequest("active"));
        var disconnectedWaiter = coordinator.Enqueue(CreateRequest("gone"));
        var next = coordinator.Enqueue(CreateRequest("next"));
        coordinator.TryActivateNext(active.Request.RequestId);

        Assert.IsTrue(coordinator.Remove(disconnectedWaiter.Request.RequestId));
        Assert.IsTrue(disconnectedWaiter.Removed.IsCancellationRequested);
        Assert.IsTrue(coordinator.Remove(active.Request.RequestId));
        Assert.IsTrue(active.Removed.IsCancellationRequested);

        Assert.AreEqual(next.Request.RequestId, coordinator.PeekNext()!.Request.RequestId);
        Assert.IsTrue(coordinator.TryActivateNext(next.Request.RequestId));
        Assert.IsTrue(next.Granted.IsCompletedSuccessfully);
    }

    [TestMethod]
    public void RunNowGrantsMultipleOverridesAndBlocksAutomaticQueue()
    {
        var coordinator = new QueueCoordinator();
        var active = coordinator.Enqueue(CreateRequest("active"));
        var firstOverride = coordinator.Enqueue(CreateRequest("override-1"));
        var secondOverride = coordinator.Enqueue(CreateRequest("override-2"));
        var waiting = coordinator.Enqueue(CreateRequest("waiting"));
        coordinator.TryActivateNext(active.Request.RequestId);

        Assert.IsTrue(coordinator.RunNow(firstOverride.Request.RequestId));
        Assert.IsTrue(coordinator.RunNow(secondOverride.Request.RequestId));
        Assert.IsTrue(firstOverride.Granted.IsCompletedSuccessfully);
        Assert.IsTrue(secondOverride.Granted.IsCompletedSuccessfully);
        Assert.IsNull(coordinator.PeekNext());

        var state = coordinator.Snapshot();
        Assert.HasCount(3, state.ActiveJobs);
        Assert.AreEqual(2, state.ActiveJobs.Count(job => job.IsManualOverride));

        coordinator.Complete(active.Request.RequestId, active.Request.LeaseName);
        coordinator.Complete(
            firstOverride.Request.RequestId,
            firstOverride.Request.LeaseName);
        Assert.IsNull(coordinator.PeekNext());

        coordinator.Complete(
            secondOverride.Request.RequestId,
            secondOverride.Request.LeaseName);
        Assert.AreEqual(waiting.Request.RequestId, coordinator.PeekNext()!.Request.RequestId);
    }

    [TestMethod]
    public void RemovingOverrideKeepsQueueBlockedByOtherActiveJobs()
    {
        var coordinator = new QueueCoordinator();
        var active = coordinator.Enqueue(CreateRequest("active"));
        var manualOverride = coordinator.Enqueue(CreateRequest("override"));
        var waiting = coordinator.Enqueue(CreateRequest("waiting"));
        coordinator.TryActivateNext(active.Request.RequestId);
        coordinator.RunNow(manualOverride.Request.RequestId);

        Assert.IsTrue(coordinator.Remove(manualOverride.Request.RequestId));
        Assert.IsNull(coordinator.PeekNext());

        coordinator.Complete(active.Request.RequestId, active.Request.LeaseName);
        Assert.AreEqual(waiting.Request.RequestId, coordinator.PeekNext()!.Request.RequestId);
    }

    [TestMethod]
    public async Task PausedJobsMoveBehindNewWaitersUntilResumed()
    {
        var coordinator = new QueueCoordinator();
        var paused = coordinator.Enqueue(CreateRequest("paused"));
        var existing = coordinator.Enqueue(CreateRequest("existing"));

        var pausedState = paused.ReadStateChangeAsync(CancellationToken.None).AsTask();
        Assert.IsTrue(coordinator.Pause(paused.Request.RequestId));
        Assert.AreEqual(QueueRegistrationState.Paused, await pausedState);
        var arrival = coordinator.Enqueue(CreateRequest("arrival"));

        CollectionAssert.AreEqual(
            new[] { existing.Request.RequestId, arrival.Request.RequestId, paused.Request.RequestId },
            coordinator.Snapshot().Waiting.Select(job => job.RequestId).ToArray());
        Assert.AreEqual(JobStatus.Paused, coordinator.Snapshot().Waiting[^1].Status);

        var resumedState = paused.ReadStateChangeAsync(CancellationToken.None).AsTask();
        Assert.IsTrue(coordinator.Resume(paused.Request.RequestId));
        Assert.AreEqual(QueueRegistrationState.Resumed, await resumedState);
        CollectionAssert.AreEqual(
            new[] { existing.Request.RequestId, arrival.Request.RequestId, paused.Request.RequestId },
            coordinator.Snapshot().Waiting.Select(job => job.RequestId).ToArray());
        Assert.AreEqual(JobStatus.Waiting, coordinator.Snapshot().Waiting[^1].Status);
    }

    [TestMethod]
    public void RunNowCanGrantPausedJob()
    {
        var coordinator = new QueueCoordinator();
        var paused = coordinator.Enqueue(CreateRequest("paused"));
        coordinator.Pause(paused.Request.RequestId);

        Assert.IsTrue(coordinator.RunNow(paused.Request.RequestId));
        Assert.IsTrue(paused.Granted.IsCompletedSuccessfully);
        Assert.IsTrue(coordinator.Snapshot().ActiveJobs.Single().IsManualOverride);
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
