param([string]$Installer = '', [Parameter(Mandatory)][string]$OutputDirectory, [int]$Minutes = 20)
# A clean-PC install test in Windows Sandbox (built into Windows Pro): a throwaway Windows with
# nothing installed, erased when it closes. The installer and the inside script are mapped
# read-only, results come back through one writable folder. The Sandbox window goes on a
# non-primary monitor when there is one. Nothing on this PC is installed or changed.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $Installer) {
    $Installer = Get-ChildItem (Join-Path $root 'artifacts\installer') -Recurse -Filter 'Deskweave-Setup.exe' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not (Test-Path -LiteralPath $Installer)) { throw "No installer: $Installer" }
if (-not (Test-Path "$env:windir\System32\WindowsSandbox.exe")) { throw 'Windows Sandbox is not enabled on this PC.' }
if (Get-Process WindowsSandboxClient -ErrorAction SilentlyContinue) { throw 'A Windows Sandbox is already open; only one can run at a time.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$setupDir = Join-Path $output 'installer'; $results = Join-Path $output 'results'
New-Item -ItemType Directory -Force $setupDir, $results | Out-Null
Copy-Item -LiteralPath $Installer -Destination (Join-Path $setupDir 'Deskweave-Setup.exe') -Force
$scripts = Join-Path $PSScriptRoot 'sandbox'
$inside = 'C:\Users\WDAGUtilityAccount\Desktop'
$config = Join-Path $output 'clean-install.wsb'
@"
<Configuration>
  <MemoryInMB>8192</MemoryInMB>
  <MappedFolders>
    <MappedFolder><HostFolder>$setupDir</HostFolder><SandboxFolder>$inside\installer</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$scripts</HostFolder><SandboxFolder>$inside\scripts</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>
    <MappedFolder><HostFolder>$results</HostFolder><SandboxFolder>$inside\results</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>
  </MappedFolders>
  <LogonCommand><Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File $inside\scripts\clean-install-inside.ps1</Command></LogonCommand>
</Configuration>
"@ | Set-Content -LiteralPath $config -Encoding UTF8

Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Host -Name Win -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetWindowPos(System.IntPtr h, System.IntPtr after, int x, int y, int w, int ht, uint flags);
'@
$other = [System.Windows.Forms.Screen]::AllScreens | Where-Object { -not $_.Primary } | Select-Object -First 1
Start-Process -FilePath $config
$report = Join-Path $results 'report.json'
$until = (Get-Date).AddMinutes($Minutes); $moved = $false
while ((Get-Date) -lt $until -and -not (Test-Path -LiteralPath $report)) {
    if (-not $moved -and $other) {
        $client = Get-Process WindowsSandboxClient -ErrorAction SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
        if ($client) {
            $w = $other.WorkingArea
            # No activation, no z-order change: it opens where the owner is not working.
            [Host.Win]::SetWindowPos($client.MainWindowHandle, [IntPtr]::Zero, $w.X + 40, $w.Y + 40, $w.Width - 80, $w.Height - 80, 0x0014) | Out-Null
            $moved = $true
        }
    }
    Start-Sleep -Seconds 2
}
Start-Sleep -Seconds 2
Get-Process WindowsSandboxClient, WindowsSandbox -ErrorAction SilentlyContinue | ForEach-Object { try { $_.Kill() } catch { } }
if (-not (Test-Path -LiteralPath $report)) { throw "No report within $Minutes minutes; see $results" }
$result = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
$result.checks | ForEach-Object { "PASS $_" }
if ($result.status -ne 'passed') { throw "Clean install failed: $($result.failure)" }
"Clean install passed. Results: $results"
