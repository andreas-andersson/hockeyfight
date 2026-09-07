; HockeyFight.iss
; Inno Setup script for the Hockey Fight screensaver.
;
; Windows discovers screensavers by scanning System32 for *.scr, so that is where
; the file goes; there is no application directory to create. Build it with:
;
;   ISCC.exe /DAppVersion=1.2.3 /DSourceScr="..\dist\Hockey Fight.scr" HockeyFight.iss
;
; The .scr is published self-contained, so the installed machine does not need the
; .NET runtime.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef SourceScr
  #define SourceScr "..\dist\Hockey Fight.scr"
#endif

#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "Hockey Fight"
#define SaverName "Hockey Fight.scr"

[Setup]
AppId={{C942AF8B-F295-4C17-9F40-1265EE035555}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=Hockey Fight
UninstallDisplayName={#AppName} Screensaver
UninstallDisplayIcon={sys}\{#SaverName}

; The screensaver is the only payload and it lives in System32, so skip the
; directory and Start Menu pages entirely.
CreateAppDir=no
DisableProgramGroupPage=yes

; Writing to System32 needs elevation.
PrivilegesRequired=admin

; Without this the installer runs as 32-bit and {sys} silently redirects to
; SysWOW64, where Windows will not find the screensaver.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

OutputDir={#OutputDir}
OutputBaseFilename=HockeyFight-{#AppVersion}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "setcurrent"; Description: "Make Hockey Fight the current screen saver"

[Files]
; restartreplace/uninsrestartdelete cover the case where the screensaver is
; running while it is being replaced or removed.
Source: "{#SourceScr}"; DestDir: "{sys}"; DestName: "{#SaverName}"; \
    Flags: ignoreversion restartreplace uninsrestartdelete

[Registry]
Root: HKCU; Subkey: "Control Panel\Desktop"; ValueType: string; \
    ValueName: "SCRNSAVE.EXE"; ValueData: "{sys}\{#SaverName}"; Tasks: setcurrent
Root: HKCU; Subkey: "Control Panel\Desktop"; ValueType: string; \
    ValueName: "ScreenSaveActive"; ValueData: "1"; Tasks: setcurrent

[Code]
// Stop a running instance so the file is not locked during install or uninstall.
procedure StopScreensaver();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM "Hockey Fight.scr"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopScreensaver();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopScreensaver();
  Result := True;
end;

// Clear the "current screen saver" setting on uninstall, but only when it still
// points at this screensaver -- if the user has since picked another one, that
// choice is theirs to keep.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Current: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    if RegQueryStringValue(HKCU, 'Control Panel\Desktop', 'SCRNSAVE.EXE', Current) then
    begin
      if CompareText(Trim(Current), ExpandConstant('{sys}\{#SaverName}')) = 0 then
      begin
        RegDeleteValue(HKCU, 'Control Panel\Desktop', 'SCRNSAVE.EXE');
        RegWriteStringValue(HKCU, 'Control Panel\Desktop', 'ScreenSaveActive', '0');
      end;
    end;
  end;
end;
