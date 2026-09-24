param([Parameter(Mandatory = $true)][string]$Connector, [string]$ProgressPath = '')
$ErrorActionPreference = 'Stop'
function Mark([string]$step) {
    if ($ProgressPath) { try { Add-Content -LiteralPath $ProgressPath -Value ((Get-Date).ToString('o') + ' ' + $step) } catch [IO.IOException] { } }
}
$exe = [IO.Path]::GetFullPath($Connector)
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Connector missing: $exe" }
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $exe
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.CreateNoWindow = $true
$start.WorkingDirectory = (Get-Location).Path
Mark 'starting connector process'
$process = [Diagnostics.Process]::Start($start)
Mark ('connector process started: ' + $process.Id)
try {
    function ReadReply([int]$id) {
        for ($attempt = 0; $attempt -lt 10; $attempt++) {
            $read = $process.StandardOutput.ReadLineAsync()
            if (-not $read.Wait(15000)) { throw "MCP response $id timed out." }
            $line = $read.Result
            if (-not $line) { throw "MCP server closed stdout before response $id." }
            $message = $line | ConvertFrom-Json
            if ($message.id -eq $id) { return $message }
        }
        throw "MCP response $id did not arrive within 10 messages."
    }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"deskweave-bundle-check","version":"0.1.0"}}}')
    $process.StandardInput.Flush()
    Mark 'waiting for initialize'
    $init = ReadReply 1
    Mark 'initialize received'
    if ($null -eq $init.result -or $null -eq $init.result.capabilities) { throw 'MCP initialize returned no capabilities.' }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list"}')
    $process.StandardInput.Flush()
    Mark 'waiting for tools/list'
    $tools = ReadReply 2
    Mark 'tools/list received'
    $names = @($tools.result.tools | ForEach-Object { $_.name })
    foreach ($name in @('run','browse','computer')) { if ($name -notin $names) { throw "Missing MCP tool: $name" } }
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"status","arguments":{}}}')
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":4,"method":"ping"}')
    $process.StandardInput.Flush()
    Mark 'waiting for status'
    $status = ReadReply 3
    Mark 'status received'
    if ($null -eq $status.result -or $status.result.isError -eq $true -or @($status.result.content).Count -eq 0) {
        throw 'MCP status tool did not return usable content.'
    }
    Mark 'waiting for ping'
    $ping = ReadReply 4
    Mark 'ping received'
    if ($null -eq $ping.result) { throw 'MCP ping failed.' }
    Mark 'closing input'
    $process.StandardInput.Close()
    Mark 'waiting for connector exit'
    if (-not $process.WaitForExit(10000)) { throw 'MCP connector did not exit after stdin closed.' }
    Mark ('connector exited ' + $process.ExitCode)
    if ($process.ExitCode -ne 0) { throw "MCP connector exited $($process.ExitCode): $($process.StandardError.ReadToEnd())" }
    [pscustomobject]@{ Initialize='passed'; ToolsList='passed'; StatusTool='passed'; Ping='passed'; ToolCount=$names.Count; ExitCode=$process.ExitCode; Connector=$exe } | ConvertTo-Json -Compress
}
finally {
    if (-not $process.HasExited) { $process.Kill() }
    $process.Dispose()
}