using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace HiveMind.AgentWorkspaces;

/// <summary>
/// Whether the corner window should be on screen at this moment. Pure, because "it faded out while I
/// was reading it" and "it never came back" are both bugs nobody can reproduce by hand.
/// </summary>
internal static class WorkspacePeekPolicy
{
    /// <param name="running">A workspace has a computer running. Nothing else has a picture to show.</param>
    /// <param name="hub">The hub window is on screen, which shows the same thing bigger.</param>
    /// <param name="dismissed">The owner pressed hide; gone until the next activity.</param>
    /// <param name="summoned">The owner called it up with the shortcut or by dragging a file at it.</param>
    /// <param name="held">The owner's pointer, a drag or his input is on it right now.</param>
    /// <param name="busy">A workspace waits on the owner: a question, or a workspace he holds.</param>
    /// <param name="quiet">How long since the last thing any workspace did.</param>
    internal static bool Wanted(CornerShow show, bool running, bool hub, bool dismissed, bool summoned,
        bool pinned, bool held, bool busy, TimeSpan quiet, TimeSpan fade)
    {
        if (!running || hub || dismissed) return false;
        if (summoned || held) return true;
        return show switch
        {
            CornerShow.Always => true,
            CornerShow.ComesAndGoes => pinned || busy || quiet < fade,
            _ => false,
        };
    }
}

/// <summary>Which edges of the card a resize drags.</summary>
[Flags]
internal enum PeekEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>How big the corner window is and where it goes, in DIPs. Pure arithmetic.</summary>
internal static class WorkspacePeekPlacement
{
    /// <summary>How far inside the work area the card sits.</summary>
    internal const double Margin = 16;

    internal const double MinWidth = 220, MaxWidth = 1600;

    /// <summary>Small, the size it starts at. A card at this width or under is not grown.</summary>
    internal const double SmallWidth = 344;

    /// <summary>The card at a width, 16:10, the width kept inside what a card may be.</summary>
    internal static Size Card(double width)
    {
        double w = Math.Round(double.IsFinite(width) ? Math.Clamp(width, MinWidth, MaxWidth) : SmallWidth);
        return new Size(w, Math.Round(w * 10 / 16));
    }

    /// <summary>Starts small; a size the owner dragged to survives a restart.</summary>
    internal static Size Card(AppSettings settings) =>
        Card(settings.CornerWidth ?? SmallWidth);

    /// <summary>The card at the bottom right of a work area.</summary>
    internal static Rect Corner(Rect work, Size size, double margin = Margin)
    {
        double width = Math.Min(size.Width, Math.Max(1, work.Width - 2 * margin));
        double height = Math.Min(size.Height, Math.Max(1, work.Height - 2 * margin));
        double left = work.Right - width - margin;
        double top = work.Bottom - height - margin;
        return Fit(work, new Rect(left, top, width, height));
    }

