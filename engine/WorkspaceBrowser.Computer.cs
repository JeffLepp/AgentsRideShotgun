using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace HiveMind.AgentWorkspaces;

public sealed partial class WorkspaceBrowser
{
    readonly SemaphoreSlim _send = new(1, 1);

    internal async Task<(int Width, int Height)> Viewport(CancellationToken cancel)
    {
        await Follow(cancel).ConfigureAwait(false);
        JsonNode? metrics = await Call("Page.getLayoutMetrics", null, cancel, _session).ConfigureAwait(false);
        JsonNode? viewport = metrics?["cssVisualViewport"];
        return ((int)(viewport?["clientWidth"]?.GetValue<double>() ?? 0),
            (int)(viewport?["clientHeight"]?.GetValue<double>() ?? 0));
    }

    internal async Task<BitmapSource?> Screenshot(CancellationToken cancel)
    {
        await Follow(cancel).ConfigureAwait(false);
        JsonNode? metrics = await Call("Page.getLayoutMetrics", null, cancel, _session).ConfigureAwait(false);
        JsonNode? viewport = metrics?["cssVisualViewport"];
        int width = (int)(viewport?["clientWidth"]?.GetValue<double>() ?? 0);
        int height = (int)(viewport?["clientHeight"]?.GetValue<double>() ?? 0);
        if (width <= 0 || height <= 0) return null;
        JsonNode? result = await Call("Page.captureScreenshot", new JsonObject
        {
            ["format"] = "png", ["captureBeyondViewport"] = false,
            // Clip away scrollbars instead of squeezing them into the content width. pageX/Y keep
            // the image origin at the visible viewport after scrolling, not the document origin.
            ["clip"] = new JsonObject { ["x"] = viewport?["pageX"]?.GetValue<double>() ?? 0,
                ["y"] = viewport?["pageY"]?.GetValue<double>() ?? 0,
                ["width"] = width, ["height"] = height, ["scale"] = 1 },
        }, cancel, _session).ConfigureAwait(false);
        string? data = result?["data"]?.GetValue<string>();
        if (string.IsNullOrEmpty(data)) return null;
        using var stream = new MemoryStream(Convert.FromBase64String(data));
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();
        // CDP input uses CSS pixels. Normalize the image to exactly that coordinate space at any DPI.
        if (frame.PixelWidth == width && frame.PixelHeight == height) return frame;
        var normalized = new TransformedBitmap(frame, new ScaleTransform((double)width / frame.PixelWidth, (double)height / frame.PixelHeight));
        normalized.Freeze();
        return normalized;
    }

    internal async Task<bool> ComputerInput(ComputerAction action, CancellationToken cancel)
    {
        await Follow(cancel).ConfigureAwait(false);
        switch (action.Type)
        {
            case "click": case "double_click":
                return await ClickPoint(action.X, action.Y, action.Button, action.Type == "double_click" ? 2 : 1, cancel).ConfigureAwait(false);
            case "move":
                return await Mouse("mouseMoved", action.X, action.Y, "none", 0, 0, cancel).ConfigureAwait(false);
            case "scroll":
                return await Call("Input.dispatchMouseEvent", new JsonObject
                {
                    ["type"] = "mouseWheel", ["x"] = action.X, ["y"] = action.Y,
                    ["deltaX"] = action.ScrollX, ["deltaY"] = action.ScrollY,
                }, cancel, _session).ConfigureAwait(false) is not null;
            case "type": return await InsertText(action.Text, cancel).ConfigureAwait(false);
            case "keypress": return await Chord(action.Keys!, cancel).ConfigureAwait(false);
            case "drag":
            {
                ComputerPoint[] points = action.Path!;
                ComputerPoint last = points[0];
                bool down = false;
                try
                {
                    if (!await Mouse("mouseMoved", last.X, last.Y, "none", 0, 0, cancel).ConfigureAwait(false)) return false;
                    down = true; // A lost acknowledgement can still mean the press reached Chromium.
                    if (!await Mouse("mousePressed", last.X, last.Y, "left", 1, 1, cancel).ConfigureAwait(false)) return false;
                    foreach (var point in points.Skip(1))
                    {
                        last = point;
                        if (!await Mouse("mouseMoved", point.X, point.Y, "left", 1, 0, cancel).ConfigureAwait(false)) return false;
                    }
                    down = false;
                    return await Mouse("mouseReleased", last.X, last.Y, "left", 0, 1, cancel).ConfigureAwait(false);
                }
                finally { if (down) await ReleaseMouse(last.X, last.Y, "left").ConfigureAwait(false); }
            }
            default: return false;
        }
    }

