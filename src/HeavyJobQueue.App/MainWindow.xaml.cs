using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using HeavyJobQueue.Core;

namespace HeavyJobQueue.App;

public partial class MainWindow : Window
{
    private readonly QueueCoordinator _coordinator;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<ActiveRow> _activeRows = [];
    private readonly ObservableCollection<WaitingRow> _waitingRows = [];
    private readonly SystemPerformanceSampler _performanceSampler = new();
    private bool _performanceSamplingFailed;

    public MainWindow(QueueCoordinator coordinator)
    {
        _coordinator = coordinator;
        InitializeComponent();
        Icon = TrayIconFactory.CreateImageSource();
        SourceInitialized += (_, _) => ThemeManager.ApplyWindowChrome(this);
        ActiveGrid.ItemsSource = _activeRows;
        WaitingGrid.ItemsSource = _waitingRows;

        _coordinator.Changed += QueueChanged;
        _timer = new DispatcherTimer(
            TimeSpan.FromSeconds(1),
            DispatcherPriority.Background,
            (_, _) => TimerTick(),
            Dispatcher);
        Closed += (_, _) =>
        {
            _timer.Stop();
            _coordinator.Changed -= QueueChanged;
        };

        RefreshQueueState();
        SamplePerformance();
    }

    public bool IsExiting { get; set; }

    public void RefreshTheme()
    {
        ThemeManager.ApplyWindowChrome(this);
        PerformanceHistory.InvalidateVisual();
    }

    public void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    protected override void OnClosing(CancelEventArgs eventArgs)
    {
        if (!IsExiting)
        {
            eventArgs.Cancel = true;
            Hide();
        }

        base.OnClosing(eventArgs);
    }

    private void MoveUp_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (WaitingGrid.SelectedItem is WaitingRow row)
        {
            _coordinator.MoveUp(row.RequestId);
            Select(row.RequestId);
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (WaitingGrid.SelectedItem is WaitingRow row)
        {
            _coordinator.MoveDown(row.RequestId);
            Select(row.RequestId);
        }
    }

    private void RunNow_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (WaitingGrid.SelectedItem is not WaitingRow row)
        {
            return;
        }

