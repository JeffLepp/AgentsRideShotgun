using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HiveMind.AgentWorkspaces;

namespace Deskweave;

internal sealed class PillInfo(string text, bool working) { public string Text => text; public bool Working => working; }
/// <summary>One "What it did" row. <paramref name="code"/> is the part shown in accent mono, such
/// as the address a page was opened at (reference 04: "Opened `localhost:5173`"); empty for a step
/// that is plain text throughout.</summary>
internal sealed class DidRow(string time, string prefix, string code, BitmapSource? thumb)
{
    public string Time => time;
    public string Prefix => prefix;
    public string Code => code;
    public bool HasCode => code.Length > 0;
    public BitmapSource? Thumb => thumb;
    public bool HasThumb => thumb is not null;
}
internal sealed class FileRow(string name, string when) { public string Name => name; public string When => when; }

/// <summary>
/// A workspace in full (reference 04): header, the live screen, "What it did" and "Files". Its own
/// preview loop picks its own rate like the stack's working cards.
/// </summary>
public partial class WorkspaceFullView : UserControl, IDisposable
{
    readonly ObservableCollection<PillInfo> _pills = [];
    readonly ObservableCollection<DidRow> _did = [];
    readonly ObservableCollection<FileRow> _files = [];
    string? _id;
    WorkspaceScreenInput? _input;
    DispatcherTimer? _screenTimer;
    bool _fixture;
    bool _renaming;
    bool _disposed;
    bool _capturingScreen;

    public WorkspaceFullView()
    {
        InitializeComponent();
        HerePanel.ItemsSource = _pills;
        DidList.ItemsSource = _did;
        FilesList.ItemsSource = _files;
        // Hidden means the wide window isn't showing this page (stack mode, or another workspace
        // selected): the timer keeps existing but does no work until it is visible again.
        IsVisibleChanged += (_, _) =>
        {
            if (_screenTimer is null) return;
            if (IsVisible) { _screenTimer.Start(); ScreenTick(null, EventArgs.Empty); }
            else _screenTimer.Stop();
        };
        Unloaded += (_, _) => StopLive();
    }

    /// <summary>The workspace was renamed from this page: id, new name.</summary>
    public event Action<string, string>? Renamed;
    /// <summary>The workspace was deleted from this page.</summary>
    public event Action<string>? Deleted;

    public string? WorkspaceId => _id;

    public void SetWorkspace(string id)
    {
        StopLive();
        _fixture = false;
        _id = id;
        Reload();
        // Clicking the screen takes over right there (brief A.3), the same as the corner window.
        _input = new WorkspaceScreenInput(ScreenImage, () => WorkspaceRuntime.Of(_id));
        StartScreenTimer();
    }

    void Reload()
    {
        if (_id is not { } id || WorkspaceStore.Find(id) is not { } workspace) return;
        HeaderError.Visibility = Visibility.Collapsed;
        NameText.Text = workspace.Name;
        RenameBox.Text = workspace.Name;
        const string folderPrefix = "folder:";
        PathText.Text = WorkspaceHome.IsFolder(workspace.Agents) ? workspace.Agents[folderPrefix.Length..] : WorkspaceStore.FolderOf(id);
        BitmapSource? last = ReadLastFrame(id);
        if (last is not null) ScreenImage.Source = last;
        RefreshLive();
        LoadDid(id);
        LoadFiles(id);
    }

    /// <summary>Cheap, frequent: the "who is here" pills and a fresh frame. Called by the timer and
    /// after a theme change, since the resources code caches here go stale otherwise.</summary>
    internal void RefreshLive()
    {
        if (_fixture || _id is not { } id) return;
        WorkspaceRuntime? runtime = WorkspaceRuntime.Of(id);
        StoredWorkspace? workspace = WorkspaceStore.Find(id);
        string driver = runtime?.Access?.Controller ?? "";
        _pills.Clear();
        if (driver.Length > 0) _pills.Add(new PillInfo(WorkspaceHome.DisplayName(driver), true));
        else if (workspace is not null && !WorkspaceHome.IsFolder(workspace.Agents) && WorkspaceHome.Label(workspace.Agents) is { Length: > 0 } kept)
            _pills.Add(new PillInfo(kept, false));
    }

    // --- "What it did", from the workspace's own evidence log --------------------------------

    static readonly HashSet<string> QuietActions = new(StringComparer.Ordinal)
        { "workspace", "control", "policy", "elements", "marks", "wait", "ceiling", "window", "untrusted" };

