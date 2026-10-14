Unicode true
RequestExecutionLevel user
ManifestDPIAware true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "FileFunc.nsh"

!ifndef APP_VERSION
  !define APP_VERSION "0.5.0"
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

Function ReleaseUpdateGate
  ${If} $UpdateMutex != 0
    System::Call 'kernel32::CloseHandle(p $UpdateMutex)'
    StrCpy $UpdateMutex 0
  ${EndIf}
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
  Call RestartAfterUpdate
FunctionEnd

Function .onGUIEnd
  Call ReleaseUpdateGate
FunctionEnd

Function .onInit
  StrCpy $UpdateMutex 0
  ${GetParameters} $3
  ${GetOptions} $3 "/DYNEXUPDATE=" $UpdateParent
  ${GetOptions} $3 "/DYNEXTRAY=" $UpdateTray
  ${If} $UpdateParent != ""
    ; The update gate blocks fresh application launches until the file copy
    ; completes. The parent only invokes this mode before starting its worker.
    System::Call 'kernel32::CreateMutexW(p 0, i 0, w "Local\DYNEX.Update") p .r4 ?e'
    Pop $5
    StrCpy $UpdateMutex $4
    ${If} $4 == 0
    ${OrIf} $5 == 183
      Call ReleaseUpdateGate
      SetErrorLevel 11
      Abort
    ${EndIf}
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
  SetOutPath "$INSTDIR"
  File "${PUBLISH_ROOT}\DYNEX.exe"
  SetOutPath "$INSTDIR\shaper"
  File /r "${PUBLISH_ROOT}\shaper\*"
  !insertmacro InstallDocumentation
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
SectionEnd

Section /o "Desktop-Verknüpfung" SEC_DESKTOP
  CreateShortcut "$DESKTOP\DYNEX.lnk" "$INSTDIR\DYNEX.exe"
SectionEnd

Section "Uninstall"
  Call un.StopRunningDynex
  ${If} $0 != 0
    MessageBox MB_ICONEXCLAMATION|MB_OK "DYNEX konnte nicht sauber beendet werden. Die Deinstallation wird abgebrochen, damit laufende Dateien nicht beschädigt werden."
    Abort
  ${EndIf}
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "DYNEX"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\DYNEX"
  DeleteRegKey HKCU "Software\DYNEX"
  Delete "$SMPROGRAMS\DYNEX\DYNEX.lnk"
  Delete "$SMPROGRAMS\DYNEX\Installation.lnk"
  RMDir "$SMPROGRAMS\DYNEX"
  Delete "$DESKTOP\DYNEX.lnk"
  MessageBox MB_YESNO|MB_ICONQUESTION|MB_DEFBUTTON2 "Soll das lokale DYNEX-Profil mit Einstellungen und Kopplungsschlüsseln gelöscht werden? Bei Nein bleibt es für eine spätere Neuinstallation erhalten." IDNO keep_profile
  RMDir /r "$LOCALAPPDATA\DYNEX"
  keep_profile:
  !insertmacro DeleteInstalledFiles
  Delete "$INSTDIR\Uninstall.exe"
  RMDir "$INSTDIR"
SectionEnd

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
