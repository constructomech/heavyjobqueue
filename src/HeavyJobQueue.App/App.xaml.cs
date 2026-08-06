using System.Drawing;
using System.Threading;
using HeavyJobQueue.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace HeavyJobQueue.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\GitHubCopilot.HeavyJobQueue.Broker.v1";
    private Mutex? _instanceMutex;
    private QueueCoordinator? _coordinator;
    private QueueBroker? _broker;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private Icon? _trayImage;

    protected override void OnStartup(System.Windows.StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        ThemeManager.Apply(this);

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "Heavy Job Queue is already running.",
                "Heavy Job Queue",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        SystemEvents.UserPreferenceChanged += SystemThemeChanged;

        var coordinator = new QueueCoordinator(new QueueStateStore());
        _coordinator = coordinator;
        _broker = new QueueBroker(coordinator);
        _broker.Start();

        _window = new MainWindow(coordinator);
        _window.Show();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open queue", null, (_, _) => ShowQueue());
        var pauseAllItem = menu.Items.Add("Pause all", null, (_, _) => TogglePauseAll());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        menu.Opening += (_, _) =>
            pauseAllItem.Text = coordinator.IsQueuePaused ? "Resume all" : "Pause all";

        _trayImage = TrayIconFactory.Create();
        _trayIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = _trayImage,
            Text = "Heavy Job Queue",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowQueue();
    }

    protected override void OnExit(System.Windows.ExitEventArgs eventArgs)
    {
        SystemEvents.UserPreferenceChanged -= SystemThemeChanged;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        _trayImage?.Dispose();

        if (_broker is not null)
        {
            _broker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        base.OnExit(eventArgs);
    }

    private void ShowQueue()
    {
        if (_window is null)
        {
            return;
        }

        _window.ShowAndActivate();
    }

    private void TogglePauseAll()
    {
        if (_coordinator is null)
        {
            return;
        }

        if (_coordinator.IsQueuePaused)
        {
            _coordinator.ResumeAll();
        }
        else
        {
            _coordinator.PauseAll();
        }
    }

    private void ExitApplication()
    {
        if (_window is not null)
        {
            _window.IsExiting = true;
            _window.Close();
        }

        Shutdown();
    }

    private void SystemThemeChanged(
        object sender,
        UserPreferenceChangedEventArgs eventArgs)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            ThemeManager.Apply(this);
            _window?.RefreshTheme();
        });
    }
}