        _coordinator.RunNow(row.RequestId);
    }

    private void PauseResume_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (WaitingGrid.SelectedItem is not WaitingRow row)
        {
            return;
        }

        if (row.IsPaused)
        {
            _coordinator.Resume(row.RequestId);
        }
        else
        {
            _coordinator.Pause(row.RequestId);
        }

        Select(row.RequestId);
    }

    private void PauseAll_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_coordinator.IsQueuePaused)
        {
            _coordinator.ResumeAll();
        }
        else
        {
            _coordinator.PauseAll();
        }
    }

    private void WaitingGrid_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs eventArgs) =>
        KillButton.IsEnabled = WaitingGrid.SelectedItem is WaitingRow { IsPaused: true };

    private void Kill_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (WaitingGrid.SelectedItem is not WaitingRow { IsPaused: true } row)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Kill the paused job \"{row.Label}\"?\n\n" +
            "Only this job will be removed. It cannot be resumed later.",
            "Kill paused job",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (!_coordinator.RemoveWaiting(row.RequestId))
        {
            MessageBox.Show(
                this,
                "The job is no longer waiting and was not killed.",
                "Job not killed",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    private void QueueChanged(object? sender, EventArgs eventArgs) =>
        Dispatcher.BeginInvoke(RefreshQueueState);

    private void RefreshQueueState()
    {
        var state = _coordinator.Snapshot();
        var isQueuePaused = _coordinator.IsQueuePaused;
        var now = DateTimeOffset.UtcNow;
        var selectedId = (WaitingGrid.SelectedItem as WaitingRow)?.RequestId;

        QueuePausedBanner.Visibility = isQueuePaused
            ? Visibility.Visible
            : Visibility.Collapsed;
        PauseAllButton.Content = isQueuePaused ? "Resume all" : "Pause all";

        _activeRows.Clear();
        foreach (var job in state.ActiveJobs)
        {
            _activeRows.Add(new ActiveRow(
                job.IsManualOverride ? "Override" : "Automatic",
                job.Label,
                job.CallerPid,
                job.Cwd,
                job.ActivatedAt!.Value,
                job.Command ?? "Command was not provided by this client."));
        }

        _waitingRows.Clear();
        for (var index = 0; index < state.Waiting.Count; index++)
        {
            var job = state.Waiting[index];
            _waitingRows.Add(new WaitingRow(
                job.RequestId,
                index + 1,
                DescribeStatus(job),
                job.Label,
                job.CallerPid,
                job.Cwd,
                job.EnqueuedAt,
                job.Status == JobStatus.Paused,
                job.Command ?? "Command was not provided by this client."));
        }

        UpdateElapsed(now);

        if (selectedId is not null)
        {
            Select(selectedId.Value);
        }
    }

    private static string DescribeStatus(JobSnapshot job) => job.Status switch
    {
        JobStatus.Paused when job.IsPausedByQueue => "Queue paused",
        JobStatus.Paused => "Paused",
        _ => "Waiting"
    };

    private void TimerTick()
    {
        UpdateElapsed(DateTimeOffset.UtcNow);
        SamplePerformance();
    }

    private void UpdateElapsed(DateTimeOffset now)
    {
        foreach (var row in _activeRows)
        {
            row.UpdateElapsed(now);
        }

        foreach (var row in _waitingRows)
        {
            row.UpdateElapsed(now);
        }
    }

    private void SamplePerformance()
    {
        if (_performanceSamplingFailed)
        {
            return;
        }

        try
        {
            PerformanceHistory.AddSample(_performanceSampler.Sample());
        }
        catch (Win32Exception exception)
        {
            _performanceSamplingFailed = true;
            PerformanceHistory.ShowError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            _performanceSamplingFailed = true;
            PerformanceHistory.ShowError(exception.Message);
        }
    }

    private void Select(Guid requestId)
    {
        WaitingGrid.SelectedItem = WaitingGrid.Items
            .OfType<WaitingRow>()
            .FirstOrDefault(item => item.RequestId == requestId);
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private abstract class TimedRow : INotifyPropertyChanged
    {
        private string _elapsed = string.Empty;

        protected TimedRow(DateTimeOffset startedAt)
        {
            StartedAt = startedAt;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Elapsed
        {
            get => _elapsed;
            private set
            {
                if (_elapsed == value)
                {
                    return;
                }

                _elapsed = value;
                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(Elapsed)));
            }
        }

        private DateTimeOffset StartedAt { get; }

        public void UpdateElapsed(DateTimeOffset now) =>
            Elapsed = FormatElapsed(now - StartedAt);
    }

    private sealed class WaitingRow : TimedRow
    {
        public WaitingRow(
            Guid requestId,
            int position,
            string status,
            string label,
            int pid,
            string cwd,
            DateTimeOffset startedAt,
            bool isPaused,
            string command)
            : base(startedAt)
        {
            RequestId = requestId;
            Position = position;
            Status = status;
            Label = label;
            Pid = pid;
            Cwd = cwd;
            IsPaused = isPaused;
            Command = command;
        }

        public Guid RequestId { get; }
        public int Position { get; }
        public string Status { get; }
        public string Label { get; }
        public int Pid { get; }
        public string Cwd { get; }
        public bool IsPaused { get; }
        public string Command { get; }
    }

    private sealed class ActiveRow : TimedRow
    {
        public ActiveRow(
            string mode,
            string label,
            int pid,
            string cwd,
            DateTimeOffset startedAt,
            string command)
            : base(startedAt)
        {
            Mode = mode;
            Label = label;
            Pid = pid;
            Cwd = cwd;
            Command = command;
        }

        public string Mode { get; }
        public string Label { get; }
        public int Pid { get; }
        public string Cwd { get; }
        public string Command { get; }
    }
}
