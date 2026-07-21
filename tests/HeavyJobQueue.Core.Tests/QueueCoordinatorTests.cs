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

        Assert.IsTrue(coordinator.Complete(first.Request.RequestId));
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
        Assert.AreEqual(active.Request.RequestId, state.Active!.RequestId);
        CollectionAssert.AreEqual(
            new[] { third.Request.RequestId, second.Request.RequestId },
            state.Waiting.Select(job => job.RequestId).ToArray());
    }

    [TestMethod]
    public void DisconnectRemovesWaiterAndReleasesActiveJob()
    {
        var coordinator = new QueueCoordinator();
        var active = coordinator.Enqueue(CreateRequest("active"));
        var disconnectedWaiter = coordinator.Enqueue(CreateRequest("gone"));
        var next = coordinator.Enqueue(CreateRequest("next"));
        coordinator.TryActivateNext(active.Request.RequestId);

        Assert.IsTrue(coordinator.Disconnect(disconnectedWaiter.Request.RequestId));
        Assert.IsTrue(disconnectedWaiter.Removed.IsCancellationRequested);
        Assert.IsTrue(coordinator.Disconnect(active.Request.RequestId));
        Assert.IsTrue(active.Removed.IsCancellationRequested);

        Assert.AreEqual(next.Request.RequestId, coordinator.PeekNext()!.Request.RequestId);
        Assert.IsTrue(coordinator.TryActivateNext(next.Request.RequestId));
        Assert.IsTrue(next.Granted.IsCompletedSuccessfully);
    }

    private static JobRequest CreateRequest(string label) =>
        new(
            Guid.NewGuid(),
            label,
            Environment.ProcessId,
            Environment.CurrentDirectory,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5));
}
