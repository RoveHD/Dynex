Unicode true
RequestExecutionLevel user
ManifestDPIAware true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "1.0.9"
!endif
!ifndef REPO_ROOT
  !error "REPO_ROOT fehlt; bitte scripts/build.ps1 verwenden."
!endif
!ifndef PUBLISH_ROOT
  !error "PUBLISH_ROOT fehlt; bitte scripts/build.ps1 verwenden."
!endif
!ifndef OUTPUT_FILE
  !define OUTPUT_FILE "${REPO_ROOT}\artifacts\DYNEX-Setup-${APP_VERSION}.exe"
!endif
!include "${PUBLISH_ROOT}\install-files.nsh"

; Optional Authenticode signing. scripts/build.ps1 passes SIGN_SCRIPT when a
; code-signing certificate is configured; %1 is the file NSIS just produced.
!ifdef SIGN_SCRIPT
  !finalize '"${SIGN_SCRIPT}" "%1"' = 0
  !uninstfinalize '"${SIGN_SCRIPT}" "%1"' = 0
!endif
!include "${PUBLISH_ROOT}\uninstall-files.nsh"

Name "DYNEX · Dynamic Network Experience"
OutFile "${OUTPUT_FILE}"
InstallDir "$LOCALAPPDATA\Programs\DYNEX"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "DYNEX · Dynamic Network Experience"
VIAddVersionKey "FileDescription" "DYNEX Windows Installer"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "CompanyName" "DYNEX"
VIAddVersionKey "LegalCopyright" "Copyright © 2026 DYNEX"
SetCompressor /SOLID lzma
ShowInstDetails show
ShowUnInstDetails show

!define MUI_ABORTWARNING
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "German"

Var UpdateParent
Var UpdateMutex
Var UpdateTray
Var PrerequisiteExit
Var PrerequisiteMessage
Var PrerequisiteLog
Var CleanupMutex
Var CleanupRestart
Var CleanupDeleteFailed
Var UpgradeLegacy
Var RollbackReady
Var PreviousVersion
Var SelfTestExit
Var SelfTestLaunchError
Var UpdateResultHandle

Function ReleaseUpdateGate
  ${If} $UpdateMutex != 0
    System::Call 'kernel32::CloseHandle(p $UpdateMutex)'
    StrCpy $UpdateMutex 0
  ${EndIf}
FunctionEnd

Function AcquireUpdateGate
  ; Manual setups, background updates and cleanup must exclude each other.
  ; Keep the gate until the payload check and prerequisite setup are complete.
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\DYNEX.Update") p .r4 ?e'
  Pop $5
  StrCpy $UpdateMutex $4
  ${If} $4 == 0
  ${OrIf} $5 == 183
    Call ReleaseUpdateGate
    SetErrorLevel 11
    Abort
  ${EndIf}
FunctionEnd

Function RunPackageSelfTest
  StrCpy $SelfTestLaunchError 0
  ClearErrors
  ExecWait '"$INSTDIR\DYNEX.exe" --self-test ${APP_VERSION}' $SelfTestExit
  ${If} ${Errors}
    StrCpy $SelfTestLaunchError 1
    StrCpy $SelfTestExit 1
  ${EndIf}
  ; Fixed, non-sensitive diagnostic. A launch failure has no valid child exit.
  ClearErrors
  FileOpen $UpdateResultHandle "$INSTDIR\update-result.txt" w
  IfErrors selftest_report_failed
  FileWrite $UpdateResultHandle "expectedVersion=${APP_VERSION}$\r$\n"
  FileWrite $UpdateResultHandle "launchError=$SelfTestLaunchError$\r$\n"
  ${If} $SelfTestLaunchError == 0
    FileWrite $UpdateResultHandle "childExit=$SelfTestExit$\r$\n"
  ${Else}
    FileWrite $UpdateResultHandle "childExit=unavailable$\r$\n"
  ${EndIf}
  FileClose $UpdateResultHandle
  Return
  selftest_report_failed:
    DetailPrint "Starttest-Diagnose konnte nicht gespeichert werden."
FunctionEnd

Function RestartAfterUpdate
  Call ReleaseUpdateGate
  ${If} $UpdateParent != ""
    IfFileExists "$INSTDIR\DYNEX.exe" 0 restart_done
    ${If} $UpdateTray == "1"
      Exec '"$INSTDIR\DYNEX.exe" --skip-update-once --tray'
    ${Else}
      Exec '"$INSTDIR\DYNEX.exe" --skip-update-once'
    ${EndIf}
    restart_done:
  ${EndIf}
