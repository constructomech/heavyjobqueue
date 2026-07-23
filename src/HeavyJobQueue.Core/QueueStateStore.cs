using System.Text.Json;

namespace HeavyJobQueue.Core;

public sealed class QueueStateStore
{
    public const int CurrentVersion = 1;

    private readonly string _statePath;
    private readonly string _backupPath;

    public QueueStateStore(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GitHubCopilot",
            "HeavyJobQueue",
            "queue-state.json");
        _backupPath = $"{_statePath}.backup";
    }

    public DurableQueueState Load()
    {
        if (!File.Exists(_statePath))
        {
            return File.Exists(_backupPath)
                ? Read(_backupPath)
                : DurableQueueState.Empty;
        }

        try
        {
            return Read(_statePath);
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or InvalidDataException)
        {
            if (!File.Exists(_backupPath))
            {
                throw;
            }

            return Read(_backupPath);
        }
    }

    public void Save(DurableQueueState state)
    {
        var directory = Path.GetDirectoryName(_statePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, state, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_statePath))
            {
                File.Replace(temporaryPath, _statePath, _backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _statePath);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static DurableQueueState Read(string path)
    {
        using var stream = File.OpenRead(path);
        var state = JsonSerializer.Deserialize<DurableQueueState>(stream, SerializerOptions) ??
            throw new InvalidDataException("The durable queue state is empty.");
        if (state.Version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"Queue state version {state.Version} is not supported; expected {CurrentVersion}.");
        }

        return state;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
}

public sealed record DurableQueueState(
    int Version,
    IReadOnlyList<DurableJob> Jobs,
    IReadOnlyList<DurableCompletion> Completions)
{
    public static DurableQueueState Empty { get; } =
        new(QueueStateStore.CurrentVersion, [], []);
}

public sealed record DurableJob(
    Guid RequestId,
    string Label,
    int CallerPid,
    string Cwd,
    DateTimeOffset EnqueuedAt,
    TimeSpan WaitTimeout,
    string? Command,
    string LeaseName,
    DateTimeOffset? ActivatedAt,
    JobStatus Status,
    bool IsManualOverride,
    DateTimeOffset? PausedAt,
    TimeSpan TotalPausedDuration);

public sealed record DurableCompletion(
    Guid RequestId,
    string LeaseName,
    DateTimeOffset CompletedAt);
