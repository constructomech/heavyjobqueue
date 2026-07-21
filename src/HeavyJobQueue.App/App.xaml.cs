using System.Drawing;
using System.Threading;
using HeavyJobQueue.Core;
using Forms = System.Windows.Forms;

namespace HeavyJobQueue.App;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\GitHubCopilot.HeavyJobQueue.Broker.v1";
    private Mutex? _instanceMutex;
    private QueueBroker? _broker;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;

    protected override void OnStartup(System.Windows.StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);
        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

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

        var coordinator = new QueueCoordinator();
        _broker = new QueueBroker(coordinator, new LegacyLock());
        _broker.Start();

        _window = new MainWindow(coordinator);
        _window.Show();

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open queue", null, (_, _) => ShowQueue());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());

        _trayIcon = new Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Application,
            Text = "Heavy Job Queue",
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => ShowQueue();
    }

    protected override void OnExit(System.Windows.ExitEventArgs eventArgs)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

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

    private void ExitApplication()
    {
        if (_window is not null)
        {
            _window.IsExiting = true;
            _window.Close();
        }

        Shutdown();
    }
}