FunctionEnd

Function .onInstSuccess
  Call RestartAfterUpdate
FunctionEnd

Function .onInstFailed
  ; Restore before restarting so the update handoff reopens the previous, working version.
  Call RestoreRollback
  Call RestartAfterUpdate
FunctionEnd

; Copies the installed program before an upgrade overwrites it. Settings, pairing and
; identity live in %LOCALAPPDATA%\DYNEX and are never touched by the rollback.
Function BackupForRollback
  StrCpy $RollbackReady 0
  StrCpy $PreviousVersion ""
  IfFileExists "$INSTDIR\DYNEX.exe" 0 backup_done
  RMDir /r "$INSTDIR\rollback"
  CreateDirectory "$INSTDIR\rollback\shaper"
  ClearErrors
  CopyFiles /SILENT "$INSTDIR\DYNEX.exe" "$INSTDIR\rollback\DYNEX.exe"
  IfFileExists "$INSTDIR\shaper\*.*" 0 +2
    CopyFiles /SILENT "$INSTDIR\shaper\*.*" "$INSTDIR\rollback\shaper"
  IfErrors backup_failed
  IfFileExists "$INSTDIR\rollback\DYNEX.exe" 0 backup_failed
  ReadRegStr $PreviousVersion HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "DisplayVersion"
  StrCpy $RollbackReady 1
  Goto backup_done
  backup_failed:
    RMDir /r "$INSTDIR\rollback"
    StrCpy $RollbackReady -1
  backup_done:
FunctionEnd

Function RestoreRollback
  ${If} $RollbackReady == 1
    DetailPrint "Update fehlgeschlagen. Vorherige DYNEX-Version wird wiederhergestellt ..."
    ClearErrors
    Delete "$INSTDIR\DYNEX.exe"
    CopyFiles /SILENT "$INSTDIR\rollback\DYNEX.exe" "$INSTDIR\DYNEX.exe"
    RMDir /r "$INSTDIR\shaper"
    CreateDirectory "$INSTDIR\shaper"
    CopyFiles /SILENT "$INSTDIR\rollback\shaper\*.*" "$INSTDIR\shaper"
    ${IfNot} ${Errors}
      ${If} $PreviousVersion != ""
        WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "DisplayVersion" "$PreviousVersion"
      ${EndIf}
      ; Tells the app not to download this version again automatically.
      CreateDirectory "$LOCALAPPDATA\DYNEX\updates"
      FileOpen $PrerequisiteLog "$LOCALAPPDATA\DYNEX\updates\rolled-back.txt" w
      FileWrite $PrerequisiteLog "${APP_VERSION}"
      FileClose $PrerequisiteLog
      RMDir /r "$INSTDIR\rollback"
      DetailPrint "Vorherige Version $PreviousVersion wiederhergestellt."
    ${Else}
      DetailPrint "Wiederherstellung unvollständig. Sicherung bleibt unter $INSTDIR\rollback erhalten."
    ${EndIf}
    StrCpy $RollbackReady 2
  ${EndIf}
FunctionEnd

Function .onGUIEnd
  Call ReleaseUpdateGate
FunctionEnd

Function .onInit
  StrCpy $UpdateMutex 0
  Call AcquireUpdateGate
  StrCpy $UpgradeLegacy ""
  ; Detect an existing installation before replacing its executable/registry.
  ; Old installers have no filter receipt; an upgrade must not turn that
  ; unknown history into a claim that the filter was independently installed.
  IfFileExists "$INSTDIR\DYNEX.exe" 0 +2
    StrCpy $UpgradeLegacy " --upgrade-legacy-installation"
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "InstallLocation"
  ${If} $0 == "$INSTDIR"
    StrCpy $UpgradeLegacy " --upgrade-legacy-installation"
  ${EndIf}
  ${GetParameters} $3
  ${GetOptions} $3 "/DYNEXUPDATE=" $UpdateParent
  ${GetOptions} $3 "/DYNEXTRAY=" $UpdateTray
  ${If} $UpdateParent != ""
    ; The parent released its helper before launching us; wait for its clean exit.
    System::Call 'kernel32::OpenProcess(i 0x100000, i 0, i $UpdateParent) p .r4'
    ${If} $4 != 0
      System::Call 'kernel32::WaitForSingleObject(p r4, i 20000) i .r5'
      System::Call 'kernel32::CloseHandle(p r4)'
      ${If} $5 != 0
        Call RestartAfterUpdate
        SetErrorLevel 12
        Abort
      ${EndIf}
    ${EndIf}
  ${EndIf}
  Call StopRunningDynex
  ${If} $0 != 0
    IfSilent +2
      MessageBox MB_ICONEXCLAMATION|MB_OK "DYNEX konnte nicht sauber beendet werden. Setup abgebrochen; Dateien wurden nicht überschrieben."
    Call RestartAfterUpdate
    SetErrorLevel 13
    Abort
  ${EndIf}
