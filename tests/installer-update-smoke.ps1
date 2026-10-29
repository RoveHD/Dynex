param(
    [Parameter(Mandatory = $true)][string]$PayloadRoot,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$NsisPath,
    [string]$OutputDirectory
)

# Exercises the actual NSIS gate, rollback transaction, payload macro and
# package self-test in disposable artifact directories only.
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $artifactRoot ('installer-update-smoke-' + [Guid]::NewGuid().ToString('N')) }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Der isolierte Ausgabeordner muss unter artifacts liegen.'
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Der Ausgabeordner muss neu sein; vorhandene Dateien werden nicht überschrieben.' }
$PayloadRoot = (Resolve-Path -LiteralPath $PayloadRoot).Path
$NsisPath = (Resolve-Path -LiteralPath $NsisPath).Path
foreach ($relative in @('DYNEX.exe', 'shaper/Dynex.Shaper.exe', 'shaper/Dynex.Shaper.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadRoot $relative) -PathType Leaf)) { throw "Prüfpaket unvollständig: $relative" }
}
function Nsis-Path([string]$Path) {
    if ($Path -match '["\r\n]') { throw 'Nicht unterstütztes Zeichen im Prüfpaketpfad.' }
    return $Path.Replace('$', '$$')
}
function Read-NsisFunction([string]$Source, [string]$Name) {
    $match = [regex]::Match($Source, '(?ms)^Function ' + [regex]::Escape($Name) + '\r?\n.*?^FunctionEnd\r?$')
    if (-not $match.Success) { throw "Produktive NSIS-Funktion fehlt: $Name" }
    return $match.Value
}
function Read-NsisMacro([string]$Source, [string]$Name) {
    $match = [regex]::Match($Source, '(?ms)^!macro ' + [regex]::Escape($Name) + '\r?\n.*?^!macroend\r?$')
    if (-not $match.Success) { throw "Produktives NSIS-Makro fehlt: $Name" }
    return $match.Value
}
function Get-TreeHashes([string]$Path) {
    $result = @{}
    if (Test-Path -LiteralPath $Path -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $Path -File -Recurse) {
            $relative = $file.FullName.Substring($Path.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
            $result[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        }
    }
    return $result
}
function Assert-TreeHashes([string]$Path, [hashtable]$Expected, [string]$Context) {
    $actual = Get-TreeHashes $Path
    if ($actual.Count -ne $Expected.Count) { throw "${Context}: Dateianzahl $($actual.Count), erwartet $($Expected.Count)." }
    foreach ($key in $Expected.Keys) {
        if (-not $actual.ContainsKey($key) -or $actual[$key] -ne $Expected[$key]) { throw "${Context}: Hash oder Datei stimmt nicht: $key" }
    }
}

New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$source = Get-Content -Raw -LiteralPath (Join-Path $repoRoot 'installer/DYNEX.nsi')
$initialization = Read-NsisFunction $source '.onInit'
$initializationSteps = @($initialization -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith(';') })
if ($initializationSteps[1] -ne 'StrCpy $UpdateMutex 0' -or $initializationSteps[2] -ne 'Call AcquireUpdateGate') {
    throw 'Auch manuelle Installationen müssen das produktive Gate vor allen anderen Aktionen anfordern.'
}
$acquire = Read-NsisFunction $source 'AcquireUpdateGate'
if ([regex]::Matches($acquire, [regex]::Escape('Local\DYNEX.Update')).Count -ne 1) { throw 'Das produktive Gate konnte nicht eindeutig isoliert werden.' }
$testGate = 'Local\DYNEX.InstallerSmoke.' + [Guid]::NewGuid().ToString('N')
$acquire = $acquire.Replace('Local\DYNEX.Update', $testGate)
$release = Read-NsisFunction $source 'ReleaseUpdateGate'
$selfTest = Read-NsisFunction $source 'RunPackageSelfTest'
$backup = Read-NsisFunction $source 'BackupForRollback'
$restore = Read-NsisFunction $source 'RestoreRollback'
$macro = Read-NsisMacro $source 'InstallApplicationPayload'

# Strip only the updater's HKCU version reads/writes, redirecting the rollback
# marker into the isolated installation. Assert the production shapes first so
# a changed or newly added external side effect cannot silently escape the test.
$regRead = 'ReadRegStr $PreviousVersion HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "DisplayVersion"'
$regWrite = 'WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "DisplayVersion" "$PreviousVersion"'
if ([regex]::Matches($backup, [regex]::Escape($regRead)).Count -ne 1) { throw 'BackupForRollback registry read shape changed; review harness isolation.' }
if ([regex]::Matches($restore, [regex]::Escape($regWrite)).Count -ne 1) { throw 'RestoreRollback registry write shape changed; review harness isolation.' }
$backup = $backup.Replace($regRead, 'StrCpy $PreviousVersion "1.0.21-fixture"')
$restore = $restore.Replace($regWrite, 'DetailPrint "Fixture registry write isolated."')
$userStatePath = '$LOCALAPPDATA\DYNEX\updates'
if ([regex]::Matches($restore, [regex]::Escape($userStatePath)).Count -ne 2) { throw 'Rollback marker directory shape changed; review harness isolation.' }
$restore = $restore.Replace($userStatePath, '$INSTDIR\test-updates')
if ($backup + $restore -match '(?i)\b(?:LOCALAPPDATA|HKCU|HKLM|ReadReg\w*|WriteReg\w*)\b') { throw 'Rollback fixture still contains a production registry or user-state side effect.' }

$harness = Join-Path $OutputDirectory 'installer-smoke.exe'
$destination = Join-Path $OutputDirectory 'isolated installed payload'
$template = @'
Unicode true
RequestExecutionLevel user
SilentInstall silent
SetCompress off
OutFile "@HARNESS@"
InstallDir "@DESTINATION@"
!include "LogicLib.nsh"
!include "FileFunc.nsh"
!define APP_VERSION "@VERSION@"
!define PUBLISH_ROOT "@PAYLOAD@"
Var UpdateMutex
Var SelfTestExit
Var SelfTestLaunchError
Var UpdateResultHandle
Var RollbackReady
Var PreviousVersion
Var PreviousAppPresent
Var PreviousShaperPresent
Var PrerequisiteLog
Var TestMode
@ACQUIRE@
@RELEASE@
@SELFTEST@
@BACKUP@
@RESTORE@
@MACRO@
Function .onInit
  StrCpy $UpdateMutex 0
  Call AcquireUpdateGate
  ${GetParameters} $0
  ${GetOptions} $0 "/TESTMODE=" $TestMode
FunctionEnd
Function .onInstSuccess
  Call ReleaseUpdateGate
FunctionEnd
Function .onInstFailed
  Call RestoreRollback
  Call ReleaseUpdateGate
FunctionEnd
Function .onGUIEnd
  Call ReleaseUpdateGate
FunctionEnd
Section
  Call BackupForRollback
  ${If} $RollbackReady == -1
    SetErrorLevel 14
    Abort
  ${EndIf}
  !insertmacro InstallApplicationPayload
  ${If} $TestMode == "missing-payload"
    Delete "$INSTDIR\shaper\Dynex.Shaper.dll"
  ${ElseIf} $TestMode == "modified-payload"
    FileOpen $0 "$INSTDIR\shaper\Dynex.Shaper.runtimeconfig.json" w
    FileWrite $0 '{}'
    FileClose $0
  ${ElseIf} $TestMode == "missing-app"
    Delete "$INSTDIR\DYNEX.exe"
  ${EndIf}
  SetOutPath "$INSTDIR\docs"
  Call RunPackageSelfTest
  ${If} $SelfTestExit != 0
    SetErrorLevel 15
    Abort
  ${EndIf}
  RMDir /r "$INSTDIR\rollback"
  StrCpy $RollbackReady 0
SectionEnd
'@
$template = $template.Replace('@HARNESS@', (Nsis-Path $harness)).Replace('@DESTINATION@', (Nsis-Path $destination)).
    Replace('@VERSION@', $ExpectedVersion).Replace('@PAYLOAD@', (Nsis-Path $PayloadRoot)).
    Replace('@ACQUIRE@', $acquire).Replace('@RELEASE@', $release).Replace('@SELFTEST@', $selfTest).
    Replace('@BACKUP@', $backup).Replace('@RESTORE@', $restore).Replace('@MACRO@', $macro)
$harnessSource = Join-Path $OutputDirectory 'installer-smoke.nsi'
Set-Content -LiteralPath $harnessSource -Value $template -Encoding utf8

if (-not ('DynexInstallerSmokeNative' -as [type])) {
    Add-Type @'
using System.Runtime.InteropServices;
public static class DynexInstallerSmokeNative {
    [DllImport("kernel32.dll")] public static extern uint GetErrorMode();
    [DllImport("kernel32.dll")] public static extern uint SetErrorMode(uint mode);
}
'@
}
$previousErrorMode = [DynexInstallerSmokeNative]::GetErrorMode()
[void][DynexInstallerSmokeNative]::SetErrorMode($previousErrorMode -bor 0x8003)
function Run-Hidden([string]$Exe, [string[]]$ProcessArguments, [string]$Name) {
    $start = [Diagnostics.ProcessStartInfo]::new($Exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $ProcessArguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Prüfprozess wurde nicht gestartet: $Name" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) { throw "Prüfprozess $Name läuft noch (PID $($process.Id)); kein weiterer Prozess wurde gestartet." }
        $stdout.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $OutputDirectory "$Name.stdout.log")
        $stderr.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $OutputDirectory "$Name.stderr.log")
        return $process.ExitCode
    }
    finally { $process.Dispose() }
}
function Invoke-Install([string[]]$Arguments, [string]$Name) {
    $exitCode = Run-Hidden $harness $Arguments $Name
    $reportPath = Join-Path $destination 'update-result.txt'
    $report = if (Test-Path -LiteralPath $reportPath) { Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-StringData } else { @{} }
    return [pscustomobject]@{ ExitCode=$exitCode; Report=$report }
}
function New-OldInstall {
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    $oldShaper = Join-Path $destination 'shaper'
    New-Item -ItemType Directory -Path $oldShaper -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $oldShaper 'nested\legacy') -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $destination 'DYNEX.exe'), [byte[]](1,0,2,0,3,0,4,0))
    [IO.File]::WriteAllBytes((Join-Path $oldShaper 'Dynex.Shaper.exe'), [byte[]](7,1,7,1))
    [IO.File]::WriteAllBytes((Join-Path $oldShaper 'nested\legacy\preserve.bin'), [byte[]](4,3,2,1,0,9))
    [IO.File]::WriteAllBytes((Join-Path $oldShaper 'mscordaccore_amd64_amd64_9.0.0.dll'), [byte[]](9,8,7,6,5))
}
try {
    $compileExit = Run-Hidden $NsisPath @('/INPUTCHARSET', 'UTF8', $harnessSource) 'compile'
    if ($compileExit -ne 0) { throw "NSIS-Testpaket konnte nicht kompiliert werden ($compileExit). Siehe $OutputDirectory." }
    $heldGate = [Threading.Mutex]::new($false, $testGate)
    try {
        $blockedExit = Run-Hidden $harness @('/S') 'blocked'
        if ($blockedExit -ne 11 -or (Test-Path -LiteralPath $destination)) { throw 'Ein belegtes Gate muss Exit 11 vor jeder Payload-Datei liefern.' }
    }
    finally { $heldGate.Dispose() }
    $checks = [Collections.Generic.List[object]]::new()
    $checks.Add([pscustomobject]@{Name='held gate rejects before payload mutation'; ExitCode=$blockedExit; Passed=$true})

    # Fresh install validates that the shared package macro still passes the strict manifest self-test.
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    $fresh = Invoke-Install @('/S') 'fresh'
    if ($fresh.ExitCode -ne 0 -or $fresh.Report.expectedVersion -ne $ExpectedVersion -or $fresh.Report.launchError -ne '0' -or $fresh.Report.childExit -ne '0') { throw "Fresh payload self-test failed: $($fresh | ConvertTo-Json -Compress)" }
    if (Test-Path -LiteralPath (Join-Path $destination 'rollback')) { throw 'Fresh success must consume its rollback transaction.' }
    $checks.Add([pscustomobject]@{Name='fresh install strict manifest self-test'; ExitCode=$fresh.ExitCode; Passed=$true})

    # Upgrade from an older install with nested files and an obsolete runtime DLL.
    New-OldInstall
    $upgrade = Invoke-Install @('/S') 'upgrade-with-stale-runtime'
    if ($upgrade.ExitCode -ne 0 -or $upgrade.Report.expectedVersion -ne $ExpectedVersion -or $upgrade.Report.launchError -ne '0' -or $upgrade.Report.childExit -ne '0') { throw "Upgrade with legacy files failed: $($upgrade | ConvertTo-Json -Compress)" }
    Assert-TreeHashes (Join-Path $destination 'shaper') (Get-TreeHashes (Join-Path $PayloadRoot 'shaper')) 'successful upgrade exact helper payload'
    if (Test-Path -LiteralPath (Join-Path $destination 'shaper/mscordaccore_amd64_amd64_9.0.0.dll')) { throw 'Upgrade retained a stale legacy runtime file.' }
    if (Test-Path -LiteralPath (Join-Path $destination 'rollback')) { throw 'Successful upgrade must consume its rollback transaction.' }
    $checks.Add([pscustomobject]@{Name='upgrade removes stale nested helper files'; ExitCode=$upgrade.ExitCode; Passed=$true})

    # A previous partial install can contain the helper tree but no desktop app.
    # A failed replacement must restore that tree and must not invent an old app.
    New-OldInstall
    Remove-Item -LiteralPath (Join-Path $destination 'DYNEX.exe') -Force
    $partialHashes = Get-TreeHashes (Join-Path $destination 'shaper')
    $partial = Invoke-Install @('/S','/TESTMODE=missing-payload') 'partial-install-rollback'
    if ($partial.ExitCode -ne 15 -or $partial.Report.childExit -ne '22') { throw "Partial-install failure was not detected: $($partial | ConvertTo-Json -Compress)" }
    if (Test-Path -LiteralPath (Join-Path $destination 'DYNEX.exe')) { throw 'Partial-install recovery created an app that was not present before the update.' }
    Assert-TreeHashes (Join-Path $destination 'shaper') $partialHashes 'partial-install restored helper'
    if (Test-Path -LiteralPath (Join-Path $destination 'rollback')) { throw 'Partial-install recovery transaction was not consumed.' }
    $checks.Add([pscustomobject]@{Name='helper-only partial install is fully restored'; ExitCode=$partial.ExitCode; Passed=$true})

    foreach ($scenario in @(
        @{Name='missing-payload'; Arguments=@('/S','/TESTMODE=missing-payload'); Launch='0'; Child='22'},
        @{Name='modified-payload'; Arguments=@('/S','/TESTMODE=modified-payload'); Launch='0'; Child='22'},
        @{Name='missing-app'; Arguments=@('/S','/TESTMODE=missing-app'); Launch='1'; Child='unavailable'}
    )) {
        New-OldInstall
        $oldAppHash = (Get-FileHash -LiteralPath (Join-Path $destination 'DYNEX.exe') -Algorithm SHA256).Hash
        $oldShaperHashes = Get-TreeHashes (Join-Path $destination 'shaper')
        $result = Invoke-Install $scenario.Arguments $scenario.Name
        if ($result.ExitCode -ne 15 -or $result.Report.expectedVersion -ne $ExpectedVersion -or $result.Report.launchError -ne $scenario.Launch -or $result.Report.childExit -ne $scenario.Child) { throw "Expected package failure for $($scenario.Name): $($result | ConvertTo-Json -Compress)" }
        if ((Get-FileHash -LiteralPath (Join-Path $destination 'DYNEX.exe') -Algorithm SHA256).Hash -ne $oldAppHash) { throw "$($scenario.Name): original app was not restored byte-for-byte." }
        Assert-TreeHashes (Join-Path $destination 'shaper') $oldShaperHashes "$($scenario.Name) restored helper"
        if (Test-Path -LiteralPath (Join-Path $destination 'rollback')) { throw "$($scenario.Name): completed recovery transaction was not consumed." }
        $fixtureMarker = Join-Path $destination 'test-updates/rolled-back.txt'
        if ((Get-Content -Raw -LiteralPath $fixtureMarker).Trim() -ne $ExpectedVersion) { throw "$($scenario.Name): rollback marker was not written into the isolated fixture." }
        $checks.Add([pscustomobject]@{Name="$($scenario.Name) failure restores complete original app/helper"; ExitCode=$result.ExitCode; Passed=$true})
    }

    # An unresolved recovery directory is protected and must block before replacing either old file.
    New-OldInstall
    $blockedAppHash = (Get-FileHash -LiteralPath (Join-Path $destination 'DYNEX.exe') -Algorithm SHA256).Hash
    $blockedHelperHashes = Get-TreeHashes (Join-Path $destination 'shaper')
    New-Item -ItemType Directory -Path (Join-Path $destination 'rollback') | Out-Null
    [IO.File]::WriteAllText((Join-Path $destination 'rollback/keep-recovery.txt'), 'must survive')
    $blockedRollbackResult = Invoke-Install @('/S') 'existing-recovery-blocks'
    if ($blockedRollbackResult.ExitCode -ne 14 -or -not (Test-Path -LiteralPath (Join-Path $destination 'rollback/keep-recovery.txt'))) { throw 'Existing recovery transaction was not preserved and safely rejected.' }
    if ((Get-FileHash -LiteralPath (Join-Path $destination 'DYNEX.exe') -Algorithm SHA256).Hash -ne $blockedAppHash) { throw 'Blocked transaction changed the old app.' }
    Assert-TreeHashes (Join-Path $destination 'shaper') $blockedHelperHashes 'existing recovery blocked old helper'
    $checks.Add([pscustomobject]@{Name='existing incomplete recovery is preserved'; ExitCode=$blockedRollbackResult.ExitCode; Passed=$true})

    # A non-delete-share handle in the old helper tree must make the directory
    # rename fail before the desktop executable or helper payload is replaced.
    Remove-Item -LiteralPath (Join-Path $destination 'rollback') -Recurse -Force
    $oldAppHash = (Get-FileHash -LiteralPath (Join-Path $destination 'DYNEX.exe') -Algorithm SHA256).Hash
    $oldShaperHashes = Get-TreeHashes (Join-Path $destination 'shaper')
    $lockedFile = [IO.File]::Open((Join-Path $destination 'shaper/nested/legacy/preserve.bin'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $locked = Invoke-Install @('/S') 'locked-old-helper'
        if ($locked.ExitCode -ne 14) { throw "Locked helper must abort before mutation (expected 14, got $($locked.ExitCode))." }
        if ((Get-FileHash -LiteralPath (Join-Path $destination 'DYNEX.exe') -Algorithm SHA256).Hash -ne $oldAppHash) { throw 'Locked backup changed the original app.' }
        Assert-TreeHashes (Join-Path $destination 'shaper') $oldShaperHashes 'locked backup old helper'
    }
    finally { $lockedFile.Dispose() }
    $checks.Add([pscustomobject]@{Name='locked old helper aborts before payload replacement'; ExitCode=$locked.ExitCode; Passed=$true})

    # Retry on that same original installation after its helper handle is released.
    $healthyAfterFailure = Invoke-Install @('/S') 'healthy-after-failures'
    if ($healthyAfterFailure.ExitCode -ne 0 -or $healthyAfterFailure.Report.expectedVersion -ne $ExpectedVersion -or $healthyAfterFailure.Report.launchError -ne '0' -or $healthyAfterFailure.Report.childExit -ne '0') { throw "Successful update after failed backup did not recover: $($healthyAfterFailure | ConvertTo-Json -Compress)" }
    Assert-TreeHashes (Join-Path $destination 'shaper') (Get-TreeHashes (Join-Path $PayloadRoot 'shaper')) 'retry successful helper payload'
    if (Test-Path -LiteralPath (Join-Path $destination 'rollback')) { throw 'Recovered successful update left its rollback transaction behind.' }
    $checks.Add([pscustomobject]@{Name='healthy retry succeeds after failed backup'; ExitCode=$healthyAfterFailure.ExitCode; Passed=$true})

    $checks | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json')
    Write-Host "$($checks.Count) installer checks passed. Isolated GUID gate; no registry, filter, firewall or real installation changed. $OutputDirectory"
}
finally { [void][DynexInstallerSmokeNative]::SetErrorMode($previousErrorMode) }