    void LoadDid(string id)
    {
        _did.Clear();
        string log = Path.Combine(WorkspaceStore.FolderOf(id), "evidence", "actions.log");
        if (!File.Exists(log)) return;
        string[] lines;
        try { lines = File.ReadAllLines(log); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        foreach (string line in lines.TakeLast(200))
        {
            string[] parts = line.Split('\t');
            if (parts.Length < 4) continue;
            if (!DateTimeOffset.TryParse(parts[0], out DateTimeOffset when)) continue;
            string action = parts[1];
            if (action.StartsWith("computer.", StringComparison.Ordinal) || QuietActions.Contains(action)) continue;
            string detail = parts[2];
            BitmapSource? thumb = null;
            if (parts.Length > 4 && parts[4].Length > 0)
            {
                string framePath = Path.Combine(WorkspaceStore.FolderOf(id), "evidence", parts[4]);
                if (File.Exists(framePath)) thumb = LoadImage(framePath);
            }
            (string prefix, string code) = Describe(action, detail);
            _did.Add(new DidRow(when.ToLocalTime().ToString("HH:mm:ss"), prefix, code, thumb));
            if (_did.Count >= 40) break;
        }
    }

    static (string Prefix, string Code) Describe(string action, string detail) => action switch
    {
        "page" => ("Opened ", detail),
        "computer" => (detail.Length > 0 && detail != "desktop" ? "Used the computer (" + detail + ")" : "Used the computer", ""),
        "run" => ("Ran a command", ""),
        "save" => ("Saved " + detail, ""),
        "file" => ("Read " + detail, ""),
        "browser" => ("Opened the browser", ""),
        "owner" => ("You said: " + detail, ""),
        "mission" => (detail, ""),
        _ when action.Length > 0 => (char.ToUpperInvariant(action[0]) + action[1..], ""),
        _ => (action, ""),
    };

    // --- "Files", newest first from the workspace folder --------------------------------------

    static readonly HashSet<string> MachineNames = new(StringComparer.OrdinalIgnoreCase)
        { "workspace.json", "last-frame.png" };

    void LoadFiles(string id)
    {
        _files.Clear();
        string folder = WorkspaceStore.FolderOf(id);
        if (!Directory.Exists(folder)) return;
        IEnumerable<FileInfo> found;
        try { found = new DirectoryInfo(folder).GetFiles().Where(f => !MachineNames.Contains(f.Name)); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        foreach (FileInfo file in found.OrderByDescending(f => f.LastWriteTimeUtc).Take(30))
            _files.Add(new FileRow(file.Name, file.LastWriteTime.ToString("HH:mm")));
    }

    // --- the live screen -----------------------------------------------------------------------

    void StartScreenTimer()
    {
        _screenTimer ??= new DispatcherTimer(DispatcherPriority.Background);
        _screenTimer.Interval = HubPreview.Interval();
        _screenTimer.Tick -= ScreenTick;
        _screenTimer.Tick += ScreenTick;
        if (!IsVisible) return;
        _screenTimer.Start();
        ScreenTick(null, EventArgs.Empty);
    }

    async void ScreenTick(object? sender, EventArgs e)
    {
        if (_disposed || _id is not { } id || !IsVisible) return;
        RefreshLive();
        // One capture in flight at a time: a slow Task.Run from an earlier tick must finish (or be
        // dropped below) before another starts, rather than racing it.
        if (_capturingScreen || !HubPreview.Allowed) return;
        WorkspaceControl? plane = WorkspaceRuntime.Of(id)?.Plane;
        if (plane is null) return;
        _capturingScreen = true;
        try
        {
            BitmapSource? frame = await Task.Run(() =>
            {
                try { BitmapSource? shot = plane.Frame(); if (shot is not null && !shot.IsFrozen) shot.Freeze(); return shot; }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
            });
            // Drop a result that lands after the view hid or moved on to another workspace.
            if (_disposed || id != _id || !IsVisible || frame is null) return;
            ScreenImage.Source = frame;
        }
        finally { _capturingScreen = false; }
    }

    static BitmapSource? ReadLastFrame(string id)
    {
        string path = WorkspaceStore.LastFrameOf(id);
        return File.Exists(path) ? LoadImage(path) : null;
    }

    static BitmapSource? LoadImage(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    void Root_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ScreenBorder.ActualWidth > 0) ScreenBorder.Height = Math.Round(ScreenBorder.ActualWidth * 9 / 16);
    }

    // --- folder, rename, more ------------------------------------------------------------------

    void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (_id is not { } id) return;
        HeaderError.Visibility = Visibility.Collapsed;
        try
        {
            string folder = WorkspaceStore.FolderOf(id);
            if (!Directory.Exists(folder)) { ShowHeaderError("Couldn't open the folder."); return; }
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { ShowHeaderError("Couldn't open the folder."); }
    }

    void ShowHeaderError(string message)
    {
        HeaderError.Text = message;
        HeaderError.Visibility = Visibility.Visible;
    }

    void More_Click(object sender, RoutedEventArgs e)
    {
        if (_id is not { } id) return;
        HeaderError.Visibility = Visibility.Collapsed;
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Bottom };
        void Add(string label, Action action)
        {
            var item = new MenuItem { Header = label };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        // Exactly Rename, Delete (brief A.3): no Stop/Start computer (automatic) or Who can use it.
        Add("Rename", BeginRename);
        menu.Items.Add(new Separator());
        Add("Delete", () => DeleteWorkspace(id));
        // Test seam (ui-probe/Scenes.Hub.cs): the gate drives this exact menu instance rather than
        // rebuilding its own copy of the item list above.
        LastMoreMenu = menu;
        menu.IsOpen = true;
    }

    internal ContextMenu? LastMoreMenu;

    void BeginRename()
    {
        HeaderError.Visibility = Visibility.Collapsed;
        _renaming = true;
        NameText.Visibility = Visibility.Collapsed;
        RenameBox.Visibility = Visibility.Visible;
        RenameBox.Focus();
        RenameBox.SelectAll();
    }

    void CommitRename()
    {
        if (!_renaming) return;
        _renaming = false;
        RenameBox.Visibility = Visibility.Collapsed;
        NameText.Visibility = Visibility.Visible;
        string name = RenameBox.Text.Trim();
        if (_id is not { } id || name.Length == 0) { Reload(); return; }
        if (WorkspaceStore.Update(id, w => w with { Name = name }) is { } updated)
        {
            NameText.Text = updated.Name;
            Renamed?.Invoke(id, updated.Name);
        }
    }

    void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { CommitRename(); e.Handled = true; }
        else if (e.Key == Key.Escape) { _renaming = false; RenameBox.Visibility = Visibility.Collapsed; NameText.Visibility = Visibility.Visible; e.Handled = true; }
    }
    void RenameBox_LostFocus(object sender, RoutedEventArgs e) => CommitRename();

