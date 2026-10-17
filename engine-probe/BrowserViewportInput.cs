using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Deskweave.AgentWorkspaces;

/// <summary>Input must land on the CSS viewport shown in the mirror, including letterboxing
/// and a cropped preview. These checks never open a window or read an owner's browser.</summary>
internal static class BrowserViewportInput
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        List<object> checks = [];
        bool failed = false;
        void Check(bool passed, string name)
        {
            checks.Add(new { passed, name });
            failed |= !passed;
        }
        bool Map(Point point, double shownWidth, double shownHeight, int width, int height,
            Stretch stretch, out int x, out int y, bool clamp = false)
            => WorkspaceScreenInput.MapPicture(point, shownWidth, shownHeight, width, height,
                stretch, out x, out y, clamp);

        Check(Map(new(200, 150), 400, 300, 1200, 900, Stretch.Uniform, out int x, out int y)
            && x == 600 && y == 450,
            "A browser mirror maps its center to CSS viewport pixels rather than desktop dimensions");
        Check(Map(new(200, 150), 400, 300, 1920, 1080, Stretch.Uniform, out x, out y)
            && x == 960 && y == 540, "Native desktop input retains its full-screen coordinate space");
        Check(!Map(new(150, 10), 300, 300, 1200, 600, Stretch.Uniform, out x, out y),
            "Letterboxing above a browser page accepts no click");
        Check(Map(new(150, 75), 300, 300, 1200, 600, Stretch.Uniform, out x, out y)
            && x == 600 && y == 0, "The visible page's upper edge remains CSS y zero with letterboxing");
        Check(Map(new(0, 150), 300, 300, 1200, 600, Stretch.UniformToFill, out x, out y)
            && x == 300 && y == 300, "A cropped corner preview maps the visible edge into the actual page");
        Check(Map(new(-100, 1000), 400, 300, 1200, 900, Stretch.Uniform, out x, out y, clamp: true)
            && x == 0 && y == 899, "Dragging outside the mirror clamps to that viewport's edge");
        Check(!Map(new(0, 0), 0, 300, 1200, 900, Stretch.Uniform, out x, out y)
            && !Map(new(0, 0), 300, 300, 0, 900, Stretch.Uniform, out x, out y),
            "An unlaid-out or empty picture never dispatches a coordinate");

        Check(WorkspaceScreenInput.BrowserChord(Key.Tab, ModifierKeys.Shift)?.SequenceEqual(["SHIFT", "TAB"]) == true,
            "Shift Tab stays a browser chord for reverse focus traversal");
        Check(WorkspaceScreenInput.BrowserChord(Key.A, ModifierKeys.Control)?.SequenceEqual(["CTRL", "A"]) == true,
            "Select all is sent to the page as a browser chord");
        Check(WorkspaceScreenInput.BrowserChord(Key.A, ModifierKeys.None) is null
            && WorkspaceScreenInput.BrowserChord(Key.A, ModifierKeys.Shift) is null,
            "Printable characters are inserted once through text composition");
        Check(WorkspaceScreenInput.BrowserChord(Key.D1, ModifierKeys.Control)?.SequenceEqual(["CTRL", "1"]) == true,
            "Digit shortcuts use their CDP key names");
        Check(WorkspaceScreenInput.BrowserChord(Key.Insert, ModifierKeys.Shift)?.SequenceEqual(["SHIFT", "INSERT"]) == true
            && WorkspaceBrowser.ChordMessage("keyDown", ["SHIFT", "INSERT"])["key"]?.ToString() == "Insert"
            && WorkspaceComputer.KeyCode("INSERT") == 45, "Insert chords use Chromium's standard key name and virtual key");
        Check(WorkspaceScreenInput.BrowserChord(Key.D, ModifierKeys.Windows) is null,
            "Owner desktop system shortcuts are not redirected into the mirrored browser");

        File.WriteAllText(Path.Combine(output, "browser-viewport-input.json"),
            JsonSerializer.Serialize(new { passed = !failed, checks }, new JsonSerializerOptions { WriteIndented = true }));
        return failed ? 1 : 0;
    }
}
