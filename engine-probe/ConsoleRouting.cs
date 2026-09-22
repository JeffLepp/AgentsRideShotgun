using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using HiveMind.AgentWorkspaces;
using Microsoft.Win32;

// Run in the actual Windows 11 guest with Windows Terminal still selected as its default terminal.
// Creates only an alternate desktop; never switches desktop, changes terminal settings or focuses UI.
internal static class ConsoleRouting
{
    internal static int Run(string output)
    {
        Directory.CreateDirectory(output);
        string nonce = Guid.NewGuid().ToString("N");
        string script = Path.Combine(output, "console-fixture.ps1");
        string result = Path.Combine(output, "parent.json");
        string childResult = Path.Combine(output, "child.json");
        string release = Path.Combine(output, "release.txt");
        File.WriteAllText(script, Fixture);
        var checks = new List<string>();
        string? failure = null;
        object? parentEvidence = null, childEvidence = null, bootEvidence = null;
        object? parentStartEvidence = null, parentError = null, childError = null;
        object? processEvidence = null, windowsAtFailure = null;
        string? parentLog = null, childLog = null;
        var diagnostics = new List<string>();
        AgentDesktop? desktop = null;
        WorkspaceRuntime? runtime = null;
        Process? process = null;
        IDisposable? store = null;
        var elapsed = Stopwatch.StartNew();
        string terminalBefore = TerminalChoice();
        string terminalAfter = terminalBefore;
        using var ownerWindows = new OwnerConsoleWatch();
        using var timeout = new Timer(_ =>
        {
            File.WriteAllText(Path.Combine(output, "timeout.json"), JsonSerializer.Serialize(new
                { observedAt = DateTimeOffset.UtcNow, stage = "console probe watchdog", seconds = 150 }));
            Environment.Exit(2);
        }, null, TimeSpan.FromSeconds(150), Timeout.InfiniteTimeSpan);
        try
        {
            string powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            Check(AgentDesktop.TryConsoleImage(powershell, out _, out bool isConsole) && isConsole,
                "PowerShell is classified from its PE console subsystem");
            Check(AgentDesktop.TryConsoleImage(Environment.ProcessPath!, out _, out bool gui) && !gui,
                "The GUI probe does not receive console startup flags");
            Check(!AgentDesktop.TryConsoleImage(script, out _, out _),
                "A non-PE input cannot take an unchecked launch path");
            store = WorkspaceStore.UseRootForTests(Path.Combine(output, "store-" + nonce[..8]));
            desktop = AgentDesktop.Create("ConsoleProbe-" + nonce[..8]);
            int pid = desktop.Launch(powershell, "-NoLogo -NoProfile -ExecutionPolicy Bypass -File "
                + Arg(script) + " parent " + Arg(result) + " " + Arg(release));
            Check(pid > 0, "Console fixture launched");
            process = Process.GetProcessById(pid);
            _ = process.Handle;
            Wait(() => Complete(result) && Complete(childResult) || process.HasExited
                || File.Exists(Path.Combine(output, "parent-error.json"))
                || File.Exists(Path.Combine(output, "child-error.json")), milliseconds: 75000);
            Check(Complete(result) && Complete(childResult),
                "Parent and inherited console child wrote independent native receipts within 75 seconds"
                + (process.HasExited ? "; shell exited " + process.ExitCode : "; shell still running"));
            using var parent = JsonDocument.Parse(File.ReadAllText(result));
            using var child = JsonDocument.Parse(File.ReadAllText(childResult));
            parentEvidence = parent.RootElement.Clone();
            childEvidence = child.RootElement.Clone();
            JsonElement p = parent.RootElement, c = child.RootElement;
            Check(p.GetProperty("pid").GetInt32() == pid && desktop.OwnsProcess(pid),
                "Launch returns the actual shell PID owned by its workspace job");
            Check(p.GetProperty("desktop").GetString() == desktop.Name
                && c.GetProperty("desktop").GetString() == desktop.Name,
                "Parent and inherited child execute on the exact workspace desktop");
            Check(p.GetProperty("windowDesktop").GetString() == desktop.Name
                && c.GetProperty("windowDesktop").GetString() == desktop.Name
                && p.GetProperty("console").GetInt64() == c.GetProperty("console").GetInt64()
                && p.GetProperty("console").GetInt64() != 0,
                "Both console clients share an inbox console window on the workspace, not Default");
            Check(p.GetProperty("className").GetString() == "ConsoleWindowClass"
                && !p.GetProperty("minimized").GetBoolean() && p.GetProperty("visible").GetBoolean(),
                "The minimized startup console was restored as a visible workspace console");
            Check(p.GetProperty("cwd").GetString() == desktop.Folder
                && c.GetProperty("cwd").GetString() == desktop.Folder,
                "Console hosting preserves parent and child working directories");
            Check(p.GetProperty("childExit").GetInt32() == 19,
                "The inherited console child retains its real exit code");
            File.WriteAllText(release, "finish");
            Check(process.WaitForExit(5000) && process.ExitCode == 37,
                "The launched shell retains its real exit code rather than a conhost wrapper code");

            // The production constructor opens nothing of its own any more (2026-09-22): a workspace
            // starts empty, so no console can reach the owner's desktop from its start at all.
            StoredWorkspace boot = WorkspaceStore.Create("Console boot " + nonce[..8]);
            WorkspaceAccessStore.Write(boot.Id, new WorkspaceAccessPolicy(false, false) { PrewarmBrowser = false });
            runtime = WorkspaceRuntime.Start(boot);
            AgentDesktop bootDesktop = runtime.Computer!;
            Thread.Sleep(3000);
            IReadOnlyList<AgentWindow> atBoot = bootDesktop.Windows();
            Check(atBoot.All(w => w.ClassName != "ConsoleWindowClass"),
                "A starting WorkspaceRuntime opens no console window of its own");
            bootEvidence = new { runtime.Id, desktop = bootDesktop.Name, windows = atBoot.Select(w => w.Title).ToArray() };
            ownerWindows.Dispose();
            Check(ownerWindows.Escapes.Count == 0 && ownerWindows.Samples > 0,
                "Owner desktop sampling observed no new visible console or Windows Terminal window");
            terminalAfter = TerminalChoice();
            Check(terminalBefore == terminalAfter, "The user's default terminal configuration is unchanged");
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            // Collect before teardown: disposing a failed fixture first destroyed its only visible
            // evidence, while a missing parent receipt used to discard an already written child.
            Guard("parent receipt", () => parentEvidence ??= ReadJson(result));
            Guard("child receipt", () => childEvidence ??= ReadJson(childResult));
            Guard("parent startup receipt", () => parentStartEvidence = ReadJson(Path.Combine(output, "parent-start.json")));
            Guard("parent error", () => parentError = ReadJson(Path.Combine(output, "parent-error.json")));
            Guard("child error", () => childError = ReadJson(Path.Combine(output, "child-error.json")));
            Guard("parent log", () => parentLog = ReadLog(Path.Combine(output, "parent.log")));
            Guard("child log", () => childLog = ReadLog(Path.Combine(output, "child.log")));
            Guard("shell process", () =>
            {
                if (process is null) return;
                processEvidence = new { pid = process.Id, exited = process.HasExited,
                    exitCode = process.HasExited ? process.ExitCode : (int?)null };
            });
            if (failure is not null)
                Guard("workspace windows", () => windowsAtFailure = new
                    { fixture = WindowEvidence(desktop), boot = WindowEvidence(runtime?.Computer) });
            Guard("fixture screen", () => SaveScreen(desktop, Path.Combine(output, "console-fixture.png")));
            Guard("boot screen", () => SaveScreen(runtime?.Computer, Path.Combine(output, "console-boot.png")));
            Guard("terminal readback", () => terminalAfter = TerminalChoice());
            Guard("runtime cleanup", () => runtime?.Dispose(), required: true);
            Guard("desktop cleanup", () => desktop?.Dispose(), required: true);
            Guard("process handle cleanup", () => process?.Dispose(), required: true);
            Guard("store cleanup", () => store?.Dispose(), required: true);
            Guard("owner watcher cleanup", ownerWindows.Dispose, required: true);
        }
        File.WriteAllText(Path.Combine(output, "console-routing.json"), JsonSerializer.Serialize(new
        {
            status = failure is null ? "passed" : "failed",
            observedAt = DateTimeOffset.UtcNow, os = Environment.OSVersion.ToString(), checks,
            failure, terminalBefore, terminalAfter, parentEvidence, childEvidence, bootEvidence,
            parentStartEvidence, parentError, childError, processEvidence, windowsAtFailure, diagnostics,
            parentLog, childLog,
            elapsedSeconds = elapsed.Elapsed.TotalSeconds,
            boundsSeconds = new { child = 45, receipts = 75, boot = 20, watchdog = 150 },
            ownerWindowSamples = ownerWindows.Samples, ownerConsoleWindows = ownerWindows.Escapes.Values.ToArray(),
            guarantee = "Direct console launches and children inheriting their console; no owner desktop switch or registry write.",
            limitation = "Descendants that explicitly allocate a fresh console are not intercepted.",
        }, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;

        void Check(bool success, string claim)
        {
            if (!success) throw new InvalidOperationException(claim);
            checks.Add(claim);
            File.AppendAllText(Path.Combine(output, "progress.log"),
                DateTimeOffset.UtcNow.ToString("o") + " PASS " + claim + Environment.NewLine);
        }
        void Guard(string stage, Action action, bool required = false)
        {
            try { action(); }
            catch (Exception ex)
            {
                diagnostics.Add(stage + ": " + ex.Message);
                if (required) failure ??= stage + ": " + ex;
            }
        }
    }

    static string Arg(string value) => "\"" + value + "\"";
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetUserObjectInformationW(
        nint obj, int index, StringBuilder text, int bytes, out int needed);
    [DllImport("user32.dll")] static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    static bool Complete(string path)
    {
        try { using var json = JsonDocument.Parse(File.ReadAllText(path)); return true; }
        catch (Exception ex) when (ex is IOException or JsonException) { return false; }
    }
    static object? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.Clone();
    }
    static string? ReadLog(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    static object? WindowEvidence(AgentDesktop? desktop) => desktop?.Windows().Select(window => new
        { hwnd = window.Handle.ToInt64(), window.Title, window.ClassName,
            window.X, window.Y, window.Width, window.Height, window.Responding }).ToArray();
    static void SaveScreen(AgentDesktop? desktop, string path)
    {
        if (desktop?.Capture(960, 600).Image is not { } image) return;
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        png.Save(file);
    }
    static bool Wait(Func<bool> predicate, int milliseconds = 15000)
    {
        long until = Environment.TickCount64 + milliseconds;
        do { if (predicate()) return true; Thread.Sleep(50); } while (Environment.TickCount64 < until);
        return false;
    }
    static string TerminalChoice()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Console\%%Startup");
        return JsonSerializer.Serialize(new { console = key?.GetValue("DelegationConsole"), terminal = key?.GetValue("DelegationTerminal") });
    }

    sealed class OwnerConsoleWatch : IDisposable
    {
        readonly CancellationTokenSource _stop = new();
        readonly Task _worker;
        readonly nint _desktop = GetThreadDesktop(GetCurrentThreadId());
        readonly HashSet<nint> _before = [];
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<nint, string> Escapes = new();
        internal int Samples;
        public OwnerConsoleWatch()
        {
            Scan(initial: true);
            _worker = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    Scan(initial: false);
                    await Task.Delay(10).ConfigureAwait(false);
                }
            });
        }
        void Scan(bool initial)
        {
            EnumDesktopWindows(_desktop, (window, _) =>
            {
                var kind = new StringBuilder(256);
                GetClassNameW(window, kind, kind.Capacity);
                string name = kind.ToString();
                if (name is not "ConsoleWindowClass" and not "CASCADIA_HOSTING_WINDOW_CLASS") return true;
                if (initial) _before.Add(window);
                else if (!_before.Contains(window) && IsWindowVisible(window))
                    Escapes.TryAdd(window, name + " hwnd=" + window);
                return true;
            }, 0);
            Interlocked.Increment(ref Samples);
        }
        public void Dispose()
        {
            _stop.Cancel();
            _worker.Wait(TimeSpan.FromSeconds(1));
        }
        delegate bool EnumWindow(nint window, nint parameter);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern nint GetThreadDesktop(uint thread);
        [DllImport("user32.dll")] static extern bool EnumDesktopWindows(nint desktop, EnumWindow callback, nint parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint window, StringBuilder text, int size);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(nint window);
    }

    const string Fixture = """"
param([string]$Mode, [string]$Result, [string]$Release)
$ErrorActionPreference = 'Stop'
$folder = [IO.Path]::GetDirectoryName($Result)
$log = Join-Path $folder ($Mode + '.log')
$errorFile = Join-Path $folder ($Mode + '-error.json')
$stage = 'startup'
function Note([string]$text) {
 [IO.File]::AppendAllText($log, ([DateTime]::UtcNow.ToString('o') + ' PID=' + $PID + ' ' + $text + [Environment]::NewLine))
}
function WriteReceipt([string]$path, $value) {
 [IO.File]::WriteAllText($path, ($value | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
}
trap {
 try {
  WriteReceipt $errorFile @{ pid=$PID; mode=$Mode; stage=$stage; observedAt=[DateTime]::UtcNow.ToString('o'); message=$_.Exception.Message; error=($_ | Out-String); stack=$_.ScriptStackTrace }
  Note ('ERROR at ' + $stage + ': ' + $_.Exception.Message)
 } catch { }
 exit 101
}
Note 'fixture entered'
$stage = 'compile native oracle'
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class ConsoleOracle {
 [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
 [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
 [DllImport("user32.dll")] public static extern IntPtr GetThreadDesktop(uint id);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool GetUserObjectInformationW(IntPtr obj, int index, StringBuilder value, int length, out int needed);
 [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassNameW(IntPtr window, StringBuilder value, int length);
 [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr window);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
 public static string Desktop(uint thread) { var s=new StringBuilder(256); int n; GetUserObjectInformationW(GetThreadDesktop(thread), 2, s, 512, out n); return s.ToString(); }
}
'@
Note 'native oracle ready'
$stage = 'read console identity'
$window = [ConsoleOracle]::GetConsoleWindow()
[uint32]$hostPid = 0
$thread = [ConsoleOracle]::GetWindowThreadProcessId($window, [ref]$hostPid)
$kind = New-Object Text.StringBuilder 256
[void][ConsoleOracle]::GetClassNameW($window, $kind, 256)
$childExit = $null
function Receipt {
 return @{ pid=$PID; desktop=[ConsoleOracle]::Desktop([ConsoleOracle]::GetCurrentThreadId()); console=$window.ToInt64(); windowDesktop=[ConsoleOracle]::Desktop($thread); consoleHostPid=$hostPid; className=$kind.ToString(); minimized=[ConsoleOracle]::IsIconic($window); visible=[ConsoleOracle]::IsWindowVisible($window); cwd=[Environment]::CurrentDirectory; childExit=$childExit; observedAt=[DateTime]::UtcNow.ToString('o') }
}
if ($Mode -eq 'parent') {
 WriteReceipt (Join-Path $folder 'parent-start.json') (Receipt)
 $stage = 'start inherited console child'
 $start = New-Object Diagnostics.ProcessStartInfo
 $start.FileName = Join-Path $PSHOME 'powershell.exe'
 $start.Arguments = '-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" child "' + (Join-Path $folder 'child.json') + '" "' + $Release + '"'
 $start.UseShellExecute = $false
 $child = [Diagnostics.Process]::Start($start)
 Note ('child started PID=' + $child.Id)
 $stage = 'wait for inherited child (45 seconds)'
 if (-not $child.WaitForExit(45000)) { throw 'Console child did not finish within 45 seconds.' }
 $childExit = $child.ExitCode
 Note ('child exit=' + $childExit)
 $child.Dispose()
}
$stage = 'write final receipt'
WriteReceipt $Result (Receipt)
Note 'final receipt written'
if ($Mode -eq 'child') { exit 19 }
$stage = 'wait for parent release (60 seconds)'
$until = [DateTime]::UtcNow.AddSeconds(60)
while (-not (Test-Path -LiteralPath $Release)) { if ([DateTime]::UtcNow -gt $until) { throw 'Probe did not release the fixture within 60 seconds.' }; Start-Sleep -Milliseconds 50 }
Note 'probe release received; exit 37'
exit 37
"""";
}
