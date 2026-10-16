[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath,

    [Parameter(Mandatory = $true)]
    [ValidateSet('NegativeStartup', 'Smoke')]
    [string]$Mode,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [switch]$FeatureChecks,

    [ValidateRange(5, 900)]
    [int]$TimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$resolvedExe = (Resolve-Path -LiteralPath $ExePath).Path
if ([IO.Path]::GetExtension($resolvedExe) -ine '.exe') {
    throw 'ExePath muss auf eine .exe-Datei zeigen.'
}
if ($FeatureChecks -and $Mode -ne 'Smoke') {
    throw '-FeatureChecks kann nur mit -Mode Smoke verwendet werden.'
}
$resolvedOutput = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($resolvedOutput) | Out-Null

if (-not ('DynexQaNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

public static class DynexQaNative
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateDesktopW(string name, IntPtr device, IntPtr devmode,
        int flags, uint desiredAccess, IntPtr securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateFileW(string name, uint access, uint shareMode,
        ref SECURITY_ATTRIBUTES securityAttributes, uint creationDisposition,
        uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CreateProcessW(string applicationName, StringBuilder commandLine,
        IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags,
        IntPtr environment, string currentDirectory, ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    public static int LastError() { return Marshal.GetLastWin32Error(); }
}
'@
}

function Quote-WindowsArgument([string]$Value) {
    if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
        return $Value
    }

    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    foreach ($character in $Value.ToCharArray()) {
        if ($character -eq '\') {
            $slashes++
            continue
        }
        if ($character -eq '"') {
            [void]$builder.Append('\' * (2 * $slashes + 1))
            [void]$builder.Append('"')
            $slashes = 0
            continue
        }
        if ($slashes -gt 0) {
            [void]$builder.Append('\' * $slashes)
            $slashes = 0
        }
        [void]$builder.Append($character)
    }
    if ($slashes -gt 0) {
        [void]$builder.Append('\' * (2 * $slashes))
    }
    [void]$builder.Append('"')
    return $builder.ToString()
}

$arguments = if ($Mode -eq 'NegativeStartup') {
    @('--test-instance', 'invalid')
} else {
    @('--smoke-test', $resolvedOutput)
}
if ($FeatureChecks) { $arguments += '--features-only' }
$commandLine = (@(Quote-WindowsArgument $resolvedExe) + @($arguments | ForEach-Object { Quote-WindowsArgument $_ })) -join ' '
$desktopName = 'DYNEX-QA-' + [Guid]::NewGuid().ToString('N')
$desktopPath = 'WinSta0\' + $desktopName
$stdoutPath = Join-Path $resolvedOutput 'stdout.log'
$stderrPath = Join-Path $resolvedOutput 'stderr.log'
$summaryPath = Join-Path $resolvedOutput 'launcher-result.txt'

$desktop = [IntPtr]::Zero
$stdoutHandle = [IntPtr]::Zero
$stderrHandle = [IntPtr]::Zero
$processInfo = [DynexQaNative+PROCESS_INFORMATION]::new()
$processStarted = $false
$timedOut = $false
$exitCode = $null
$failure = $null

try {
    # Read/create/write/enumerate are sufficient for WPF. The desktop is never switched into view.
    $desktop = [DynexQaNative]::CreateDesktopW($desktopName, [IntPtr]::Zero, [IntPtr]::Zero, 0, 0x00C3, [IntPtr]::Zero)
    if ($desktop -eq [IntPtr]::Zero) {
        throw "CreateDesktop failed (Win32 $([DynexQaNative]::LastError()))."
    }

    $security = [DynexQaNative+SECURITY_ATTRIBUTES]::new()
    $security.nLength = [Runtime.InteropServices.Marshal]::SizeOf([type][DynexQaNative+SECURITY_ATTRIBUTES])
    $security.bInheritHandle = $true
    $stdoutHandle = [DynexQaNative]::CreateFileW($stdoutPath, 0x40000000, 0x00000001, [ref]$security, 2, 0x00000080, [IntPtr]::Zero)
    if ($stdoutHandle -eq [IntPtr]::new(-1)) {
        $stdoutHandle = [IntPtr]::Zero
        throw "stdout log konnte nicht angelegt werden (Win32 $([DynexQaNative]::LastError()))."
    }
    $stderrHandle = [DynexQaNative]::CreateFileW($stderrPath, 0x40000000, 0x00000001, [ref]$security, 2, 0x00000080, [IntPtr]::Zero)
    if ($stderrHandle -eq [IntPtr]::new(-1)) {
        $stderrHandle = [IntPtr]::Zero
        throw "stderr log konnte nicht angelegt werden (Win32 $([DynexQaNative]::LastError()))."
    }

    $startup = [DynexQaNative+STARTUPINFO]::new()
    $startup.cb = [Runtime.InteropServices.Marshal]::SizeOf([type][DynexQaNative+STARTUPINFO])
    $startup.lpDesktop = $desktopPath
    $startup.dwFlags = 0x00000100 # STARTF_USESTDHANDLES
    $startup.hStdInput = $stdoutHandle
    $startup.hStdOutput = $stdoutHandle
    $startup.hStdError = $stderrHandle

    $created = [DynexQaNative]::CreateProcessW($resolvedExe, [Text.StringBuilder]::new($commandLine), [IntPtr]::Zero, [IntPtr]::Zero, $true, 0, [IntPtr]::Zero, [IO.Path]::GetDirectoryName($resolvedExe), [ref]$startup, [ref]$processInfo)
    if (-not $created) {
        throw "CreateProcess failed (Win32 $([DynexQaNative]::LastError()))."
    }
    $processStarted = $true

    # The process has its own inherited copies now; close the launcher's copies before waiting.
    [void][DynexQaNative]::CloseHandle($stdoutHandle)
    [void][DynexQaNative]::CloseHandle($stderrHandle)
    $stdoutHandle = [IntPtr]::Zero
    $stderrHandle = [IntPtr]::Zero

    $wait = [DynexQaNative]::WaitForSingleObject($processInfo.hProcess, [uint32]($TimeoutSeconds * 1000))
    if ($wait -eq 0x00000102) {
        $timedOut = $true
        if (-not [DynexQaNative]::TerminateProcess($processInfo.hProcess, 124)) {
            throw "Zeitlimit erreicht; TerminateProcess für PID $($processInfo.dwProcessId) schlug fehl (Win32 $([DynexQaNative]::LastError()))."
        }
        [void][DynexQaNative]::WaitForSingleObject($processInfo.hProcess, 5000)
        $exitCode = 124
    } elseif ($wait -eq 0) {
        [uint32]$nativeExitCode = 0
        if (-not [DynexQaNative]::GetExitCodeProcess($processInfo.hProcess, [ref]$nativeExitCode)) {
            throw "GetExitCodeProcess schlug fehl (Win32 $([DynexQaNative]::LastError()))."
        }
        $exitCode = [int]$nativeExitCode
    } else {
        throw "WaitForSingleObject schlug fehl (Status 0x$('{0:X8}' -f $wait), Win32 $([DynexQaNative]::LastError()))."
    }
} catch {
    $failure = $_.Exception.Message
} finally {
    if ($stdoutHandle -ne [IntPtr]::Zero) { [void][DynexQaNative]::CloseHandle($stdoutHandle) }
    if ($stderrHandle -ne [IntPtr]::Zero) { [void][DynexQaNative]::CloseHandle($stderrHandle) }
    if ($processStarted) {
        if ($processInfo.hThread -ne [IntPtr]::Zero) { [void][DynexQaNative]::CloseHandle($processInfo.hThread) }
        if ($processInfo.hProcess -ne [IntPtr]::Zero) { [void][DynexQaNative]::CloseHandle($processInfo.hProcess) }
    }
    if ($desktop -ne [IntPtr]::Zero) { [void][DynexQaNative]::CloseDesktop($desktop) }
}

$status = if ($failure) { "ERROR: $failure" } elseif ($timedOut) { "TIMEOUT: PID $($processInfo.dwProcessId) nach ${TimeoutSeconds}s beendet; nur dieser Prozess wurde terminiert." } else { "EXIT: $exitCode (PID $($processInfo.dwProcessId))" }
@(
    "Mode: $Mode"
    "Executable: $resolvedExe"
    "Desktop: $desktopPath (nicht aktiviert)"
    "TimeoutSeconds: $TimeoutSeconds"
    $status
) | Set-Content -LiteralPath $summaryPath -Encoding UTF8

if ($failure) { throw $failure }
if ($timedOut) { exit 124 }
if ($Mode -eq 'NegativeStartup' -and $exitCode -ne 1) {
    throw "NegativeStartup erwartete Exitcode 1, erhielt $exitCode. Details: $summaryPath"
}
if ($Mode -eq 'Smoke' -and $exitCode -ne 0) {
    throw "Smoke erwartete Exitcode 0, erhielt $exitCode. Details: $summaryPath"
}

Write-Output $status
exit $exitCode
