param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$McpbCli = '',
    [string]$SignTool = '',
    [string]$SigningDlib = '',
    [switch]$Sign,
    [switch]$UnsignedDevelopment
)
$ErrorActionPreference = 'Stop'
if ($Sign -and $UnsignedDevelopment) { throw 'Choose either -Sign or -UnsignedDevelopment.' }
if (-not $Sign -and -not $UnsignedDevelopment) { throw 'Release bundles must use -Sign. Use -UnsignedDevelopment only for private experiments.' }
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Version must be an explicit semantic version.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$source = Join-Path $root 'mcp-bundle\Connector\Deskweave.McpConnector.csproj'
$release = Join-Path $root ("artifacts\mcp-bundle\$Version")
$bundle = Join-Path $release ("ARS-MCP-win-x64-$Version.mcpb")
if (Test-Path -LiteralPath $bundle) { throw "Bundle already exists: $bundle. Released versions are immutable." }
if (-not $McpbCli) {
    $found = Get-Command mcpb -ErrorAction SilentlyContinue
    if ($found) { $McpbCli = $found.Source }
    else {
        $local = Join-Path $root 'scratch\mcpb-tools\node_modules\.bin\mcpb.cmd'
        if (Test-Path -LiteralPath $local) { $McpbCli = $local }
    }
}
if (-not $McpbCli -or -not (Test-Path -LiteralPath $McpbCli)) {
    throw 'Install the official MCPB CLI: npm install -g @anthropic-ai/mcpb@2.1.2, or pass -McpbCli.'
}
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$work = Join-Path $release "work-$stamp"
$publish = Join-Path $work 'publish'
$stage = Join-Path $work 'stage'
$server = Join-Path $stage 'server'
New-Item -ItemType Directory -Force -Path $publish,$server | Out-Null
& dotnet publish $source -c Release -r win-x64 -o $publish
if ($LASTEXITCODE -ne 0) { throw 'MCP connector publish failed.' }
$exe = Join-Path $publish 'ARS.McpConnector.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'Self-contained MCP connector exe is missing.' }
$payloadExe = $exe
if ($Sign) {
    foreach ($name in @('AZURE_CLIENT_ID','AZURE_CLIENT_SECRET','AZURE_TENANT_ID','DESKWEAVE_SIGN_ACCOUNT','DESKWEAVE_SIGN_ENDPOINT','DESKWEAVE_SIGN_PROFILE')) {
        if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Azure signing needs $name in the environment." }
    }
    if (-not $SignTool) {
        $found = Get-Command signtool.exe -ErrorAction SilentlyContinue
        if ($found) { $SignTool = $found.Source }
    }
    if (-not $SignTool -or -not (Test-Path -LiteralPath $SignTool -PathType Leaf)) { throw 'Microsoft SignTool is required for -Sign. Pass -SignTool with an SDK Build Tools x64 signtool.exe path.' }
    if (-not $SigningDlib -or -not (Test-Path -LiteralPath $SigningDlib -PathType Leaf)) { throw 'Azure Artifact Signing dlib is required for -Sign. Pass -SigningDlib with its x64 Azure.CodeSigning.Dlib.dll path.' }
    $signMetadata = Join-Path $work 'azure-sign-metadata.json'
    $signConfig = [ordered]@{
        Endpoint = $env:DESKWEAVE_SIGN_ENDPOINT
        CodeSigningAccountName = $env:DESKWEAVE_SIGN_ACCOUNT
        CertificateProfileName = $env:DESKWEAVE_SIGN_PROFILE
        ExcludeCredentials = @('ManagedIdentityCredential','WorkloadIdentityCredential','SharedTokenCacheCredential','VisualStudioCredential','VisualStudioCodeCredential','AzureCliCredential','AzurePowerShellCredential','AzureDeveloperCliCredential','InteractiveBrowserCredential')
    }
    [IO.File]::WriteAllText($signMetadata,($signConfig | ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    try {
        # SignTool prints the private signing-account name; keep its full log in ignored artifacts.
        $signLog = Join-Path $work 'signing.log'
        & $SignTool sign /fd SHA256 /tr 'http://timestamp.acs.microsoft.com' /td SHA256 `
            /dlib $SigningDlib /dmdf $signMetadata $exe *> $signLog
        if ($LASTEXITCODE -ne 0) { throw "Microsoft SignTool failed ($LASTEXITCODE). Inspect the private signing.log in the build work folder." }
    }
    finally { Remove-Item -LiteralPath $signMetadata -ErrorAction SilentlyContinue }
    if ((Get-AuthenticodeSignature -LiteralPath $exe).Status -ne 'Valid') { throw 'MCP connector signature is not valid.' }
    $payloadExe = $exe
}
Copy-Item -LiteralPath $payloadExe -Destination (Join-Path $server 'ARS.McpConnector.exe')
Copy-Item -LiteralPath (Join-Path $root 'mcp-bundle\README.md') -Destination (Join-Path $stage 'README.md')
$manifest = [ordered]@{
    manifest_version = '0.3'
    name = 'ars'
    display_name = 'ARS'
    version = $Version
    description = 'Connect an AI agent to the installed ARS Windows desktop app.'
    long_description = 'Windows x64 only. Install ARS first. This bundle connects an MCP client to its local bridge and does not install a second app.'
    author = [ordered]@{ name = 'Jefferson Kline' }
    repository = [ordered]@{ type = 'git'; url = 'https://github.com/JeffLepp/Deskweave' }
    homepage = 'https://github.com/JeffLepp/Deskweave'
    support = 'https://github.com/JeffLepp/Deskweave/issues'
    license = 'MIT'
    compatibility = [ordered]@{ platforms = @('win32') }
    server = [ordered]@{
        type = 'binary'
        entry_point = 'server/ARS.McpConnector.exe'
        mcp_config = [ordered]@{ command = '${__dirname}/server/ARS.McpConnector.exe'; args = @() }
    }
}
$manifestPath = Join-Path $stage 'manifest.json'
[IO.File]::WriteAllText($manifestPath,($manifest | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
& $McpbCli validate $manifestPath
if ($LASTEXITCODE -ne 0) { throw 'Official MCPB manifest validation failed.' }
& $McpbCli pack $stage $bundle
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $bundle -PathType Leaf)) { throw 'Official MCPB pack failed.' }
& $McpbCli info $bundle
if ($LASTEXITCODE -ne 0) { throw 'Official MCPB could not read the packed bundle.' }
$unpacked = Join-Path $work 'unpacked'
& $McpbCli unpack $bundle $unpacked
if ($LASTEXITCODE -ne 0) { throw 'Official MCPB could not unpack the packed bundle.' }
$unpackedExe = Join-Path $unpacked 'server\ARS.McpConnector.exe'
if ((Get-FileHash -LiteralPath $unpackedExe -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $payloadExe -Algorithm SHA256).Hash) {
    throw 'Packed connector differs from the tested build.'
}
if ($Sign -and (Get-AuthenticodeSignature -LiteralPath $unpackedExe).Status -ne 'Valid') {
    throw 'Packed connector signature is not valid.'
}
[pscustomobject]@{
    Bundle = $bundle
    Sha256 = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash.ToLowerInvariant()
    Bytes = (Get-Item -LiteralPath $bundle).Length
    Version = $Version
    Signed = [bool]$Sign
} | ConvertTo-Json -Compress