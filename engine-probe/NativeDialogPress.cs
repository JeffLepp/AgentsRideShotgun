using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HiveMind.AgentWorkspaces;

/// <summary>Two consecutive native MessageBoxes prove a press answers only the intended dialog.
/// Run: Deskweave.Probe.exe --native-dialog-press C:\absolute\output</summary>
internal static class NativeDialogPress
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Native dialog press exceeded its 60-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        string nonce = Guid.NewGuid().ToString("N")[..8];
        string firstTitle = "native-first-" + nonce, secondTitle = "native-second-" + nonce;
        string replies = Path.Combine(output, "replies-" + nonce + ".txt");
        string diagnostics = replies + ".log";
        var checks = new List<string>();
        var diagnosticIssues = new List<string>();
        string? failure = null;
        int launchedPid = 0;
        string processState = "not launched";
        int? exitCode = null;
        Process? fixtureProcess = null;
        object? windowsAtFailure = null;
        AgentDesktop? desktop = null;
        WorkspaceControl? control = null;
        try
        {
            desktop = AgentDesktop.Create("dialog-" + nonce);
            control = new WorkspaceControl(desktop);
            Check(control.AgentTakes(), "Agent acquired control");
            string script = Path.Combine(output, "native-dialog.ps1");
            File.WriteAllText(script, Fixture);
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            launchedPid = desktop.Launch(powershell, $"-NoProfile -STA -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\" \"{firstTitle}\" \"{secondTitle}\" \"{replies}\"");
            Check(launchedPid > 0, "Fixture process launched");
            fixtureProcess = Process.GetProcessById(launchedPid);
            // GetProcessById does not open a handle. Retain it while the child is live so
            // its exit code remains available after Windows removes the process entry.
            _ = fixtureProcess.Handle;
            AgentWindow first = Window(firstTitle);
            WorkspaceElement yes = control.Elements(first.Handle).Single(e => e.Type == "Button" && e.AutomationId == "6");
            control.OwnerTakes();
            Check(!control.Press(yes.Id) && !File.Exists(replies), "Owner takeover refuses a native button press before dispatch");
            control.Release();
            Check(control.AgentTakes(), "Agent reacquired control after release");
            Check(control.Press(yes.Id), "Native Yes button press confirmed");
            AgentWindow second = Window(secondTitle);
            Check(File.ReadAllLines(replies).SequenceEqual(new[] { "Yes" }), "First press answered Yes exactly once and left the next dialog open");
            Check(!control.Press(yes.Id), "A stale control ID cannot press the next dialog");
            Check(File.ReadAllLines(replies).Length == 1, "Stale refusal did not add a second answer");
            WorkspaceElement no = control.Elements(second.Handle).Single(e => e.Type == "Button" && e.AutomationId == "7");
            Check(control.Press(no.Id), "Native No button press confirmed");
            Check(WaitFor(() => File.Exists(replies) && File.ReadAllLines(replies).Length == 2), "Second dialog completed");
            Check(File.ReadAllLines(replies).SequenceEqual(new[] { "Yes", "No" }), "Exactly the two requested button results were recorded");
            Check(WaitFor(() => !desktop.Windows().Any(w => w.Title == firstTitle || w.Title == secondTitle)), "Both native dialogs closed");
        }
        catch (Exception ex)
        {
            failure = ex.ToString();
            Guard("failure window snapshot", () =>
                windowsAtFailure = desktop?.Windows().Select(w => new { w.Title, w.ClassName, w.X, w.Y, w.Width, w.Height }).ToArray());
        }
        finally
        {
            Guard("fixture process state", () =>
            {
                if (fixtureProcess is null) return;
                fixtureProcess.Refresh();
                processState = fixtureProcess.HasExited ? "exited" : "running before fixture cleanup";
                if (fixtureProcess.HasExited) exitCode = fixtureProcess.ExitCode;
            });
            Guard("fixture process handle cleanup", () => fixtureProcess?.Dispose(), required: true);
            Guard("control cleanup", () => control?.Dispose(), required: true);
            Guard("desktop cleanup", () => desktop?.Dispose(), required: true);
        }
        string fixtureLog = "Fixture did not create a log.";
        string[] recordedReplies = [];
        Guard("fixture log read", () => { if (File.Exists(diagnostics)) fixtureLog = File.ReadAllText(diagnostics); });
        Guard("fixture replies read", () => { if (File.Exists(replies)) recordedReplies = File.ReadAllLines(replies); });
        File.WriteAllText(Path.Combine(output, "native-dialog-press.json"), JsonSerializer.Serialize(new
        {
            observedAt = DateTimeOffset.UtcNow, checks, failure, launchedPid, processState, exitCode, windowsAtFailure,
            diagnosticIssues, fixtureLog, replies = recordedReplies,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Guard(string stage, Action action, bool required = false)
        {
            try { action(); }
            catch (Exception ex)
            {
                diagnosticIssues.Add(stage + ": " + ex);
                if (required) failure ??= stage + ": " + ex;
            }
        }

        void Check(bool passed, string description)
        {
            if (!passed) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        AgentWindow Window(string title)
        {
            AgentWindow? found = null;
            if (!WaitFor(() => (found = desktop!.Windows().FirstOrDefault(w => w.Title == title)) is not null))
                throw new InvalidOperationException("Dialog did not appear: " + title);
            return found!;
        }
    }

    static bool WaitFor(Func<bool> ready)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            if (ready()) return true;
            Thread.Sleep(100);
        }
        return ready();
    }

    const string Fixture = """
param([string]$FirstTitle, [string]$SecondTitle, [string]$Replies)
$ErrorActionPreference = 'Stop'
$diagnostics = $Replies + '.log'
[System.IO.File]::WriteAllText($diagnostics, "Fixture started: PID $PID`n")
trap {
    [System.IO.File]::AppendAllText($diagnostics, "ERROR: $($_ | Out-String)`n")
    exit 1
}
Add-Type -AssemblyName PresentationFramework
$window = New-Object System.Windows.Window
$window.Title = 'Native dialog press fixture'
$window.Width = 420
$window.Height = 260
$window.WindowStartupLocation = 'CenterScreen'
# ContentRendered is not raised for an empty Window.
$window.Content = 'Native dialog press fixture'
$window.Add_ContentRendered({
    [System.IO.File]::AppendAllText($diagnostics, "Opening first dialog: $FirstTitle`n")
    $answer = [System.Windows.MessageBox]::Show($window, 'Choose Yes.', $FirstTitle, 'YesNo', 'Question')
    [System.IO.File]::AppendAllText($Replies, "$answer`n")
    [System.IO.File]::AppendAllText($diagnostics, "Opening second dialog: $SecondTitle`n")
    $answer = [System.Windows.MessageBox]::Show($window, 'Choose No.', $SecondTitle, 'YesNo', 'Question')
    [System.IO.File]::AppendAllText($Replies, "$answer`n")
    [System.IO.File]::AppendAllText($diagnostics, "Both dialogs answered.`n")
    $window.Close()
})
$window.ShowDialog() | Out-Null
""";
}
