param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ReleaseTag = '',
    [switch]$VerifyPublicDownload,
    [switch]$OfflineValidation
)
$ErrorActionPreference = 'Stop'
if ([bool]$VerifyPublicDownload -eq [bool]$OfflineValidation) {
    throw 'Choose exactly one: -VerifyPublicDownload for release, or -OfflineValidation for private checks.'
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Use the exact MCP bundle semantic version.' }
if (-not $ReleaseTag) { $ReleaseTag = "v$Version" }
if ($ReleaseTag -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') { throw 'Invalid GitHub release tag.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$folder = Join-Path $root ("artifacts\mcp-bundle\$Version")
$name = "ARS-MCP-win-x64-$Version.mcpb"
$bundle = Join-Path $folder $name
if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) { throw "Build the exact MCPB first: $bundle" }
$inspection = Join-Path $folder ("inspect-" + [Guid]::NewGuid().ToString('N') + '.exe')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($bundle)
try {
    $manifestEntry = $archive.GetEntry('manifest.json')
    $connectorEntry = $archive.GetEntry('server/ARS.McpConnector.exe')
    if ($null -eq $manifestEntry -or $null -eq $connectorEntry) { throw 'MCPB is missing its manifest or connector.' }
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
    if ($manifest.name -cne 'ars' -or $manifest.version -cne $Version -or
        $manifest.server.type -cne 'binary' -or $manifest.server.entry_point -cne 'server/ARS.McpConnector.exe' -or
        $manifest.server.mcp_config.command -cne '${__dirname}/server/ARS.McpConnector.exe') {
        throw 'MCPB manifest does not match the requested ARS version and connector.'
    }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($connectorEntry, $inspection)
}
finally { $archive.Dispose() }
try {
    $signature = Get-AuthenticodeSignature -LiteralPath $inspection
    if ($signature.Status -ne 'Valid' -or -not $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch '^CN=Jefferson Kline(?:,|$)') {
        throw 'The bundled connector must have a valid Jefferson Kline signature.'
    }
}
finally { Remove-Item -LiteralPath $inspection -ErrorAction SilentlyContinue }
$url = "https://github.com/JeffLepp/AgentsRideShotgun/releases/download/$ReleaseTag/$name"
if ($VerifyPublicDownload) {
    $installerFolder = Join-Path $root ("artifacts\installer\" + $Version)
    $installer = Join-Path $installerFolder 'ARS-Setup.exe'
    $evidencePath = Join-Path $installerFolder 'package-evidence.json'
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf) -or
        -not (Test-Path -LiteralPath $evidencePath -PathType Leaf)) {
        throw 'The matching signed ARS installer and package evidence must exist before registry publication.'
    }
    $evidence = Get-Content -Raw -LiteralPath $evidencePath | ConvertFrom-Json
    $installerHash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    $installerSignature = Get-AuthenticodeSignature -LiteralPath $installer
    if ($evidence.version -cne $Version -or $evidence.signature -cne 'Valid' -or
        ([string]$evidence.installerSha256).ToLowerInvariant() -cne $installerHash -or
        $installerSignature.Status -ne 'Valid' -or -not $installerSignature.SignerCertificate -or
        $installerSignature.SignerCertificate.Subject -notmatch '^CN=Jefferson Kline(?:,|$)') {
        throw 'The matching installer version, evidence, hash, or signature is invalid.'
    }
}
$hash = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
if ($VerifyPublicDownload) {
    $download = Join-Path $folder ("verify-public-" + [Guid]::NewGuid().ToString('N') + '.mcpb')
    try {
        Invoke-WebRequest -Uri $url -MaximumRedirection 10 -OutFile $download
        $remoteHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remoteHash -ne $hash) { throw "Public download SHA-256 differs from the local bundle: $url" }
    }
    finally { Remove-Item -LiteralPath $download -ErrorAction SilentlyContinue }
    $installerUrl = "https://github.com/JeffLepp/AgentsRideShotgun/releases/download/$ReleaseTag/ARS-Setup.exe"
    $installerDownload = Join-Path $folder ("verify-installer-" + [Guid]::NewGuid().ToString('N') + '.exe')
    try {
        Invoke-WebRequest -Uri $installerUrl -MaximumRedirection 10 -OutFile $installerDownload
        $remoteInstallerHash = (Get-FileHash -LiteralPath $installerDownload -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remoteInstallerHash -ne $installerHash) {
            throw "Public installer SHA-256 differs from the signed local package: $installerUrl"
        }
    }
    finally { Remove-Item -LiteralPath $installerDownload -ErrorAction SilentlyContinue }
}
$metadata = [ordered]@{
    '$schema' = 'https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json'
    name = 'io.github.JeffLepp/ars'
    title = 'ARS'
    description = 'Connect agents to a second Windows desktop. Requires the ARS app on Windows x64.'
    repository = [ordered]@{ url = 'https://github.com/JeffLepp/AgentsRideShotgun'; source = 'github' }
    version = $Version
    packages = @([ordered]@{
        registryType = 'mcpb'
        identifier = $url
        fileSha256 = $hash
        transport = [ordered]@{ type = 'stdio' }
    })
}
$output = Join-Path $folder 'server.json'
$json = $metadata | ConvertTo-Json -Depth 10
if ((Test-Path -LiteralPath $output) -and ([IO.File]::ReadAllText($output).Trim() -ne $json.Trim())) {
    throw "Existing registry metadata differs: $output. Inspect before replacing a versioned release."
}
[IO.File]::WriteAllText($output,$json,[Text.UTF8Encoding]::new($false))
[pscustomobject]@{ Metadata=$output; ReleaseUrl=$url; BundleSha256=$hash; PublicDownloadChecked=[bool]$VerifyPublicDownload } | ConvertTo-Json -Compress
if ($VerifyPublicDownload) {
    Write-Output 'Public bytes matched. Next: mcp-publisher validate <server.json>; mcp-publisher login github; mcp-publisher publish <server.json>.'
}
else {
    Write-Output 'Offline validation only. No public download was checked; do not publish this metadata.'
}
