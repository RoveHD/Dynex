# Fails when source code uses APIs that anti-cheat systems treat as cheating or tampering.
# DYNEX may only observe processes with PROCESS_QUERY_LIMITED_INFORMATION (0x1000) and must
# never read/write game memory, inject code, hook input/windows or draw over games.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$forbidden = @(
    'ReadProcessMemory', 'WriteProcessMemory', 'VirtualAllocEx', 'VirtualProtectEx', 'CreateRemoteThread',
    'NtCreateThreadEx', 'QueueUserAPC', 'SetWindowsHookEx', 'SetWinEventHook', 'SendInput', 'keybd_event', 'mouse_event',
    'DuplicateHandle', 'NtOpenProcess', 'DebugActiveProcess', 'LoadLibrary', 'EnumProcessModules', '\.Modules\b',
    'PROCESS_ALL_ACCESS', 'PROCESS_VM_', 'SetWindowDisplayAffinity', 'WS_EX_TOPMOST', 'Topmost\s*=\s*"?True'
)
$violations = @()
Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Include *.cs, *.xaml |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    ForEach-Object {
        $file = $_
        $lines = @(Get-Content -LiteralPath $file.FullName)
        for ($i = 0; $i -lt $lines.Count; $i++) {
            foreach ($pattern in $forbidden) {
                if ($lines[$i] -match $pattern) { $violations += "$($file.FullName):$($i + 1): $pattern" }
            }
            # OpenProcess is allowed only with the query-limited right.
            if ($lines[$i] -match 'OpenProcess\(' -and $lines[$i] -notmatch 'OpenProcess\(QueryLimitedInformation' -and $lines[$i] -notmatch 'extern') {
                $violations += "$($file.FullName):$($i + 1): OpenProcess ohne QueryLimitedInformation"
            }
            # MainModule needs VM read rights; only DYNEX' own processes may be inspected that way.
            if ($lines[$i] -match 'MainModule' -and $file.Name -ne 'InstallerPrerequisites.cs') {
                $violations += "$($file.FullName):$($i + 1): MainModule"
            }
        }
    }
if ($violations.Count -gt 0) { $violations | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
Write-Host 'Anti-Cheat-Prüfung bestanden: keine Speicherzugriffe, Injektion, Eingabe-Hooks oder Overlays.'
