using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using HiveMind.AgentWorkspaces;
using HiveMind.Product;

namespace Deskweave;

public partial class App : Application
{
    Mutex? _instance;
    EventWaitHandle? _activate;
    RegisteredWaitHandle? _activationWait;
    System.Windows.Forms.NotifyIcon? _tray;
    bool _ownsInstance;
    bool _quitting;
    bool _hiddenNotice;

    protected override void OnStartup(StartupEventArgs e)
    {
        // This is the composition root. Set identity before any copied engine observes paths.
        ProductContext.Configure("Deskweave");
        base.OnStartup(e);
        try
        {
            string user = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                Environment.UserDomainName + "\\" + Environment.UserName)))[..20];
            _instance = new Mutex(false, "Local\\Deskweave.App." + user);
            try { _ownsInstance = _instance.WaitOne(0); }
            catch (AbandonedMutexException) { _ownsInstance = true; }
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Deskweave.Show." + user);
            if (!_ownsInstance)
            {
                _activate.Set();
                Shutdown();
                return;
            }

            DispatcherUnhandledException += (_, failure) =>
            {
                failure.Handled = true;
                LogFailure(failure.Exception);
                MessageBox.Show("Deskweave hit an unexpected error and will close to release its workspaces. " +
                    "Your saved workspace files are retained.\n\n" + failure.Exception.Message,
                    "Deskweave", MessageBoxButton.OK, MessageBoxImage.Error);
                _quitting = true;
                Shutdown(1);
            };

            SessionEnding += (_, _) => _quitting = true;
            ModuleEntry.Initialize();
            var window = new MainWindow();
            MainWindow = window;
            window.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/Deskweave.ico"));
            window.Closing += WindowClosing;
            CreateTray();
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activate,
                (_, _) => Dispatcher.BeginInvoke(ShowWorkspace), null, Timeout.Infinite, false);
            StartWithWindows.Sync(AppSettingsStore.Current);
            AppSettingsStore.Changed += StartWithWindows.Sync;
            // Started by Windows at sign-in: stay in the tray; the corner window still comes and goes.
            if (!e.Args.Contains(StartWithWindows.Background)) window.Show();
        }
        catch (Exception failure)
        {
            LogFailure(failure);
            MessageBox.Show("Deskweave couldn't open.\n\n" + failure.Message,
                "Deskweave", MessageBoxButton.OK, MessageBoxImage.Error);
            _quitting = true;
            Shutdown(1);
        }
    }

    void CreateTray()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Deskweave", null, (_, _) => Dispatcher.BeginInvoke(ShowWorkspace));
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit Deskweave", null, (_, _) => Dispatcher.BeginInvoke(RequestQuit));
        using var source = GetResourceStream(new Uri("pack://application:,,,/Assets/Deskweave.ico")).Stream;
        using var icon = new System.Drawing.Icon(source);
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "Deskweave · local agent workspaces",
            Icon = (System.Drawing.Icon)icon.Clone(),
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowWorkspace);
    }

    void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        MainWindow.Hide();
        if (_hiddenNotice || _tray is null) return;
        _hiddenNotice = true;
        _tray.ShowBalloonTip(3500, "Deskweave is still here",
            "Your workspaces can keep working. Open or quit Deskweave from this icon.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    void ShowWorkspace()
    {
        if (!_quitting && MainWindow is MainWindow window) window.RestoreWorkspaceWindow();
    }

    public void RequestQuit()
    {
        if (WorkspaceRuntime.AnyRunning && MessageBox.Show(
            "Quit Deskweave and stop its running desktops?\n\nSaved workspace files and conversations are retained. Active work stops. " +
            "To keep it running, cancel and close the window to the notification area.",
            "Quit Deskweave", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _quitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _quitting = true;
        _activationWait?.Unregister(null);
        _activate?.Dispose();
        if (_ownsInstance)
        {
            try { (MainWindow as IDisposable)?.Dispose(); }
            catch (Exception failure) { LogFailure(failure); }
            try { ModuleEntry.Shutdown(); }
            catch (Exception failure) { LogFailure(failure); }
            finally { _instance?.ReleaseMutex(); }
        }
        _instance?.Dispose();
        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.ContextMenuStrip?.Dispose();
            _tray.Icon?.Dispose();
            _tray.Dispose();
        }
        base.OnExit(e);
    }

    static void LogFailure(Exception failure)
    {
        try
        {
            string root = ProductContext.Local("logs");
            Directory.CreateDirectory(root);
            // One retained failure, bounded storage; never include provider config or credentials.
            string detail = DateTimeOffset.Now + "\n" + failure;
            File.WriteAllText(Path.Combine(root, "last-error.txt"), detail[..Math.Min(detail.Length, 131072)]);
        }
        catch (Exception loggingFailure) { Debug.WriteLine(loggingFailure.Message); }
    }
}
