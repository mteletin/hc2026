; HC2000 Emu Windows Installer (NSIS)
; Built by deploy/emu/downloads.sh on the Linux builder.
; Variables passed via -D:
;   HC2000_VERSION     - version string, e.g. 0.1.62
;   HC2000_VERSION_4   - 4-part version for VIProductVersion, e.g. 0.1.62.0
;   PAYLOAD_DIR        - directory containing app/ and roms/
;   OUTFILE            - absolute path for the output installer
;
; Layout expected in PAYLOAD_DIR:
;   app/          - dotnet publish output (self-contained win-x64)
;   roms/         - ROM files to copy to %APPDATA%\hc2000\roms
;   disks/        - disk images (CPM22-HC.DSK) to copy to %APPDATA%\hc2000\disks

!ifndef HC2000_VERSION
  !define HC2000_VERSION "0.0.0-dev"
!endif
!ifndef HC2000_VERSION_4
  !define HC2000_VERSION_4 "${HC2000_VERSION}.0"
!endif
!ifndef PAYLOAD_DIR
  !define PAYLOAD_DIR "."
!endif
!ifndef OUTFILE
  !define OUTFILE "hc2000-emu-${HC2000_VERSION}-setup.exe"
!endif

Name "HC2000 Emu"
OutFile "${OUTFILE}"
RequestExecutionLevel user
Unicode true

; ------------------------------------------------------------------
; MUI2
; ------------------------------------------------------------------
!include "MUI2.nsh"
!include "LogicLib.nsh"

!define MUI_ABORTWARNING

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

; Default install dir: %LOCALAPPDATA%\Programs\HC2000
!define HC2000_DEFAULT_DIR '$LOCALAPPDATA\Programs\HC2000'
!define MUI_DIRECTORYPAGE_DEFAULTDIR '${HC2000_DEFAULT_DIR}'

; ------------------------------------------------------------------
; Custom page: upgrade info (shown after welcome, before directory)
; ------------------------------------------------------------------
!define MUI_PAGE_CUSTOMFUNCTION_SHOW "ShowUpgradeInfo"
Page custom "" ""

Function ShowUpgradeInfo
  ReadRegStr $R0 HKCU "Software\HC2000" "Version"
  ReadRegStr $R1 HKCU "Software\HC2000" "InstallDir"
  StrCmp $R0 "" no_upgrade_info
  StrCmp $R1 "" no_upgrade_info
  MessageBox MB_OK|MB_ICONINFORMATION \
    "HC2000 $R0 is installed in $R1.$\r$\nThis will upgrade it to ${HC2000_VERSION}.$\r$\nUser data (settings, ROMs, disk/tape/snapshot images) will not be touched."
no_upgrade_info:
FunctionEnd

; ------------------------------------------------------------------
; Install section
; ------------------------------------------------------------------
Section "Install" SEC01
  ; --- Upgrade: remove previous program files (app/ subfolder only) ---
  ReadRegStr $R0 HKCU "Software\HC2000" "Version"
  StrCmp $R0 "" no_prev_install
  # Previous install exists: remove its program files
  # The app is in $INSTDIR\app — safe to RMDir /r
  RMDir /r "$INSTDIR\app"
  Delete "$INSTDIR\Uninstall.exe"
no_prev_install:

  ; --- Copy new files ---
  SetOutPath "$INSTDIR\app"
  File /r "${PAYLOAD_DIR}\app\*.*"

  ; --- Copy ROMs to %APPDATA%\hc2000\roms (copy-if-absent) ---
  CreateDirectory "$APPDATA\hc2000\roms"
  SetOutPath "$APPDATA\hc2000\roms"
  SetOverwrite off
  File /r "${PAYLOAD_DIR}\roms\*.*"
  SetOverwrite on

  ; --- Copy G1 disk image to %APPDATA%\hc2000\disks (copy-if-absent) ---
  ; The one installed disk location the --disk resolver checks (spec v4.2):
  ; a fresh install must run `hc2000 --disk A:CPM22-HC.DSK --start` with no
  ; setup. An existing user copy is never touched.
  CreateDirectory "$APPDATA\hc2000\disks"
  SetOutPath "$APPDATA\hc2000\disks"
  SetOverwrite off
  File "${PAYLOAD_DIR}\disks\CPM22-HC.DSK"
  ; Mandatory generated artifacts: File refuses missing media at package time.
  File "${PAYLOAD_DIR}\disks\HC2000-UTIL.DSK"
  File "${PAYLOAD_DIR}\disks\HC2000-UTIL-8M.DSK"
  File "${PAYLOAD_DIR}\disks\CPM22-HC-ram-paged-upgrade.DSK"
  File "${PAYLOAD_DIR}\disks\utilities-manifest.json"
  File "${PAYLOAD_DIR}\disks\m-v1-source-fit.txt"
  SetOverwrite on

  ; --- Registry (HKCU only) ---
  WriteRegStr HKCU "Software\HC2000" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "Software\HC2000" "Version" "${HC2000_VERSION}"

  ; --- Uninstall entry (Add/Remove Programs) ---
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HC2000" "DisplayName" "HC2000 Emu"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HC2000" "DisplayVersion" "${HC2000_VERSION}"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HC2000" "Publisher" "In-house"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HC2000" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HC2000" "UninstallString" '$\"$INSTDIR\Uninstall.exe$\"'

  ; --- Shortcuts ---
  CreateDirectory "$APPDATA\Microsoft\Windows\Start Menu\Programs\HC2000"
  CreateShortcut "$APPDATA\Microsoft\Windows\Start Menu\Programs\HC2000\HC2000 Emu.lnk" "$INSTDIR\app\Hc2000.App.exe"
  CreateShortcut "$DESKTOP\HC2000 Emu.lnk" "$INSTDIR\app\Hc2000.App.exe"

  ; --- Uninstaller ---
  WriteUninstaller "$INSTDIR\Uninstall.exe"
SectionEnd

; ------------------------------------------------------------------
; Uninstall section
; ------------------------------------------------------------------
Section "Uninstall"
  ; Delete program files
  RMDir /r "$INSTDIR"

  ; Delete shortcuts
  Delete "$APPDATA\Microsoft\Windows\Start Menu\Programs\HC2000\HC2000 Emu.lnk"
  RMDir "$APPDATA\Microsoft\Windows\Start Menu\Programs\HC2000"
  Delete "$DESKTOP\HC2000 Emu.lnk"

  ; Delete registry entries (HKCU only)
  DeleteRegKey HKCU "Software\HC2000"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\HC2000"

  ; Inform user that data was kept
  MessageBox MB_OK|MB_ICONINFORMATION \
    "HC2000 Emu has been uninstalled.$\r$\nUser data was kept:$\r$\n  Settings: $APPDATA\hc2000$\r$\n  ROMs: $APPDATA\hc2000\roms$\r$\n  Disk/tape/snapshot images: wherever you stored them."
SectionEnd
