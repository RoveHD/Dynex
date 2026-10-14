$ErrorActionPreference = 'Stop'
$taskRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$published = Join-Path $taskRoot 'artifacts/publish-0.4.0'
$installed = Join-Path $env:LOCALAPPDATA 'Programs/DYNEX'
$qa = Join-Path $taskRoot 'artifacts/qa-installed-0.4.0'
$smoke = Join-Path $taskRoot 'artifacts/qa-packaged-0.4.0/result.txt'
if (-not (Test-Path -LiteralPath $smoke) -or -not (Get-Content -Raw -LiteralPath $smoke).StartsWith('PASS:')) {
    throw 'Packaged smoke test must pass before installation.'
}
foreach ($source in (Get-Content -Raw -LiteralPath (Join-Path $taskRoot 'artifacts/qa-ui-0.4.0/source-hashes-at-package.json') | ConvertFrom-Json)) {
    if ((Get-FileHash -LiteralPath $source.Path).Hash -ne $source.Hash) { throw "Source changed during packaging: $($source.Path)" }
}
New-Item -ItemType Directory -Force -Path $qa | Out-Null
$settings = Join-Path $env:LOCALAPPDATA 'DYNEX/settings.json'
Copy-Item -LiteralPath $settings -Destination (Join-Path $qa 'settings-before.json')
$settingsHash = (Get-FileHash -LiteralPath $settings).Hash
$setup = Start-Process -FilePath (Join-Path $taskRoot 'artifacts/DYNEX-Setup-0.4.0.exe') -ArgumentList '/S' -WindowStyle Hidden -PassThru
$setup.WaitForExit()
if ($setup.ExitCode -ne 0) { throw "Setup failed: $($setup.ExitCode)" }
foreach ($file in @('DYNEX.exe', 'shaper/Dynex.Shaper.dll')) {
    if ((Get-FileHash -LiteralPath (Join-Path $published $file)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $installed $file)).Hash) {
        throw "Installed file differs from the tested package: $file"
    }
}
if ((Get-FileHash -LiteralPath $settings).Hash -ne $settingsHash) { throw 'Setup unexpectedly changed production settings.' }
'PASS: setup exit 0; installed app and helper hashes match; production settings unchanged.' | Set-Content -LiteralPath (Join-Path $qa 'installation.txt')
Start-Process -FilePath (Join-Path $installed 'DYNEX.exe') -ArgumentList @('--verify-live', $qa) -WindowStyle Hidden | Out-Null
Get-Content -LiteralPath (Join-Path $qa 'installation.txt')
