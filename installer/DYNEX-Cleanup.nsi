Unicode true
RequestExecutionLevel user
ManifestDPIAware true
!include "LogicLib.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "1.0.6"
!endif
!ifndef PUBLISH_ROOT
  !error "PUBLISH_ROOT fehlt; bitte scripts/build.ps1 verwenden."
!endif
!ifndef OUTPUT_FILE
  !error "OUTPUT_FILE fehlt."
!endif

; Optional Authenticode signing. scripts/build.ps1 passes SIGN_SCRIPT when a
; code-signing certificate is configured; %1 is the file NSIS just produced.
!ifdef SIGN_SCRIPT
  !finalize '"${SIGN_SCRIPT}" "%1"' = 0
!endif
Name "DYNEX · Reste entfernen"
OutFile "${OUTPUT_FILE}"
VIProductVersion "${APP_VERSION}.0"
VIAddVersionKey "ProductName" "DYNEX · Reste entfernen"
VIAddVersionKey "FileDescription" "Bereinigung einer bereits entfernten DYNEX-Installation"
VIAddVersionKey "FileVersion" "${APP_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_VERSION}"
VIAddVersionKey "LegalCopyright" "Copyright © 2026 DYNEX"
SetCompressor /SOLID lzma
ShowInstDetails show
AutoCloseWindow false
Page instfiles

Var CleanupMutex
Var CleanupExit
Var CleanupRestart
Var CleanupMessage
Var ResultHandle

Function .onInit
  IfSilent cleanup_confirmed
  MessageBox MB_OKCANCEL|MB_ICONINFORMATION|MB_DEFBUTTON2 "DYNEX bereits deinstalliert?$\r$\n$\r$\nDieses Programm entfernt zurückgebliebene DYNEX-Netzwerkeinstellungen, Autostart, Verknüpfungen, Einstellungen, Kopplungen und Update-Dateien für diesen Windows-Benutzer. Es installiert DYNEX nicht erneut.$\r$\n$\r$\nBei älteren Installationen wird vor dem Entfernen von Windows Packet Filter gesondert gefragt. Die Verbindung kann dabei kurz abbrechen. Vorhandene fremde Netzwerkkomponenten werden nicht pauschal zurückgesetzt." IDOK cleanup_confirmed
  SetErrorLevel 1602
  Abort
  cleanup_confirmed:
  System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\DYNEX.Update") p .r0 ?e'
  Pop $1
  StrCpy $CleanupMutex $0
  ${If} $0 == 0
  ${OrIf} $1 == 183
    Call ReleaseCleanupGate
    IfSilent +2
      MessageBox MB_ICONEXCLAMATION|MB_OK "Eine DYNEX-Installation, Aktualisierung oder Bereinigung läuft bereits. Bitte erst abschließen."
    SetErrorLevel 11
    Abort
  ${EndIf}
FunctionEnd

Function ReleaseCleanupGate
  ${If} $CleanupMutex != 0
    System::Call 'kernel32::CloseHandle(p $CleanupMutex)'
    StrCpy $CleanupMutex 0
  ${EndIf}
FunctionEnd

Function .onGUIEnd
  Call ReleaseCleanupGate
FunctionEnd

Function .onInstSuccess
  Call ReleaseCleanupGate
FunctionEnd

Function .onInstFailed
  Call ReleaseCleanupGate
FunctionEnd

