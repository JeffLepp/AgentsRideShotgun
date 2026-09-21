using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HiveMind.AgentWorkspaces;

/// <summary>
/// Proves prompt 4b (design/FIX-PROMPTS-2026-09-20.md): a busy window used to make
/// WorkspaceTree.Read throw TimeoutException outright once its 3-second budget ran out, so the agent
/// got nothing at all - and combined with 4a, no way to press anything on that window either
/// (found in app testing). This launches a WPF window with sixty
/// controls, reads it once at the ordinary budget to know the true count, then reads it again with
/// WorkspaceTree's own test-only knobs turned down - so the deadline is hit deterministically instead
/// of racing a real slow window - and checks that the second read still comes back, marked partial,
/// with some but not all of the elements, plus that a name filter scopes a read down on its own.
///
/// Run: Deskweave.Probe.exe --tree-budget "C:\absolute\output"
/// Wire it with one line in Program.cs, next to --click-proof:
///   if (args.Length == 2 && args[0] == "--tree-budget" && Path.IsPathFullyQualified(args[1]))
///       return TreeBudgetProof.Run(Path.GetFullPath(args[1]));
/// </summary>
internal static class TreeBudgetProof
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        using var watchdog = new System.Threading.Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.txt"), "Tree budget proof exceeded its 60-second bound.");
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);

        string name = "tree-" + Guid.NewGuid().ToString("N")[..8];
        string title = "tree-budget-" + Guid.NewGuid().ToString("N")[..8];
        AgentDesktop? desktop = null;
        WorkspaceTree? tree = null;
        object? result = null;
        object? failure = null;
        try
        {
            desktop = AgentDesktop.Create(name);
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            string scriptPath = Path.Combine(output, "busy.ps1");
            File.WriteAllText(scriptPath, BusyScript);
            desktop.Launch(powershell,
                $"-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" \"{title}\"");

            AgentWindow? window = null;
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(25) && window is null)
            {
                window = desktop.Windows().FirstOrDefault(w => w.Title == title);
                if (window is null) Thread.Sleep(200);
            }
            if (window is null) throw new InvalidOperationException("The busy fixture window never appeared.");
            Thread.Sleep(500); // let WPF finish laying out sixty buttons before the first read

            tree = new WorkspaceTree(desktop.Name);
            IReadOnlyList<WorkspaceElement> whole = tree.Read(window.Handle, true, null, out bool wholePartial);

            WorkspaceTree.BudgetMillisecondsForTests = 150;
            WorkspaceTree.SlowNodeForTests = TimeSpan.FromMilliseconds(20);
            IReadOnlyList<WorkspaceElement> cut;
            bool cutPartial;
            try { cut = tree.Read(window.Handle, true, null, out cutPartial); }
            finally
            {
                WorkspaceTree.BudgetMillisecondsForTests = 0;
                WorkspaceTree.SlowNodeForTests = TimeSpan.Zero;
            }

            IReadOnlyList<WorkspaceElement> named = tree.Read(window.Handle, true, "button 3");

            result = new
            {
                wholeReadCount = whole.Count,
                wholeReadPartial = wholePartial,
                budgetedReadCount = cut.Count,
                budgetedReadPartial = cutPartial,
                budgetedReadSmallerThanWhole = cut.Count < whole.Count,
                budgetedReadNonEmpty = cut.Count > 0,
                nameFilterCount = named.Count,
                nameFilterSmallerThanWhole = named.Count < whole.Count,
                nameFilterExample = named.Count > 0 ? named[0].ToString() : null,
            };
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            WorkspaceTree.BudgetMillisecondsForTests = 0;
            WorkspaceTree.SlowNodeForTests = TimeSpan.Zero;
            tree?.Dispose();
            desktop?.Dispose();
        }

        // Reaching this line at all is part of the proof: the old code never got here on a busy
        // window, because Read threw before anything could be written down. failure is null only
        // when every read above, including the budgeted one, returned instead of throwing.
        File.WriteAllText(Path.Combine(output, "tree-budget.json"), JsonSerializer.Serialize(new
        {
            observedAt = DateTimeOffset.UtcNow,
            result,
            failure,
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }

    const string BusyScript = """
param([string]$Title)
Add-Type -AssemblyName PresentationFramework
$window = New-Object System.Windows.Window
$window.Title = $Title
$window.Width = 500
$window.Height = 400
$window.WindowStartupLocation = [System.Windows.WindowStartupLocation]::Manual
$window.Left = 40
$window.Top = 40
$panel = New-Object System.Windows.Controls.WrapPanel
for ($i = 0; $i -lt 60; $i++) {
    $b = New-Object System.Windows.Controls.Button
    $b.Content = "button $i"
    $b.Width = 90
    $b.Height = 28
    $b.Margin = '2'
    $panel.Children.Add($b) | Out-Null
}
$window.Content = $panel
$window.Show()
[System.Windows.Threading.Dispatcher]::Run()
""";
}
