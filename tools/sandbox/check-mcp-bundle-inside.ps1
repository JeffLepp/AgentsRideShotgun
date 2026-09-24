$ErrorActionPreference = 'Stop'
$inputFolder = 'C:\Users\WDAGUtilityAccount\Desktop\mcp-input'
$results = 'C:\Users\WDAGUtilityAccount\Desktop\mcp-results'
$installer = Join-Path $inputFolder 'ARS-Setup.exe'
$bundle = Join-Path $inputFolder 'Deskweave.mcpb'
function Mark([string]$step) {
    try { Add-Content -LiteralPath (Join-Path $results 'progress.txt') -Value ((Get-Date).ToString('o') + ' ' + $step) }
    catch [IO.IOException] { }
}
Mark 'script started; input paths mounted'
$report = [ordered]@{
    status = 'failed'
    observedAt = (Get-Date).ToString('o')
    installerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
    bundleSha256 = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash
    checks = @()
}
try {
    if ($env:USERNAME -ne 'WDAGUtilityAccount') { throw 'This check only runs inside Windows Sandbox.' }
    $report.windows = (Get-CimInstance Win32_OperatingSystem).Caption
    $app = Join-Path $env:LOCALAPPDATA 'ARSApp\current\ARS.exe'
    if (Test-Path -LiteralPath $app) { throw 'This Windows user already has Deskweave installed.' }
    $setupLog = Join-Path $results 'setup.log'
    Mark 'starting installer'
    $process = Start-Process -FilePath $installer -ArgumentList ('--silent --log "' + $setupLog + '"') -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(300000) -or $process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
    Mark ('installer exited ' + $process.ExitCode)
    $report.checks += 'fresh per-user install'
    if (-not (Test-Path -LiteralPath $app)) { throw 'Installed ARS.exe is missing.' }
    $report.appVersion = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
    Get-Process ARS -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unpacked = Join-Path $env:TEMP 'deskweave-mcpb-check'
    Mark 'extracting MCPB'
    [IO.Compression.ZipFile]::ExtractToDirectory($bundle,$unpacked)
    Mark 'MCPB extracted'
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $unpacked 'manifest.json') | ConvertFrom-Json
    if ($manifest.server.mcp_config.command -cne '${__dirname}/server/ARS.McpConnector.exe') { throw 'MCPB executable command is not install-relative.' }
    $connector = $manifest.server.mcp_config.command.Replace('${__dirname}', $unpacked.Replace('\','/'))
    if (-not (Test-Path -LiteralPath $connector)) { throw 'MCPB command did not resolve to an executable.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $connector
    if ($signature.Status -ne 'Valid') { throw 'MCPB connector signature is invalid.' }
    $report.checks += 'signed MCPB command resolves after extraction'
    $check = Join-Path $inputFolder 'check-mcp-connector.ps1'
    Mark 'starting connector handshake'
    $stdoutPath = Join-Path $results 'mcp-stdout.txt'
    $stderrPath = Join-Path $results 'mcp-stderr.txt'
    $progressPath = Join-Path $results 'mcp-progress.txt'
    $checkProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile','-ExecutionPolicy','Bypass','-File',('"' + $check + '"'),
        '-Connector',('"' + $connector + '"'),'-ProgressPath',('"' + $progressPath + '"')
    ) -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    if (-not $checkProcess.WaitForExit(120000)) {
        $checkProcess.Kill()
        throw 'MCP handshake exceeded two minutes; inspect mcp-progress.txt.'
    }
    $checkProcess.Refresh()
    $output = Get-Content -LiteralPath $stdoutPath
    Mark ('checker exited ' + $checkProcess.ExitCode + '; stdout lines ' + @($output).Count)
    if ($null -ne $checkProcess.ExitCode -and $checkProcess.ExitCode -ne 0) {
        throw ('MCP checker exited ' + $checkProcess.ExitCode + ': ' + ((Get-Content -LiteralPath $stderrPath) | Out-String))
    }
    $handshake = ($output | Select-Object -Last 1) | ConvertFrom-Json
    if ($handshake.Initialize -ne 'passed' -or $handshake.ToolsList -ne 'passed' -or
        $handshake.StatusTool -ne 'passed' -or $handshake.Ping -ne 'passed' -or $handshake.ExitCode -ne 0) {
        throw 'MCP handshake did not complete.'
    }
    Mark ('connector handshake returned ' + $handshake.ToolCount + ' tools')
    $report.toolCount = $handshake.ToolCount
    $report.checks += 'cold MCP initialize, tools/list, status, ping, shutdown'
    if (-not (Get-Process ARS -ErrorAction SilentlyContinue)) { throw 'The connector did not start the installed app.' }
    $report.checks += 'connector starts installed app in background'
    $report.status = 'passed'
}
catch { Mark ('failed: ' + $_.Exception.Message); $report.failure = $_.Exception.Message }
finally {
    Mark ('report ' + $report.status)
    $temporaryReport = Join-Path $results 'report.json.tmp'
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $temporaryReport -Encoding UTF8
    Move-Item -LiteralPath $temporaryReport -Destination (Join-Path $results 'report.json')
}