namespace HeavyJobQueue.Core;

using System.Threading.Channels;

public sealed class QueueCoordinator
{
    private const int MaximumCompletionHistory = 256;

    private readonly object _gate = new();
    private readonly List<QueueRegistration> _waiting = [];
    private readonly List<QueueRegistration> _paused = [];
    private readonly List<QueueRegistration> _active = [];
    private readonly List<DurableCompletion> _completions = [];
    private readonly QueueStateStore? _stateStore;

    public QueueCoordinator(QueueStateStore? stateStore = null)
    {
        _stateStore = stateStore;
        if (stateStore is not null)
        {
            Restore(stateStore.Load());
        }
    }

    public event EventHandler? Changed;

    public QueueAttachment AttachOrEnqueue(JobRequest request)
    {
        QueueRegistration registration;
        var isNew = false;
        lock (_gate)
        {
            if (_completions.Any(item => item.RequestId == request.RequestId))
            {
                throw new RequestCompletedException(request.RequestId);
            }

            registration = FindRegistration(request.RequestId)!;
            if (registration is null)
            {
                registration = new QueueRegistration(request, isConnected: true);
                _waiting.Add(registration);
                PersistOrRollbackLocked(() => _waiting.Remove(registration));
                isNew = true;
            }
            else
            {
                registration.Attach(request);
            }
        }

        OnChanged();
        return new QueueAttachment(registration, isNew);
    }

    public QueueRegistration Enqueue(JobRequest request) =>
        AttachOrEnqueue(request).Registration;

    public QueueRegistration? PeekNext()
    {
        lock (_gate)
        {
            return _active.Count == 0 &&
                _waiting.Count > 0 &&
                _waiting[0].IsConnected
                    ? _waiting[0]
                    : null;
        }
    }

    public bool TryActivateNext(Guid requestId)
    {
        QueueRegistration? registration = null;
        lock (_gate)
        {
            if (_active.Count > 0 ||
                _waiting.Count == 0 ||
                _waiting[0].Request.RequestId != requestId ||
                !_waiting[0].IsConnected)
            {
                return false;
            }

            registration = _waiting[0];
            _waiting.RemoveAt(0);
            var previousActivatedAt = registration.ActivatedAt;
            registration.ActivatedAt = DateTimeOffset.UtcNow;
            _active.Add(registration);
            PersistOrRollbackLocked(() =>
            {
                _active.Remove(registration);
                registration.ActivatedAt = previousActivatedAt;
                _waiting.Insert(0, registration);
            });
        }

        registration.MarkGranted();
        OnChanged();
        return true;
    }

    public bool RunNow(Guid requestId)
    {
        QueueRegistration? registration = null;
        lock (_gate)
        {
            List<QueueRegistration> source;
            var index = _waiting.FindIndex(item => item.Request.RequestId == requestId);
            if (index >= 0)
            {
                source = _waiting;
                registration = _waiting[index];
                if (!registration.IsConnected)
                {
                    return false;
                }

                _waiting.RemoveAt(index);
            }
            else
            {
                index = _paused.FindIndex(item => item.Request.RequestId == requestId);
                if (index < 0 || !_paused[index].IsConnected)
                {
                    return false;
                }

                source = _paused;
                registration = _paused[index];
                _paused.RemoveAt(index);
            }

            var previousActivatedAt = registration.ActivatedAt;
            var wasPaused = registration.IsPaused;
            var previousPausedAt = registration.PausedAt;
            var wasManualOverride = registration.IsManualOverride;
            registration.ActivatedAt = DateTimeOffset.UtcNow;
            registration.IsPaused = false;
            registration.PausedAt = null;
            registration.IsManualOverride = true;
            registration.CancelScheduling();
            _active.Add(registration);
            PersistOrRollbackLocked(() =>
            {
                _active.Remove(registration);
                registration.ActivatedAt = previousActivatedAt;
                registration.IsPaused = wasPaused;
                registration.PausedAt = previousPausedAt;
                registration.IsManualOverride = wasManualOverride;
                registration.ResetScheduling();
                if (wasPaused)
                {
                    registration.CancelScheduling();
                }
                source.Insert(index, registration);
            });
        }

        registration.MarkGranted();
        OnChanged();
        return true;
    }