FunctionEnd

Function StopRunningDynex
  StrCpy $0 0
  IfFileExists "$INSTDIR\DYNEX.exe" 0 stop_done
  ; The app handles --shutdown through its per-user named event. It exits with
  ; success if no instance is running; nsExec waits for that IPC request.
  ${If} $UpdateParent != ""
    nsExec::ExecToStack /TIMEOUT=20000 '"$INSTDIR\DYNEX.exe" --update-idle'
  ${Else}
    nsExec::ExecToStack /TIMEOUT=20000 '"$INSTDIR\DYNEX.exe" --shutdown'
  ${EndIf}
  Pop $1
  Pop $2
  ${If} $1 != "error"
  ${AndIf} $1 != "timeout"
    StrCpy $0 $1
  ${Else}
    StrCpy $0 1
  ${EndIf}
  stop_done:
FunctionEnd

Section "DYNEX Anwendung" SEC_MAIN
  SectionIn RO
  Call StopRunningDynex
  ${If} $0 != 0
    IfSilent +2
      MessageBox MB_ICONEXCLAMATION|MB_OK "DYNEX konnte nicht sauber beendet werden. Setup abgebrochen; Dateien wurden nicht überschrieben."
    SetErrorLevel 13
    Abort
  ${EndIf}
  Call BackupForRollback
  ${If} $RollbackReady == -1
    IfSilent +2
      MessageBox MB_ICONEXCLAMATION|MB_OK "Die installierte DYNEX-Version konnte nicht gesichert werden. Setup abgebrochen; Dateien wurden nicht überschrieben."
    SetErrorLevel 14
    Abort
  ${EndIf}
  SetOutPath "$INSTDIR"
  File "${PUBLISH_ROOT}\DYNEX.exe"
  SetOutPath "$INSTDIR\shaper"
  File /r "${PUBLISH_ROOT}\shaper\*"
  !insertmacro InstallDocumentation
  ; Prove the new binary starts and its payload is complete before anything else changes.
  Call RunPackageSelfTest
  ${If} $SelfTestExit != 0
    DetailPrint "Starttest der neuen Version fehlgeschlagen (Fehler $SelfTestExit)."
    IfSilent +2
      MessageBox MB_ICONEXCLAMATION|MB_OK "Die neue DYNEX-Version ließ sich nicht starten (Fehler $SelfTestExit). Die vorherige Version wird wiederhergestellt."
    SetErrorLevel 15
    Abort
  ${EndIf}
  CreateDirectory "$SMPROGRAMS\DYNEX"
  CreateShortcut "$SMPROGRAMS\DYNEX\DYNEX.lnk" "$INSTDIR\DYNEX.exe"
  CreateShortcut "$SMPROGRAMS\DYNEX\Installation.lnk" "$INSTDIR\docs\installation.md"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "DisplayName" "DYNEX · Dynamic Network Experience"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "Publisher" "DYNEX"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "NoModify" 1
  WriteRegDWORD HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "NoRepair" 1
  WriteUninstaller "$INSTDIR\Uninstall.exe"
  ; The desktop's dedicated CLI does not start normal application workers or
  ; read settings. Only missing system prerequisites request one UAC elevation.
  ; The official MSI is fetched directly from its publisher, never redistributed.
  DetailPrint "Downloadbegrenzung und private Heimnetzverbindung einrichten ..."
  Delete "$INSTDIR\setup-result.txt"
  ClearErrors
  ExecWait '"$INSTDIR\DYNEX.exe" --setup-prerequisites --setup-result "$INSTDIR\setup-result.txt"$UpgradeLegacy' $PrerequisiteExit
  ${If} ${Errors}
    StrCpy $PrerequisiteExit 1
  ${EndIf}
  StrCpy $PrerequisiteMessage "Die Netzwerkeinrichtung konnte nicht abgeschlossen werden. Starte das DYNEX-Setup erneut."
  IfFileExists "$INSTDIR\setup-result.txt" 0 prerequisite_result_missing
    FileOpen $PrerequisiteLog "$INSTDIR\setup-result.txt" r
    IfErrors prerequisite_result_missing
    FileReadUTF16LE $PrerequisiteLog $PrerequisiteMessage
    FileClose $PrerequisiteLog
    Goto prerequisite_result_ready
  prerequisite_result_missing:
    StrCpy $PrerequisiteExit 1
  prerequisite_result_ready:
  DetailPrint "$PrerequisiteMessage"
  ${If} $PrerequisiteExit == 3010
    ; Report restart-required without requesting or forcing a restart.
    IfSilent +2
      MessageBox MB_ICONINFORMATION|MB_OK "$PrerequisiteMessage"
    SetErrorLevel 3010
  ${ElseIf} $PrerequisiteExit != 0
    IfSilent +2
      MessageBox MB_ICONEXCLAMATION|MB_OK "$PrerequisiteMessage$\r$\n$\r$\nDetails: $INSTDIR\setup-result.txt"
    SetErrorLevel $PrerequisiteExit
    Abort
  ${EndIf}
  ; Success: the backup is no longer needed, and a previous failure marker is obsolete.
  RMDir /r "$INSTDIR\rollback"
  StrCpy $RollbackReady 0
  Delete "$LOCALAPPDATA\DYNEX\updates\rolled-back.txt"
