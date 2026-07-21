using System.Text;
using System.Text.Json;

namespace HeavyJobQueue.Core;

public sealed class LegacyLock
{
    private readonly string _lockPath;
    private readonly string _ownerPath;

    public LegacyLock(string? localAppData = null)
    {
        var baseDirectory = localAppData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var lockDirectory = Path.Combine(baseDirectory, "GitHubCopilot", "locks");
        _lockPath = Path.Combine(lockDirectory, "heavy-job.lock");
        _ownerPath = Path.Combine(lockDirectory, "heavy-job.owner.json");
    }

    public async Task<LegacyLockLease> AcquireAsync(
        JobRequest request,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);

                try
                {
                    var metadata = new
                    {
                        LockId = request.RequestId.ToString("D"),
                        request.Label,
                        UserName = Environment.UserName,
                        MachineName = Environment.MachineName,
                        ProcessId = request.CallerPid,
                        AcquiredAt = DateTimeOffset.Now.ToString("o"),
                        request.Cwd,
                        RequestId = request.RequestId.ToString("D")
                    };
                    var json = JsonSerializer.Serialize(metadata);
                    await File.WriteAllTextAsync(
                        _ownerPath,
                        json,
                        new UTF8Encoding(false),
                        cancellationToken);
                    return new LegacyLockLease(stream, _ownerPath);
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
            }
        }
    }
}

public sealed class LegacyLockLease : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _ownerPath;
    private int _disposed;

    internal LegacyLockLease(FileStream stream, string ownerPath)
    {
        _stream = stream;
        _ownerPath = ownerPath;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            File.Delete(_ownerPath);
        }
        finally
        {
            _stream.Dispose();
        }
    }
}