    async Task<bool> InsertText(string text, CancellationToken cancel)
    {
        if (text.Length > WorkspaceComputer.MaxText || text.Contains('\0')) return false;
        // Small bounded chunks keep takeover responsive without paying two round trips per character.
        for (int at = 0; at < text.Length;)
        {
            int count = Math.Min(256, text.Length - at);
            if (at + count < text.Length && char.IsHighSurrogate(text[at + count - 1])) count--;
            if (await Call("Input.insertText", new JsonObject { ["text"] = text.Substring(at, count) }, cancel, _session)
                .ConfigureAwait(false) is null) return false;
            at += count;
        }
        return !cancel.IsCancellationRequested;
    }

    async Task<bool> ClickPoint(double x, double y, string button, int count, CancellationToken cancel)
    {
        if (!await Mouse("mouseMoved", x, y, "none", 0, 0, cancel).ConfigureAwait(false)) return false;
        for (int click = 1; click <= count; click++)
        {
            bool release = true;
            try
            {
                if (!await Mouse("mousePressed", x, y, button, button == "right" ? 2 : 1, click, cancel).ConfigureAwait(false)) return false;
                bool delivered = await Mouse("mouseReleased", x, y, button, 0, click, cancel).ConfigureAwait(false);
                release = !delivered;
                if (!delivered) return false;
            }
            finally { if (release) await ReleaseMouse(x, y, button).ConfigureAwait(false); }
        }
        return true;
    }

    async Task ReleaseMouse(double x, double y, string button)
    {
        // Cleanup only: never finish a cancelled click/drag by replaying its press or movement.
        using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Mouse("mouseReleased", x, y, button, 0, 0, cleanup.Token).ConfigureAwait(false);
    }

    async Task<bool> Mouse(string type, double x, double y, string button, int buttons, int count, CancellationToken cancel)
        => await Call("Input.dispatchMouseEvent", new JsonObject
        {
            ["type"] = type, ["x"] = x, ["y"] = y, ["button"] = button,
            ["buttons"] = buttons, ["clickCount"] = count,
        }, cancel, _session).ConfigureAwait(false) is not null;

    internal static JsonObject ChordMessage(string type, string[] keys)
    {
        string last = keys[^1];
        int modifiers = (keys.Contains("ALT") ? 1 : 0) | (keys.Contains("CTRL") ? 2 : 0) | (keys.Contains("SHIFT") ? 8 : 0);
        string key = last switch
        {
            "LEFT" => "ArrowLeft", "RIGHT" => "ArrowRight", "UP" => "ArrowUp", "DOWN" => "ArrowDown",
            "SPACE" => " ", "PAGEUP" => "PageUp", "PAGEDOWN" => "PageDown", "BACKSPACE" => "Backspace",
            "ESCAPE" => "Escape", "ENTER" => "Enter", "DELETE" => "Delete", "TAB" => "Tab", "HOME" => "Home", "END" => "End",
            _ when last.Length == 1 => (modifiers & 8) != 0 ? last : last.ToLowerInvariant(),
            _ => last,
        };
        var result = new JsonObject
        {
            ["type"] = type, ["key"] = key, ["modifiers"] = modifiers,
            ["windowsVirtualKeyCode"] = WorkspaceComputer.KeyCode(last),
        };
        if (type == "keyDown" && (modifiers & 3) == 0)
        {
            if (last.Length == 1 || last == "SPACE") result["text"] = key;
            else if (last == "ENTER") result["text"] = "\r";
        }
        return result;
    }

    async Task<bool> Chord(string[] keys, CancellationToken cancel)
    {
        bool up = false;
        try
        {
            if (await Call("Input.dispatchKeyEvent", ChordMessage("keyDown", keys), cancel, _session).ConfigureAwait(false) is null) return false;
            up = await Call("Input.dispatchKeyEvent", ChordMessage("keyUp", keys), cancel, _session).ConfigureAwait(false) is not null;
            return up;
        }
        finally
        {
            if (!up)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
                await Call("Input.dispatchKeyEvent", ChordMessage("keyUp", keys), cleanup.Token, _session).ConfigureAwait(false);
            }
        }
    }
}
