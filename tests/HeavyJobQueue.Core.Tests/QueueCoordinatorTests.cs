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

    [TestMethod]
    public async Task PauseAllHoldsCurrentWaitersAndNotifiesThem()
    {
        var coordinator = new QueueCoordinator();
        var first = coordinator.Enqueue(CreateRequest("first"));
        var second = coordinator.Enqueue(CreateRequest("second"));
        var firstState = first.ReadStateChangeAsync(CancellationToken.None).AsTask();
        var secondState = second.ReadStateChangeAsync(CancellationToken.None).AsTask();

        Assert.IsTrue(coordinator.PauseAll());
        Assert.IsFalse(coordinator.PauseAll());

        Assert.IsTrue(coordinator.IsQueuePaused);
        Assert.AreEqual(QueueRegistrationState.Paused, await firstState);
        Assert.AreEqual(QueueRegistrationState.Paused, await secondState);
        Assert.IsNull(coordinator.PeekNext());
        CollectionAssert.AreEqual(
            new[] { first.Request.RequestId, second.Request.RequestId },
            coordinator.Snapshot().Waiting.Select(job => job.RequestId).ToArray());
        Assert.IsTrue(coordinator.Snapshot().Waiting.All(job =>
            job.Status == JobStatus.Paused && job.IsPausedByQueue));
    }

    [TestMethod]
    public void JobEnqueuedWhileQueuePausedIsHeldUntilResumeAll()
    {
        var coordinator = new QueueCoordinator();
        coordinator.PauseAll();

        var arrival = coordinator.Enqueue(CreateRequest("arrival"));

        Assert.IsTrue(arrival.IsPaused);
        Assert.IsTrue(arrival.IsPausedByQueue);
        Assert.IsTrue(arrival.SchedulingCanceled.IsCancellationRequested);
        Assert.IsNull(coordinator.PeekNext());
        Assert.AreEqual(JobStatus.Paused, coordinator.Snapshot().Waiting.Single().Status);

        Assert.IsTrue(coordinator.ResumeAll());
        Assert.IsFalse(coordinator.ResumeAll());

        Assert.IsFalse(arrival.IsPaused);
        Assert.AreEqual(
            arrival.Request.RequestId,
            coordinator.PeekNext()!.Request.RequestId);
    }

    [TestMethod]
    public void ResumeAllLeavesIndividuallyPausedJobsPaused()
    {
        var coordinator = new QueueCoordinator();
        var individual = coordinator.Enqueue(CreateRequest("individual"));
        var held = coordinator.Enqueue(CreateRequest("held"));
        Assert.IsTrue(coordinator.Pause(individual.Request.RequestId));
        Assert.IsTrue(coordinator.PauseAll());
        Assert.IsTrue(coordinator.ResumeAll());

        Assert.IsFalse(coordinator.IsQueuePaused);
        Assert.IsTrue(individual.IsPaused);
        Assert.IsFalse(individual.IsPausedByQueue);
        Assert.IsFalse(held.IsPaused);

        var state = coordinator.Snapshot();
        CollectionAssert.AreEqual(
            new[] { held.Request.RequestId, individual.Request.RequestId },
            state.Waiting.Select(job => job.RequestId).ToArray());
        Assert.AreEqual(JobStatus.Paused, state.Waiting[^1].Status);
        Assert.AreEqual(held.Request.RequestId, coordinator.PeekNext()!.Request.RequestId);
    }

    [TestMethod]
    public void CompletingActiveJobWhileQueuePausedDoesNotGrantNextWaiter()
    {
        var coordinator = new QueueCoordinator();
        var active = coordinator.Enqueue(CreateRequest("active"));
        var waiting = coordinator.Enqueue(CreateRequest("waiting"));
        coordinator.TryActivateNext(active.Request.RequestId);

        Assert.IsTrue(coordinator.PauseAll());
        Assert.AreEqual(
            active.Request.RequestId,
            coordinator.Snapshot().ActiveJobs.Single().RequestId);

        Assert.IsTrue(coordinator.Complete(
            active.Request.RequestId,
            active.Request.LeaseName));
        Assert.IsNull(coordinator.PeekNext());
        Assert.IsFalse(coordinator.TryActivateNext(waiting.Request.RequestId));
        Assert.IsFalse(waiting.Granted.IsCompleted);

        Assert.IsTrue(coordinator.ResumeAll());
        Assert.AreEqual(waiting.Request.RequestId, coordinator.PeekNext()!.Request.RequestId);
    }

    [TestMethod]
    public void RunNowOverridesQueuePause()
    {
        var coordinator = new QueueCoordinator();
        var waiter = coordinator.Enqueue(CreateRequest("waiter"));
        var held = coordinator.Enqueue(CreateRequest("held"));
        coordinator.PauseAll();

        Assert.IsTrue(coordinator.RunNow(waiter.Request.RequestId));

        Assert.IsTrue(waiter.Granted.IsCompletedSuccessfully);
        Assert.IsFalse(waiter.IsPaused);
        Assert.IsFalse(waiter.IsPausedByQueue);
        Assert.IsTrue(coordinator.IsQueuePaused);
        Assert.IsTrue(coordinator.Snapshot().ActiveJobs.Single().IsManualOverride);
        Assert.IsTrue(held.IsPaused);
        Assert.IsNull(coordinator.PeekNext());
    }

    [TestMethod]
    public void ResumingSingleJobWhileQueuePausedExemptsItFromTheHold()
    {
        var coordinator = new QueueCoordinator();
        var exempt = coordinator.Enqueue(CreateRequest("exempt"));
        var held = coordinator.Enqueue(CreateRequest("held"));
        coordinator.PauseAll();

        Assert.IsTrue(coordinator.Resume(exempt.Request.RequestId));

        Assert.IsTrue(coordinator.IsQueuePaused);
        Assert.AreEqual(exempt.Request.RequestId, coordinator.PeekNext()!.Request.RequestId);
        Assert.IsTrue(coordinator.TryActivateNext(exempt.Request.RequestId));
        Assert.IsTrue(exempt.Granted.IsCompletedSuccessfully);
        Assert.IsTrue(held.IsPaused);

        Assert.IsTrue(coordinator.Complete(
            exempt.Request.RequestId,
            exempt.Request.LeaseName));
        Assert.IsNull(coordinator.PeekNext());
    }

    [TestMethod]
    public void PausingExemptedJobReturnsItToTheQueueHold()
    {
        var coordinator = new QueueCoordinator();
        var job = coordinator.Enqueue(CreateRequest("job"));
        coordinator.PauseAll();
        Assert.IsTrue(coordinator.Resume(job.Request.RequestId));

        Assert.IsTrue(coordinator.Pause(job.Request.RequestId));

        Assert.IsTrue(job.IsPaused);
        Assert.IsTrue(job.IsPausedByQueue);
        Assert.IsNull(coordinator.PeekNext());

        Assert.IsTrue(coordinator.ResumeAll());
        Assert.IsFalse(job.IsPaused);
        Assert.AreEqual(job.Request.RequestId, coordinator.PeekNext()!.Request.RequestId);
    }

    [TestMethod]
    public void QueuePausedTimeDoesNotCountAgainstTheWaitTimeout()
    {
        var coordinator = new QueueCoordinator();
        var requestId = Guid.NewGuid();
        var waiter = coordinator.Enqueue(new JobRequest(
            requestId,
            "waiter",
            Environment.ProcessId,
            Environment.CurrentDirectory,
            DateTimeOffset.UtcNow - TimeSpan.FromMinutes(4),
            TimeSpan.FromMinutes(5),
            "Write-Output 'waiter'",
            RequestLease.GetName(requestId)));

        Assert.IsTrue(coordinator.PauseAll());

        var remaining = waiter.GetRemainingWait(DateTimeOffset.UtcNow + TimeSpan.FromHours(1));
        Assert.IsTrue(
            remaining > TimeSpan.FromSeconds(30),
            $"Queue-paused time counted against the wait timeout ({remaining}).");
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
