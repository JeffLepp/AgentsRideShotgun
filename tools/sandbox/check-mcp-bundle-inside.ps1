$ErrorActionPreference = 'Stop'
$inputFolder = 'C:\Users\WDAGUtilityAccount\Desktop\mcp-input'
$results = 'C:\Users\WDAGUtilityAccount\Desktop\mcp-results'
$installer = Join-Path $inputFolder 'Deskweave-Setup.exe'
$bundle = Join-Path $inputFolder 'Deskweave.mcpb'
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
    $app = Join-Path $env:LOCALAPPDATA 'DeskweaveApp\current\Deskweave.exe'
    if (Test-Path -LiteralPath $app) { throw 'This Windows user already has Deskweave installed.' }
    $setupLog = Join-Path $results 'setup.log'
    $process = Start-Process -FilePath $installer -ArgumentList ('--silent --log "' + $setupLog + '"') -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(300000) -or $process.ExitCode -ne 0) { throw "Installer failed: $($process.ExitCode)" }
    $report.checks += 'fresh per-user install'
    if (-not (Test-Path -LiteralPath $app)) { throw 'Installed Deskweave.exe is missing.' }
    $report.appVersion = (Get-Item -LiteralPath $app).VersionInfo.ProductVersion
    Get-Process Deskweave -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $unpacked = Join-Path $env:TEMP 'deskweave-mcpb-check'
    [IO.Compression.ZipFile]::ExtractToDirectory($bundle,$unpacked)
    $manifest = Get-Content -Raw -LiteralPath (Join-Path $unpacked 'manifest.json') | ConvertFrom-Json
    if ($manifest.server.mcp_config.command -cne '${__dirname}/server/Deskweave.McpConnector.exe') { throw 'MCPB executable command is not install-relative.' }
    $connector = $manifest.server.mcp_config.command.Replace('${__dirname}', $unpacked.Replace('\','/'))
    if (-not (Test-Path -LiteralPath $connector)) { throw 'MCPB command did not resolve to an executable.' }
    $signature = Get-AuthenticodeSignature -LiteralPath $connector
    if ($signature.Status -ne 'Valid') { throw 'MCPB connector signature is invalid.' }
    $report.checks += 'signed MCPB command resolves after extraction'
    $check = Join-Path $inputFolder 'check-mcp-connector.ps1'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $check -Connector $connector 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('MCP handshake failed: ' + ($output | Out-String)) }
    $handshake = ($output | Select-Object -Last 1) | ConvertFrom-Json
    if ($handshake.Initialize -ne 'passed' -or $handshake.StatusTool -ne 'passed' -or $handshake.Ping -ne 'passed') {
        throw 'MCP handshake did not complete.'
    }
    $report.toolCount = $handshake.ToolCount
    $report.checks += 'cold MCP initialize, tools/list, status, ping, shutdown'
    if (-not (Get-Process Deskweave -ErrorAction SilentlyContinue)) { throw 'The connector did not start the installed app.' }
    $report.checks += 'connector starts installed app in background'
    $report.status = 'passed'
}
catch { $report.failure = $_.Exception.Message }
finally {
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'report.json') -Encoding UTF8
}