param(
    [Parameter(Mandatory = $true)][string]$PayloadRoot,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$NsisPath,
    [string]$OutputDirectory
)

# Exercises the actual NSIS gate/self-test functions in a disposable package.
# No production mutex, installed application, registry, driver or firewall is used.
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
Var UpdateMutex
Var SelfTestExit
Var SelfTestLaunchError
Var UpdateResultHandle
Var TestMode
@ACQUIRE@
@RELEASE@
@SELFTEST@
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
  Call ReleaseUpdateGate
FunctionEnd
Function .onGUIEnd
  Call ReleaseUpdateGate
FunctionEnd
Section
  SetOutPath "$INSTDIR"
  FileOpen $0 "$INSTDIR\section-entered.txt" w
  FileWrite $0 "payload section entered"
  FileClose $0
  File /oname=DYNEX.exe "@DESKTOP@"
  SetOutPath "$INSTDIR\shaper"
  File /oname=Dynex.Shaper.exe "@SHAPEREXE@"
  File /oname=Dynex.Shaper.dll "@SHAPERDLL@"
  ${If} $TestMode == "missing-payload"
    Delete "$INSTDIR\shaper\Dynex.Shaper.dll"
  ${ElseIf} $TestMode == "missing-app"
    Delete "$INSTDIR\DYNEX.exe"
  ${EndIf}
  ; Match the working directory left by the real InstallDocumentation macro.
  SetOutPath "$INSTDIR\docs"
  Call RunPackageSelfTest
  ${If} $SelfTestExit != 0
    SetErrorLevel 15
    Abort
  ${EndIf}
SectionEnd
'@
$template = $template.Replace('@HARNESS@', (Nsis-Path $harness)).Replace('@DESTINATION@', (Nsis-Path $destination)).
    Replace('@VERSION@', $ExpectedVersion).Replace('@ACQUIRE@', $acquire).Replace('@RELEASE@', $release).Replace('@SELFTEST@', $selfTest).
    Replace('@DESKTOP@', (Nsis-Path (Join-Path $PayloadRoot 'DYNEX.exe'))).
    Replace('@SHAPEREXE@', (Nsis-Path (Join-Path $PayloadRoot 'shaper/Dynex.Shaper.exe'))).
    Replace('@SHAPERDLL@', (Nsis-Path (Join-Path $PayloadRoot 'shaper/Dynex.Shaper.dll')))
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
    foreach ($scenario in @(
        @{ Name='healthy'; Arguments=@('/S'); Exit=0; Launch='0'; Child='0' },
        @{ Name='missing-payload'; Arguments=@('/S','/TESTMODE=missing-payload'); Exit=15; Launch='0'; Child='22' },
        @{ Name='missing-app'; Arguments=@('/S','/TESTMODE=missing-app'); Exit=15; Launch='1'; Child='unavailable' },
        @{ Name='healthy-after-failures'; Arguments=@('/S'); Exit=0; Launch='0'; Child='0' }
    )) {
        $exitCode = Run-Hidden $harness $scenario.Arguments $scenario.Name
        $report = Get-Content -Raw -LiteralPath (Join-Path $destination 'update-result.txt') | ConvertFrom-StringData
        if ($exitCode -ne $scenario.Exit -or $report.expectedVersion -ne $ExpectedVersion -or
            $report.launchError -ne $scenario.Launch -or $report.childExit -ne $scenario.Child) {
            throw "Installer-Test $($scenario.Name) fehlgeschlagen: Exit=$exitCode, Diagnose=$($report | ConvertTo-Json -Compress)"
        }
        Copy-Item -LiteralPath (Join-Path $destination 'update-result.txt') -Destination (Join-Path $OutputDirectory ($scenario.Name + '.result.txt'))
        $checks.Add([pscustomobject]@{Name=$scenario.Name; ExitCode=$exitCode; Passed=$true})
    }
    $checks | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'result.json')
    Write-Host "$($checks.Count) installer checks passed. Isolated GUID gate; no registry, filter, firewall or real installation changed. $OutputDirectory"
}
finally { [void][DynexInstallerSmokeNative]::SetErrorMode($previousErrorMode) }
