; SPDX-License-Identifier: MIT
; The setup program, for Inno Setup 6: one exe that copies winawdl to Program
; Files, then runs install.ps1 -Setup for what can only be done on the PC
; itself (signing the driver, the firewall rules, the startup task). Its
; uninstaller runs uninstall.ps1 -Setup before it removes the files.
; scripts\build-installer.ps1 compiles this from the folder it stages.
#ifndef Version
  #error Version is not defined: compile through scripts\build-installer.ps1
#endif
#ifndef Stage
  #error Stage is not defined: compile through scripts\build-installer.ps1
#endif
#ifndef Output
  #define Output "."
#endif

[Setup]
AppId={{6B0F6E0C-6C5E-4E0B-9B5C-5D7E7C0A2A31}
AppName=winawdl
AppVersion={#Version}
AppVerName=winawdl {#Version}
AppPublisher=winawdl project
AppPublisherURL=https://github.com/raph559/winawdl
AppSupportURL=https://github.com/raph559/winawdl/issues
; The two scripts know this folder and no other.
DefaultDirName={autopf}\winawdl
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; The app holds this while it runs, so setup asks for it to be closed first.
AppMutex=winawdl-app
InfoBeforeFile=before.txt
SetupIconFile=..\app\winawdl.ico
UninstallDisplayIcon={app}\winawdl.exe
UninstallDisplayName=winawdl
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputDir={#Output}
OutputBaseFilename=winawdl-{#Version}-setup
VersionInfoVersion={#Version}
VersionInfoProductName=winawdl
VersionInfoDescription=winawdl setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; The two launchers are for the zip: here setup and its uninstaller do their work.
Source: "{#Stage}\*"; DestDir: "{app}"; Excludes: "install.cmd,uninstall.cmd"; Flags: recursesubdirs ignoreversion
; Needed before anything is copied, to ask what the PC still lacks.
Source: "{#Stage}\install.ps1"; Flags: dontcopy

[Dirs]
; Where the app keeps its logs. Setup's own go there too, and stay after an uninstall.
Name: "{commonappdata}\winawdl\logs"; Flags: uninsneveruninstall

[Icons]
Name: "{autoprograms}\winawdl"; Filename: "{app}\winawdl.exe"; WorkingDir: "{app}"; Comment: "AirDrop on Windows"

[Run]
; As the person who started setup, not as administrator: Explorer cannot drop files on a window that is.
Filename: "{app}\winawdl.exe"; Description: "Start winawdl"; Flags: postinstall nowait skipifsilent runasoriginaluser; Check: SetUpWell

[Code]
const
  CardKey = 'SYSTEM\CurrentControlSet\Enum\PCI';
  CardPrefix = 'VEN_10EC&DEV_C852';

var
  Failed: Boolean;

function LogFolder: String;
begin
  Result := ExpandConstant('{commonappdata}\winawdl\logs');
end;

{ Runs one of the two scripts under Windows PowerShell, out of sight, and
  keeps what it said. The scripts move themselves to the 64-bit PowerShell
  when a 32-bit program starts them, as this one may be. }
function RunScript(const Script, Arguments, LogName: String; var Code: Integer): Boolean;
var
  Command: String;
begin
  ForceDirectories(LogFolder);
  { The list stays on the line of the call: a line that begins with a bracket is read as a section. }
  Command := '/c ""powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%s" %s > "%s\%s" 2>&1"';
  Command := Format(Command, [Script, Arguments, LogFolder, LogName]);
  Result := Exec(ExpandConstant('{cmd}'), Command, '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function LastLines(const LogName: String; Count: Integer): String;
var
  Lines: TArrayOfString;
  I, First: Integer;
begin
  Result := '';
  if not LoadStringsFromFile(LogFolder + '\' + LogName, Lines) then
    exit;
  First := GetArrayLength(Lines) - Count;
  if First < 0 then
    First := 0;
  for I := First to GetArrayLength(Lines) - 1 do
    Result := Result + Lines[I] + #13#10;
end;

{ Every PCI device Windows has ever seen in this PC has a key here, so this
  can say yes for a card that was taken out. install.ps1 looks again, properly. }
function HasCard: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetSubkeyNames(HKEY_LOCAL_MACHINE, CardKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if Pos(CardPrefix, Uppercase(Names[I])) = 1 then
        Result := True;
end;

function InitializeSetup: Boolean;
begin
  Result := HasCard;
  if not Result then
    SuppressibleMsgBox('No Realtek RTL8852CE Wi-Fi card was found in this PC.' + #13#10#13#10 +
                       'winawdl drives that card and no other. Nothing was installed.',
                       mbInformation, MB_OK, IDOK);
end;

{ What install.ps1 -Missing lists: a winget id, a tab, a name, one a line. }
function StillMissing(var Ids, Names: TArrayOfString): Boolean;
var
  Lines: TArrayOfString;
  I, Tab, Count, Code: Integer;
begin
  Count := 0;
  SetArrayLength(Ids, 0);
  SetArrayLength(Names, 0);
  ExtractTemporaryFile('install.ps1');
  RunScript(ExpandConstant('{tmp}\install.ps1'), '-Missing', 'setup-missing.log', Code);
  if LoadStringsFromFile(LogFolder + '\setup-missing.log', Lines) then
    for I := 0 to GetArrayLength(Lines) - 1 do
    begin
      Tab := Pos(#9, Lines[I]);
      if Tab > 1 then
      begin
        SetArrayLength(Ids, Count + 1);
        SetArrayLength(Names, Count + 1);
        Ids[Count] := Copy(Lines[I], 1, Tab - 1);
        Names[Count] := Copy(Lines[I], Tab + 1, Length(Lines[I]));
        Count := Count + 1;
      end;
    end;
  Result := Count > 0;
end;

function Listed(const Names: TArrayOfString): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Names) - 1 do
    Result := Result + '  ' + Names[I] + #13#10;
end;

{ Before anything is copied: what the app runs on has to be there, or be fetched. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Ids, Names: TArrayOfString;
  I, Code: Integer;
begin
  Result := '';
  if not StillMissing(Ids, Names) then
    exit;
  if SuppressibleMsgBox('Not on this PC yet:' + #13#10 + Listed(Names) + #13#10 +
                        'Fetch them now with winget? Each opens a window of its own and asks ' +
                        'for your agreement there.', mbConfirmation, MB_YESNO, IDNO) = IDYES then
    for I := 0 to GetArrayLength(Ids) - 1 do
      Exec(ExpandConstant('{cmd}'), '/c winget install --id ' + Ids[I] + ' -e', '', SW_SHOW,
           ewWaitUntilTerminated, Code);
  if StillMissing(Ids, Names) then
    Result := 'winawdl needs these, and they are not on this PC:' + #13#10 + Listed(Names) + #13#10 +
              'Install them, then start this setup again. Nothing was installed.' + #13#10 +
              'One that was just fetched may only be seen by a setup started afterwards.';
end;

function SetUpWell: Boolean;
begin
  Result := not Failed;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
  Said: AnsiString;
begin
  if CurStep <> ssPostInstall then
    exit;
  WizardForm.StatusLabel.Caption := 'Signing the driver on this PC...';
  WizardForm.FilenameLabel.Caption := '';
  if not RunScript(ExpandConstant('{app}\install.ps1'), '-Yes -Setup', 'install.log', Code) then
    Code := -1;
  if Code <> 0 then
  begin
    Failed := True;
    { Whatever was done is taken back, so that Wi-Fi keeps the card and nothing half set up is left. }
    RunScript(ExpandConstant('{app}\uninstall.ps1'), '-Yes -Setup', 'install-undone.log', Code);
    SuppressibleMsgBox('winawdl could not be set up on this PC:' + #13#10#13#10 +
                       LastLines('install.log', 8) + #13#10 +
                       'Wi-Fi was left with the card. The files are in place: ' +
                       'start this setup again, or uninstall winawdl from Installed apps. ' +
                       'The whole log is in ' + LogFolder + '.', mbError, MB_OK, IDOK);
  end
  else if LoadStringFromFile(LogFolder + '\install.log', Said) and
          (Pos('Lock pages in memory', String(Said)) > 0) then
    SuppressibleMsgBox('The radio will not start yet: this account may not lock pages in memory, ' +
                       'which the radio needs for its buffers.' + #13#10#13#10 +
                       'Give it "Lock pages in memory" under Local Security Policy, Local Policies, ' +
                       'User Rights Assignment, then sign out and in.', mbInformation, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then
    exit;
  if not RunScript(ExpandConstant('{app}\uninstall.ps1'), '-Yes -Setup', 'uninstall.log', Code) then
    Code := -1;
  if Code <> 0 then
    MsgBox('The Wi-Fi card may not have been given back to its own driver:' + #13#10#13#10 +
           LastLines('uninstall.log', 8) + #13#10 +
           'The whole log is in ' + LogFolder + '.', mbError, MB_OK);
end;
