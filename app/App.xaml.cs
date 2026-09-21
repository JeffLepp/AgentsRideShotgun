using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using HiveMind.AgentWorkspaces;
using HiveMind.Product;

namespace Deskweave;

public partial class App : Application
{
    Mutex? _instance;
    EventWaitHandle? _activate;
    RegisteredWaitHandle? _activationWait;
    TrayIcon? _tray;
    bool _ownsInstance;
    bool _quitting;
    bool _infoShowing;
    Action? _refreshTrayPause;
    Action<string, string, string, string>? _attention;
    (string Workspace, string Request)? _attentionFor;

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
                // A background start (Windows at sign-in, or an agent's bridge) found Deskweave
                // already running: that is all it wanted, so nothing opens on the owner's screen.
                if (!e.Args.Contains(StartWithWindows.Background)) _activate.Set();
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
            AppSettingsStore.Changed += settings => StartWithWindows.Sync(settings);
            // Started by Windows at sign-in: stay in the tray; the corner window still comes and goes.
            if (e.Args.Contains(StartWithWindows.Background)) return;
            // First launch comes before the hub and instead of it. It is shown once, whichever way
            // it is answered, so an unanswered one is the only reason to hold the hub back.
            if (FirstRunWindow.Needed) ShowFirstRun(window); else window.Show();
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

    /// <summary>
    /// Setup connects agents, then leaves Deskweave in the tray. Dismissing setup without
    /// connecting opens the hub so Settings remains discoverable.
    /// </summary>
    static void ShowFirstRun(MainWindow hub)
    {
        var first = new FirstRunWindow();
        first.Closed += (_, _) =>
        {
            if (AppSettingsStore.Current.ConnectAgents) return;
            if (!hub.IsVisible) hub.Show();
        };
        first.Show();
    }

    void CreateTray()
    {
        if (_tray is not null || !_ownsInstance || _quitting) return;
        var menu = TrayMenu.Build(
            () => Dispatcher.BeginInvoke(ShowWorkspace),
            () => Dispatcher.BeginInvoke(ModuleEntry.RequestShowCorner),
            () => Dispatcher.BeginInvoke(ModuleEntry.RequestPauseAll),
            () => Dispatcher.BeginInvoke(ShowSettings),
            () => Dispatcher.BeginInvoke(RequestQuit));
        _refreshTrayPause = () => Dispatcher.BeginInvoke(() => TrayMenu.RefreshPause(menu));
        ModuleEntry.AllPausedChanged += _refreshTrayPause;
        using var source = GetResourceStream(new Uri("pack://application:,,,/Assets/Deskweave.ico")).Stream;
        using var icon = new System.Drawing.Icon(source);
        _tray = new TrayIcon(icon, menu, TrayIcon.IdentityFor(Environment.ProcessPath!));
        _tray.OpenRequested += () => Dispatcher.BeginInvoke(ShowWorkspace);
        // An agent needs the owner and the corner can't show it. Clicking brings the corner up
        // with the question on it, even when Settings has the corner off, as the tray's own item does.
        _attention = (title, text, workspace, request) => Dispatcher.BeginInvoke(() =>
        {
            _infoShowing = false;
            _attentionFor = (workspace, request);
            if (!_quitting && !WorkspacePresentation.Suppressed) _tray?.ShowBalloonTip(title, text);
        });
        ModuleEntry.AttentionNeeded += _attention;
        _tray.BalloonClicked += () =>
        {
            if (_infoShowing) { _infoShowing = false; return; }
            var clicked = _attentionFor;
            Dispatcher.BeginInvoke(() =>
            {
                if (clicked is { } one) WorkspacePeekHost.ShowFor(one.Workspace, one.Request);
                else ModuleEntry.RequestShowCorner();
            });
        };
    }

    /// <summary>One line from the tray, for a state the owner should hear once and nothing shows.</summary>
    internal void Tell(string title, string text)
    {
        _infoShowing = true;   // clicking it opens nothing
        if (!_quitting && !WorkspacePresentation.Suppressed) _tray?.ShowBalloonTip(title, text);
    }

