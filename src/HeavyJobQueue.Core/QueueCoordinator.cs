namespace HeavyJobQueue.Core;

using System.Threading.Channels;

public sealed class QueueCoordinator
{
    private readonly object _gate = new();
    private readonly List<QueueRegistration> _waiting = [];
    private readonly List<QueueRegistration> _paused = [];
    private readonly List<QueueRegistration> _active = [];
    private CancellationTokenSource _activePeriodEnded = CreateCanceledTokenSource();

    public event EventHandler? Changed;

    public QueueRegistration Enqueue(JobRequest request)
    {
        QueueRegistration registration;
        lock (_gate)
        {
            if (_active.Any(item => item.Request.RequestId == request.RequestId) ||
                _waiting.Any(item => item.Request.RequestId == request.RequestId) ||
                _paused.Any(item => item.Request.RequestId == request.RequestId))
            {
                throw new InvalidOperationException($"Request '{request.RequestId}' is already queued.");
            }

            registration = new QueueRegistration(request);
            _waiting.Add(registration);
        }

        OnChanged();
        return registration;
    }

    public QueueRegistration? PeekNext()
    {
        lock (_gate)
        {
            return _active.Count == 0 && _waiting.Count > 0 ? _waiting[0] : null;
        }
    }

    public bool TryActivateNext(Guid requestId)
    {
        QueueRegistration? registration = null;
        lock (_gate)
        {
            if (_active.Count > 0 ||
                _waiting.Count == 0 ||
                _waiting[0].Request.RequestId != requestId)
            {
                return false;
            }

            registration = _waiting[0];
            _waiting.RemoveAt(0);
            BeginActivePeriod();
            registration.ActivatedAt = DateTimeOffset.UtcNow;
            _active.Add(registration);
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
            var index = _waiting.FindIndex(item => item.Request.RequestId == requestId);
            if (index >= 0)
            {
                registration = _waiting[index];
                _waiting.RemoveAt(index);
            }
            else
            {
                index = _paused.FindIndex(item => item.Request.RequestId == requestId);
                if (index < 0)
                {
                    return false;
                }

                registration = _paused[index];
                _paused.RemoveAt(index);
            }

            BeginActivePeriod();
            registration.ActivatedAt = DateTimeOffset.UtcNow;
            registration.IsPaused = false;
            registration.IsManualOverride = true;
            registration.CancelScheduling();
            _active.Add(registration);
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
            registration.CancelScheduling();
            _paused.Add(registration);
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
            registration.IsPaused = false;
            registration.ResetScheduling();
            _waiting.Add(registration);
        }

        registration.MarkResumed();
        OnChanged();
        return true;
    }

    public bool Complete(Guid requestId)
    {
        QueueRegistration? removed = null;
        lock (_gate)
        {
            var index = _active.FindIndex(item => item.Request.RequestId == requestId);
            if (index < 0)
            {
                return false;
            }

            removed = _active[index];
            _active.RemoveAt(index);
            EndActivePeriodIfEmpty();
        }

        removed.MarkRemoved();
        OnChanged();
        return true;
    }

    public bool Disconnect(Guid requestId)
    {
        QueueRegistration? removed = null;
        lock (_gate)
        {
            var activeIndex = _active.FindIndex(item => item.Request.RequestId == requestId);
            if (activeIndex >= 0)
            {
                removed = _active[activeIndex];
                _active.RemoveAt(activeIndex);
                EndActivePeriodIfEmpty();
            }
            else
            {
                var index = _waiting.FindIndex(item => item.Request.RequestId == requestId);
                if (index >= 0)
                {
                    removed = _waiting[index];
                    _waiting.RemoveAt(index);
                }
                else
                {
                    index = _paused.FindIndex(item => item.Request.RequestId == requestId);
                    if (index >= 0)
                    {
                        removed = _paused[index];
                        _paused.RemoveAt(index);
                    }
                }
            }
        }

        if (removed is null)
        {
            return false;
        }

        removed.MarkRemoved();
        OnChanged();
        return true;
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

    public ActiveBarrier? PeekActiveBarrier()
    {
        lock (_gate)
        {
            return _active.Count == 0
                ? null
                : new ActiveBarrier(_active[0].Request, _activePeriodEnded.Token);
        }
    }

    public void DisconnectAll()
    {
        QueueRegistration[] registrations;
        lock (_gate)
        {
            registrations = _waiting
                .Concat(_paused)
                .Concat(_active)
                .ToArray();
            _waiting.Clear();
            _paused.Clear();
            _active.Clear();
            EndActivePeriodIfEmpty();
        }

        foreach (var registration in registrations)
        {
            registration.MarkRemoved();
        }

        if (registrations.Length > 0)
        {
            OnChanged();
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
        }

        OnChanged();
        return true;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private void BeginActivePeriod()
    {
        if (_active.Count == 0)
        {
            _activePeriodEnded = new CancellationTokenSource();
        }
    }

    private void EndActivePeriodIfEmpty()
    {
        if (_active.Count == 0)
        {
            _activePeriodEnded.Cancel();
        }
    }

    private static CancellationTokenSource CreateCanceledTokenSource()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source;
    }
}

public sealed record JobRequest(
    Guid RequestId,
    string Label,
    int CallerPid,
    string Cwd,
    DateTimeOffset EnqueuedAt,
    TimeSpan WaitTimeout,
    string? Command);

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

public sealed record ActiveBarrier(JobRequest Request, CancellationToken ActivePeriodEnded);

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

    internal QueueRegistration(JobRequest request)
    {
        Request = request;
    }

    public JobRequest Request { get; }

    public DateTimeOffset? ActivatedAt { get; internal set; }

    public bool IsManualOverride { get; internal set; }

    public bool IsPaused { get; internal set; }

    public Task Granted => _granted.Task;

    public CancellationToken Removed => _removed.Token;

    public CancellationToken SchedulingCanceled => _schedulingCanceled.Token;

    public ValueTask<QueueRegistrationState> ReadStateChangeAsync(
        CancellationToken cancellationToken) =>
        _stateChanges.Reader.ReadAsync(cancellationToken);

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
