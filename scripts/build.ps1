param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.25',
    # Authenticode signing. Either a certificate in the Windows certificate store
    # (thumbprint; works with hardware tokens/EV certificates) or a PFX file whose
    # password is read only from DYNEX_SIGN_PFX_PASSWORD, never from the command line.
    [string]$SignCertificateThumbprint = $env:DYNEX_SIGN_THUMBPRINT,
    [string]$SignPfxPath = $env:DYNEX_SIGN_PFX,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    # Release builds should pass this so an unsigned setup can never be published by accident.
    [switch]$RequireSignature
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

$signScript = $null
if ($SignCertificateThumbprint -or $SignPfxPath) {
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
        $signtool = Get-ChildItem -LiteralPath $kits -Filter signtool.exe -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object { $_.Directory.Parent.Name } -Descending | Select-Object -First 1
    }
    if (-not $signtool) { throw 'signtool.exe nicht gefunden (Windows SDK installieren).' }
    $signtoolPath = if ($signtool -is [System.IO.FileInfo]) { $signtool.FullName } else { $signtool.Source }
    if ($SignCertificateThumbprint) {
        if ($SignCertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Ungültiger Zertifikat-Fingerabdruck.' }
        $certificateArgs = "/sha1 $SignCertificateThumbprint"
    } else {
        if (-not (Test-Path -LiteralPath $SignPfxPath)) { throw "PFX-Datei nicht gefunden: $SignPfxPath" }
        if (-not $env:DYNEX_SIGN_PFX_PASSWORD) { throw 'DYNEX_SIGN_PFX_PASSWORD ist nicht gesetzt.' }
        $certificateArgs = '/f "' + (Resolve-Path -LiteralPath $SignPfxPath).Path + '" /p "%DYNEX_SIGN_PFX_PASSWORD%"'
    }
    # One script signs and verifies every file, including NSIS' generated uninstaller.
    # The PFX password stays an environment reference and is never written to disk.
    New-Item -ItemType Directory -Force -Path $artifactRoot | Out-Null
    $signScript = Join-Path $artifactRoot 'sign.cmd'
    Set-Content -LiteralPath $signScript -Encoding Ascii -Value @(
        '@echo off',
        ('"{0}" sign /fd SHA256 /tr "{1}" /td SHA256 /d "DYNEX" /du "https://github.com/RoveHD/Dynex" {2} "%~1" || exit /b 1' -f $signtoolPath, $TimestampUrl, $certificateArgs),
        ('"{0}" verify /pa /q "%~1" || exit /b 1' -f $signtoolPath)
    )
} elseif ($RequireSignature) {
    throw 'Signierung verlangt, aber weder -SignCertificateThumbprint noch -SignPfxPath (bzw. DYNEX_SIGN_THUMBPRINT/DYNEX_SIGN_PFX) gesetzt.'
} else {
    Write-Warning 'Kein Signaturzertifikat konfiguriert: App und Setup bleiben unsigniert (Windows SmartScreen warnt).'
}
function Invoke-Sign([string]$Path) {
    if (-not $signScript) { return }
    & $signScript $Path
    if ($LASTEXITCODE -ne 0) { throw "Signieren fehlgeschlagen: $Path" }
}
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
$payloadManifest = Join-Path $publishRoot '.helper-manifest.json'
$cleanPaths = @($desktopOut, $shaperOut, $docsOut, $payloadManifest, (Join-Path $publishRoot 'DYNEX.exe'), (Join-Path $publishRoot 'install-files.nsh'), (Join-Path $publishRoot 'uninstall-files.nsh'))
foreach ($path in $cleanPaths) {
    Assert-ArtifactPath $path
    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path $desktopOut, $shaperOut, $docsOut | Out-Null

Write-Host 'Veröffentliche DYNEX Shaper (self-contained win-x64)...'
& dotnet publish $shaperProject -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=true -p:Version=$Version -o $shaperOut
if ($LASTEXITCODE -ne 0) { throw "Shaper-Publish fehlgeschlagen (Exitcode $LASTEXITCODE)." }

$desktopExe = Join-Path $desktopOut 'DYNEX.exe'
$shaperExe = Join-Path $shaperOut 'Dynex.Shaper.exe'
$shaperAssembly = Join-Path $shaperOut 'Dynex.Shaper.dll'
if (-not (Test-Path $shaperExe)) { throw "Publish hat $shaperExe nicht erzeugt." }
if (-not (Test-Path $shaperAssembly)) { throw "Der erforderliche Shaper-Enginecode fehlt: $shaperAssembly" }
$forbiddenPayloads = @('WinDivert.dll', 'WinDivert64.sys', 'ndisapi.dll', 'Windows.Packet.Filter.3.6.2.1.x64.msi')
$unexpectedPayload = Get-ChildItem -LiteralPath $shaperOut -File -Recurse | Where-Object { $_.Name -in $forbiddenPayloads } | Select-Object -First 1
if ($unexpectedPayload) { throw "Der Shaper-Publish enthält einen nicht erlaubten Treiber-/MSI-Bestandteil: $($unexpectedPayload.FullName)" }
# Sign our own binaries before NSIS packs them. Runtime DLLs are already signed by Microsoft.
Invoke-Sign $shaperExe
Invoke-Sign $shaperAssembly
# The elevated setup never installs mutable helper files on trust. The desktop
# embeds the exact digests of the already signed self-contained helper payload.
$helperFiles = @(Get-ChildItem -LiteralPath $shaperOut -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{
        Name = [System.IO.Path]::GetRelativePath($shaperOut, $_.FullName).Replace('\', '/')
        Length = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
$manifestJson = [ordered]@{ Schema = 1; Files = $helperFiles } | ConvertTo-Json -Depth 5 -Compress
[System.IO.File]::WriteAllText($payloadManifest, $manifestJson, [System.Text.UTF8Encoding]::new($false))
Write-Host 'Veröffentliche DYNEX Desktop mit geprüftem Netzwerkhelfer (self-contained win-x64)...'
& dotnet publish $desktopProject -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishReadyToRun=true -p:Version=$Version "-p:PrioritizationPayloadManifest=$payloadManifest" -o $desktopOut
if ($LASTEXITCODE -ne 0) { throw "Desktop-Publish fehlgeschlagen (Exitcode $LASTEXITCODE)." }
if (-not (Test-Path $desktopExe)) { throw "Publish hat $desktopExe nicht erzeugt." }
Invoke-Sign $desktopExe
Copy-Item -LiteralPath $desktopExe -Destination (Join-Path $publishRoot 'DYNEX.exe') -Force
Remove-Item -LiteralPath $desktopOut -Recurse -Force

# The helper engine is required. DYNEX setup automatically obtains the official
# independently licensed NDISRD MSI directly from its publisher when missing;
# the installer-only CLI verifies its pinned digest and Authenticode signature.
# Never bundle that MSI, a kernel driver, WinDivert files, or native engine DLLs.

# Ship the available project documentation alongside the application.
$documentation = @('README.md', 'docs/architecture.md', 'docs/protocol.md', 'docs/shaping.md', 'docs/installation.md', 'docs/TESTING.md', 'docs/adaptive.md', 'docs/game-detection.md', 'docs/ndis-backend.md', 'docs/qos.md', 'docs/video-detection.md', 'docs/updates.md', 'docs/desktop-features.md', 'docs/peer-devices.md', 'docs/elfix-integration.md', 'docs/release-notes-1.0.19.md', 'docs/release-notes-1.0.20.md', 'docs/release-notes-1.0.21.md', 'docs/release-notes-1.0.22.md', 'docs/release-notes-1.0.24.md', 'docs/release-notes-1.0.25.md')
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
$cleanup.Add('  StrCpy $CleanupDeleteFailed 0')
foreach ($set in @(@{ Path = $shaperOut; Target = 'shaper' }, @{ Path = $docsOut; Target = 'docs' })) {
    foreach ($item in Get-ChildItem -LiteralPath $set.Path -File -Recurse) {
        $relative = $item.FullName.Substring($set.Path.Length).TrimStart([char[]]'\\')
        $cleanup.Add(('  Delete "$INSTDIR\{0}\{1}"' -f $set.Target, $relative))
        $cleanup.Add(('  IfFileExists "$INSTDIR\{0}\{1}" 0 +2' -f $set.Target, $relative))
        $cleanup.Add('    StrCpy $CleanupDeleteFailed 1')
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
$cleanup.Add('  ${If} $CleanupDeleteFailed != 0')
$cleanup.Add('    IfSilent +2')
$cleanup.Add('      MessageBox MB_ICONEXCLAMATION|MB_OK "Einige DYNEX-Programmdateien konnten nicht gelöscht werden. Schließe Programme, die diese Dateien geöffnet haben, und starte die Deinstallation erneut."')
$cleanup.Add('    SetErrorLevel 1')
$cleanup.Add('    Abort')
$cleanup.Add('  ${EndIf}')
# Keep the cleanup entry point until all other owned files have been removed.
$cleanup.Add('  Delete "$INSTDIR\DYNEX.exe"')
$cleanup.Add('  ${If} ${FileExists} "$INSTDIR\DYNEX.exe"')
$cleanup.Add('    IfSilent +2')
$cleanup.Add('      MessageBox MB_ICONEXCLAMATION|MB_OK "DYNEX.exe ist noch geöffnet oder geschützt. Die Deinstallation kann erneut gestartet werden."')
$cleanup.Add('    SetErrorLevel 1')
$cleanup.Add('    Abort')
$cleanup.Add('  ${EndIf}')
$cleanup.Add('!macroend')
$uninstallManifest = Join-Path $publishRoot 'uninstall-files.nsh'
Assert-ArtifactPath $uninstallManifest
Set-Content -LiteralPath $uninstallManifest -Value $cleanup -Encoding utf8BOM

$nsis = Get-Command makensis.exe -ErrorAction SilentlyContinue
if (-not $nsis) {
    $cache = Join-Path $env:LOCALAPPDATA 'electron-builder/Cache'
    $nsis = Get-ChildItem -LiteralPath $cache -Filter makensis.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
}
if (-not $nsis) {
    # Standard NSIS installation (also used by the CI runner).
    $nsis = @("${env:ProgramFiles(x86)}\NSIS\makensis.exe", "$env:ProgramFiles\NSIS\makensis.exe") |
        Where-Object { $_ -and (Test-Path -LiteralPath $_) } | ForEach-Object { Get-Item -LiteralPath $_ } | Select-Object -First 1
}
if (-not $nsis) { throw 'NSIS makensis.exe nicht gefunden. Es wurde nichts global installiert.' }
$nsisPath = if ($nsis -is [System.IO.FileInfo]) { $nsis.FullName } else { $nsis.Source }

$signDefine = if ($signScript) { @("/DSIGN_SCRIPT=$signScript") } else { @() }
$setup = Join-Path $repoRoot "artifacts/DYNEX-Setup-$Version.exe"
Write-Host "Erzeuge Installer: $setup"
& $nsisPath '/INPUTCHARSET' 'UTF8' "/DAPP_VERSION=$Version" "/DREPO_ROOT=$repoRoot" "/DPUBLISH_ROOT=$publishRoot" "/DOUTPUT_FILE=$setup" @signDefine $nsisSource
if ($LASTEXITCODE -ne 0) { throw "NSIS fehlgeschlagen (Exitcode $LASTEXITCODE)." }
if (-not (Test-Path $setup)) { throw 'NSIS meldete Erfolg, aber die Setup-EXE fehlt.' }
Write-Host "Fertig: $setup"

# Standalone recovery for older DYNEX versions that have already been removed.
# This contains only the app's dedicated cleanup entry points, no MSI or driver.
$cleanupSource = Join-Path $repoRoot 'installer/DYNEX-Cleanup.nsi'
$cleanupExe = Join-Path $repoRoot "artifacts/DYNEX-Cleanup-$Version.exe"
& $nsisPath '/INPUTCHARSET' 'UTF8' "/DAPP_VERSION=$Version" "/DPUBLISH_ROOT=$publishRoot" "/DOUTPUT_FILE=$cleanupExe" @signDefine $cleanupSource
if ($LASTEXITCODE -ne 0) { throw "Cleanup-NSIS fehlgeschlagen (Exitcode $LASTEXITCODE)." }
if (-not (Test-Path -LiteralPath $cleanupExe)) { throw 'NSIS hat kein Bereinigungsprogramm erzeugt.' }
Write-Host "Fertig: $cleanupExe"

# Release assets: the app's updater requires DYNEX-Setup-X.Y.Z.exe.sha256 (hash and exact name).
foreach ($file in @($setup, $cleanupExe)) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    $leaf = Split-Path $file -Leaf
    Set-Content -LiteralPath "$file.sha256" -Encoding Ascii -NoNewline -Value "$hash  $leaf"
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    Write-Host ("{0}: SHA-256 {1} · Signatur: {2}{3}" -f $leaf, $hash, $signature.Status, $(if ($signature.SignerCertificate) { ' · ' + $signature.SignerCertificate.Subject } else { '' }))
    if ($RequireSignature -and $signature.Status -ne 'Valid') { throw "$leaf ist nicht gültig signiert." }
}