    void WindowClosing(object? sender, CancelEventArgs e)
    {
        // Closing the window hides it and says nothing. The tray icon is the answer to "where did
        // it go", and a notification for something the owner did on purpose is one more thing to
        // dismiss every time.
        if (_quitting) return;
        HideOnClose(MainWindow, e);
    }

    void ShowWorkspace()
    {
        if (!_quitting && MainWindow is MainWindow window) window.RestoreWorkspaceWindow();
    }

    internal static void HideOnClose(Window window, CancelEventArgs e)
    {
        e.Cancel = true;
        window.Hide();
    }

    void ShowSettings()
    {
        ShowWorkspace();
        if (!_quitting && MainWindow is MainWindow window) window.ShowSettings();
    }

    public void RequestQuit()
    {
        if (!QuitQuestion.Ask(MainWindow)) return;
        _quitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _quitting = true;
        _activationWait?.Unregister(null);
        _activate?.Dispose();
        if (_refreshTrayPause is not null) ModuleEntry.AllPausedChanged -= _refreshTrayPause;
        if (_attention is not null) ModuleEntry.AttentionNeeded -= _attention;
        // Remove the shell entry while this instance still owns the lock, even if engine cleanup
        // is slow or fails. A new process must never inherit an icon the old process can delete.
        try { _tray?.Dispose(); }
        catch (Exception failure) { LogFailure(failure); }
        _tray = null;
        if (_ownsInstance)
        {
            try { (MainWindow as IDisposable)?.Dispose(); }
            catch (Exception failure) { LogFailure(failure); }
            try { ModuleEntry.Shutdown(); }
            catch (Exception failure) { LogFailure(failure); }
            finally { _instance?.ReleaseMutex(); }
        }
        _instance?.Dispose();
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

internal static class TrayMenu
{
    internal static System.Windows.Forms.ContextMenuStrip Build(
        Action open, Action showCorner, Action pauseAll, Action settings, Action quit)
    {
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open Deskweave", null, (_, _) => open());
        menu.Items.Add("Show the corner window", null, (_, _) => showCorner());
        menu.Items.Add(ModuleEntry.AllPaused ? "Resume every agent" : "Pause every agent", null, (_, _) => pauseAll());
        menu.Items.Add("Settings", null, (_, _) => settings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Quit Deskweave", null, (_, _) => quit());
        return menu;
    }

    internal static void RefreshPause(System.Windows.Forms.ContextMenuStrip menu) =>
        menu.Items[2].Text = ModuleEntry.AllPaused ? "Resume every agent" : "Pause every agent";
}

internal static class QuitQuestion
{
    internal const string Title = "Quit Deskweave?";
    internal const string Body = "Agents working now will stop. Your files stay. Deskweave starts again in the background when an agent needs it.";
    internal const string QuitLabel = "Quit";
    internal const string CancelLabel = "Cancel";
    internal static Func<bool>? ConfirmForTests;

    internal static bool Ask(Window? owner)
    {
        if (ConfirmForTests is { } confirm) return confirm();
        var dialog = new Window
        {
            Title = Title,
            Width = 380,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            WindowStartupLocation = owner?.IsVisible == true ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.FindResource("SkinFontFamily"),
            FontSize = 13,
            Background = (System.Windows.Media.Brush)Application.Current.FindResource("WindowBrush"),
            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("InkBrush"),
        };
        if (owner?.IsVisible == true) dialog.Owner = owner;
        var content = new Grid { Margin = new Thickness(24) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var body = new TextBlock { Text = Body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
        content.Children.Add(body);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = CancelLabel, MinWidth = 76, Height = 30, IsCancel = true,
            Style = (Style)Application.Current.FindResource("QuietButton"), Margin = new Thickness(0, 0, 8, 0) };
        var quit = new Button { Content = QuitLabel, MinWidth = 76, Height = 30,
            Style = (Style)Application.Current.FindResource("PrimaryButton") };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        quit.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(quit);
        Grid.SetRow(actions, 1);
        content.Children.Add(actions);
        dialog.Content = content;
        return dialog.ShowDialog() == true;
    }
}
