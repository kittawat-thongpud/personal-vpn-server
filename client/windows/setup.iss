#ifndef Architecture
  #define Architecture "x64"
#endif
#ifndef PackageDir
  #define PackageDir "..\..\dist\windows"
#endif
#define AppVersion "0.6.0"
[Setup]
AppId={{5A4D9679-160C-4DAA-A3E8-06409432D512}-{#Architecture}
AppName=Personal VPN
AppVersion={#AppVersion}
AppPublisher=Personal VPN
DefaultDirName={autopf}\PersonalVPN\{#AppVersion}-{#Architecture}
DefaultGroupName=Personal VPN
OutputDir=..\..\dist
OutputBaseFilename=PersonalVPN-Setup-{#AppVersion}-{#Architecture}
SetupIconFile=app\Assets\personal-vpn.ico
UninstallDisplayIcon={app}\PersonalVPN.exe
PrivilegesRequired=admin
MinVersion=10.0.19041
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
#if Architecture == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif
[Files]
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Personal VPN"; Filename: "{app}\PersonalVPN.exe"; WorkingDir: "{app}"; IconFilename: "{app}\PersonalVPN.exe"
Name: "{autodesktop}\Personal VPN"; Filename: "{app}\PersonalVPN.exe"; WorkingDir: "{app}"; IconFilename: "{app}\PersonalVPN.exe"
[Run]
Filename: "{app}\PersonalVPN.exe"; Description: "Open Personal VPN"; Flags: nowait postinstall skipifsilent runascurrentuser
[UninstallRun]
Filename: "{app}\PersonalVPN.exe"; Parameters: "--uninstall-adapter"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveOwnedAdapterService"
[Code]
var AdapterPreparationFailed: Boolean;

function GetCustomSetupExitCode: Integer;
begin
  if AdapterPreparationFailed then Result := 1 else Result := 0;
end;
procedure CurStepChanged(CurStep: TSetupStep);
var ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    AdapterPreparationFailed := True;
    if not Exec(ExpandConstant('{app}\PersonalVPN.exe'),
      '--install-adapter --report "' + ExpandConstant('{app}\driver-install.json') + '"',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException('Could not prepare the signed Wintun driver.');
    if ResultCode <> 0 then RaiseException('Wintun installation failed. Run the setup as Administrator.');
    AdapterPreparationFailed := False;
  end;
end;
