param(
    [string]$ProcessName = "VR_Kanojo",
    [string]$OutDir = "E:\tmp\rk\dumps",
    [int]$WaitSeconds = 0
)

$ErrorActionPreference = "Stop"
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$src = @"
using System;
using System.Runtime.InteropServices;

public static class MiniDump
{
    const uint PROCESS_ALL_ACCESS = 0x1F0FFF;
    const uint PROCESS_DUMP_RIGHTS = 0x0410; // QUERY_INFORMATION | VM_READ
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1;
    const uint CREATE_ALWAYS = 2;
    const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa,
                                            uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId, IntPtr hFile,
        uint dumpType, IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);

    public static int LastOpenError;

    public static bool Write(uint pid, string path, uint dumpType, out int err)
    {
        err = 0;
        LastOpenError = 0;
        IntPtr hProc = OpenProcess(PROCESS_ALL_ACCESS, false, pid);
        if (hProc == IntPtr.Zero)
        {
            LastOpenError = Marshal.GetLastWin32Error();
            hProc = OpenProcess(PROCESS_DUMP_RIGHTS, false, pid);
        }
        if (hProc == IntPtr.Zero) { err = Marshal.GetLastWin32Error(); return false; }

        IntPtr hFile = CreateFileW(path, GENERIC_WRITE, FILE_SHARE_READ, IntPtr.Zero,
                                   CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (hFile == IntPtr.Zero || hFile == new IntPtr(-1))
        {
            err = Marshal.GetLastWin32Error();
            CloseHandle(hProc);
            return false;
        }

        bool ok = MiniDumpWriteDump(hProc, pid, hFile, dumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (!ok) err = Marshal.GetLastWin32Error();
        CloseHandle(hFile);
        CloseHandle(hProc);
        return ok;
    }
}
"@

if (-not ("MiniDump" -as [type])) { Add-Type -TypeDefinition $src -Language CSharp }

# Normal(0) | WithDataSegs(1) | WithFullMemoryInfo(8) | WithUnloadedModules(0x20)
# | WithProcessThreadData(0x100) | WithThreadInfo(0x1000)
$type = 0x1 -bor 0x8 -bor 0x20 -bor 0x100 -bor 0x1000

$proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc -and $WaitSeconds -gt 0) {
    for ($i = 0; $i -lt $WaitSeconds; $i++) {
        Start-Sleep -Seconds 1
        $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($proc) { break }
    }
}
if (-not $proc) {
    Write-Output "NO PROCESS: $ProcessName"
    exit 2
}

$stamp = Get-Date -Format "HHmmss"
$out = Join-Path $OutDir ("{0}_{1}_{2}.dmp" -f $ProcessName, $stamp, $proc.Id)

$err = 0
$ok = [MiniDump]::Write([uint32]$proc.Id, $out, [uint32]$type, [ref]$err)
if (-not $ok) {
    Write-Output ("DUMP FAILED pid={0} win32err={1} openErr={2}" -f $proc.Id, $err, [MiniDump]::LastOpenError)
    exit 3
}

$fi = Get-Item -LiteralPath $out
Write-Output ("DUMP OK pid={0} path={1} bytes={2} type=0x{3:X}" -f $proc.Id, $fi.FullName, $fi.Length, $type)
exit 0
