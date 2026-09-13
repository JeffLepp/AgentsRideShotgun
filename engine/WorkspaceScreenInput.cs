using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// The owner's mouse and keyboard on a picture of a workspace screen: the corner window, the
/// workspace page, anything that shows one. The real pointer never moves onto the agent's desktop;
/// a click is mapped to the workspace's screen and delivered as a window message, the way the
/// agent's own clicks are, so there is one input path to trust whichever view the owner uses.
///
/// Settings > Control decides what using it means. Take turns: the agent waits and carries on
/// after the owner has left it alone for the set seconds. Full stop: the agent waits until
/// <see cref="HandBack"/>. Work alongside: the owner's input goes in and nobody waits.
/// </summary>
public sealed class WorkspaceScreenInput : IDisposable
{
    readonly Image _screen;
    readonly Func<WorkspaceRuntime?> _runtime;
    DispatcherTimer? _quiet;
    // The workspace this view took, kept so handing back releases that one even if the view has
    // moved on to another workspace since.
    WorkspaceControl? _held;
    bool _carryOn;

    /// <param name="screen">Shows a whole workspace screen, Stretch Uniform or UniformToFill.</param>
    /// <param name="runtime">The workspace it shows now; asked on every event.</param>
    public WorkspaceScreenInput(Image screen, Func<WorkspaceRuntime?> runtime)
    {
        _screen = screen;
        _runtime = runtime;
        screen.Focusable = true;
        screen.MouseDown += MouseDown;
        screen.MouseWheel += MouseWheel;
        screen.TextInput += TextInput;
        screen.KeyDown += KeyDown;
    }

    /// <summary>The owner clicked, scrolled or typed on this picture.</summary>
    public event Action? OwnerActed;

    /// <summary>Gives the workspace back to whoever was working. What Full stop waits for.</summary>
    public void HandBack()
    {
        _quiet?.Stop();
        _carryOn = false;
        WorkspaceControl? held = _held;
        _held = null;
        if (held is { Driving: Driver.Owner }) held.Release();
    }

    void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_runtime() is not { Computer: { } computer, Plane: { } plane }) return;
        // A click on the letterbox around the picture is not a click on the workspace.
        if (!Map(e.GetPosition(_screen), out int x, out int y)) return;
        ControlMode mode = AppSettingsStore.Current.Control;
        if (mode != ControlMode.WorkAlongside && plane.Driving != Driver.Owner)
        {
            // The view has moved to another workspace: the one it held goes back first.
            if (_held is { } previous && !ReferenceEquals(previous, plane)) HandBack();
            // Instant: the lease changes here and input the agent had queued is dropped.
            plane.OwnerTakes();
            _held = plane;
            _carryOn = mode == ControlMode.TakeTurns;
        }
        Keyboard.Focus(_screen);
        computer.Click(x, y, e.ChangedButton == MouseButton.Right);
        Acted();
        e.Handled = true;
    }

    void MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Using() is not { } computer || !Map(e.GetPosition(_screen), out int x, out int y)) return;
        computer.Scroll(x, y, e.Delta);
        Acted();
        e.Handled = true;
    }

    void TextInput(object sender, TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || Using() is not { } computer) return;
        computer.TypeText(e.Text);
        Acted();
        e.Handled = true;
    }

    void KeyDown(object sender, KeyEventArgs e)
    {
        if (Using() is not { } computer) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (Keyboard.Modifiers == ModifierKeys.Control && key is Key.C or Key.V)
        {
            // Copy and paste go through the workspace's own clipboard, never the owner's.
            if (key == Key.C) computer.Copy(); else computer.Paste();
        }
        // TextInput carries the printable characters; these are the plain keys it never reports.
        // ponytail: other chords (Ctrl+A, Ctrl+Z, Shift+arrows) need chord delivery on the desktop pump, Wave 2 slice E.
        else if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Enter or Key.Tab or Key.Back or Key.Delete
            or Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.Insert or (>= Key.F1 and <= Key.F12))
            computer.SendKey(KeyInterop.VirtualKeyFromKey(key));
        else return;
        Acted();
        e.Handled = true;
    }

    /// <summary>The desktop to type into, when the owner may: he holds it, or he works alongside.</summary>
    AgentDesktop? Using() => _runtime() is { Computer: { } computer, Plane: { } plane }
        && (plane.Driving == Driver.Owner || AppSettingsStore.Current.Control == ControlMode.WorkAlongside) ? computer : null;

    void Acted()
    {
        if (_carryOn)
        {
            _quiet ??= new DispatcherTimer(DispatcherPriority.Background, _screen.Dispatcher);
            _quiet.Tick -= Quiet;
            _quiet.Tick += Quiet;
            _quiet.Interval = TimeSpan.FromSeconds(AppSettingsStore.Current.CarryOnSeconds);
            _quiet.Stop();
            _quiet.Start();
        }
        OwnerActed?.Invoke();
    }

    void Quiet(object? sender, EventArgs e) { if (_carryOn) HandBack(); }

    /// <summary>A point on the picture to a pixel on the workspace screen, undoing the stretch.</summary>
    bool Map(Point point, out int x, out int y)
    {
        x = y = 0;
        double width = AgentDesktop.ScreenWidth, height = AgentDesktop.ScreenHeight;
        if (_screen.ActualWidth <= 0 || _screen.ActualHeight <= 0) return false;
        double scale = _screen.Stretch == Stretch.UniformToFill
            ? Math.Max(_screen.ActualWidth / width, _screen.ActualHeight / height)
            : Math.Min(_screen.ActualWidth / width, _screen.ActualHeight / height);
        double left = (_screen.ActualWidth - width * scale) / 2, top = (_screen.ActualHeight - height * scale) / 2;
        x = (int)Math.Round((point.X - left) / scale);
        y = (int)Math.Round((point.Y - top) / scale);
        return x >= 0 && y >= 0 && x < width && y < height;
    }

    /// <summary>The view is going: whatever it held goes back, so no agent waits on a closed window.</summary>
    public void Dispose()
    {
        HandBack();
        _screen.MouseDown -= MouseDown;
        _screen.MouseWheel -= MouseWheel;
        _screen.TextInput -= TextInput;
        _screen.KeyDown -= KeyDown;
    }
}
