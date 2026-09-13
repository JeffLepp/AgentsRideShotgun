using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Input;

namespace HiveMind.AgentWorkspaces;

/// <summary>When the corner view is on the owner's screen.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeekMode>))]
public enum PeekMode
{
    /// <summary>Never on its own. The hotkey still calls it up.</summary>
    Off,

    /// <summary>Fades in while the workspace is doing something and out again when it goes quiet.</summary>
    Activity,

    /// <summary>Stays up for as long as a workspace is running.</summary>
    Always,
}

/// <summary>Which corner it sits in. The owner's main screen, and its work area, not the whole of it.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PeekCorner>))]
public enum PeekCorner { BottomRight, BottomLeft, TopRight, TopLeft }

/// <summary>
/// The corner view's settings. One surface for the whole module rather than one per workspace: it
/// shows whichever workspace is working, and two of them over each other in the same corner would
/// be worse than either.
///
/// Kept beside the access policy rather than in the workspace folder, which is Low-writable - an
/// agent inside a workspace has no business deciding what appears on the owner's own screen.
/// </summary>
internal sealed record PeekSettings
{
    /// <summary>Off until the owner asks for it. A window that floats over everything he does is
    /// not something to switch on for him.</summary>
    public PeekMode Mode { get; init; } = PeekMode.Off;

    public PeekCorner Corner { get; init; } = PeekCorner.BottomRight;

    /// <summary>The global hotkey, as "Ctrl+Alt+D". Empty registers nothing.</summary>
    public string Hotkey { get; init; } = "Ctrl+Alt+D";

    /// <summary>How wide it is, in device-independent pixels. The picture is 16:9 under the header.</summary>
    public double Width { get; init; } = 360;

    /// <summary>How long after the last thing the agent did it fades away in Activity mode.</summary>
    public int QuietSeconds { get; init; } = 8;

    /// <summary>
    /// Where the owner dragged it to, when that is not a corner of the main screen. A second monitor
    /// has no corner in these settings, and snapping a window back off the monitor he just put it on
    /// is the one thing a dragged window must never do.
    /// </summary>
    public double? Left { get; init; }
    public double? Top { get; init; }

    /// <summary>Whatever was on disk, coerced into something the window can actually use.</summary>
    internal PeekSettings Sane() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : PeekMode.Off,
        Corner = Enum.IsDefined(Corner) ? Corner : PeekCorner.BottomRight,
        Hotkey = WorkspaceHotkey.Parse(Hotkey, out _, out _) ? Hotkey.Trim() : string.Empty,
        Width = double.IsFinite(Width) ? Math.Clamp(Width, 220, 900) : 360,
        QuietSeconds = Math.Clamp(QuietSeconds, 2, 300),
        Left = Left is { } left && double.IsFinite(left) ? left : null,
        Top = Top is { } top && double.IsFinite(top) ? top : null,
    };

    /// <summary>The whole window, header included, at this width.</summary>
    internal Size Size => new(Width, Math.Round(Width * 9 / 16) + WorkspacePeekPlacement.HeaderHeight);
}

/// <summary>The settings file. Small, owner-owned, and read once per change rather than polled.</summary>
internal static class WorkspacePeekStore
{
    internal static string File => Path.Combine(WorkspaceAccessStore.Root, "corner-view.json");

    internal static PeekSettings Read()
    {
        try
        {
            if (!System.IO.File.Exists(File) || new FileInfo(File).Length > 4096) return new PeekSettings().Sane();
            return (JsonSerializer.Deserialize<PeekSettings>(System.IO.File.ReadAllText(File)) ?? new()).Sane();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new PeekSettings().Sane();
        }
    }

