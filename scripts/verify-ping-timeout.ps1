$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$qa = Join-Path $taskRoot ('artifacts/qa-ping-timeout-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$app = Join-Path $env:LOCALAPPDATA 'Programs/DYNEX/DYNEX.exe'
$runner = Join-Path $taskRoot 'tests/Dynex.Live.Tests/bin/Release/net9.0-windows/Dynex.Live.Tests.exe'
$wasRunning = @(Get-Process DYNEX -ErrorAction SilentlyContinue).Count -gt 0
Write-Output "Output: $qa"
try {
    $stopper = Start-Process -FilePath $app -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru
    if (-not $stopper.WaitForExit(22000) -or $stopper.ExitCode -ne 0) { throw 'Shutdown not confirmed.' }
    if (Get-Process Dynex.Shaper -ErrorAction SilentlyContinue) { throw 'Filter helper remains active.' }
    & $runner --ping-patterns (Join-Path $qa 'filter-off')
    if ($LASTEXITCODE -ne 0) { throw 'Timeout comparison failed.' }
}
finally {
    if ($wasRunning -and -not (Get-Process DYNEX -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $app -ArgumentList @('--verify-live', (Join-Path $qa 'restarted-live')) -WindowStyle Hidden | Out-Null
    }
}
& $runner --ping-patterns (Join-Path $qa 'filter-on')
if ($LASTEXITCODE -ne 0) { throw 'Timeout comparison failed.' }
