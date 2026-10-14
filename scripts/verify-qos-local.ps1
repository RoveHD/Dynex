param([string]$HelperPath = '', [string[]]$Modes = @('--qos', '--live', '--watchdog'), [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $HelperPath) { $HelperPath = Join-Path $taskRoot 'src/Dynex.Shaper/bin/Release/net9.0-windows/Dynex.Shaper.exe' }
$HelperPath = (Resolve-Path -LiteralPath $HelperPath).Path
$installedApp = Join-Path $env:LOCALAPPDATA 'Programs/DYNEX/DYNEX.exe'
$runner = Join-Path $taskRoot 'tests/Dynex.Live.Tests/bin/Release/net9.0-windows/Dynex.Live.Tests.exe'
$qaRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskRoot 'artifacts/qa-native-0.4.0' }
New-Item -ItemType Directory -Force -Path $qaRoot | Out-Null
$settingsPath = Join-Path $env:LOCALAPPDATA 'DYNEX/settings.json'
Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $qaRoot 'settings-before.json')
$beforeHash = (Get-FileHash -LiteralPath $settingsPath).Hash
$wasRunning = @(Get-Process DYNEX -ErrorAction SilentlyContinue).Count -gt 0
try {
    $stopper = Start-Process -FilePath $installedApp -ArgumentList '--shutdown' -WindowStyle Hidden -PassThru
    if (-not $stopper.WaitForExit(22000) -or $stopper.ExitCode -ne 0) { throw 'DYNEX shutdown was not confirmed.' }
    if (Get-Process Dynex.Shaper -ErrorAction SilentlyContinue) { throw 'A production helper is still running; native test cancelled.' }
    foreach ($mode in $Modes) {
        $output = Join-Path $qaRoot $mode.TrimStart('-')
        & $runner $mode $HelperPath $output
        if ($LASTEXITCODE -ne 0) { throw "Native test $mode failed with exit $LASTEXITCODE." }
    }
    if ((Get-FileHash -LiteralPath $settingsPath).Hash -ne $beforeHash) { throw 'Production settings changed during native tests.' }
}
finally {
    if ($wasRunning -and -not (Get-Process DYNEX -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $installedApp -WindowStyle Hidden | Out-Null
    }
}