    /// <summary>Saves, and says whether it reached the disk. A corner it could not write is still
    /// the corner it is using now, so a failure changes nothing on screen.</summary>
    internal static bool Write(PeekSettings settings)
    {
        string temporary = File + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(temporary,
                JsonSerializer.Serialize(settings.Sane(), new JsonSerializerOptions { WriteIndented = true }));
            System.IO.File.Move(temporary, File, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// Whether the corner view should be on screen at this moment. Pure, because "it faded out while I
/// was reading it" and "it never came back" are both bugs nobody can reproduce by hand.
/// </summary>
internal static class WorkspacePeekPolicy
{
    /// <param name="running">A workspace has a computer running. Nothing else is worth a window.</param>
    /// <param name="busy">It is working, waiting on the owner, or someone is driving it.</param>
    /// <param name="quiet">How long since the last thing it did.</param>
    /// <param name="settles">How long a quiet workspace stays up before it fades.</param>
    /// <param name="pinned">The owner pressed the hotkey. That beats every mode, including Off.</param>
    internal static bool Wanted(PeekMode mode, bool running, bool busy, TimeSpan quiet, TimeSpan settles,
        bool pinned)
    {
        if (!running) return false;
        if (pinned) return true;
        return mode switch
        {
            PeekMode.Always => true,
            // Busy keeps it up however long it takes; quiet holds it for the settle time, so an
            // agent between two tool calls does not make the window blink.
            PeekMode.Activity => busy || quiet < settles,
            _ => false,
        };
    }
}

/// <summary>Where the window goes. Pure arithmetic, like <see cref="WorkspaceScreen"/>.</summary>
internal static class WorkspacePeekPlacement
{
    /// <summary>The chrome above the picture.</summary>
    internal const double HeaderHeight = 30;

    /// <summary>How far off the edges of the work area it sits.</summary>
    internal const double Margin = 14;

    /// <summary>The window's place in a corner of a work area.</summary>
    internal static Rect Place(Rect work, PeekCorner corner, Size size, double margin = Margin)
    {
        double width = Math.Min(size.Width, Math.Max(1, work.Width));
        double height = Math.Min(size.Height, Math.Max(1, work.Height));
        double left = corner is PeekCorner.BottomLeft or PeekCorner.TopLeft
            ? work.Left + margin
            : work.Right - width - margin;
        double top = corner is PeekCorner.TopLeft or PeekCorner.TopRight
            ? work.Top + margin
            : work.Bottom - height - margin;
        return Fit(work, new Rect(left, top, width, height));
    }

    /// <summary>
    /// The corner a dragged window ended up nearest, by its own centre against the work area's.
    /// Only asked when the window is still on the main screen; a window dropped on another monitor
    /// keeps the exact place it was dropped.
    /// </summary>
    internal static PeekCorner Nearest(Rect work, Rect window)
    {
        bool left = window.Left + window.Width / 2 < work.Left + work.Width / 2;
        bool top = window.Top + window.Height / 2 < work.Top + work.Height / 2;
        return top ? left ? PeekCorner.TopLeft : PeekCorner.TopRight
            : left ? PeekCorner.BottomLeft : PeekCorner.BottomRight;
    }

    /// <summary>Whether a dropped window is on the main screen, and so belongs to a corner of it.</summary>
    internal static bool OnWorkArea(Rect work, Rect window) =>
        work.IntersectsWith(window)
        && Rect.Intersect(work, window) is { Width: > 0, Height: > 0 } shared
        && shared.Width * shared.Height >= window.Width * window.Height / 2;

    /// <summary>Clamps a window inside a bounding rectangle, moving it rather than shrinking it.</summary>
    internal static Rect Fit(Rect bounds, Rect window)
    {
        double width = Math.Min(window.Width, bounds.Width);
        double height = Math.Min(window.Height, bounds.Height);
        double left = Math.Clamp(window.Left, bounds.Left, Math.Max(bounds.Left, bounds.Right - width));
        double top = Math.Clamp(window.Top, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - height));
        return new Rect(left, top, width, height);
    }
}

/// <summary>A hotkey as the owner writes it: "Ctrl+Alt+D". Parsing lives here so the settings file,
/// the panel's box and the registration all agree on what is a hotkey and what is not.</summary>
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
        // program on the PC, which is not something a corner view gets to do.
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
