param(
    [Parameter(Mandatory=$true)][string]$Package,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'ARSApp'),
    [string]$ExpectedPackageSha256,
    [string]$OutputPath
)
# Read-only verification against what Velopack actually ships, including the two
# root-level host binaries. A publish manifest also contains excluded diagnostics.
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$install=[IO.Path]::GetFullPath($InstallRoot).TrimEnd('\','/')
$current=Join-Path $install 'current'
$packageHash=(Get-FileHash -LiteralPath $Package).Hash
if($ExpectedPackageSha256-and$packageHash-cne$ExpectedPackageSha256){throw 'Package hash differs from the reviewed release'}
$zip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Package))
$rows=[Collections.Generic.List[object]]::new()
$expectedCurrent=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
try {
    foreach($entry in $zip.Entries){
        if(-not$entry.FullName.StartsWith('lib/app/')-or-not$entry.Name){continue}
        $relative=$entry.FullName.Substring(8)
        $root=$current;$mapped=$relative
        if($relative-eq'ARS_ExecutionStub.exe'){$root=$install;$mapped='ARS.exe'}
        elseif($relative-eq'Squirrel.exe'){$root=$install;$mapped='Update.exe'}
        else{$null=$expectedCurrent.Add($relative.Replace('/','\'))}
        $path=[IO.Path]::GetFullPath((Join-Path $root $mapped))
        $prefix=[IO.Path]::GetFullPath($root).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
        if(-not$path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Package file escaped installation root'}
        $stream=$entry.Open()
        try{$expected=(Get-FileHash -InputStream $stream).Hash}finally{$stream.Dispose()}
        $actual=if(Test-Path -LiteralPath $path -PathType Leaf){(Get-FileHash -LiteralPath $path).Hash}else{$null}
        $rows.Add([pscustomobject]@{packagePath=$relative;installedPath=$path;expectedSha256=$expected;actualSha256=$actual;match=($expected-ceq$actual)})
    }
}finally{$zip.Dispose()}
$prefix=[IO.Path]::GetFullPath($current).TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar
$extra=@(Get-ChildItem -LiteralPath $current -Recurse -File|Where-Object{-not$expectedCurrent.Contains($_.FullName.Substring($prefix.Length))}|ForEach-Object{$_.FullName})
$bad=@($rows|Where-Object{-not$_.match})
$result=[pscustomobject]@{success=($rows.Count-gt0-and$bad.Count-eq0-and$extra.Count-eq0);package=[IO.Path]::GetFullPath($Package);packageSha256=$packageHash;installedVersion=(Get-Item -LiteralPath (Join-Path $current 'ARS.dll')).VersionInfo.FileVersion;packageFiles=$rows.Count;matchedFiles=($rows.Count-$bad.Count);mismatches=$bad;unexpectedCurrentFiles=$extra;files=$rows;verifiedUtc=[DateTime]::UtcNow.ToString('o')}
if($OutputPath){$result|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $OutputPath -Encoding UTF8}
if(-not$result.success){throw 'Installed payload does not match the reviewed package; inspect verification output'}
Write-Output "Verified $($rows.Count) installed package files, including the root execution stub/updater; version $($result.installedVersion)."
