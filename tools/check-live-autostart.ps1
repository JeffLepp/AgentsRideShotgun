param([Parameter(Mandatory)][string]$OutputDirectory)
# Run with PowerShell 7 while Deskweave is closed and first launch is unanswered. The published
# bridge, pointed at the real router ticket, must start the published app in the background and
# answer the agent; nothing may open on screen and nothing outside Deskweave's data may change.
# No model calls. Stops only the instance this check caused.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Validation output must be inside this Deskweave checkout.'
}
if (Get-Process Deskweave -ErrorAction SilentlyContinue) { throw 'Quit Deskweave before running this check.' }
$product = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Deskweave'
$settings = Join-Path $product 'settings.json'
if ((Test-Path $settings) -and (Get-Content -Raw $settings | ConvertFrom-Json).FirstRunDone) {
    throw 'This check requires unanswered first launch so a background start cannot write agent configuration or the Run key.'
}
function Hash([string]$path) { if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path).Hash } else { 'missing' } }
$shell = Join-Path $product 'shell.json'
$settingsBefore = Hash $settings; $shellBefore = Hash $shell
$ticket = Join-Path $product 'agent-workspaces.access\router.json'
$project = Join-Path $output 'project'
[IO.Directory]::CreateDirectory((Join-Path $project '.git')) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$report = [ordered]@{ observedAt = [DateTimeOffset]::UtcNow; modelCalls = 0; checks = $checks }
$failure = $null
function Check([bool]$ok, [string]$claim) { if (-not $ok) { throw $claim }; $checks.Add($claim); Write-Output "PASS $claim" }

