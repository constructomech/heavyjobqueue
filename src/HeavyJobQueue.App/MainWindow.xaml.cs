using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using HeavyJobQueue.Core;

namespace HeavyJobQueue.App;

public partial class MainWindow : Window
{
    private readonly QueueCoordinator _coordinator;
    private readonly DispatcherTimer _timer;

    public MainWindow(QueueCoordinator coordinator)
    {
        _coordinator = coordinator;
        InitializeComponent();

        _coordinator.Changed += QueueChanged;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Refresh(), Dispatcher);
        Closed += (_, _) =>
        {
            _timer.Stop();
            _coordinator.Changed -= QueueChanged;
        };

        Refresh();
    }

    public bool IsExiting { get; set; }

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

    private void QueueChanged(object? sender, EventArgs eventArgs) =>
        Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        var state = _coordinator.Snapshot();
        var now = DateTimeOffset.UtcNow;

        if (state.Active is null)
        {
            ActiveLabel.Text = "No active job";
            ActivePid.Text = string.Empty;
            ActiveCwd.Text = string.Empty;
            ActiveElapsed.Text = string.Empty;
        }
        else
        {
            ActiveLabel.Text = state.Active.Label;
            ActivePid.Text = $"PID {state.Active.CallerPid}";
            ActiveCwd.Text = state.Active.Cwd;
            ActiveElapsed.Text = FormatElapsed(now - state.Active.ActivatedAt!.Value);
        }

        var selectedId = (WaitingGrid.SelectedItem as WaitingRow)?.RequestId;
        WaitingGrid.ItemsSource = state.Waiting
            .Select((job, index) => new WaitingRow(
                job.RequestId,
                index + 1,
                job.Label,
                job.CallerPid,
                job.Cwd,
                FormatElapsed(now - job.EnqueuedAt)))
            .ToArray();

        if (selectedId is not null)
        {
            Select(selectedId.Value);
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

    private sealed record WaitingRow(
        Guid RequestId,
        int Position,
        string Label,
        int Pid,
        string Cwd,
        string Elapsed);
}
