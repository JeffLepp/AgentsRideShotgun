param([Parameter(Mandatory)][string]$ProcessName, [Parameter(Mandatory)][string]$Report, [int]$Seconds = 240)
# Records every visible top-level window a test process shows and whether any of it lands on the
# primary monitor, the one the owner works on. Run beside the UI gate: tests should use another monitor.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace Watch -Name Win -MemberDefinition @'
public delegate bool EnumProc(System.IntPtr h, System.IntPtr l);
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, System.IntPtr l);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsWindowVisible(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool IsIconic(System.IntPtr h);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(System.IntPtr h, out uint pid);
[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool GetWindowRect(System.IntPtr h, out RECT r);
public static System.Collections.Generic.List<RECT> Visible(uint pid) {
    var found = new System.Collections.Generic.List<RECT>();
    EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p);
        if (p == pid && IsWindowVisible(h) && !IsIconic(h)) { RECT r; if (GetWindowRect(h, out r) && r.Right > r.Left) found.Add(r); } return true; }, System.IntPtr.Zero);
    return found;
}
'@
$primary = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$until = [DateTime]::UtcNow.AddSeconds($Seconds)
$seen = 0; $onPrimary = [Collections.Generic.List[string]]::new(); $started = $false
while ([DateTime]::UtcNow -lt $until) {
    $process = Get-Process $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $process) { if ($started) { break } else { Start-Sleep -Milliseconds 200; continue } }
    $started = $true
    foreach ($r in [Watch.Win]::Visible([uint32]$process.Id)) {
        $seen++
        # Any overlap with the primary monitor counts, beyond a window's invisible edge (the corner
        # window's transparent 60 px shadow margin is the widest).
        $w = [Math]::Min($r.Right, $primary.Right) - [Math]::Max($r.Left, $primary.Left)
        $h = [Math]::Min($r.Bottom, $primary.Bottom) - [Math]::Max($r.Top, $primary.Top)
        if ($w -gt 64 -and $h -gt 64) { $onPrimary.Add("$($r.Left),$($r.Top),$($r.Right),$($r.Bottom)") }
    }
    Start-Sleep -Milliseconds 150
}
[ordered]@{ primary = "$primary"; windowSamples = $seen; onPrimary = @($onPrimary | Select-Object -Unique) } |
    ConvertTo-Json | Set-Content -LiteralPath $Report -Encoding utf8
