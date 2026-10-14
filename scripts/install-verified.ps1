param([Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)
$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$published = Join-Path $taskRoot "artifacts/publish-$Version"
$installed = Join-Path $env:LOCALAPPDATA 'Programs/DYNEX'
$qa = Join-Path $taskRoot "artifacts/qa-installed-$Version"
$smoke = Join-Path $taskRoot "artifacts/qa-packaged-$Version/result.txt"
if (-not (Test-Path -LiteralPath $smoke) -or -not (Get-Content -Raw -LiteralPath $smoke).StartsWith('PASS:')) { throw 'Packaged smoke test must pass before installation.' }
foreach ($source in (Get-Content -Raw -LiteralPath (Join-Path $taskRoot "artifacts/source-hashes-$Version.json") | ConvertFrom-Json)) {
    if ((Get-FileHash -LiteralPath $source.Path).Hash -ne $source.Hash) { throw "Source changed during packaging: $($source.Path)" }
}
New-Item -ItemType Directory -Force -Path $qa | Out-Null
$settings = Join-Path $env:LOCALAPPDATA 'DYNEX/settings.json'
Copy-Item -LiteralPath $settings -Destination (Join-Path $qa 'settings-before.json')
$settingsHash = (Get-FileHash -LiteralPath $settings).Hash
$setup = Start-Process -FilePath (Join-Path $taskRoot "artifacts/DYNEX-Setup-$Version.exe") -ArgumentList '/S' -WindowStyle Hidden -PassThru
$setup.WaitForExit()
if ($setup.ExitCode -ne 0) { throw "Setup failed: $($setup.ExitCode)" }
foreach ($file in @('DYNEX.exe', 'shaper/Dynex.Shaper.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $published $file)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $installed $file)).Hash) { throw "Installed file differs from tested package: $file" }
}
if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash) { throw 'Setup unexpectedly changed production settings.' }
'PASS: setup exit 0; installed app/helper match tested package; production settings unchanged.' | Set-Content -LiteralPath (Join-Path $qa 'installation.txt')
Start-Process -FilePath (Join-Path $installed 'DYNEX.exe') -ArgumentList @('--verify-live', $qa) -WindowStyle Hidden | Out-Null
Get-Content -LiteralPath (Join-Path $qa 'installation.txt')