    /// <summary>Moves a window inside a bounding rectangle, shrinking it only when it cannot fit.</summary>
    internal static Rect Fit(Rect bounds, Rect window)
    {
        double width = Math.Min(window.Width, bounds.Width);
        double height = Math.Min(window.Height, bounds.Height);
        double left = Math.Clamp(window.Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - width));
        double top = Math.Clamp(window.Top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - height));
        return new Rect(left, top, width, height);
    }

    /// <summary>
    /// A resize by dragging edges, keeping 16:10. The edges not dragged stay put; an edge the drag
    /// says nothing about (the sides, when only the top moves) keeps the side nearer the middle of
    /// the work area fixed, so a card in the bottom right grows up and left, into the screen.
    /// </summary>
    internal static Rect Resize(Rect start, PeekEdges edges, Vector delta, Rect work)
    {
        double across = edges.HasFlag(PeekEdges.Left) ? start.Width - delta.X
            : edges.HasFlag(PeekEdges.Right) ? start.Width + delta.X : double.NaN;
        double down = edges.HasFlag(PeekEdges.Top) ? (start.Height - delta.Y) * 16 / 10
            : edges.HasFlag(PeekEdges.Bottom) ? (start.Height + delta.Y) * 16 / 10 : double.NaN;
        double wanted = double.IsNaN(across) ? down
            : double.IsNaN(down) ? across
            : Math.Abs(across - start.Width) >= Math.Abs(down - start.Width) ? across : down;
        if (double.IsNaN(wanted)) return start;
        Size size = Card(Math.Min(wanted, Math.Min(work.Width, work.Height * 16 / 10)));

        bool keepRight = edges.HasFlag(PeekEdges.Left)
            || !edges.HasFlag(PeekEdges.Right) && start.Left + start.Width / 2 > work.Left + work.Width / 2;
        bool keepBottom = edges.HasFlag(PeekEdges.Top)
            || !edges.HasFlag(PeekEdges.Bottom) && start.Top + start.Height / 2 > work.Top + work.Height / 2;
        double left = keepRight ? start.Right - size.Width : start.Left;
        double top = keepBottom ? start.Bottom - size.Height : start.Top;
        return Fit(work, new Rect(left, top, size.Width, size.Height));
    }

    /// <summary>Whether a card at this width counts as grown - the shrink button only means
    /// something once dragging or Settings has made it bigger than Small.</summary>
    internal static bool Grown(AppSettings settings) => Card(settings).Width > SmallWidth + 0.5;

    /// <summary>How far the back card of a stack sits above the front one, before its own scale.</summary>
    internal const double StackRise = 22;
    internal const double StackScale = 0.93;

    /// <summary>The back card of a two-up stack: the same rect, scaled 0.93 about its own center and
    /// then risen 22 DIP - so it stays centered under the front card, only smaller and higher.</summary>
    internal static Rect Back(Rect front)
    {
        double width = front.Width * StackScale, height = front.Height * StackScale;
        double left = front.Left + (front.Width - width) / 2;
        double top = front.Top + (front.Height - height) / 2 - StackRise;
        return new Rect(left, top, width, height);
    }

    /// <summary>
    /// Whether a place the owner dragged it to, read back from disk, still lands on a monitor
    /// Windows knows about. A monitor unplugged since he moved it there is not somewhere to trust;
    /// the caller falls back to the ordinary corner instead.
    /// </summary>
    internal static bool OnConnectedMonitor(Rect dip)
    {
        // MonitorFor already found the nearest monitor and its real DPI; converting with that scale
        // (rather than always the primary's) and then asking whether any monitor is really there
        // (MONITOR_DEFAULTTONULL) is what makes this correct on a mixed-DPI secondary monitor.
        double scale = MonitorFor(dip).Scale;
        var rect = new NativeRect(
            (int)Math.Round(dip.Left * scale), (int)Math.Round(dip.Top * scale),
            (int)Math.Round(dip.Right * scale), (int)Math.Round(dip.Bottom * scale));
        try { return MonitorFromRect(rect, 0) != 0; } // MONITOR_DEFAULTTONULL
        catch (DllNotFoundException) { return true; }
        catch (EntryPointNotFoundException) { return true; }
    }

    /// <summary>A monitor's DPI scale and its work area, in DIPs of that same scale.</summary>
    internal readonly record struct MonitorGeometry(double Scale, Rect WorkArea);

    /// <summary>
    /// The DPI scale and work area of whichever monitor a DIP rect is nearest to - found with a
    /// first pass at the primary monitor's scale (the only one known before any monitor is picked),
    /// then corrected to that monitor's own DPI and work area. Saved positions, drop targets and
    /// resize clamping all go through this, so a secondary monitor at a different DPI than the
    /// primary one places and clamps correctly instead of by the primary's scale. Falls back to the
    /// primary monitor's own work area when nothing can be queried.
    /// </summary>
    internal static MonitorGeometry MonitorFor(Rect dip)
    {
        double guess = PrimaryScale();
        try
        {
            var probe = new NativeRect(
                (int)Math.Round(dip.Left * guess), (int)Math.Round(dip.Top * guess),
                (int)Math.Round(dip.Right * guess), (int)Math.Round(dip.Bottom * guess));
            nint monitor = MonitorFromRect(probe, 2); // MONITOR_DEFAULTTONEAREST: always finds one
            if (monitor != 0 && GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0)
            {
                double scale = dpi / 96.0;
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                    return new MonitorGeometry(scale, new Rect(
                        info.Work.Left / scale, info.Work.Top / scale,
                        (info.Work.Right - info.Work.Left) / scale, (info.Work.Bottom - info.Work.Top) / scale));
            }
        }
        catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        return new MonitorGeometry(guess, SystemParameters.WorkArea);
    }

    /// <summary>The primary monitor's DPI scale (1.0 at 96 DPI). Used only as the first-pass guess
    /// <see cref="MonitorFor"/> needs before it knows which monitor it is really asking about, and to
    /// place a window that has never been placed anywhere before.</summary>
    internal static double PrimaryScale()
    {
        try
        {
            nint monitor = MonitorFromPoint(default, 1); // MONITOR_DEFAULTTOPRIMARY
            if (GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 && dpi > 0) return dpi / 96.0;
        }
        catch (DllNotFoundException) { } catch (EntryPointNotFoundException) { }
        return 1.0;
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly struct NativeRect(int left, int top, int right, int bottom)
    {
        public readonly int Left = left, Top = top, Right = right, Bottom = bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    readonly struct NativePoint(int x, int y) { public readonly int X = x, Y = y; }
    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor, Work;
        public uint Flags;
    }
    [DllImport("user32.dll")] static extern nint MonitorFromRect(NativeRect rect, uint flags);
    [DllImport("user32.dll")] static extern nint MonitorFromPoint(NativePoint point, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(nint monitor, int kind, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}

/// <summary>A hotkey as the owner writes it: "Ctrl+Alt+D". Parsing lives here so the settings file,
/// the Settings page and the registration all agree on what is a hotkey and what is not.</summary>
internal static class WorkspaceHotkey
{
    internal static bool Parse(string? text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= ModifierKeys.Control; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "win" or "windows" or "meta": modifiers |= ModifierKeys.Windows; break;
                default:
                    string token = part.Length == 1 && char.IsAsciiDigit(part[0]) ? "D" + part : part;
                    if (!Enum.TryParse(token, true, out key) || !Enum.IsDefined(key)) return false;
                    break;
            }
        }
        // A bare letter is not a global hotkey. Registering one would take that key away from every
        // program on the PC, which is not something a corner window gets to do.
        return key != Key.None && modifiers != ModifierKeys.None;
    }

    internal static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(4);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key is >= Key.D0 and <= Key.D9 ? ((int)(key - Key.D0)).ToString() : key.ToString());
        return string.Join("+", parts);
    }
}
