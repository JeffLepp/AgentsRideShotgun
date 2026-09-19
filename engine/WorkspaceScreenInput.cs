using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace HiveMind.AgentWorkspaces;

/// <summary>The owner's mouse and keyboard on a picture of a workspace screen.</summary>
public sealed class WorkspaceScreenInput : IDisposable
{
    internal static readonly TimeSpan LeaveDelay = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan StayDelay = TimeSpan.FromSeconds(60);

    readonly Image _screen;
    readonly Func<WorkspaceRuntime?> _runtime;
    readonly DispatcherTimer _quiet;
    Func<DateTimeOffset> _now = () => DateTimeOffset.UtcNow;
    WorkspaceControl? _held;
    DateTimeOffset? _deadline;
    bool _hovering;

    public WorkspaceScreenInput(Image screen, Func<WorkspaceRuntime?> runtime)
    {
        _screen = screen;
        _runtime = runtime;
        screen.Focusable = true;
        screen.MouseDown += MouseDown;
        screen.MouseWheel += MouseWheel;
        screen.TextInput += TextInput;
        screen.KeyDown += KeyDown;
        screen.MouseEnter += MouseEnter;
        screen.MouseLeave += MouseLeave;
        _quiet = new DispatcherTimer(DispatcherPriority.Background, screen.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _quiet.Tick += (_, _) => CheckDeadline();
    }

    public event Action? OwnerActed;

    /// <summary>Release only the screen this view took, including when the view closes.</summary>
    internal void Release()
    {
        _quiet.Stop();
        _deadline = null;
        WorkspaceControl? held = _held;
        _held = null;
        if (held is { Driving: Driver.Owner }) held.Release();
    }

    void TakeOver(WorkspaceControl plane)
    {
        if (_held is { } previous && !ReferenceEquals(previous, plane)) Release();
        // An existing owner lease may belong to Pause every agent or another view. This view
        // must never release that lease when its own quiet timer expires.
        if (plane.Driving != Driver.Owner)
        {
            plane.OwnerTakes();
            _held = plane;
        }
        ResetDeadline();
    }

    void MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_runtime() is not { Computer: { } computer, Plane: { } plane }) return;
        if (!Map(e.GetPosition(_screen), out int x, out int y)) return;
        TakeOver(plane);
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
            if (key == Key.C) computer.Copy(); else computer.Paste();
        }
        // TextInput carries printable characters; desktop chord keys are handled separately.
        else if (Keyboard.Modifiers == ModifierKeys.None && key is Key.Enter or Key.Tab or Key.Back or Key.Delete
            or Key.Escape or Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End
            or Key.PageUp or Key.PageDown or Key.Insert or (>= Key.F1 and <= Key.F12))
            computer.SendKey(KeyInterop.VirtualKeyFromKey(key));
        else return;
        Acted();
        e.Handled = true;
    }

    AgentDesktop? Using() => _runtime() is { Computer: { } computer, Plane: { Driving: Driver.Owner } }
        ? computer : null;

    void MouseEnter(object sender, MouseEventArgs e) => Enter();
    void MouseLeave(object sender, MouseEventArgs e) => Leave();
    void Enter() { _hovering = true; ResetDeadline(); }
    void Leave() { _hovering = false; ResetDeadline(); }

    void Acted()
    {
        ResetDeadline();
        OwnerActed?.Invoke();
    }

    void ResetDeadline()
    {
        if (_held is null) return;
        _deadline = _now() + (_hovering ? StayDelay : LeaveDelay);
        _quiet.Start();
    }

    void CheckDeadline()
    {
        if (ModuleEntry.AllPaused) return;
        if (_held is not null && _deadline is { } deadline && _now() >= deadline) Release();
    }

    /// <summary>
    /// The owner acted on this workspace from beside the picture (its taskbar): the agent waits,
    /// and carries on as it does after the pointer leaves the screen.
    /// </summary>
    internal void Touch()
    {
        if (_runtime()?.Plane is { } plane) { TakeOver(plane); Acted(); }
    }

    internal void UseClockForTests(Func<DateTimeOffset> now) => _now = now;
    internal void SimulateEnterForTests() => Enter();
    internal void SimulateLeaveForTests() => Leave();
    internal void SimulateInputForTests() => Acted();
    internal void SimulateClickForTests()
    {
        if (_runtime()?.Plane is { } plane) { TakeOver(plane); Acted(); }
    }
    internal void CheckForTests() => CheckDeadline();

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
        Release();
        _screen.MouseDown -= MouseDown;
        _screen.MouseWheel -= MouseWheel;
        _screen.TextInput -= TextInput;
        _screen.KeyDown -= KeyDown;
        _screen.MouseEnter -= MouseEnter;
        _screen.MouseLeave -= MouseLeave;
    }
}
