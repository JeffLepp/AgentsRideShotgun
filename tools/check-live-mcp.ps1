param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    # The folder the agent works in. The router keeps a workspace for it, so a second run reuses one.
    [Parameter(Mandatory = $true)][string]$ProjectFolder,
    # Connect through the real `claude mcp add` and `codex mcp add`, into isolated roots under the output.
    [switch]$ProviderCli,
    # Fail unless the agent lands in a workspace that already existed before this run.
    [switch]$RequireExisting
)
# Checks the running out/Deskweave.exe the way a connected agent reaches it: the entry an agent app
# was configured with, the packaged bridge, the router ticket, and a workspace. Harmless calls only
# (initialize, tools/list, status, one screenshot, release). No model is called, the owner's own Claude
# Code and Codex configuration is fingerprinted before and after, and nothing here starts or stops
# Deskweave itself.
$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class DeskweavePipe {
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "WaitNamedPipeW")]
    static extern bool WaitNamedPipe(string name, uint timeout);
    public static bool Served(string pipe) {
        if (WaitNamedPipe(@"\\.\pipe\" + pipe, 1)) return true;
        int error = Marshal.GetLastWin32Error();
        return error != 2 && error != 123 && error != 161;
    }
}
'@
$deskweaveRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$executable = Join-Path $deskweaveRoot 'out\Deskweave.exe'
$bridge = Join-Path $deskweaveRoot 'out\Bridge\Deskweave.WorkspaceBridge.exe'
$product = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Deskweave'
$store = Join-Path $product 'agent-workspaces'
$ticket = Join-Path $product 'agent-workspaces.access\router.json'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$project = [IO.Path]::GetFullPath($ProjectFolder)
[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.Directory]::CreateDirectory($project) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$failure = $null
$report = [ordered]@{ observedAt = [DateTimeOffset]::UtcNow.ToString('o'); modelCalls = 0 }

function Check([bool]$passed, [string]$claim) {
    if (-not $passed) { throw "Failed: $claim" }
    $checks.Add($claim)
    Write-Output "PASS $claim"
}
function Hash([string]$path) { if (Test-Path -LiteralPath $path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { 'missing' } }

# Deskweave's entry, as text, in each real agent configuration this user has. Only the entry: the rest
# of those files belongs to the owner's own sessions, which rewrite their history while this runs.
function Get-OwnerEntries {
    $profile = [Environment]::GetFolderPath('UserProfile')
    $claudeFiles = @((Join-Path $profile '.claude.json'))
    foreach ($folder in @((Join-Path $profile '.claude2'), [Environment]::GetEnvironmentVariable('CLAUDE_CONFIG_DIR'))) {
        if ($folder -and (Test-Path -LiteralPath (Join-Path $folder '.claude.json'))) { $claudeFiles += (Join-Path $folder '.claude.json') }
    }
    $entries = [ordered]@{}
    foreach ($file in ($claudeFiles | Select-Object -Unique)) {
        $entries[$file] = if (Test-Path -LiteralPath $file) {
            $json = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
            if ($json['mcpServers'] -and $json['mcpServers']['deskweave']) { $json['mcpServers']['deskweave'] | ConvertTo-Json -Compress -Depth 5 } else { '' }
        } else { '' }
    }
    $codex = Join-Path $profile '.codex\config.toml'
    $entries[$codex] = if (Test-Path -LiteralPath $codex) { (Get-TomlTable (Get-Content -LiteralPath $codex) 'deskweave') -join "`n" } else { '' }
    return $entries
}
function Get-TomlTable([string[]]$lines, [string]$name) {
    $inside = $false
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed.StartsWith('[') -and $trimmed.EndsWith(']')) { $inside = $trimmed -in @("[mcp_servers.$name]", "[mcp_servers.`"$name`"]"); continue }
        if ($inside) { $line }
    }
}
function Get-TomlStrings([string]$table, [string]$key) {
    $match = [regex]::Match($table, "(?m)^[ \t]*$key[ \t]*=[ \t]*(.*)$")
    if (-not $match.Success) { return @() }
    $value = $match.Groups[1].Value
    if ($value.StartsWith('[')) { $value = [regex]::Match($table.Substring($match.Groups[1].Index), '(?s)\[.*?\]').Value }
    foreach ($token in [regex]::Matches($value, "'([^']*)'|`"((?:[^`"\\]|\\.)*)`"")) {
        if ($token.Groups[1].Success) { $token.Groups[1].Value } else { [regex]::Unescape($token.Groups[2].Value) }
    }
}
function Get-Workspaces {
    if (-not (Test-Path -LiteralPath $store)) { return @() }
    Get-ChildItem -LiteralPath $store -Directory | ForEach-Object {
        $record = Join-Path $_.FullName 'workspace.json'
        if (Test-Path -LiteralPath $record) {
            $w = Get-Content -LiteralPath $record -Raw | ConvertFrom-Json
            [pscustomobject]@{ id = $w.Id; name = $w.Name; agents = $w.Agents }
        }
    }
}
function Invoke-Tool([string]$command, [string[]]$arguments, [hashtable]$environment, [int]$seconds = 90) {
    $start = [Diagnostics.ProcessStartInfo]::new($command)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.WorkingDirectory = $output
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    foreach ($key in $environment.Keys) { $start.Environment[$key] = $environment[$key] }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($seconds * 1000)) { $process.Kill($true); throw "$command $($arguments -join ' ') did not finish in $seconds s." }
    [pscustomobject]@{ code = $process.ExitCode; output = $stdout.Result.Trim(); errors = $stderr.Result.Trim() }
}
function Find-Codex {
    $profile = [Environment]::GetFolderPath('UserProfile')
    $direct = Join-Path $profile '.local\bin\codex.exe'
    if (Test-Path -LiteralPath $direct) { return $direct }
    foreach ($root in @((Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'npm\node_modules\@openai'), (Join-Path $profile '.vscode\extensions'))) {
        if (-not (Test-Path -LiteralPath $root)) { continue }
        $found = Get-ChildItem -LiteralPath $root -Recurse -Filter codex.exe -ErrorAction SilentlyContinue |
            Where-Object FullName -like '*codex*' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

# One agent session over the bridge, exactly as the agent app would start it.
function Invoke-McpSession([string]$label, [string]$command, [string[]]$arguments, [string]$client) {
    $start = [Diagnostics.ProcessStartInfo]::new($command)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.WorkingDirectory = $project
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment.Remove('CLAUDE_PROJECT_DIR') | Out-Null
    $process = [Diagnostics.Process]::Start($start)
    $errors = $process.StandardError.ReadToEndAsync()
    $script:sequence = 0
    $steps = [Collections.Generic.List[object]]::new()
    function Request([string]$method, $parameters) {
        $id = ++$script:sequence
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $process.StandardInput.WriteLine((@{ jsonrpc = '2.0'; id = $id; method = $method; params = $parameters } | ConvertTo-Json -Compress -Depth 10))
        $process.StandardInput.Flush()
        while ($true) {
            $read = $process.StandardOutput.ReadLineAsync()
            if (-not $read.Wait(120000)) { throw "$label ${method}: no reply in 120 s." }
            if ($null -eq $read.Result) { throw "$label ${method}: the bridge closed. $($errors.Result)" }
            $reply = $read.Result | ConvertFrom-Json -Depth 20
            if ($reply.id -eq $id) { break }
        }
        $summary = [ordered]@{ method = $method; ms = $watch.ElapsedMilliseconds }
        if ($parameters.name) { $summary.tool = $parameters.name }
        if ($reply.error) { $summary.error = $reply.error.message }
        elseif ($reply.result.content) {
            $summary.isError = [bool]$reply.result.isError
            $summary.content = @($reply.result.content | ForEach-Object {
                if ($_.type -eq 'image') {
                    $bytes = [Convert]::FromBase64String($_.data)
                    $file = Join-Path $output "$label-screenshot.png"
                    [IO.File]::WriteAllBytes($file, $bytes)
                    [ordered]@{ type = 'image'; file = (Split-Path -Leaf $file); bytes = $bytes.Length; sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
                } else { [ordered]@{ type = $_.type; text = $_.text } }
            })
        }
        $steps.Add($summary)
        return $reply
    }
    try {
        $hello = Request 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = $client; version = '1' } }
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
        $tools = Request 'tools/list' @{}
        $before = Request 'tools/call' @{ name = 'status'; arguments = @{} }
        $shot = Request 'tools/call' @{ name = 'computer'; arguments = @{ screenshot = $true } }
        $after = Request 'tools/call' @{ name = 'status'; arguments = @{} }
        $release = Request 'tools/call' @{ name = 'release'; arguments = @{} }
    }
    finally {
        $process.StandardInput.Close()
        if (-not $process.WaitForExit(15000)) { $process.Kill($true) }
    }
    $state = $after.result.content[0].text | ConvertFrom-Json
    [pscustomobject]@{
        label = $label; command = $command; arguments = $arguments; client = $client; workingDirectory = $project
        exitCode = $process.ExitCode; stderr = $errors.Result.Trim(); steps = $steps
        serverName = $hello.result.serverInfo.name
        toolNames = @($tools.result.tools | ForEach-Object name)
        statusBefore = $before.result.content[0].text
        screenshot = @($shot.result.content | Where-Object type -eq 'image').Count -eq 1
        screenshotText = @($shot.result.content | Where-Object type -eq 'text')[0].text
        workspace = $state.workspace; hasControl = $state.hasControl; controller = $state.controller
        released = $release.result.content[0].text
    }
}

try {
    $owner = Get-OwnerEntries
    $running = @(Get-Process Deskweave -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $executable })
    Check ($running.Count -eq 1) 'Exactly one Deskweave is running, from this publish folder'
    $report.app = [ordered]@{ pid = $running[0].Id; started = $running[0].StartTime.ToString('o'); exeSha256 = Hash $executable
        engineSha256 = Hash (Join-Path $deskweaveRoot 'out\HiveMind.AgentWorkspaces.dll'); bridgeSha256 = Hash $bridge
        bridgeManagedSha256 = Hash ([IO.Path]::ChangeExtension($bridge, '.dll')) }
    $routerTicket = Get-Content -LiteralPath $ticket -Raw | ConvertFrom-Json
    Check ($routerTicket.schema -eq 1 -and [DeskweavePipe]::Served($routerTicket.pipe)) 'The router ticket names a pipe Deskweave is serving right now'
    $report.routerPipe = $routerTicket.pipe
    $workspacesBefore = @(Get-Workspaces)

    $entries = [Collections.Generic.List[object]]::new()
    if ($ProviderCli) {
        $claudeHome = Join-Path $output 'agents\claude'; $codexHome = Join-Path $output 'agents\codex'
        [IO.Directory]::CreateDirectory($claudeHome) | Out-Null; [IO.Directory]::CreateDirectory($codexHome) | Out-Null
        $claude = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.local\bin\claude.exe'
        $codex = Find-Codex
        $report.provider = [ordered]@{ claude = $claude; codex = $codex; claudeConfigDir = $claudeHome; codexHome = $codexHome }
        if (Test-Path -LiteralPath $claude) {
            $isolated = @{ CLAUDE_CONFIG_DIR = $claudeHome }
            $report.provider.claudeVersion = (Invoke-Tool $claude @('--version') $isolated).output
            $add = Invoke-Tool $claude @('mcp', 'add', '--scope', 'user', 'deskweave', '--', $bridge, '--workspace', $ticket) $isolated
            $list = Invoke-Tool $claude @('mcp', 'list') $isolated 120
            $report.provider.claudeAdd = $add; $report.provider.claudeList = $list
            $json = Get-Content -LiteralPath (Join-Path $claudeHome '.claude.json') -Raw | ConvertFrom-Json -AsHashtable
            $entry = $json['mcpServers']['deskweave']
            Check ($add.code -eq 0 -and $entry['command'] -eq $bridge -and ($entry['args'] -join '|') -eq "--workspace|$ticket") `
                'Claude Code, with an isolated configuration root, records the entry as the packaged bridge on the router ticket'
            Check ($list.code -eq 0 -and $list.output -match 'deskweave:.*Connected') `
                "Claude Code's own MCP health check connects to the live router through that entry"
            $entries.Add(@{ label = 'claude-code'; client = 'claude-code'; command = $entry['command']; arguments = [string[]]$entry['args'] })
        }
        if ($codex) {
            $isolated = @{ CODEX_HOME = $codexHome }
            $report.provider.codexVersion = (Invoke-Tool $codex @('--version') $isolated).output
            $add = Invoke-Tool $codex @('mcp', 'add', 'deskweave', '--', $bridge, '--workspace', $ticket) $isolated
            $report.provider.codexAdd = $add
            $table = (Get-TomlTable (Get-Content -LiteralPath (Join-Path $codexHome 'config.toml')) 'deskweave') -join "`n"
            $report.provider.codexEntry = $table
            $command = @(Get-TomlStrings $table 'command')[0]; $arguments = [string[]]@(Get-TomlStrings $table 'args')
            Check ($add.code -eq 0 -and $command -eq $bridge -and ($arguments -join '|') -eq "--workspace|$ticket") `
                'Codex, with an isolated CODEX_HOME, records the entry as the packaged bridge on the router ticket'
            $entries.Add(@{ label = 'codex'; client = 'codex-mcp-client'; command = $command; arguments = $arguments })
        }
    }
    if ($entries.Count -eq 0) { $entries.Add(@{ label = 'bridge'; client = 'deskweave-live-check'; command = $bridge; arguments = [string[]]@('--workspace', $ticket) }) }

    $sessions = [Collections.Generic.List[object]]::new()
    foreach ($entry in $entries) {
        $session = Invoke-McpSession $entry.label $entry.command $entry.arguments $entry.client
        $sessions.Add($session)
        Check ($session.serverName -eq 'deskweave' -and $session.toolNames.Count -ge 20 -and 'status' -in $session.toolNames -and 'computer' -in $session.toolNames) `
            "$($entry.label): the bridge initializes as deskweave and lists $($session.toolNames.Count) tools"
        Check ($session.screenshot -and $session.workspace) `
            "$($entry.label): a screenshot comes back from workspace '$($session.workspace)', and status names it"
        Check ($session.exitCode -eq 0) "$($entry.label): closing the agent's end ends the bridge cleanly"
    }
    $workspacesAfter = @(Get-Workspaces)
    $ids = @($sessions | ForEach-Object workspace | Select-Object -Unique)
    Check ($ids.Count -eq 1) 'Every session from the same project folder lands in the same workspace'
    $existed = $ids[0] -in @($workspacesBefore | ForEach-Object id)
    if ($RequireExisting) { Check $existed "The workspace '$($ids[0])' existed before this run and was reused, not created" }
    Check ($workspacesAfter.Count -le $workspacesBefore.Count + 1) 'No more than one workspace record was added'
    $report.sessions = $sessions
    $report.workspace = [ordered]@{ id = $ids[0]; existedBefore = $existed; before = $workspacesBefore; after = $workspacesAfter }
    $ownerAfter = Get-OwnerEntries
    Check ((($ownerAfter.Keys | ForEach-Object { "$_=$($ownerAfter[$_])" }) -join "`n") -eq (($owner.Keys | ForEach-Object { "$_=$($owner[$_])" }) -join "`n")) `
        "Deskweave's entry in the owner's own Claude Code and Codex configuration is exactly as it was"
}
catch { $failure = $_.ToString() }
$report.status = if ($failure) { 'failed' } else { 'passed' }
$report.checks = $checks
$report.failure = $failure
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output 'live-mcp-report.json') -Encoding utf8
if ($failure) { Write-Output "FAILED $failure"; exit 1 }
