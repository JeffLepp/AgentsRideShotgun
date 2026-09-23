param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$ReleaseTag = '',
    [switch]$VerifyPublicDownload
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Use the exact MCP bundle semantic version.' }
if (-not $ReleaseTag) { $ReleaseTag = "v$Version" }
if ($ReleaseTag -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') { throw 'Invalid GitHub release tag.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$folder = Join-Path $root ("artifacts\mcp-bundle\$Version")
$name = "Deskweave-MCP-win-x64-$Version.mcpb"
$bundle = Join-Path $folder $name
if (-not (Test-Path -LiteralPath $bundle -PathType Leaf)) { throw "Build the exact MCPB first: $bundle" }
$url = "https://github.com/JeffLepp/Deskweave/releases/download/$ReleaseTag/$name"
$hash = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
if ($VerifyPublicDownload) {
    $download = Join-Path $folder ("verify-public-" + [Guid]::NewGuid().ToString('N') + '.mcpb')
    try {
        Invoke-WebRequest -Uri $url -MaximumRedirection 10 -OutFile $download
        $remoteHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($remoteHash -ne $hash) { throw "Public download SHA-256 differs from the local bundle: $url" }
    }
    finally { Remove-Item -LiteralPath $download -ErrorAction SilentlyContinue }
}
$metadata = [ordered]@{
    '$schema' = 'https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json'
    name = 'io.github.JeffLepp/deskweave'
    title = 'Deskweave'
    description = 'Connect agents to a second Windows desktop. Requires the Deskweave app on Windows x64.'
    repository = [ordered]@{ url = 'https://github.com/JeffLepp/Deskweave'; source = 'github' }
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
Write-Output 'After the release is public, run: mcp-publisher validate <server.json>; mcp-publisher login github; mcp-publisher publish <server.json>.'