    public bool Pause(Guid requestId)
    {
        QueueRegistration registration;
        lock (_gate)
        {
            var index = _waiting.FindIndex(item => item.Request.RequestId == requestId);
            if (index < 0)
            {
                return false;
            }

            registration = _waiting[index];
            _waiting.RemoveAt(index);
            registration.IsPaused = true;
            registration.PausedAt = DateTimeOffset.UtcNow;
            registration.CancelScheduling();
            _paused.Add(registration);
            PersistOrRollbackLocked(() =>
            {
                _paused.Remove(registration);
                registration.IsPaused = false;
                registration.PausedAt = null;
                registration.ResetScheduling();
                _waiting.Insert(index, registration);
            });
        }

        registration.MarkPaused();
        OnChanged();
        return true;
    }

    public bool Resume(Guid requestId)
    {
        QueueRegistration registration;
        lock (_gate)
        {
            var index = _paused.FindIndex(item => item.Request.RequestId == requestId);
            if (index < 0)
            {
                return false;
            }

            registration = _paused[index];
            _paused.RemoveAt(index);
            var previousPausedAt = registration.PausedAt;
            var previousPausedDuration = registration.TotalPausedDuration;
            registration.AddPausedDuration(DateTimeOffset.UtcNow);
            registration.IsPaused = false;
            registration.PausedAt = null;
            registration.ResetScheduling();
            _waiting.Add(registration);
            PersistOrRollbackLocked(() =>
            {
                _waiting.Remove(registration);
                registration.IsPaused = true;
                registration.PausedAt = previousPausedAt;
                registration.TotalPausedDuration = previousPausedDuration;
                registration.CancelScheduling();
                _paused.Insert(index, registration);
            });
        }

        registration.MarkResumed();
        OnChanged();
        return true;
    }

    public bool Complete(Guid requestId, string leaseName)
    {
        QueueRegistration? removed;
        lock (_gate)
        {
            if (_completions.Any(item =>
                item.RequestId == requestId &&
                string.Equals(item.LeaseName, leaseName, StringComparison.Ordinal)))
            {
                return true;
            }

            var source = _active;
            var index = source.FindIndex(item =>
                item.Request.RequestId == requestId &&
                string.Equals(item.Request.LeaseName, leaseName, StringComparison.Ordinal));
            if (index < 0)
            {
                source = _waiting;
                index = source.FindIndex(item =>
                    !item.IsConnected &&
                    item.Request.RequestId == requestId &&
                    string.Equals(
                        item.Request.LeaseName,
                        leaseName,
                        StringComparison.Ordinal));
            }
            if (index < 0)
            {
                source = _paused;
                index = source.FindIndex(item =>
                    !item.IsConnected &&
                    item.Request.RequestId == requestId &&
                    string.Equals(
                        item.Request.LeaseName,
                        leaseName,
                        StringComparison.Ordinal));
            }
            if (index < 0)
            {
                return false;
            }

            removed = source[index];
            source.RemoveAt(index);
            var completion = new DurableCompletion(
                requestId,
                leaseName,
                DateTimeOffset.UtcNow);
            _completions.Add(completion);
            DurableCompletion[]? prunedCompletions = null;
            if (_completions.Count > MaximumCompletionHistory)
            {
                var pruneCount = _completions.Count - MaximumCompletionHistory;
                prunedCompletions = _completions.Take(pruneCount).ToArray();
                _completions.RemoveRange(0, pruneCount);
            }

            PersistOrRollbackLocked(() =>
            {
                _completions.Remove(completion);
                if (prunedCompletions is not null)
                {
                    _completions.InsertRange(0, prunedCompletions);
                }
                source.Insert(index, removed);
            });
        }

        removed.MarkRemoved();
        OnChanged();
        return true;
    }

    public bool Remove(Guid requestId, string? leaseName = null)
    {
        QueueRegistration? removed;
        lock (_gate)
        {
            removed = FindRegistration(requestId);
            if (removed is null ||
                leaseName is not null &&
                !string.Equals(removed.Request.LeaseName, leaseName, StringComparison.Ordinal))
            {
                return false;
            }

            var source = _active.Contains(removed)
                ? _active
                : _waiting.Contains(removed)
                    ? _waiting
                    : _paused;
            var index = source.IndexOf(removed);
            source.RemoveAt(index);
            PersistOrRollbackLocked(() => source.Insert(index, removed));
        }

        removed.MarkRemoved();
        OnChanged();
        return true;
    }

    public bool MarkDisconnected(Guid requestId)
    {
        lock (_gate)
        {
            var registration = FindRegistration(requestId);
            if (registration is null || !registration.IsConnected)
            {
                return false;
            }

            registration.MarkDisconnected();
        }

        OnChanged();
        return true;
    }

