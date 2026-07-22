using System.Text.Json;
using HeavyJobQueue.Core;

namespace HeavyJobQueue.Core.Tests;

[TestClass]
public sealed class LegacyLockTests
{
    [TestMethod]
    public async Task UpdatingLeaseOwnerReplacesDiagnosticMetadata()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            $"HeavyJobQueue.Tests.{Guid.NewGuid():N}");
        try
        {
            var legacyLock = new LegacyLock(directory);
            var original = CreateRequest("queued");
            var activeOverride = CreateRequest("active override");

            using var lease = await legacyLock.AcquireAsync(original, CancellationToken.None);
            await lease.UpdateOwnerAsync(activeOverride, CancellationToken.None);

            var ownerPath = Path.Combine(
                directory,
                "GitHubCopilot",
                "locks",
                "heavy-job.owner.json");
            using var owner = JsonDocument.Parse(await File.ReadAllTextAsync(ownerPath));
            Assert.AreEqual(
                activeOverride.RequestId.ToString("D"),
                owner.RootElement.GetProperty("RequestId").GetString());
            Assert.AreEqual(
                activeOverride.Label,
                owner.RootElement.GetProperty("Label").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JobRequest CreateRequest(string label) =>
        new(
            Guid.NewGuid(),
            label,
            Environment.ProcessId,
            Environment.CurrentDirectory,
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(5),
            "Write-Output test");
}
