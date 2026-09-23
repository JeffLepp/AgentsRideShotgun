using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Deskweave.AgentWorkspaces;
using Deskweave.Product;

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
            string user = UserKey();
            _instance = new Mutex(false, InstancePrefix + user);
            _activate = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Deskweave.Show." + user);
            _ownsInstance = ClaimInstance(_instance, _activate, e.Args.Contains(StartWithWindows.Background), TimeSpan.FromSeconds(5));
            if (!_ownsInstance)
            {
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
            Updates.Start((title, text) => Dispatcher.BeginInvoke(() => Tell(title, text)));
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
    /// Setup connects agents, then opens the hub either way, so a fresh install shows where
    /// Deskweave lives instead of vanishing into the tray.
    /// </summary>
    void ShowFirstRun(MainWindow hub)
    {
        var first = new FirstRunWindow();
        first.Closed += (_, _) =>
        {
            ShowHubAfterFirstRun(hub, _quitting);
        };
        first.Show();
    }

    /// <summary>
    /// Quitting, signing out or a failed start closes every window, the hub before setup. Showing
    /// the closed hub again threw, and OnExit's tray, engine and lock cleanup never ran.
    /// </summary>
    internal static void ShowHubAfterFirstRun(Window hub, bool quitting)
    {
        if (!quitting && !hub.IsVisible) hub.Show();
    }

    const string InstancePrefix = "Local\\Deskweave.App.";

    static string UserKey() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Environment.UserDomainName + "\\" + Environment.UserName)))[..20];

    /// <summary>The one-per-account lock a running Deskweave holds.</summary>
    internal static string InstanceName => InstancePrefix + UserKey();

    /// <summary>
    /// Takes the one-per-account lock. A Deskweave already running is shown, unless this is a
    /// background start (Windows at sign-in, or an agent's bridge) that only wanted it running, and
    /// this launch ends. One that is quitting has stopped listening but keeps the lock until its
    /// cleanup is done, so the launch waits a moment for it and then starts normally.
    /// </summary>
    internal static bool ClaimInstance(Mutex instance, EventWaitHandle activate, bool background, TimeSpan patience)
    {
        try { if (instance.WaitOne(0)) return true; }
        catch (AbandonedMutexException) { return true; }
        if (!background) activate.Set();
        try { if (!instance.WaitOne(patience)) return false; }
        catch (AbandonedMutexException) { }
        // Nobody took the request; this launch opens its own window instead.
        activate.Reset();
        return true;
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
        if (!_quitting && MainWindow is MainWindow window) window.OpenSettings();
    }

    public void RequestQuit()
    {
        if (!QuitQuestion.Ask(MainWindow)) return;
        ShutdownAfterConfirmedQuit();
    }

    internal void ShutdownAfterConfirmedQuit()
    {
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
        }
        ExitHoldingInstance(() => base.OnExit(e), _instance, _ownsInstance);
    }

    /// <summary>
    /// Raises Exit with the one-per-account lock still held. "Delete all data" and Uninstall remove
    /// the data folders there, and a launch or an agent's bridge must not start a new Deskweave
    /// into a folder being deleted.
    /// </summary>
    internal static void ExitHoldingInstance(Action raiseExit, Mutex? instance, bool owns)
    {
        try { raiseExit(); }
        finally
        {
            if (owns) instance?.ReleaseMutex();
            instance?.Dispose();
        }
    }

    internal static void LogFailure(Exception failure)
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
        TrayMenuStyle.Apply(menu);
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

    internal static bool Ask(Window? owner) =>
        ConfirmForTests is { } confirm ? confirm() : Question.Ask(owner, Title, Body, QuitLabel, CancelLabel);
}

/// <summary>
/// The app's one way to ask before something that cannot be taken back: the app's own font, colours
/// and buttons, in its theme down to the title bar. Deleting a workspace used the Windows message
/// box instead - grey system chrome, Yes/No and a warning sign - in the middle of an app that
/// otherwise never shows one (2026-09-22).
/// </summary>
internal static class Question
{
    internal static bool Ask(Window? owner, string title, string body, string yes, string no = "Cancel", bool danger = false) =>
        Build(owner, title, body, yes, no, danger).ShowDialog() == true;

    /// <summary>The dialog, not yet shown - also what the scene harness photographs.</summary>
    internal static Window Build(Window? owner, string title, string body, string yes, string no = "Cancel", bool danger = false)
    {
        var dialog = new Window
        {
            Title = title,
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
        // A dark app with a white Windows title bar on its dialog looks pasted in.
        dialog.SourceInitialized += (_, _) =>
        {
            int dark = AppearanceManager.Dark ? 1 : 0;
            try { DwmSetWindowAttribute(new System.Windows.Interop.WindowInteropHelper(dialog).Handle, 20, ref dark, sizeof(int)); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        };
        // The question leads, in the dialog itself, not only in its title bar - the way Windows' own
        // dialogs put their main instruction first - and the consequences follow in plain text.
        var content = new Grid();
        content.SetResourceReference(Panel.BackgroundProperty, "WindowBrush");
        var inner = new Grid { Margin = new Thickness(24, 20, 24, 24) };
        content.Children.Add(inner);
        for (int row = 0; row < 3; row++) inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var heading = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        inner.Children.Add(heading);
        var text = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "MutedInkBrush");
        Grid.SetRow(text, 1);
        inner.Children.Add(text);
        var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = no, MinWidth = 76, Height = 30, IsCancel = true, IsDefault = danger,
            Style = (Style)Application.Current.FindResource("QuietButton"), Margin = new Thickness(0, 0, 8, 0) };
        // Something that cannot be undone looks like Settings' own Delete - an outlined button in
        // red ink - and Enter does not do it.
        var accept = new Button { Content = yes, MinWidth = 76, Height = 30, IsDefault = !danger,
            Style = (Style)Application.Current.FindResource(danger ? "DeskButton" : "PrimaryButton") };
        if (danger) accept.SetResourceReference(Control.ForegroundProperty, "DangerInkBrush");
        cancel.Click += (_, _) => dialog.DialogResult = false;
        accept.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(cancel);
        actions.Children.Add(accept);
        Grid.SetRow(actions, 2);
        inner.Children.Add(actions);
        dialog.Content = content;
        return dialog;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