SectionEnd

Section /o "Desktop-Verknüpfung" SEC_DESKTOP
  CreateShortcut "$DESKTOP\DYNEX.lnk" "$INSTDIR\DYNEX.exe"
SectionEnd

Section "Uninstall"
  Call un.StopRunningDynex
  ${If} $0 != 0
    IfSilent +2
      MessageBox MB_ICONEXCLAMATION|MB_OK "DYNEX konnte nicht sauber beendet werden. Die Deinstallation wird abgebrochen, damit laufende Dateien nicht beschädigt werden."
    SetErrorLevel 13
    Abort
  ${EndIf}
  ; Keep the installed recovery entry point until all system and user cleanup
  ; has succeeded. A cancelled UAC prompt must never become a successful uninstall.
  StrCpy $CleanupRestart 0
  Delete "$INSTDIR\uninstall-result.txt"
  ClearErrors
  ExecWait '"$INSTDIR\DYNEX.exe" --uninstall-system' $0
  ${If} ${Errors}
    StrCpy $0 1
  ${EndIf}
  ${If} $0 == 5201
    ; Older releases did not record who installed the shared filter. Never
    ; infer permission to remove it from silent mode or a missing receipt.
    IfSilent un_cleanup_failed
    MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Windows Packet Filter ist noch installiert. Ältere DYNEX-Versionen haben nicht gespeichert, ob er vorher schon vorhanden war.$\r$\n$\r$\nSoll dieses Netzwerkfilter-Paket jetzt ebenfalls entfernt werden? Die Verbindung kann kurz abbrechen. Andere Programme, die diesen Filter benötigen, können danach nicht mehr damit arbeiten." IDNO un_cleanup_failed
    ClearErrors
    ExecWait '"$INSTDIR\DYNEX.exe" --uninstall-system --confirm-legacy-filter-removal' $0
    ${If} ${Errors}
      StrCpy $0 1
    ${EndIf}
  ${EndIf}
  ${If} $0 == 3010
    StrCpy $CleanupRestart 3010
  ${ElseIf} $0 != 0
    Goto un_cleanup_failed
  ${EndIf}
  StrCpy $PrerequisiteMessage "Systembereinigung abgeschlossen."
  IfFileExists "$INSTDIR\uninstall-result.txt" 0 +4
    FileOpen $PrerequisiteLog "$INSTDIR\uninstall-result.txt" r
    FileReadUTF16LE $PrerequisiteLog $PrerequisiteMessage
    FileClose $PrerequisiteLog
  DetailPrint "$PrerequisiteMessage"
  Delete "$INSTDIR\user-cleanup-result.txt"
  ClearErrors
  ExecWait '"$INSTDIR\DYNEX.exe" --remove-user-data' $0
  ${If} ${Errors}
    StrCpy $0 1
  ${EndIf}
  ${If} $0 != 0
    IfSilent +2
      MessageBox MB_ICONEXCLAMATION|MB_OK "Die DYNEX-Benutzerdaten konnten nicht vollständig entfernt werden. Die Deinstallation bleibt für einen erneuten Versuch erhalten.$\r$\n$\r$\nDetails: $INSTDIR\user-cleanup-result.txt"
    SetErrorLevel $0
    Abort
  ${EndIf}
  StrCpy $PrerequisiteMessage "DYNEX-Benutzerdaten wurden bereinigt."
  IfFileExists "$INSTDIR\user-cleanup-result.txt" 0 +4
    FileOpen $PrerequisiteLog "$INSTDIR\user-cleanup-result.txt" r
    FileReadUTF16LE $PrerequisiteLog $PrerequisiteMessage
    FileClose $PrerequisiteLog
  DetailPrint "$PrerequisiteMessage"
  Delete "$INSTDIR\update-result.txt"
  ${If} ${FileExists} "$INSTDIR\update-result.txt"
    SetErrorLevel 1
    Abort
  ${EndIf}
  !insertmacro DeleteInstalledFiles
  ; Additional files somebody placed in the program directory are preserved.
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX"
  Delete "$INSTDIR\setup-result.txt"
  Delete "$INSTDIR\uninstall-result.txt"
  Delete "$INSTDIR\user-cleanup-result.txt"
  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR\rollback"
  RMDir "$INSTDIR"
  ${If} $CleanupRestart == 3010
    IfSilent +2
      MessageBox MB_ICONINFORMATION|MB_OK "DYNEX wurde entfernt. Windows muss noch neu gestartet werden, um die Entfernung des Netzwerkfilters abzuschließen. Es wird kein automatischer Neustart ausgelöst."
  ${EndIf}
  SetErrorLevel $CleanupRestart
  Goto un_cleanup_done
  un_cleanup_failed:
    StrCpy $PrerequisiteMessage "Die Systembereinigung wurde nicht abgeschlossen (Fehler $0)."
    IfFileExists "$INSTDIR\uninstall-result.txt" 0 +4
      FileOpen $PrerequisiteLog "$INSTDIR\uninstall-result.txt" r
      FileReadUTF16LE $PrerequisiteLog $PrerequisiteMessage
      FileClose $PrerequisiteLog
    DetailPrint "$PrerequisiteMessage"
    IfSilent +2
      MessageBox MB_ICONEXCLAMATION|MB_OK "$PrerequisiteMessage$\r$\n$\r$\nDie Deinstallation wurde angehalten. DYNEX und der Uninstaller bleiben für einen erneuten Versuch erhalten."
    SetErrorLevel $0
    Abort
  un_cleanup_done:
