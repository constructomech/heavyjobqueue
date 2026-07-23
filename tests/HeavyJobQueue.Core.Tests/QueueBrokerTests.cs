using HeavyJobQueue.Core;

namespace HeavyJobQueue.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class QueueBrokerTests
{
    [TestMethod]
    public void SynchronousDisposeDoesNotCaptureCallingSynchronizationContext()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new NonPumpingSynchronizationContext());
                var broker = new QueueBroker(
                    new QueueCoordinator(),
                    $"{Protocol.PipeName}.Tests.{Guid.NewGuid():N}");
                broker.Start();
                broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.Start();

        Assert.IsTrue(
            completed.Wait(TimeSpan.FromSeconds(5)),
            "Broker disposal deadlocked on the calling synchronization context.");
        Assert.IsNull(failure);
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(1)));
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        {
        }
    }
}
