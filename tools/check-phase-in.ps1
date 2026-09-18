# Phase-in check (MVP_SPEC, Behavior): real Claude Code / Codex sessions on a fixture project,
# prompts that need a screen and prompts that don't, recording whether each agent used Deskweave.
# This makes real model calls on the owner's account. Run it only with his go-ahead.
# Uses the published out/ bridge through --mcp-config (Claude) or -c overrides (Codex), so the
# owner's own agent configuration files are read for login only and never written.
param(
    [ValidateSet('claude', 'codex')][string]$Agent = 'claude',
    [string]$Model = '',
    [switch]$IncludeOwnerOpen,
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Out) { $Out = Join-Path $root ("artifacts/phase-in/{0}-{1}" -f $Agent, [DateTime]::Now.ToString('yyyyMMdd-HHmmss')) }
$bridge = Join-Path $root 'out/Bridge/Deskweave.WorkspaceBridge.exe'
$ticket = Join-Path $env:LOCALAPPDATA 'Deskweave/agent-workspaces.access/router.json'
if (-not (Test-Path -LiteralPath $bridge)) { throw "Publish first: $bridge is missing." }
New-Item -ItemType Directory -Force $Out | Out-Null

# The fixture: a repository with a web page and a small Windows app, both with one button.
$project = Join-Path $Out 'tiny-shop'
New-Item -ItemType Directory -Force (Join-Path $project '.git') | Out-Null
Set-Content -LiteralPath (Join-Path $project 'index.html') -Encoding UTF8 -Value @'
<!doctype html><html><head><meta charset="utf-8"><title>Shop</title></head>
<body><h1>Tiny shop</h1><p>Blue mug, $12</p>
<button id="add" onclick="this.textContent='Added!'">Add to cart</button></body></html>
'@
Set-Content -LiteralPath (Join-Path $project 'gui.ps1') -Encoding UTF8 -Value @'
Add-Type -AssemblyName System.Windows.Forms
$form = New-Object Windows.Forms.Form -Property @{ Text = 'Tiny shop'; Width = 360; Height = 200 }
$label = New-Object Windows.Forms.Label -Property @{ Text = 'Cart is empty'; Left = 20; Top = 20; Width = 300 }
$button = New-Object Windows.Forms.Button -Property @{ Text = 'Add to cart'; Left = 20; Top = 60; Width = 140 }
$button.Add_Click({ $label.Text = 'Cart has 1 item' })
$form.Controls.AddRange(@($label, $button))
[void]$form.ShowDialog()
'@

$cases = @(
    @{ name = 'web-test'; screen = $true; prompt = 'index.html is a small shop page. Open it in a browser, click Add to cart, and tell me what the button says afterwards.' },
    @{ name = 'gui-test'; screen = $true; prompt = 'gui.ps1 is a small Windows app. Start it, press its Add to cart button, and tell me what the label says afterwards. Close it when done.' },
    @{ name = 'edit'; screen = $false; prompt = 'Change the h1 in index.html to say "Tiny Shop". Do not test it.' },
    @{ name = 'count'; screen = $false; prompt = 'How many lines are in gui.ps1? Just answer.' }
)
if ($IncludeOwnerOpen) {
    # Opens a real page on the owner's desktop when the agent gets it right.
    $cases += @{ name = 'owner-open'; screen = $false; prompt = 'Open index.html in my own browser so I can look at it.' }
}

$results = @()
foreach ($case in $cases) {
    $log = Join-Path $Out "$($case.name).jsonl"
    Push-Location $project
    try {
        if ($Agent -eq 'claude') {
            $config = Join-Path $Out 'mcp.json'
            @{ mcpServers = @{ deskweave = @{ type = 'stdio'; command = $bridge; args = @('--workspace', $ticket) } } } |
                ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $config -Encoding UTF8
            $flags = @('-p', $case.prompt, '--mcp-config', $config, '--strict-mcp-config', '--output-format', 'stream-json',
                '--verbose', '--max-turns', '30', '--allowedTools', 'Bash,Read,Edit,Write,Glob,Grep,mcp__deskweave')
            if ($Model) { $flags += @('--model', $Model) }
            & claude @flags 2>&1 | Set-Content -LiteralPath $log -Encoding UTF8
        } else {
            $flags = @('exec', '--json', '--skip-git-repo-check', '--dangerously-bypass-approvals-and-sandbox',
                '-c', ('mcp_servers.deskweave.command="{0}"' -f $bridge.Replace('\', '\\')),
                '-c', ('mcp_servers.deskweave.args=["--workspace","{0}"]' -f $ticket.Replace('\', '\\')))
            if ($Model) { $flags += @('-m', $Model) }
            & codex @flags $case.prompt 2>&1 | Set-Content -LiteralPath $log -Encoding UTF8
        }
    } finally { Pop-Location }

    # Which tools the agent reached for, from its own event stream.
    $tools = @(); $shell = @()
    foreach ($line in Get-Content -LiteralPath $log) {
        if (-not $line.StartsWith('{')) { continue }
        try { $event = $line | ConvertFrom-Json } catch { continue }
        if ($Agent -eq 'claude' -and $event.type -eq 'assistant') {
            foreach ($part in $event.message.content) {
                if ($part.type -ne 'tool_use') { continue }
                $tools += $part.name
                if ($part.name -eq 'Bash') { $shell += $part.input.command }
            }
        } elseif ($Agent -eq 'codex' -and $event.item) {
            if ($event.item.type -eq 'mcp_tool_call') { $tools += "mcp__$($event.item.server)__$($event.item.tool)" }
            if ($event.item.type -eq 'command_execution') { $tools += 'shell'; $shell += $event.item.command }
        }
    }
    $used = @($tools | Where-Object { $_ -like 'mcp__deskweave__*' -and $_ -notlike '*__status' -and $_ -notlike '*__release' }).Count -gt 0
    $results += [pscustomobject]@{
        case = $case.name; needsScreen = $case.screen; usedDeskweave = $used; right = ($used -eq $case.screen)
        tools = ($tools | Select-Object -Unique) -join ' '; shell = $shell
    }
}
$summary = [pscustomobject]@{
    agent = $Agent; model = $Model; at = [DateTimeOffset]::Now.ToString('o'); right = @($results | Where-Object right).Count
    of = $results.Count; cases = $results
}
$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Out 'phase-in-report.json') -Encoding UTF8
$results | Format-Table case, needsScreen, usedDeskweave, right -AutoSize | Out-String -Width 200
"$($summary.right) of $($summary.of) right. Logs: $Out"
