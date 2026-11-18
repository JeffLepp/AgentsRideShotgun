using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Deskweave.AgentWorkspaces;

namespace Deskweave;

public partial class WorkspaceWindow : Window
{
    static readonly Dictionary<string, WorkspaceWindow> OpenWindows = new(StringComparer.Ordinal);

    WorkspaceWindow(MainWindow main, string id)
    {
        InitializeComponent();
        Title = (WorkspaceStore.Find(id)?.Name ?? id) + " · ARS";
        ShellPlacement placement = ShellPlacement.ForWorkspaceWindow(main);
        SourceInitialized += (_, _) =>
        {
            placement.Restore(this, 960, 600);
            ApplyTheme();
        };
        View.Renamed += (renamed, name) =>
        {
            Title = name + " · ARS";
            main.WorkspaceRenamed(renamed, name);
        };
        View.Deleted += _ => Close();
        Closed += (_, _) =>
        {
            AppearanceManager.Changed -= ApplyTheme;
            OpenWindows.Remove(id);
            View.Dispose();
        };
        AppearanceManager.Changed += ApplyTheme;
        View.SetWorkspace(id);
    }

    internal static WorkspaceWindow? Find(string id) => OpenWindows.GetValueOrDefault(id);

    internal static WorkspaceWindow Open(MainWindow main, string id)
    {
        if (Find(id) is { } existing) return existing;
        var window = new WorkspaceWindow(main, id);
        OpenWindows.Add(id, window);
        window.Show();
        return window;
    }

    internal void BringForward()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal static void CloseAll()
    {
        foreach (WorkspaceWindow window in OpenWindows.Values.ToArray()) window.Close();
    }

    void ApplyTheme()
    {
        int dark = AppearanceManager.Dark ? 1 : 0;
        try { DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        View.RefreshLive();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