function Start-Bridge {
    $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $root 'out\Bridge\Deskweave.WorkspaceBridge.exe'))
    $start.UseShellExecute = $false; $start.WorkingDirectory = $project
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--workspace'); $start.ArgumentList.Add($ticket)
    # What an agent session carries; the app it starts must not inherit these.
    $start.Environment['CLAUDE_PROJECT_DIR'] = $project
    $start.Environment['CLAUDE_CONFIG_DIR'] = Join-Path $output 'agent-claude'
    $start.Environment['CODEX_HOME'] = Join-Path $output 'agent-codex'
    [Diagnostics.Process]::Start($start)
}
function Ask($bridge, [int]$id, [string]$method, $params) {
    $bridge.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = $id; method = $method; params = $params } | ConvertTo-Json -Compress -Depth 6))
    $bridge.StandardInput.Flush()
    $read = $bridge.StandardOutput.ReadLineAsync()
    # Watch the screen while waiting, so a window or focus grab during startup is seen too.
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not $read.Wait(100)) {
        Watch-Screen
        if ([DateTime]::UtcNow -gt $deadline) { throw "No answer to $method within 20 s" }
    }
    if ($null -eq $read.Result) { throw "Bridge closed: $($bridge.StandardError.ReadToEnd())" }
    $read.Result | ConvertFrom-Json
}
# Every visible top-level window a process owns, and who has the foreground: MainWindowHandle
# sees only one window and nothing about focus.
Add-Type -Namespace Probe -Name Win -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern System.IntPtr GetForegroundWindow();
public static int Visible(uint pid) { int n = 0; EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) n++; return true; }, System.IntPtr.Zero); return n; }
public static uint Foreground() { uint p; GetWindowThreadProcessId(GetForegroundWindow(), out p); return p; }
'@
# Every Deskweave process seen with a visible window, and every process that held the foreground.
$script:shownBy = @(); $script:foreground = @(); $script:pids = @()
function Watch-Screen {
    foreach ($p in @(Get-Process Deskweave -ErrorAction SilentlyContinue)) {
        $script:pids += [uint32]$p.Id
        if ([Probe.Win]::Visible([uint32]$p.Id) -gt 0) { $script:shownBy += $p.Id }
    }
    $script:foreground += [Probe.Win]::Foreground()
}
$hello = @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'claude-code'; version = '1' } }
$app = $null
try {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $bridge = Start-Bridge
    $twin = Start-Bridge   # a second agent session starting at the same moment
    $init = Ask $bridge 1 'initialize' $hello
    $twinInit = Ask $twin 1 'initialize' $hello
    $report.coldStartSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2)
    Check ($init.result.serverInfo.name -eq 'deskweave') "With Deskweave closed, the agent's bridge starts it and initializes ($($report.coldStartSeconds) s)"
    Check ($report.coldStartSeconds -lt 10) 'The first answer arrives inside Codex''s default 10 s MCP startup timeout'
    Check ($twinInit.result.serverInfo.name -eq 'deskweave') 'A second session starting at the same moment connects too'
    $twin.StandardInput.Close()
    foreach ($i in 1..15) { Watch-Screen; Start-Sleep -Milliseconds 100 }   # keep watching while it settles
    $twin.WaitForExit(5000) | Out-Null
    $app = @(Get-Process Deskweave)
    Check ($app.Count -eq 1) 'Exactly one Deskweave is running'
    $app = $app[0]
    $command = (Get-CimInstance Win32_Process -Filter "ProcessId = $($app.Id)").CommandLine
    Check ($command -like '*--background*') 'It was started in the background, the way Windows starts it at sign-in'
    foreach ($i in 1..10) { Watch-Screen; Start-Sleep -Milliseconds 200 }
    $report.foregroundSamples = $script:foreground.Count
    Check ($script:shownBy.Count -eq 0 -and @($script:foreground | Where-Object { $script:pids -contains $_ }).Count -eq 0) 'From the first moment of startup, nothing opened on the owner''s screen and focus never moved to Deskweave'
    $status = Ask $bridge 2 'tools/call' @{ name = 'status'; arguments = @{} }
    Check ($status.result.content[0].text -like 'No workspace yet*') 'A status call answers without starting a workspace'
    $bridge.StandardInput.Close()
    Check ($bridge.WaitForExit(5000) -and $bridge.ExitCode -eq 0) 'The agent closing its end ends the bridge'
    Start-Sleep -Milliseconds 500
    Check (-not $app.HasExited) 'Deskweave keeps running after the session that started it ends'

    $clock.Restart()
    $again = Start-Bridge
    $init = Ask $again 1 'initialize' $hello
    $report.warmStartSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2)
    Check ($init.result.serverInfo.name -eq 'deskweave' -and @(Get-Process Deskweave).Count -eq 1) "A second session joins the running Deskweave without starting another ($($report.warmStartSeconds) s)"
    $again.StandardInput.Close(); $again.WaitForExit(5000) | Out-Null

    $second = Start-Process -FilePath (Join-Path $root 'out\Deskweave.exe') -ArgumentList '--background' -PassThru
    Check ($second.WaitForExit(15000)) 'A second background start exits by itself'
    Start-Sleep -Seconds 1
    $app.Refresh()
    Check ([Probe.Win]::Visible([uint32]$app.Id) -eq 0 -and [Probe.Win]::Foreground() -ne [uint32]$app.Id -and -not $app.HasExited) 'A second background start does not open the hub on the running one'
    Check ((Hash $settings) -eq $settingsBefore -and (Hash $shell) -eq $shellBefore) 'Owner settings and shell preferences are unchanged'
}
catch { $failure = $_.ToString() }
finally {
    if ($app -and -not $app.HasExited) { $app.Kill(); $app.WaitForExit(10000) | Out-Null }
    $report.validationInstanceExited = -not $app -or $app.HasExited
    $report.status = if ($failure) { 'failed' } else { 'passed' }
    $report.failure = $failure
    $report.exeSha256 = Hash (Join-Path $root 'out\Deskweave.exe')
    $report.bridgeSha256 = Hash (Join-Path $root 'out\Bridge\Deskweave.WorkspaceBridge.dll')
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'live-autostart-report.json') -Encoding utf8
}
if ($failure) { throw $failure }
