param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.3'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$artifactRoot = Join-Path $repoRoot 'artifacts'
$publishRoot = Join-Path $repoRoot "artifacts/publish-$Version"
$desktopProject = Join-Path $repoRoot 'src/Dynex.Desktop/Dynex.Desktop.csproj'
$shaperProject = Join-Path $repoRoot 'src/Dynex.Shaper/Dynex.Shaper.csproj'
$nsisSource = Join-Path $repoRoot 'installer/DYNEX.nsi'

if ($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') { throw "Ungültige Paketversion: $Version" }
function Assert-ArtifactPath([string]$Path) {
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $rootPath = [System.IO.Path]::GetFullPath($artifactRoot).TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullPath.StartsWith($rootPath, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Sicherheitsprüfung: Pfad liegt außerhalb von artifacts: $fullPath"
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET SDK nicht gefunden.' }
if (-not (Test-Path $desktopProject) -or -not (Test-Path $shaperProject)) { throw 'DYNEX Desktop- oder Shaper-Projekt fehlt.' }

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null
$existingExe = Join-Path $publishRoot 'DYNEX.exe'
if (Test-Path -LiteralPath $existingExe) {
    # Check before cleaning any companion files: never partially replace a running build.
    $buildLock = [System.IO.File]::Open($existingExe, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    $buildLock.Dispose()
}
$desktopOut = Join-Path $publishRoot '.desktop-publish'
$shaperOut = Join-Path $publishRoot 'shaper'
$docsOut = Join-Path $publishRoot 'docs'
$cleanPaths = @($desktopOut, $shaperOut, $docsOut, (Join-Path $publishRoot 'DYNEX.exe'), (Join-Path $publishRoot 'install-files.nsh'), (Join-Path $publishRoot 'uninstall-files.nsh'))
foreach ($path in $cleanPaths) {
    Assert-ArtifactPath $path
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $desktopOut, $shaperOut, $docsOut | Out-Null

Write-Host 'Veröffentliche DYNEX Desktop (self-contained win-x64)...'
& dotnet publish $desktopProject -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $desktopOut
if ($LASTEXITCODE -ne 0) { throw "Desktop-Publish fehlgeschlagen (Exitcode $LASTEXITCODE)." }

Write-Host 'Veröffentliche DYNEX Shaper (self-contained win-x64)...'
& dotnet publish $shaperProject -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -p:Version=$Version -o $shaperOut
if ($LASTEXITCODE -ne 0) { throw "Shaper-Publish fehlgeschlagen (Exitcode $LASTEXITCODE)." }

$desktopExe = Join-Path $desktopOut 'DYNEX.exe'
$shaperExe = Join-Path $shaperOut 'Dynex.Shaper.exe'
$shaperAssembly = Join-Path $shaperOut 'Dynex.Shaper.dll'
if (-not (Test-Path $desktopExe)) { throw "Publish hat $desktopExe nicht erzeugt." }
if (-not (Test-Path $shaperExe)) { throw "Publish hat $shaperExe nicht erzeugt." }
if (-not (Test-Path $shaperAssembly)) { throw "Der erforderliche Shaper-Enginecode fehlt: $shaperAssembly" }
$forbiddenPayloads = @('WinDivert.dll', 'WinDivert64.sys', 'ndisapi.dll', 'Windows.Packet.Filter.3.6.2.1.x64.msi')
$unexpectedPayload = Get-ChildItem -LiteralPath $shaperOut -File -Recurse | Where-Object { $_.Name -in $forbiddenPayloads } | Select-Object -First 1
if ($unexpectedPayload) { throw "Der Shaper-Publish enthält einen nicht erlaubten Treiber-/MSI-Bestandteil: $($unexpectedPayload.FullName)" }
Copy-Item -LiteralPath $desktopExe -Destination (Join-Path $publishRoot 'DYNEX.exe') -Force
Remove-Item -LiteralPath $desktopOut -Recurse -Force

# The helper engine is required. DYNEX setup automatically obtains the official
# independently licensed NDISRD MSI directly from its publisher when missing;
# the installer-only CLI verifies its pinned digest and Authenticode signature.
# Never bundle that MSI, a kernel driver, WinDivert files, or native engine DLLs.

# Ship the available project documentation alongside the application.
$documentation = @('README.md', 'docs/architecture.md', 'docs/protocol.md', 'docs/shaping.md', 'docs/installation.md', 'docs/TESTING.md', 'docs/adaptive.md', 'docs/game-detection.md', 'docs/ndis-backend.md', 'docs/qos.md', 'docs/updates.md')
foreach ($relativePath in $documentation) {
    $source = Join-Path $repoRoot $relativePath
    if (Test-Path $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $docsOut (Split-Path $relativePath -Leaf)) -Force }
}
if (-not (Test-Path (Join-Path $docsOut 'installation.md'))) { throw 'Die Installationsdokumentation fehlt.' }

# Generate exact uninstaller deletions from this publish. Directories are
# removed only when empty, so files added by somebody else remain untouched.
$install = [System.Collections.Generic.List[string]]::new()
$install.Add('!macro InstallDocumentation')
$install.Add('  SetOutPath "$INSTDIR\docs"')
$install.Add('  File /r "${PUBLISH_ROOT}\docs\*"')
$install.Add('!macroend')
$installManifest = Join-Path $publishRoot 'install-files.nsh'
Assert-ArtifactPath $installManifest
Set-Content -LiteralPath $installManifest -Value $install -Encoding Ascii

$cleanup = [System.Collections.Generic.List[string]]::new()
$cleanup.Add('!macro DeleteInstalledFiles')
$cleanup.Add('  Delete "$INSTDIR\DYNEX.exe"')
foreach ($set in @(@{ Path = $shaperOut; Target = 'shaper' }, @{ Path = $docsOut; Target = 'docs' })) {
    foreach ($item in Get-ChildItem -LiteralPath $set.Path -File -Recurse) {
        $relative = $item.FullName.Substring($set.Path.Length).TrimStart([char[]]'\\')
        $cleanup.Add(('  Delete "$INSTDIR\{0}\{1}"' -f $set.Target, $relative))
    }
}
foreach ($set in @(@{ Path = $shaperOut; Target = 'shaper' }, @{ Path = $docsOut; Target = 'docs' })) {
    foreach ($directory in (Get-ChildItem -LiteralPath $set.Path -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending)) {
        $relative = $directory.FullName.Substring($set.Path.Length).TrimStart([char[]]'\\')
        $cleanup.Add(('  RMDir "$INSTDIR\{0}\{1}"' -f $set.Target, $relative))
    }
}
$cleanup.Add('  RMDir "$INSTDIR\shaper"')
$cleanup.Add('  RMDir "$INSTDIR\docs"')
$cleanup.Add('!macroend')
$uninstallManifest = Join-Path $publishRoot 'uninstall-files.nsh'
Assert-ArtifactPath $uninstallManifest
Set-Content -LiteralPath $uninstallManifest -Value $cleanup -Encoding Ascii

$nsis = Get-Command makensis.exe -ErrorAction SilentlyContinue
if (-not $nsis) {
    $cache = Join-Path $env:LOCALAPPDATA 'electron-builder/Cache'
    $nsis = Get-ChildItem -LiteralPath $cache -Filter makensis.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $nsis) { throw 'NSIS makensis.exe nicht gefunden. Es wurde nichts global installiert.' }
$nsisPath = if ($nsis -is [System.IO.FileInfo]) { $nsis.FullName } else { $nsis.Source }

$setup = Join-Path $repoRoot "artifacts/DYNEX-Setup-$Version.exe"
Write-Host "Erzeuge Installer: $setup"
& $nsisPath "/DAPP_VERSION=$Version" "/DREPO_ROOT=$repoRoot" "/DPUBLISH_ROOT=$publishRoot" "/DOUTPUT_FILE=$setup" $nsisSource
if ($LASTEXITCODE -ne 0) { throw "NSIS fehlgeschlagen (Exitcode $LASTEXITCODE)." }
if (-not (Test-Path $setup)) { throw 'NSIS meldete Erfolg, aber die Setup-EXE fehlt.' }
Write-Host "Fertig: $setup"
