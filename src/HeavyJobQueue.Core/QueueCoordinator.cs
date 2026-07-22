namespace HeavyJobQueue.Core;

public sealed class QueueCoordinator
{
    private readonly object _gate = new();
    private readonly List<QueueRegistration> _waiting = [];
    private QueueRegistration? _active;

    public event EventHandler? Changed;

    public QueueRegistration Enqueue(JobRequest request)
    {
        QueueRegistration registration;
        lock (_gate)
        {
            if ((_active?.Request.RequestId == request.RequestId) ||
                _waiting.Any(item => item.Request.RequestId == request.RequestId))
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
            return _active is null && _waiting.Count > 0 ? _waiting[0] : null;
        }
    }

    public bool TryActivateNext(Guid requestId)
    {
        QueueRegistration? registration = null;
        lock (_gate)
        {
            if (_active is not null ||
                _waiting.Count == 0 ||
                _waiting[0].Request.RequestId != requestId)
            {
                return false;
            }

            registration = _waiting[0];
            _waiting.RemoveAt(0);
            registration.ActivatedAt = DateTimeOffset.UtcNow;
            _active = registration;
        }

        registration.MarkGranted();
        OnChanged();
        return true;
    }

    public bool Complete(Guid requestId)
    {
        QueueRegistration? removed = null;
        lock (_gate)
        {
            if (_active?.Request.RequestId != requestId)
            {
                return false;
            }

            removed = _active;
            _active = null;
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
            if (_active?.Request.RequestId == requestId)
            {
                removed = _active;
                _active = null;
            }
            else
            {
                var index = _waiting.FindIndex(item => item.Request.RequestId == requestId);
                if (index >= 0)
                {
                    removed = _waiting[index];
                    _waiting.RemoveAt(index);
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
            return index < 0 ? 0 : index + 1;
        }
    }

    public QueueState Snapshot()
    {
        lock (_gate)
        {
            return new QueueState(
                _active is null ? null : JobSnapshot.From(_active, JobStatus.Active),
                _waiting.Select(item => JobSnapshot.From(item, JobStatus.Waiting)).ToArray());
        }
    }

    public void DisconnectAll()
    {
        QueueRegistration[] registrations;
        lock (_gate)
        {
            registrations = _waiting
                .Prepend(_active)
                .Where(item => item is not null)
                .Cast<QueueRegistration>()
                .ToArray();
            _waiting.Clear();
            _active = null;
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
            var index = _waiting.FindIndex(item => item.Request.RequestId == requestId);
            var target = index + offset;
            if (index < 0 || target < 0 || target >= _waiting.Count)
            {
                return false;
            }

            (_waiting[index], _waiting[target]) = (_waiting[target], _waiting[index]);
        }

        OnChanged();
        return true;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

public sealed record JobRequest(
    Guid RequestId,
    string Label,
    int CallerPid,
    string Cwd,
    DateTimeOffset EnqueuedAt,
    TimeSpan WaitTimeout);

public enum JobStatus
{
    Waiting,
    Active
}

public sealed record JobSnapshot(
    Guid RequestId,
    string Label,
    int CallerPid,
    string Cwd,
    DateTimeOffset EnqueuedAt,
    DateTimeOffset? ActivatedAt,
    JobStatus Status)
{
    internal static JobSnapshot From(QueueRegistration registration, JobStatus status) =>
        new(
            registration.Request.RequestId,
            registration.Request.Label,
            registration.Request.CallerPid,
            registration.Request.Cwd,
            registration.Request.EnqueuedAt,
            registration.ActivatedAt,
            status);
}

public sealed record QueueState(JobSnapshot? Active, IReadOnlyList<JobSnapshot> Waiting);

public sealed class QueueRegistration
{
    private readonly TaskCompletionSource _granted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _removed = new();

    internal QueueRegistration(JobRequest request)
    {
        Request = request;
    }

    public JobRequest Request { get; }

    public DateTimeOffset? ActivatedAt { get; internal set; }

    public Task Granted => _granted.Task;

    public CancellationToken Removed => _removed.Token;

    internal void MarkGranted() => _granted.TrySetResult();

    internal void MarkRemoved() => _removed.Cancel();
}
