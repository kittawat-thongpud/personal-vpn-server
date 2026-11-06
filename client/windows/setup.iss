#ifndef Architecture
  #define Architecture "x64"
#endif
#ifndef PackageDir
  #define PackageDir "..\..\dist\windows"
#endif
#ifndef AppVersion
  #error Pass /DAppVersion from client/VERSION
#endif
[Setup]
AppId={{5A4D9679-160C-4DAA-A3E8-06409432D512}
AppName=Personal VPN
AppVersion={#AppVersion}
AppPublisher=Personal VPN
DefaultDirName={autopf}\PersonalVPN
UsePreviousAppDir=yes
DefaultGroupName=Personal VPN
OutputDir=..\..\dist
OutputBaseFilename=PersonalVPN-Setup-{#AppVersion}-{#Architecture}
SetupIconFile=app\Assets\personal-vpn.ico
UninstallDisplayIcon={app}\Assets\personal-vpn.ico
PrivilegesRequired=admin
MinVersion=10.0.19041
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
SetupMutex=Global\PersonalVPN-Setup
ArchitecturesInstallIn64BitMode=x64compatible
#if Architecture == "x64"
ArchitecturesAllowed=x64compatible
#endif
[Files]
; A private maintenance copy runs before any installed binary is overwritten.
Source: "{#PackageDir}\*"; DestDir: "{tmp}\PersonalVPN-update"; Flags: dontcopy recursesubdirs createallsubdirs
Source: "{#PackageDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; BeforeInstall: FinalizeUpgradePreparation
[InstallDelete]
#if Architecture == "x64"
Type: files; Name: "{app}\architecture-x86.txt"
#else
Type: files; Name: "{app}\architecture-x64.txt"
#endif
[Icons]
Name: "{group}\Personal VPN"; Filename: "{app}\PersonalVPN.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\personal-vpn.ico"; AppUserModelID: "PersonalVPN.Client"
Name: "{autodesktop}\Personal VPN"; Filename: "{app}\PersonalVPN.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\personal-vpn.ico"; AppUserModelID: "PersonalVPN.Client"
Name: "{userdesktop}\Personal VPN"; Filename: "{app}\PersonalVPN.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\personal-vpn.ico"; AppUserModelID: "PersonalVPN.Client"; Check: RepairUserDesktop
Name: "{userprograms}\Personal VPN"; Filename: "{app}\PersonalVPN.exe"; WorkingDir: "{app}"; IconFilename: "{app}\Assets\personal-vpn.ico"; AppUserModelID: "PersonalVPN.Client"; Check: RepairUserPrograms
[Run]
Filename: "{app}\PersonalVPN.exe"; Parameters: "--wait-for-setup"; Description: "Open Personal VPN"; Flags: nowait postinstall skipifsilent runascurrentuser
[UninstallRun]
Filename: "{app}\PersonalVPN.exe"; Parameters: "--uninstall-adapter"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveOwnedAdapterService"
[Code]
var AdapterPreparationFailed, UpgradePrepared: Boolean;
    LegacyUninstallers: TArrayOfString;
    UserDesktopOwned, UserProgramsOwned: Boolean;

function OwnedShortcut(const Filename: String): Boolean;
var Shell, Link: Variant; Target: String;
begin
  Result := False;
  if not FileExists(Filename) then Exit;
  try
    Shell := CreateOleObject('WScript.Shell'); Link := Shell.CreateShortcut(Filename);
    Target := Link.TargetPath;
    Result := (CompareText(ExtractFileName(Target), 'PersonalVPN.exe') = 0) and
      FileExists(AddBackslash(ExtractFileDir(Target)) + 'CLIENT-VERSION.txt');
  except Log('Could not inspect a previous user shortcut.'); end;
end;
function RepairUserDesktop: Boolean; begin Result := UserDesktopOwned; end;
function RepairUserPrograms: Boolean; begin Result := UserProgramsOwned; end;

procedure AddLegacy(const Uninstaller: String);
var I: Integer; Folder: String;
begin
  Folder := ExtractFileDir(Uninstaller);
  if not FileExists(Uninstaller) or not FileExists(AddBackslash(Folder) + 'PersonalVPN.exe') or
     not FileExists(AddBackslash(Folder) + 'CLIENT-VERSION.txt') then Exit;
  for I := 0 to GetArrayLength(LegacyUninstallers) - 1 do
    if CompareText(LegacyUninstallers[I], Uninstaller) = 0 then Exit;
  I := GetArrayLength(LegacyUninstallers); SetArrayLength(LegacyUninstallers, I + 1);
  LegacyUninstallers[I] := Uninstaller;
end;

procedure FindRegisteredLegacy(Root: Integer);
var Arch, Command, Folder, Key: String; I: Integer;
begin
  for I := 0 to 1 do begin
    if I = 0 then Arch := 'x86' else Arch := 'x64';
    Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{5A4D9679-160C-4DAA-A3E8-06409432D512}-' + Arch + '_is1';
    if RegQueryStringValue(Root, Key, 'UninstallString', Command) and
       RegQueryStringValue(Root, Key, 'Inno Setup: App Path', Folder) then begin
      Command := RemoveQuotes(Command);
      if (CompareText(ExtractFileDir(Command), Folder) = 0) and
         (CompareText(Copy(ExtractFileName(Command), 1, 5), 'unins') = 0) and
         (CompareText(ExtractFileExt(Command), '.exe') = 0) then AddLegacy(Command);
    end;
  end;
end;

procedure FindOrphanedVersions(const Base: String);
var Entry, Uninstaller: TFindRec; Folder, Version: String; VersionBytes: AnsiString;
begin
  if FindFirst(AddBackslash(Base) + '*', Entry) then try
    repeat
      if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0) and
         (Entry.Attributes and $400 = 0) and (Entry.Name <> '.') and (Entry.Name <> '..') then begin
        Folder := AddBackslash(Base) + Entry.Name;
        if LoadStringFromFile(AddBackslash(Folder) + 'CLIENT-VERSION.txt', VersionBytes) then begin
          Version := Trim(String(VersionBytes));
          if (Version <> '') and ((Entry.Name = Version + '-x86') or (Entry.Name = Version + '-x64')) and
             (CompareText(ExpandFileName(ExtractFileDir(Folder)), ExpandFileName(Base)) = 0) then
            if FindFirst(AddBackslash(Folder) + 'unins*.exe', Uninstaller) then try
              repeat AddLegacy(AddBackslash(Folder) + Uninstaller.Name); until not FindNext(Uninstaller);
            finally FindClose(Uninstaller); end;
        end;
      end;
    until not FindNext(Entry);
  finally FindClose(Entry); end;
end;

function InitializeSetup: Boolean;
begin
  FindRegisteredLegacy(HKLM32); if IsWin64 then FindRegisteredLegacy(HKLM64);
  FindOrphanedVersions(ExpandConstant('{commonpf32}\PersonalVPN'));
  if IsWin64 then FindOrphanedVersions(ExpandConstant('{commonpf64}\PersonalVPN'));
  UserDesktopOwned := OwnedShortcut(ExpandConstant('{userdesktop}\Personal VPN.lnk'));
  UserProgramsOwned := OwnedShortcut(ExpandConstant('{userprograms}\Personal VPN.lnk'));
  Result := True;
end;

procedure RegisterExtraCloseApplicationsResources;
var I: Integer; Folder: String;
begin
  for I := 0 to GetArrayLength(LegacyUninstallers) - 1 do begin
    Folder := ExtractFileDir(LegacyUninstallers[I]);
#if VER >= EncodeVer(7, 0, 0)
    RegisterExtraCloseApplicationsResource(AddBackslash(Folder) + 'PersonalVPN.exe');
    RegisterExtraCloseApplicationsResource(AddBackslash(Folder) + 'cores\wintun.dll');
    RegisterExtraCloseApplicationsResource(AddBackslash(Folder) + 'cores\xray.exe');
    RegisterExtraCloseApplicationsResource(AddBackslash(Folder) + 'cores\hysteria.exe');
    RegisterExtraCloseApplicationsResource(AddBackslash(Folder) + 'cores\sing-box.exe');
#else
    RegisterExtraCloseApplicationsResource(Is64BitInstallMode, AddBackslash(Folder) + 'PersonalVPN.exe');
    RegisterExtraCloseApplicationsResource(Is64BitInstallMode, AddBackslash(Folder) + 'cores\wintun.dll');
    RegisterExtraCloseApplicationsResource(Is64BitInstallMode, AddBackslash(Folder) + 'cores\xray.exe');
    RegisterExtraCloseApplicationsResource(Is64BitInstallMode, AddBackslash(Folder) + 'cores\hysteria.exe');
    RegisterExtraCloseApplicationsResource(Is64BitInstallMode, AddBackslash(Folder) + 'cores\sing-box.exe');
#endif
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var ResultCode: Integer;
begin
  ExtractTemporaryFiles('{tmp}\PersonalVPN-update\*');
  Result := '';
  // New clients can disconnect and release the service before Restart Manager
  // looks for mapped binaries. A legacy GUI is left for Restart Manager below.
  if not Exec(ExpandConstant('{tmp}\PersonalVPN-update\PersonalVPN.exe'),
    '--prepare-upgrade --allow-legacy-running', ExpandConstant('{tmp}\PersonalVPN-update'),
    SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    Result := 'Personal VPN could not finish disconnecting. Exit it from its tray menu and retry Setup.';
end;

procedure FinalizeUpgradePreparation;
var ResultCode: Integer; Started: Boolean; Report: AnsiString;
begin
  if UpgradePrepared then Exit;
  // ssInstall runs BEFORE Restart Manager shuts down files-in-use applications.
  // The first installed file's callback runs afterwards, before replacing it.
  Started := Exec(ExpandConstant('{tmp}\PersonalVPN-update\PersonalVPN.exe'),
    '--prepare-upgrade --report "' + ExpandConstant('{tmp}\PersonalVPN-upgrade.json') + '"',
    ExpandConstant('{tmp}\PersonalVPN-update'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Log('Final upgrade preparation result: ' + IntToStr(ResultCode));
  if LoadStringFromFile(ExpandConstant('{tmp}\PersonalVPN-upgrade.json'), Report) then
    Log('Upgrade preparation: ' + Copy(String(Report), 1, 2048));
  if not Started or (ResultCode <> 0) then
    RaiseException('Personal VPN could not finish disconnecting. Disconnect and exit it from its tray menu, then retry Setup.');
  UpgradePrepared := True;
end;

procedure RemoveLegacyRegistrations(Root: Integer);
var I: Integer; Arch, Key, Folder: String;
begin
  for I := 0 to 1 do begin
    if I = 0 then Arch := 'x86' else Arch := 'x64';
    Key := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{5A4D9679-160C-4DAA-A3E8-06409432D512}-' + Arch + '_is1';
    if RegQueryStringValue(Root, Key, 'Inno Setup: App Path', Folder) then
      if (CompareText(Folder, ExpandConstant('{app}')) = 0) or not FileExists(AddBackslash(Folder) + 'PersonalVPN.exe') then
        RegDeleteKeyIncludingSubkeys(Root, Key);
  end;
end;

procedure RetireLegacy;
var I, ResultCode: Integer; Links, Backups: TArrayOfString;
begin
  SetArrayLength(Links, 4); SetArrayLength(Backups, 4);
  Links[0] := ExpandConstant('{autodesktop}\Personal VPN.lnk');
  Links[1] := ExpandConstant('{group}\Personal VPN.lnk');
  Links[2] := ExpandConstant('{userdesktop}\Personal VPN.lnk');
  Links[3] := ExpandConstant('{userprograms}\Personal VPN.lnk');
  for I := 0 to 3 do begin
    Backups[I] := ExpandConstant('{tmp}\PersonalVPN-link-') + IntToStr(I) + '.lnk';
    if FileExists(Links[I]) and not FileCopy(Links[I], Backups[I], False) then RaiseException('Could not preserve the updated shortcut.');
  end;
  try
    for I := 0 to GetArrayLength(LegacyUninstallers) - 1 do
      // Never run a legacy uninstall log against the new installation directory.
      if CompareText(ExpandFileName(ExtractFileDir(LegacyUninstallers[I])), ExpandFileName(ExpandConstant('{app}'))) <> 0 then
        if FileExists(LegacyUninstallers[I]) then
          if not Exec(LegacyUninstallers[I], '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-',
            ExtractFileDir(LegacyUninstallers[I]), SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
            Log('A legacy uninstall did not finish; its files were left intact.');
  finally
    // Old uninstallers own the old shortcut names too. Restore the newly-created
    // links byte-for-byte, including the explicit icon and AppUserModelID.
    for I := 0 to 3 do if FileExists(Backups[I]) then begin
      ForceDirectories(ExtractFileDir(Links[I]));
      if not FileCopy(Backups[I], Links[I], False) then RaiseException('Could not restore the updated shortcut.');
    end;
  end;
  RemoveLegacyRegistrations(HKLM32); if IsWin64 then RemoveLegacyRegistrations(HKLM64);
end;

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
    RetireLegacy;
    if not Exec(ExpandConstant('{app}\PersonalVPN.exe'),
      '--install-adapter --report "' + ExpandConstant('{app}\driver-install.json') + '"',
      ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
      RaiseException('Could not prepare the signed Wintun driver.');
    if ResultCode <> 0 then RaiseException('Wintun installation failed. Run the setup as Administrator.');
    AdapterPreparationFailed := False;
    Exec(ExpandConstant('{app}\PersonalVPN.exe'), '--refresh-shortcuts', ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;