    void DeleteWorkspace(string id)
    {
        StoredWorkspace? workspace = WorkspaceStore.Find(id);
        if (workspace is null) return;
        if (MessageBox.Show(Window.GetWindow(this), $"Delete {workspace.Name} and all files inside its workspace folder?\n\n"
                + "Its running desktop and active work will stop. This cannot be undone.",
                "Delete workspace", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        WorkspaceRuntime.Of(id)?.Dispose();
        WorkspaceAccessStore.Write(id, new WorkspaceAccessPolicy());
        WorkspaceAccessStore.Withdraw(id);
        WorkspaceStore.Delete(id);
        Deleted?.Invoke(id);
    }

    // --- a scene's fixed content, in place of the real stores ----------------------------------

    internal void LoadFixture(string name, string path, IEnumerable<(string Text, bool Working)> pills,
        BitmapSource screen, IEnumerable<(string Time, string Prefix, string Code, BitmapSource? Thumb)> did,
        IEnumerable<(string Name, string When)> files)
    {
        StopLive();
        _fixture = true;
        _id = null;
        NameText.Text = name;
        PathText.Text = path;
        _pills.Clear();
        foreach (var (text, working) in pills) _pills.Add(new PillInfo(text, working));
        ScreenImage.Source = screen;
        _did.Clear();
        foreach (var (time, prefix, code, thumb) in did) _did.Add(new DidRow(time, prefix, code, thumb));
        _files.Clear();
        foreach (var (fname, when) in files) _files.Add(new FileRow(fname, when));
    }

    void StopLive()
    {
        if (_screenTimer is not null) { _screenTimer.Stop(); _screenTimer.Tick -= ScreenTick; _screenTimer = null; }
        if (_input is not null) { _input.Dispose(); _input = null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopLive();
    }
}