    public int RemoveExpiredDisconnectedLeases()
    {
        (QueueRegistration Registration, List<QueueRegistration> Source, int Index)[] removed;
        lock (_gate)
        {
            removed = _active
                .Concat(_waiting)
                .Concat(_paused)
                .Where(item =>
                    !item.IsConnected &&
                    !RequestLease.IsHeld(item.Request.LeaseName))
                .Select(item =>
                {
                    var source = _active.Contains(item)
                        ? _active
                        : _waiting.Contains(item)
                            ? _waiting
                            : _paused;
                    return (item, source, source.IndexOf(item));
                })
                .ToArray();
            if (removed.Length == 0)
            {
                return 0;
            }

            foreach (var (registration, source, _) in removed)
            {
                source.Remove(registration);
            }

            PersistOrRollbackLocked(() =>
            {
                foreach (var group in removed.GroupBy(item => item.Source))
                {
                    foreach (var item in group.OrderBy(item => item.Index))
                    {
                        item.Source.Insert(item.Index, item.Registration);
                    }
                }
            });
        }

        foreach (var (registration, _, _) in removed)
        {
            registration.MarkRemoved();
        }

        OnChanged();
        return removed.Length;
    }

    public bool MoveUp(Guid requestId) => Move(requestId, -1);

    public bool MoveDown(Guid requestId) => Move(requestId, 1);

    public int GetWaitingPosition(Guid requestId)
    {
        lock (_gate)
        {
            var index = _waiting.FindIndex(item => item.Request.RequestId == requestId);
            if (index >= 0)
            {
                return index + 1;
            }

            index = _paused.FindIndex(item => item.Request.RequestId == requestId);
            return index < 0 ? 0 : _waiting.Count + index + 1;
        }
    }

    public QueueState Snapshot()
    {
        lock (_gate)
        {
            return new QueueState(
                _active.Select(item => JobSnapshot.From(item, JobStatus.Active)).ToArray(),
                _waiting.Select(item => JobSnapshot.From(item, JobStatus.Waiting))
                    .Concat(_paused.Select(item => JobSnapshot.From(item, JobStatus.Paused)))
                    .ToArray());
        }
    }

    private bool Move(Guid requestId, int offset)
    {
        lock (_gate)
        {
            var list = _waiting.Any(item => item.Request.RequestId == requestId)
                ? _waiting
                : _paused;
            var index = list.FindIndex(item => item.Request.RequestId == requestId);
            var target = index + offset;
            if (index < 0 || target < 0 || target >= list.Count)
            {
                return false;
            }

            (list[index], list[target]) = (list[target], list[index]);
            PersistOrRollbackLocked(() =>
                (list[index], list[target]) = (list[target], list[index]));
        }

        OnChanged();
        return true;
    }

    private QueueRegistration? FindRegistration(Guid requestId) =>
        _active
            .Concat(_waiting)
            .Concat(_paused)
            .FirstOrDefault(item => item.Request.RequestId == requestId);

    private void Restore(DurableQueueState state)
    {
        foreach (var job in state.Jobs)
        {
            var request = new JobRequest(
                job.RequestId,
                job.Label,
                job.CallerPid,
                job.Cwd,
                job.EnqueuedAt,
                job.WaitTimeout,
                job.Command,
                job.LeaseName);
            var registration = new QueueRegistration(request, isConnected: false)
            {
                ActivatedAt = job.ActivatedAt,
                IsManualOverride = job.IsManualOverride,
                IsPaused = job.Status == JobStatus.Paused,
                PausedAt = job.PausedAt,
                TotalPausedDuration = job.TotalPausedDuration
            };

            switch (job.Status)
            {
                case JobStatus.Active:
                    registration.MarkGranted();
                    _active.Add(registration);
                    break;
                case JobStatus.Paused:
                    registration.CancelScheduling();
                    _paused.Add(registration);
                    break;
                default:
                    _waiting.Add(registration);
                    break;
            }
        }

        _completions.AddRange(state.Completions.TakeLast(MaximumCompletionHistory));
    }

    private void PersistLocked()
    {
        _stateStore?.Save(new DurableQueueState(
            QueueStateStore.CurrentVersion,
            _active.Select(item => DurableJobFactory.From(item, JobStatus.Active))
                .Concat(_waiting.Select(item => DurableJobFactory.From(item, JobStatus.Waiting)))
                .Concat(_paused.Select(item => DurableJobFactory.From(item, JobStatus.Paused)))
                .ToArray(),
            _completions.ToArray()));
    }

