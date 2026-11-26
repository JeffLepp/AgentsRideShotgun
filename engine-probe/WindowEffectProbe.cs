using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Deskweave.AgentWorkspaces;

/// <summary>
/// A drag or a double-click says what it did to its window, from the window's own rect read back
/// before and after, rather than "input delivered". An agent dragged a custom-drawn title bar, was
/// told its input went out, and believed the window had moved; it had not.
///
/// The fixture is a borderless WinForms window - its whole rect is client area, the way an app that
/// draws its own title bar is - with a blue strip along the top whose double-click toggles maximize
/// (WinForms raises MouseDown with Clicks=2 straight from WM_LBUTTONDBLCLK) and a white canvas below
/// that does nothing to the window. Every press is logged by the fixture, so delivery has its own
/// oracle beside the window rect. Everything goes through WorkspaceControl.Computer, the computer
/// tool's own path, on a workspace desktop of its own. No model is called.
///
/// Run: Deskweave.Probe.exe --window-effect "C:\absolute\output"; it also runs in the full suite.
/// </summary>
internal static class WindowEffectProbe
{
    const string Plain = "Input delivered. Verify the visible application result; delivery is not proof of task completion.";

    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Window-effect probe exceeded its 90-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(90), Timeout.InfiniteTimeSpan);
        var claims = new List<string>();
        string? failure = null;
        try { Run(Check, output); }
        catch (Exception ex) { failure = ex.ToString(); }
        File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed", observedAt = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.ToString(), claims, failure, modelCalls = 0,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Check(bool passed, string claim)
        {
            if (!passed) throw new InvalidOperationException("Failed: " + claim);
            claims.Add(claim);
            File.AppendAllText(Path.Combine(output, "progress.log"), "PASS " + claim + Environment.NewLine);
        }
    }

    /// <summary>The checks alone, for the full suite.</summary>
    internal static void Run(Action<bool, string> check, string output)
    {
        string nonce = Guid.NewGuid().ToString("N")[..8];
        string folder = Path.Combine(output, "window-effect");
        Directory.CreateDirectory(folder);
        string log = Path.Combine(folder, "presses.txt");
        string script = Path.Combine(folder, "fixture.ps1");
        File.WriteAllText(script, Fixture);
        string title = "effect-" + nonce;
        using AgentDesktop desktop = AgentDesktop.Create("effect-" + nonce);
        using var control = new WorkspaceControl(desktop);
        check(control.AgentTakes(), "Window effect: the agent holds its fixture workspace");
        string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        desktop.Launch(powershell, $"-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" \"{log}\" \"{title}\"");
        AgentWindow window = WaitForWindow(desktop, title);
        Thread.Sleep(300);

        // computer's coordinates are those of its capped picture; the fixture is placed in workspace pixels.
        double scale = WorkspaceMarks.Fit(Native.GetSystemMetrics(Native.SmCxScreen), Native.GetSystemMetrics(Native.SmCyScreen),
            WorkspaceMarks.ActionWidth, WorkspaceMarks.ActionHeight).Scale;
        int Image(int source) => (int)Math.Round(source * scale);
        string At(int x, int y) => $"\"x\":{Image(x)},\"y\":{Image(y)}";
        string Point(int x, int y) => "{" + At(x, y) + "}";

        (WindowEffect Effect, ComputerReceipt Receipt) Act(string what, string actions)
        {
            ComputerResult result = control.Computer(WorkspaceComputer.Parse(JsonDocument.Parse(
                $$"""{"screenshot":false,"actions":[{{actions}}]}""").RootElement), CancellationToken.None).GetAwaiter().GetResult();
            ComputerReceipt receipt = result.Receipt;
            string seen = JsonSerializer.Serialize(new { receipt.Status, receipt.Reason });
            check(receipt.Status == "completed", $"Window effect: {what} completed" + (receipt.Status == "completed" ? "" : ": " + seen));
            bool one = receipt.Effects is { Count: 1 } && receipt.Effects[0].Window == window.Handle;
            check(one, $"Window effect: {what} reports exactly one effect, on the fixture window" + (one ? "" : ": " + seen));
            // Never only "Input delivered": the reason names the window and what it did.
            bool said = receipt.Reason != Plain && receipt.Reason.Contains("window \"" + title + "\"", StringComparison.Ordinal);
            check(said, $"Window effect: {what}'s receipt says what happened to the window, not only that input went out" + (said ? "" : ": " + seen));
            WindowEffect effect = receipt.Effects![0];
            // The computer tool's JSON, from the same projection the MCP reply uses.
            using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(effect.Json(2)));
            JsonElement root = json.RootElement;
            bool shaped = root.GetProperty("before").GetProperty("width").GetInt32() == effect.Before.Width
                && root.GetProperty("after").GetProperty("x").GetInt32() == effect.After!.X
                && root.GetProperty("moved").GetBoolean() == effect.Moved
                && root.GetProperty("resized").GetBoolean() == effect.Resized
                && root.GetProperty("maximized").GetBoolean() == effect.Maximized
                && root.GetProperty("window").GetInt32() == 2 && root.GetProperty("type").GetString() == effect.Type;
            check(shaped, $"Window effect: {what}'s JSON carries window, before, after, moved, resized and maximized" + (shaped ? "" : ": " + root.GetRawText()));
            File.AppendAllText(Path.Combine(folder, "receipts.txt"), what + ": " + seen + Environment.NewLine);
            return (effect, receipt);
        }

        int left = window.X, top = window.Y, width = window.Width, height = window.Height;
        int cx = left + width / 2, cy = top + height * 3 / 4;

        // (a) A drag inside the canvas: delivered, and the window is where it was.
        WindowEffect drag = Act("an in-window drag", $$"""{"type":"drag","path":[{{Point(cx, cy)}},{{Point(cx + 60, cy + 20)}}]}""").Effect;
        check(!drag.Moved && !drag.Resized && !drag.Maximized && drag.After == drag.Before,
            "Window effect: an in-window drag reports moved:false and resized:false, with before equal to after");
        check(WaitFor(() => Lines(log).Contains("down 1")), "Window effect: the in-window drag reached the fixture (its own press log)");

        // A drag along the top strip, where a title bar would be: the window does not move, and the
        // receipt says so and names the way that does move it.
        (WindowEffect topDrag, ComputerReceipt strip) = Act("a drag along the top strip",
            $$"""{"type":"drag","path":[{{Point(left + 100, top + 8)}},{{Point(left + 220, top + 30)}}]}""");
        check(!topDrag.Moved && topDrag.NearTop && strip.Reason.Contains("did not move", StringComparison.Ordinal)
            && strip.Reason.Contains("window with action=move", StringComparison.Ordinal),
            "Window effect: a drag on the top strip that moves nothing says so and points to window action=move");

        // (b) A double-click that does nothing to the window reports false...
        WindowEffect still = Act("a double-click on the canvas", $$"""{"type":"double_click",{{At(cx, cy)}}}""").Effect;
        check(!still.Maximized && !still.Resized && !still.Moved, "Window effect: a double-click that leaves the window alone reports maximized:false and resized:false");
        check(WaitFor(() => Lines(log).Contains("down 2")), "Window effect: the canvas double-click reached the fixture as a double-click");

        // ...and one that maximizes it reports true, with the rect it ended at.
        WindowEffect zoomed = Act("a double-click that maximizes", $$"""{"type":"double_click",{{At(left + 100, top + 10)}}}""").Effect;
        check(zoomed.Maximized && zoomed.Resized && zoomed.After is { Maximized: true } && WaitFor(() => Lines(log).Contains("zoom")),
            $"Window effect: a double-click that maximizes reports maximized:true and resized:true ({zoomed.Before} -> {zoomed.After})");

        // And back: restored, not maximized.
        WindowBox now = zoomed.After!;
        WindowEffect restored = Act("a double-click that restores", $$"""{"type":"double_click",{{At(now.X + 100, Math.Max(0, now.Y) + 10)}}}""").Effect;
        check(restored.Restored && !restored.Maximized && restored.Resized && restored.After is { Maximized: false },
            $"Window effect: a double-click that restores reports restored:true and maximized:false ({restored.Before} -> {restored.After})");
    }

    static string[] Lines(string path)
    {
        try { return File.Exists(path) ? File.ReadAllLines(path) : []; }
        catch (IOException) { return []; }
    }

    static AgentWindow WaitForWindow(AgentDesktop desktop, string title)
    {
        AgentWindow? found = null;
        if (!WaitFor(() => (found = desktop.Windows().FirstOrDefault(w => w.Title == title)) is not null, TimeSpan.FromSeconds(25)))
            throw new InvalidOperationException("No window titled " + title);
        return found!;
    }

    static bool WaitFor(Func<bool> predicate, TimeSpan? bound = null)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < (bound ?? TimeSpan.FromSeconds(3))) { if (predicate()) return true; Thread.Sleep(50); }
        return predicate();
    }

    // Borderless, so the whole rect is client area and the strip's top edge is the window's top edge.
    // The canvas is added first: WinForms docks the last-added control first, so the strip takes the top.
    const string Fixture = """
param([string]$OutFile, [string]$Title)
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$form = New-Object System.Windows.Forms.Form
$form.Text = $Title
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Location = New-Object System.Drawing.Point(80, 80)
$form.Size = New-Object System.Drawing.Size(480, 360)
$canvas = New-Object System.Windows.Forms.Panel
$canvas.Dock = [System.Windows.Forms.DockStyle]::Fill
$canvas.BackColor = [System.Drawing.Color]::White
$canvas.Add_MouseDown({ param($s, $e) [System.IO.File]::AppendAllText($OutFile, "down $($e.Clicks)`n") })
$strip = New-Object System.Windows.Forms.Panel
$strip.Dock = [System.Windows.Forms.DockStyle]::Top
$strip.Height = 60
$strip.BackColor = [System.Drawing.Color]::SteelBlue
$strip.Add_MouseDown({ param($s, $e)
  if ($e.Clicks -ge 2) {
    if ($form.WindowState -eq [System.Windows.Forms.FormWindowState]::Maximized) { $form.WindowState = [System.Windows.Forms.FormWindowState]::Normal }
    else { $form.WindowState = [System.Windows.Forms.FormWindowState]::Maximized }
    [System.IO.File]::AppendAllText($OutFile, "zoom`n")
  }
})
$form.Controls.Add($canvas)
$form.Controls.Add($strip)
[System.Windows.Forms.Application]::Run($form)
""";
}