SectionEnd

Function un.onInit
  ; Reuse the startup gate understood by DYNEX and its background updater.
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\DYNEX.Update") p .r0 ?e'
  Pop $1
  StrCpy $CleanupMutex $0
  ${If} $0 == 0
  ${OrIf} $1 == 183
    Call un.ReleaseCleanupGate
    SetErrorLevel 11
    Abort
  ${EndIf}
FunctionEnd

Function un.ReleaseCleanupGate
  ${If} $CleanupMutex != 0
    System::Call 'kernel32::CloseHandle(p $CleanupMutex)'
    StrCpy $CleanupMutex 0
  ${EndIf}
FunctionEnd

Function un.onGUIEnd
  Call un.ReleaseCleanupGate
FunctionEnd

Function un.onUninstSuccess
  Call un.ReleaseCleanupGate
FunctionEnd

Function un.onUninstFailed
  Call un.ReleaseCleanupGate
FunctionEnd

Function un.StopRunningDynex
  StrCpy $0 0
  IfFileExists "$INSTDIR\DYNEX.exe" 0 un_stop_done
  nsExec::ExecToStack /TIMEOUT=20000 '"$INSTDIR\DYNEX.exe" --shutdown'
  Pop $1
  Pop $2
  ${If} $1 != "error"
  ${AndIf} $1 != "timeout"
    StrCpy $0 $1
  ${Else}
    StrCpy $0 1
  ${EndIf}
  un_stop_done:
FunctionEnd