    private void PersistOrRollbackLocked(Action rollback)
    {
        try
        {
            PersistLocked();
        }
        catch
        {
            rollback();
            throw;
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed record JobRequest(
    Guid RequestId,
    string Label,
    int CallerPid,
    string Cwd,
    DateTimeOffset EnqueuedAt,
    TimeSpan WaitTimeout,
    string? Command,
    string LeaseName);

public enum JobStatus
{
    Waiting,
    Paused,
    Active
}

public sealed record JobSnapshot(
    Guid RequestId,
    string Label,
    int CallerPid,
    string Cwd,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? ActivatedAt,
    JobStatus Status,
    bool IsManualOverride,
    string? Command)
{
    internal static JobSnapshot From(QueueRegistration registration, JobStatus status) =>
        new(
            registration.Request.RequestId,
            registration.Request.Label,
            registration.Request.CallerPid,
            registration.Request.Cwd,
            registration.Request.EnqueuedAt,
            registration.ActivatedAt,
            status,
            registration.IsManualOverride,
            registration.Request.Command);
}

public sealed record QueueState(
    IReadOnlyList<JobSnapshot> ActiveJobs,
    IReadOnlyList<JobSnapshot> Waiting);

public sealed record QueueAttachment(QueueRegistration Registration, bool IsNew);

public sealed class RequestCompletedException(Guid requestId)
    : InvalidOperationException($"Request '{requestId}' has already completed.");

public sealed class QueueRegistration
{
    private readonly TaskCompletionSource _granted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _removed = new();
    private readonly Channel<QueueRegistrationState> _stateChanges =
        Channel.CreateUnbounded<QueueRegistrationState>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private CancellationTokenSource _schedulingCanceled = new();

    internal QueueRegistration(JobRequest request, bool isConnected)
    {
        Request = request;
        IsConnected = isConnected;
    }

    public JobRequest Request { get; }

    public DateTimeOffset? ActivatedAt { get; internal set; }

    public bool IsManualOverride { get; internal set; }

    public bool IsPaused { get; internal set; }

    public bool IsConnected { get; private set; }

    public DateTimeOffset? PausedAt { get; internal set; }

    public TimeSpan TotalPausedDuration { get; internal set; }

    public Task Granted => _granted.Task;

    public CancellationToken Removed => _removed.Token;

    public CancellationToken SchedulingCanceled => _schedulingCanceled.Token;

    public TimeSpan GetRemainingWait(DateTimeOffset now)
    {
        var paused = TotalPausedDuration;
        if (IsPaused && PausedAt is not null)
        {
            paused += now - PausedAt.Value;
        }

        return Request.WaitTimeout - (now - Request.EnqueuedAt - paused);
    }

    public ValueTask<QueueRegistrationState> ReadStateChangeAsync(
        CancellationToken cancellationToken) =>
        _stateChanges.Reader.ReadAsync(cancellationToken);

    internal void Attach(JobRequest request)
    {
        if (IsConnected)
        {
            throw new InvalidOperationException(
                $"Request '{request.RequestId}' already has a connected client.");
        }

        if (request.CallerPid != Request.CallerPid ||
            !string.Equals(request.LeaseName, Request.LeaseName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Request '{request.RequestId}' does not match its durable lease.");
        }

        IsConnected = true;
    }

    internal void MarkDisconnected() => IsConnected = false;

    internal void AddPausedDuration(DateTimeOffset resumedAt)
    {
        if (PausedAt is not null)
        {
            TotalPausedDuration += resumedAt - PausedAt.Value;
        }
    }

    internal void MarkGranted() => _granted.TrySetResult();

    internal void MarkPaused() =>
        _stateChanges.Writer.TryWrite(QueueRegistrationState.Paused);

    internal void MarkResumed() =>
        _stateChanges.Writer.TryWrite(QueueRegistrationState.Resumed);

    internal void CancelScheduling() => _schedulingCanceled.Cancel();

    internal void ResetScheduling() => _schedulingCanceled = new CancellationTokenSource();

    internal void MarkRemoved()
    {
        _schedulingCanceled.Cancel();
        _removed.Cancel();
        _stateChanges.Writer.TryComplete();
    }
}

public enum QueueRegistrationState
{
    Paused,
    Resumed
}

internal static class DurableJobFactory
{
    public static DurableJob From(QueueRegistration registration, JobStatus status) =>
        new(
            registration.Request.RequestId,
            registration.Request.Label,
            registration.Request.CallerPid,
            registration.Request.Cwd,
            registration.Request.EnqueuedAt,
            registration.Request.WaitTimeout,
            registration.Request.Command,
            registration.Request.LeaseName,
            registration.ActivatedAt,
            status,
            registration.IsManualOverride,
            registration.PausedAt,
            registration.TotalPausedDuration);
}
