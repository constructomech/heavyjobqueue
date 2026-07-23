namespace HeavyJobQueue.Core;

public static class RequestLease
{
    public static string GetName(Guid requestId) =>
        $@"Local\GitHubCopilot.HeavyJobQueue.Lease.{requestId:D}";

    public static bool IsHeld(string leaseName)
    {
        try
        {
            using var mutex = Mutex.OpenExisting(leaseName);
            try
            {
                if (!mutex.WaitOne(0))
                {
                    return true;
                }
            }
            catch (AbandonedMutexException)
            {
            }

            mutex.ReleaseMutex();
            return false;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }
}
