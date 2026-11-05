$ErrorActionPreference = 'Continue'
$log = 'E:\tmp\rk\dumps\stallwatch.log'
function Say($m) { "$(Get-Date -Format 'HH:mm:ss') $m" | Tee-Object -FilePath $log -Append }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Dmp {
    [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr h);
    [DllImport("dbghelp.dll", SetLastError=true, CharSet=CharSet.Unicode)]
    public static extern bool MiniDumpWriteDump(IntPtr hProcess, int pid, IntPtr hFile, int dumpType,
        IntPtr exceptionParam, IntPtr userParam, IntPtr callbackParam);
}
'@

Say 'conditional stall watcher armed (dumps only if the window stops responding)'

# wait for the game to appear (max 20 min)
$deadline = (Get-Date).AddMinutes(20)
$proc = $null
while ((Get-Date) -lt $deadline) {
    $proc = Get-Process VR_Kanojo -ErrorAction SilentlyContinue
    if ($proc) { break }
    Start-Sleep -Seconds 2
}
if (-not $proc) { Say 'game never started; nothing to do'; exit 0 }
$pid0 = $proc.Id
Say "game started pid=$pid0"

$marks = @(25, 60, 120)
$start = Get-Date
foreach ($t in $marks) {
    while (((Get-Date) - $start).TotalSeconds -lt $t) {
        if (-not (Get-Process -Id $pid0 -ErrorAction SilentlyContinue)) { Say "process exited before ${t}s"; exit 0 }
        Start-Sleep -Seconds 2
    }
    $p = Get-Process -Id $pid0 -ErrorAction SilentlyContinue
    if (-not $p) { Say "process gone at ${t}s"; exit 0 }
    $p.Refresh()
    Say ("+${t}s responding=" + $p.Responding + " ws=" + [int]($p.WorkingSet64/1MB) + "MB cpu=" + [math]::Round($p.TotalProcessorTime.TotalSeconds,1) + "s")
    if (-not $p.Responding) {
        $out = 'E:\tmp\rk\dumps\VR_Kanojo_stall_' + (Get-Date -Format 'HHmmss') + '_' + $pid0 + '.dmp'
        $h = [Dmp]::OpenProcess(0x1F0FFF, $false, $pid0)
        if ($h -eq [IntPtr]::Zero) { $h = [Dmp]::OpenProcess(0x0410, $false, $pid0) }
        if ($h -ne [IntPtr]::Zero) {
            $fs = [IO.File]::Create($out)
            $ok = [Dmp]::MiniDumpWriteDump($h, $pid0, $fs.SafeFileHandle.DangerousGetHandle(), 0x1129, [IntPtr]::Zero, [IntPtr]::Zero, [IntPtr]::Zero)
            $fs.Close(); [Dmp]::CloseHandle($h)
            if ($ok) { Say ("dump written " + $out) } else { Say ('MiniDumpWriteDump failed err=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
        } else { Say ('OpenProcess failed err=' + [Runtime.InteropServices.Marshal]::GetLastWin32Error()) }
    }
}
Say 'watcher done'
