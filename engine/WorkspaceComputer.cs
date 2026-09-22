using System.Text.Json;

namespace Deskweave.AgentWorkspaces;

internal sealed record ComputerPoint(int X, int Y);
internal sealed record ComputerAction(string Type, int X = 0, int Y = 0, string Button = "left",
    string Text = "", string[]? Keys = null, int ScrollX = 0, int ScrollY = 0,
    int Milliseconds = 0, ComputerPoint[]? Path = null, int Control = 0);
internal sealed record ComputerRequest(string Target, bool Screenshot, IReadOnlyList<ComputerAction> Actions,
    bool Marks = false);
internal sealed record ComputerReceipt(string Status, int Next, string Reason, IReadOnlyList<string> Completed);

/// <summary>Provider-neutral computer actions. No model calls, automatic retries, or hidden pacing.</summary>
internal static class WorkspaceComputer
{
    internal const int MaxActions = 16, MaxText = 32000, MaxPath = 128, MaxWait = 2000;
    internal static readonly string[] Types = ["click", "double_click", "move", "drag", "scroll", "type", "keypress", "wait", "screenshot"];

    internal static ComputerRequest Parse(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a computer request.");
        Fields(json, "target", "screenshot", "actions", "marks");
        string target = String(json, "target", "desktop");
        if (target is not ("desktop" or "browser")) throw new ArgumentException("target must be desktop or browser.");
        bool screenshot = !json.TryGetProperty("screenshot", out var picture) || picture.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw new ArgumentException("screenshot must be a boolean."),
        };
        bool marks = json.TryGetProperty("marks", out var marked) && marked.ValueKind switch
        {
            JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw new ArgumentException("marks must be a boolean."),
        };
        var result = new List<ComputerAction>();
        int textLength = 0, waits = 0;
        if (json.TryGetProperty("actions", out var actions))
        {
            if (actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() > MaxActions)
                throw new ArgumentException($"actions must contain at most {MaxActions} actions.");
            foreach (var step in actions.EnumerateArray())
            {
                if (step.ValueKind != JsonValueKind.Object) throw new ArgumentException("Each action must be an object.");
                string type = String(step, "type");
                ComputerAction action;
                switch (type)
                {
                    case "click": case "double_click":
                        Fields(step, "type", "x", "y", "button", "control");
                        string button = String(step, "button", "left");
                        if (button is not ("left" or "right")) throw new ArgumentException("button must be left or right.");
                        action = Placed(step, target, new(type, Button: button));
                        break;
                    case "move":
                        Fields(step, "type", "x", "y", "control");
                        action = Placed(step, target, new(type));
                        break;
                    case "scroll":
                        Fields(step, "type", "x", "y", "scroll_x", "scroll_y", "control");
                        action = Placed(step, target, new(type,
                            ScrollX: Integer(step, "scroll_x", 0), ScrollY: Integer(step, "scroll_y", 0)));
                        if (Math.Abs((long)action.ScrollX) > 10000 || Math.Abs((long)action.ScrollY) > 10000)
                            throw new ArgumentException("Scroll amounts must be between -10000 and 10000.");
                        break;
                    case "type":
                        Fields(step, "type", "text");
                        string text = String(step, "text");
                        textLength += text.Length;
                        if (textLength > MaxText || text.Contains('\0')) throw new ArgumentException($"Text must not contain NUL and is limited to {MaxText} characters per group.");
                        action = new(type, Text: text);
                        break;
                    case "keypress":
                        Fields(step, "type", "keys");
                        if (!step.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array
                            || keys.GetArrayLength() is < 1 or > 4) throw new ArgumentException("keys must contain modifiers followed by one key.");
                        string[] chord = keys.EnumerateArray().Select(key => key.ValueKind == JsonValueKind.String
                            ? NormalizeKey(key.GetString()!) : throw new ArgumentException("Every key must be a string.")).ToArray();
                        if (chord.Distinct().Count() != chord.Length || KeyCode(chord[^1]) == 0
                            || chord.Take(chord.Length - 1).Any(key => key is not ("CTRL" or "SHIFT" or "ALT")))
                            throw new ArgumentException("Use CTRL, SHIFT, ALT followed by one supported key. Windows/system shortcuts are not exposed.");
                        action = new(type, Keys: chord);
                        break;
                    case "drag":
                        Fields(step, "type", "path");
                        if (!step.TryGetProperty("path", out var path) || path.ValueKind != JsonValueKind.Array
                            || path.GetArrayLength() is < 2 or > MaxPath) throw new ArgumentException($"drag needs 2 to {MaxPath} points.");
                        var points = path.EnumerateArray().Select(point =>
                        {
                            Fields(point, "x", "y");
                            return new ComputerPoint(Integer(point, "x"), Integer(point, "y"));
                        }).ToArray();
                        action = new(type, Path: points);
                        break;
                    case "wait":
                        Fields(step, "type", "milliseconds");
                        int milliseconds = Integer(step, "milliseconds", 250);
                        if (milliseconds is < 0 or > MaxWait || (waits += milliseconds) > MaxWait)
                            throw new ArgumentException($"At most {MaxWait} ms of explicit waiting per group; use wait for longer waits.");
                        action = new(type, Milliseconds: milliseconds);
                        break;
                    case "screenshot":
                        Fields(step, "type");
                        screenshot = true;
                        action = new(type);
                        break;
                    default: throw new ArgumentException("Unknown computer action: " + type);
                }
                result.Add(action);
            }
        }
        // Validate the entire request, including unsupported desktop chords, before any effects.
        foreach (var action in result)
            if (target == "desktop" && action.Type == "keypress" && action.Keys!.Length > 1
                && !(action.Keys.Length == 2 && action.Keys[0] == "CTRL" && action.Keys[1] == "A"))
                throw new ArgumentException("Desktop modifiers currently support CTRL+A in native edit controls only. Browser target supports CTRL/SHIFT/ALT chords. Use controls for other native commands.");
        return new(target, screenshot, result, marks);
    }

    /// <summary>
    /// Where a pointer action goes: x and y in the latest picture, or a control by the number marks
    /// and controls gave it. A number is exact where a pixel read off a scaled-down picture is not -
    /// on a tight row of buttons, a pixel is the difference between two of them.
    /// </summary>
    static ComputerAction Placed(JsonElement step, string target, ComputerAction action)
    {
        if (!step.TryGetProperty("control", out JsonElement control))
            return action with { X = Integer(step, "x"), Y = Integer(step, "y") };
        if (step.TryGetProperty("x", out _) || step.TryGetProperty("y", out _))
            throw new ArgumentException("Give a control or x and y, not both.");
        if (target == "browser")
            throw new ArgumentException("Control numbers are for desktop windows. In the browser use page, or x and y.");
        int id = Integer(step, "control");
        if (id < 1) throw new ArgumentException("control must be a number from marks or controls.");
        return action with { Control = id };
    }

    internal static string? Bounds(ComputerAction action, int width, int height)
    {
        if (action.Control > 0) return null;
        IEnumerable<ComputerPoint> points = action.Type == "drag" ? action.Path!
            : action.Type is "click" or "double_click" or "move" or "scroll" ? [new(action.X, action.Y)] : [];
        return points.Any(point => point.X < 0 || point.Y < 0 || point.X >= width || point.Y >= height)
            ? "A point is outside the target image. Look again; use that image's coordinate space." : null;
    }

    internal static async Task<ComputerReceipt> Execute(IReadOnlyList<ComputerAction> actions, Func<bool> ownsLease,
        Func<ComputerAction, Task<bool>> apply, CancellationToken cancel)
    {
        var completed = new List<string>();
        for (int i = 0; i < actions.Count; i++)
        {
            if (!ownsLease()) return new("paused", i, "Control changed. No remaining actions were started.", completed);
            if (cancel.IsCancellationRequested) return new("cancelled", i, "Cancelled before the next action.", completed);
            try
            {
                bool delivered = actions[i].Type switch
                {
                    "screenshot" => true, // One image at the end, never one image per intermediate step.
                    "wait" => await Delay(actions[i].Milliseconds, cancel).ConfigureAwait(false),
                    _ => await apply(actions[i]).ConfigureAwait(false),
                };
                if (!delivered) return new("interrupted", i, "The action was not confirmed and may be partially applied. Inspect the result before continuing; never replay the group automatically.", completed);
                completed.Add(actions[i].Type);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return new(cancel.IsCancellationRequested ? "cancelled" : "interrupted", i,
                    "The current action may be partially applied. Inspect before continuing. " + ex.GetType().Name, completed);
            }
        }
        return new("completed", actions.Count, "Input delivered. Verify the visible application result; delivery is not proof of task completion.", completed);
    }

    static async Task<bool> Delay(int milliseconds, CancellationToken cancel)
    { await Task.Delay(milliseconds, cancel).ConfigureAwait(false); return true; }

    internal static string NormalizeKey(string key) => key.Trim().ToUpperInvariant() switch
    {
        "CONTROL" => "CTRL", "RETURN" => "ENTER", "ESC" => "ESCAPE", "DEL" => "DELETE",
        "ARROWLEFT" => "LEFT", "ARROWRIGHT" => "RIGHT", "ARROWUP" => "UP", "ARROWDOWN" => "DOWN",
        var name => name,
    };
    internal static int KeyCode(string name) => name switch
    {
        "ENTER" => 13, "TAB" => 9, "ESCAPE" => 27, "BACKSPACE" => 8, "DELETE" => 46,
        "SPACE" => 32, "LEFT" => 37, "UP" => 38, "RIGHT" => 39, "DOWN" => 40,
        "HOME" => 36, "END" => 35, "PAGEUP" => 33, "PAGEDOWN" => 34,
        _ when name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]) => name[0],
        _ when name.StartsWith('F') && int.TryParse(name[1..], out int n) && n is >= 1 and <= 12 => 111 + n,
        _ => 0,
    };

    static void Fields(JsonElement json, params string[] allowed)
    {
        if (json.ValueKind != JsonValueKind.Object || json.EnumerateObject().Any(p => !allowed.Contains(p.Name)))
            throw new ArgumentException("Unexpected fields in computer request/action.");
    }
    static string String(JsonElement json, string name, string? fallback = null) =>
        json.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString()!
            : throw new ArgumentException(name + " must be a string.")
        : fallback ?? throw new ArgumentException(name + " must be a string.");
    static int Integer(JsonElement json, string name, int? fallback = null) =>
        json.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int n)
            ? n : throw new ArgumentException(name + " must be an integer.")
        : fallback ?? throw new ArgumentException(name + " is required.");
}