Section "DYNEX-Reste entfernen"
  ; The self-contained cleanup entry points never start the desktop, install
  ; prerequisites or accept arbitrary target paths. NSIS removes this unique
  ; temporary extraction directory after the process exits.
  InitPluginsDir
  SetOutPath "$PLUGINSDIR"
  File /oname=DYNEX.exe "${PUBLISH_ROOT}\DYNEX.exe"
  StrCpy $CleanupRestart 0
  DetailPrint "Zurückgebliebene Netzwerkeinstellungen prüfen ..."
  ClearErrors
  ExecWait '"$PLUGINSDIR\DYNEX.exe" --cleanup-removed-installation' $CleanupExit
  ${If} ${Errors}
    StrCpy $CleanupExit 1
  ${EndIf}
  ${If} $CleanupExit == 5201
    IfSilent cleanup_failed
    MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Windows Packet Filter ist noch installiert. Ältere DYNEX-Versionen haben nicht gespeichert, ob er vorher schon vorhanden war.$\r$\n$\r$\nSoll dieses Netzwerkfilter-Paket jetzt ebenfalls entfernt werden? Die Verbindung kann kurz abbrechen. Andere Programme, die diesen Filter benötigen, können danach nicht mehr damit arbeiten." IDNO cleanup_failed
    ClearErrors
    ExecWait '"$PLUGINSDIR\DYNEX.exe" --cleanup-removed-installation --confirm-legacy-filter-removal' $CleanupExit
    ${If} ${Errors}
      StrCpy $CleanupExit 1
    ${EndIf}
  ${EndIf}
  ${If} $CleanupExit == 3010
    StrCpy $CleanupRestart 3010
  ${ElseIf} $CleanupExit != 0
    Goto cleanup_failed
  ${EndIf}
  StrCpy $CleanupMessage "Systembereinigung abgeschlossen."
  IfFileExists "$PLUGINSDIR\cleanup-result.txt" 0 +4
    FileOpen $ResultHandle "$PLUGINSDIR\cleanup-result.txt" r
    FileReadUTF16LE $ResultHandle $CleanupMessage
    FileClose $ResultHandle
  DetailPrint "$CleanupMessage"
  DetailPrint "DYNEX-Benutzerdaten und Autostart entfernen ..."
  Delete "$PLUGINSDIR\user-cleanup-result.txt"
  ClearErrors
  ExecWait '"$PLUGINSDIR\DYNEX.exe" --remove-user-data' $CleanupExit
  ${If} ${Errors}
    StrCpy $CleanupExit 1
  ${EndIf}
  ${If} $CleanupExit != 0
    StrCpy $CleanupMessage "Benutzerdaten konnten nicht vollständig entfernt werden (Fehler $CleanupExit)."
    IfFileExists "$PLUGINSDIR\user-cleanup-result.txt" 0 +4
      FileOpen $ResultHandle "$PLUGINSDIR\user-cleanup-result.txt" r
      FileReadUTF16LE $ResultHandle $CleanupMessage
      FileClose $ResultHandle
    Goto cleanup_show_failure
  ${EndIf}
  StrCpy $CleanupMessage "DYNEX-Benutzerdaten wurden bereinigt."
  IfFileExists "$PLUGINSDIR\user-cleanup-result.txt" 0 +4
    FileOpen $ResultHandle "$PLUGINSDIR\user-cleanup-result.txt" r
    FileReadUTF16LE $ResultHandle $CleanupMessage
    FileClose $ResultHandle
  DetailPrint "$CleanupMessage"
  ReadRegStr $0 HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX" "InstallLocation"
  ${If} $0 == "$LOCALAPPDATA\Programs\DYNEX"
    DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX"
  ${EndIf}
  ${If} $CleanupRestart == 3010
    DetailPrint "Bereinigung abgeschlossen. Bitte Windows neu starten, um die Filterentfernung abzuschließen."
    IfSilent +2
      MessageBox MB_ICONINFORMATION|MB_OK "Die DYNEX-Reste wurden bereinigt. Windows muss noch neu gestartet werden, um die Entfernung des Netzwerkfilters abzuschließen. Es wird kein automatischer Neustart ausgelöst."
  ${Else}
    DetailPrint "Bereinigung abgeschlossen."
  ${EndIf}
  SetErrorLevel $CleanupRestart
  Goto cleanup_done
  cleanup_failed:
    StrCpy $CleanupMessage "Die Systembereinigung wurde nicht abgeschlossen (Fehler $CleanupExit)."
    IfFileExists "$PLUGINSDIR\cleanup-result.txt" 0 +4
      FileOpen $ResultHandle "$PLUGINSDIR\cleanup-result.txt" r
      FileReadUTF16LE $ResultHandle $CleanupMessage
      FileClose $ResultHandle
  cleanup_show_failure:
    DetailPrint "$CleanupMessage"
    IfSilent +2
      MessageBox MB_ICONEXCLAMATION|MB_OK "$CleanupMessage$\r$\n$\r$\nDie Bereinigung wurde angehalten. Du kannst dieses Programm nach Beheben der Ursache erneut ausführen."
    SetErrorLevel $CleanupExit
    Abort
  cleanup_done:
SectionEnd
