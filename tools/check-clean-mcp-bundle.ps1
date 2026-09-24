param(
    [Parameter(Mandatory = $true)][string]$Installer,
    [Parameter(Mandatory = $true)][string]$Bundle,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [ValidateRange(2,20)][int]$Minutes = 10
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'presentation-state.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputDirectory must be inside this ARS checkout.'
}
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory.' }
if (-not (Test-Path -LiteralPath $Installer -PathType Leaf) -or -not (Test-Path -LiteralPath $Bundle -PathType Leaf)) {
    throw 'The installer and MCPB must both exist.'
}
if ((Get-AuthenticodeSignature -LiteralPath $Installer).Status -ne 'Valid') { throw 'Installer signature is invalid.' }
if (-not (Test-Path "$env:windir\System32\WindowsSandbox.exe")) { throw 'Windows Sandbox is unavailable.' }
if (Get-Process WindowsSandbox,WindowsSandboxClient -ErrorAction SilentlyContinue) { throw 'A Windows Sandbox is already open.' }
function Assert-Quiet {
    $state = Get-DeskweavePresentationState
    if (-not $state.ScanComplete -or $state.Quiet) { throw 'Sandbox launch deferred while fullscreen or quiet presentation is active.' }
}
Assert-Quiet
$inputFolder = Join-Path $output 'input'
$results = Join-Path $output 'results'
New-Item -ItemType Directory -Force -Path $inputFolder,$results | Out-Null
Copy-Item -LiteralPath $Installer -Destination (Join-Path $inputFolder 'ARS-Setup.exe')
Copy-Item -LiteralPath $Bundle -Destination (Join-Path $inputFolder 'ARS.mcpb')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'check-mcp-connector.ps1') -Destination (Join-Path $inputFolder 'check-mcp-connector.ps1')
$inside = 'C:\Users\WDAGUtilityAccount\Desktop'
$xmlInput = [Security.SecurityElement]::Escape($inputFolder)
$xmlScripts = [Security.SecurityElement]::Escape((Join-Path $PSScriptRoot 'sandbox'))
$xmlResults = [Security.SecurityElement]::Escape($results)
$config = Join-Path $output 'check-mcp-bundle.wsb'
@"
<Configuration>
  <MemoryInMB>8192</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$xmlInput</HostFolder><SandboxFolder>$inside\mcp-input</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$xmlScripts</HostFolder><SandboxFolder>$inside\mcp-scripts</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$xmlResults</HostFolder><SandboxFolder>$inside\mcp-results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File $inside\mcp-scripts\check-mcp-bundle-inside.ps1</Command></LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $config -Encoding UTF8
Assert-Quiet
Start-Process -FilePath $config -WindowStyle Hidden
$report = Join-Path $results 'report.json'
$deadline = (Get-Date).AddMinutes($Minutes)
try {
    while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $report)) { Start-Sleep -Seconds 2 }
}
finally {
    Get-Process WindowsSandboxClient,WindowsSandbox -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch {} }
}
if (-not (Test-Path -LiteralPath $report)) { throw "No Sandbox report within $Minutes minutes. See $results." }
$result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
if ($result.installerSha256 -ne (Get-FileHash -LiteralPath $Installer -Algorithm SHA256).Hash -or
    $result.bundleSha256 -ne (Get-FileHash -LiteralPath $Bundle -Algorithm SHA256).Hash) {
    throw 'Sandbox report does not match the input installer and MCPB.'
}
if ($result.status -ne 'passed') { throw "Sandbox MCPB check failed: $($result.failure)" }
$result | ConvertTo-Json -Depth 5