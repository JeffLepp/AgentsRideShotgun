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
        if (_runtime()?.Plane is { Driving: Driver.Owner } plane) plane.Release();
    }

    void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_runtime() is not { Computer: { } computer, Plane: { } plane }) return;
        ControlMode mode = AppSettingsStore.Current.Control;
        if (mode != ControlMode.WorkAlongside && plane.Driving != Driver.Owner)
        {
            // Instant: the lease changes here and input the agent had queued is dropped.
            plane.OwnerTakes();
            _carryOn = mode == ControlMode.TakeTurns;
        }
        Keyboard.Focus(_screen);
        if (Map(e.GetPosition(_screen), out int x, out int y)) computer.Click(x, y, e.ChangedButton == MouseButton.Right);
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
        // TextInput carries the printable characters; these are the ones it never reports.
        if (e.Key is not (Key.Enter or Key.Tab or Key.Back or Key.Delete or Key.Escape
            or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End)) return;
        if (Using() is not { } computer) return;
        computer.SendKey(KeyInterop.VirtualKeyFromKey(e.Key));
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

    public void Dispose()
    {
        _quiet?.Stop();
        _screen.MouseDown -= MouseDown;
        _screen.MouseWheel -= MouseWheel;
        _screen.TextInput -= TextInput;
        _screen.KeyDown -= KeyDown;
    }
}
