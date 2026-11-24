#ifndef AppVersion
  #error AppVersion must be supplied by tools/build-release.ps1
#endif
#ifndef PayloadDir
  #error PayloadDir must be supplied by tools/build-release.ps1
#endif
#ifndef ReleaseDir
  #error ReleaseDir must be supplied by tools/build-release.ps1
#endif
#ifndef VariantName
  #error VariantName must be supplied by tools/build-release.ps1
#endif
#ifndef SelfContained
  #error SelfContained must be supplied by tools/build-release.ps1
#endif
#ifndef DistributionTermsSha256
  #error DistributionTermsSha256 must be supplied by tools/build-release.ps1
#endif

[Setup]
#if Ver >= EncodeVer(7, 0, 0)
SetupArchitecture=x86
#endif
AppId={{BD06D8DD-F49B-46BD-AC3D-6581D6C8DBB8}
AppName=Pulse Reader
AppVersion={#AppVersion}
AppVerName=Pulse Reader {#AppVersion}
AppPublisher=Quantum Dot Games
DefaultDirName={code:GetInstallationDirectory}
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#ReleaseDir}
OutputBaseFilename=PulseReader-{#AppVersion}-{#VariantName}-Setup
SetupIconFile=..\src\PulseReader\Assets\TrayIcon\pulse-reader.ico
UninstallDisplayIcon={app}\PulseReader.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile={#PayloadDir}\Distribution-Terms.txt
AppMutex=Local\PulseReader.7A6B7D15-2B8D-4B2F-9A04-899F056E35CD
CloseApplications=no
RestartApplications=no
Uninstallable=yes
SignedUninstaller=no

[Tasks]
Name: desktopicon; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "installation-scope.json"; Flags: ignoreversion recursesubdirs createallsubdirs

[Dirs]
Name: "{app}\OptionalRuntime"; Check: IsAdminInstallMode; Flags: uninsneveruninstall
Name: "{commonappdata}\PulseReader"; Check: IsAdminInstallMode; Flags: uninsneveruninstall

[Icons]
Name: "{autoprograms}\Pulse Reader"; Filename: "{app}\PulseReader.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\Pulse Reader"; Filename: "{app}\PulseReader.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\QuantumDotGames\PulseReader"; ValueType: string; ValueName: "AcceptedDistributionTermsSha256"; ValueData: "{#DistributionTermsSha256}"; Check: not IsAdminInstallMode

[Run]
; Allow the saved original user after UAC only when its token is actually non-administrative.
Filename: "{app}\PulseReader.exe"; Description: "Launch Pulse Reader"; Check: CanLaunchAsOriginalUser; Flags: nowait postinstall skipifsilent unchecked runasoriginaluser

[UninstallDelete]
Type: files; Name: "{app}\pending-payload-cleanup.txt"
Type: files; Name: "{app}\installation-scope.json"
Type: dirifempty; Name: "{app}\OptionalRuntime"; Check: IsAdminInstallMode and SafeEmptyDirectory('{app}\OptionalRuntime')
Type: dirifempty; Name: "{commonappdata}\PulseReader\ContextualPronunciation"; Check: IsAdminInstallMode and SafeEmptyDirectory('{commonappdata}\PulseReader\ContextualPronunciation')
Type: dirifempty; Name: "{commonappdata}\PulseReader"; Check: IsAdminInstallMode and SafeEmptyDirectory('{commonappdata}\PulseReader')
Type: dirifempty; Name: "{app}"; Check: SafeEmptyDirectory('{app}')

[Code]
type
  TNativeSecurityAttributes = record
    Length: LongWord;
    SecurityDescriptor: UINT_PTR;
    InheritHandle: LongWord;
  end;
const
  StartupKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  DesktopRuntimeKey = 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';
  RuntimeUrl = 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0';
  CleanupStateFileName = 'pending-payload-cleanup.txt';
var
  PreviousPayloadFiles: TArrayOfString;
  CleanupFailed: Boolean;
  UninstallDataChoice: String;
  OriginalLaunchChecked, OriginalLaunchAllowed: Boolean;

function GetFileAttributesW(const FileName: String): LongWord;
  external 'GetFileAttributesW@kernel32.dll stdcall';
function ConvertStringSecurityDescriptorToSecurityDescriptorW(const Sddl: String; Revision: LongWord;
  var Descriptor: UINT_PTR; var DescriptorSize: LongWord): Boolean;
  external 'ConvertStringSecurityDescriptorToSecurityDescriptorW@advapi32.dll stdcall';
function CreateDirectoryW(const DirectoryName: String; var Attributes: TNativeSecurityAttributes): Boolean;
  external 'CreateDirectoryW@kernel32.dll stdcall';
function LocalFree(Handle: UINT_PTR): UINT_PTR;
  external 'LocalFree@kernel32.dll stdcall';
function GetVolumeNameForVolumeMountPointW(const MountPoint: String; VolumeName: String; BufferLength: LongWord): Boolean;
  external 'GetVolumeNameForVolumeMountPointW@kernel32.dll stdcall';

function GetInstallationDirectory(Param: String): String;
begin
  if IsAdminInstallMode then Result := ExpandConstant('{commonpf64}\PulseReader')
  else Result := ExpandConstant('{localappdata}\Programs\PulseReader');
end;

function CanLaunchAsOriginalUser: Boolean;
var
  ExitCode: Integer;
begin
  if not OriginalLaunchChecked then
  begin
    OriginalLaunchChecked := True;
    OriginalLaunchAllowed := False;
    if not WizardSilent then
    begin
      if not IsAdmin then OriginalLaunchAllowed := True
      else
      begin
        try
          { A token query only. No user profile, app settings or registry values are created. }
          OriginalLaunchAllowed := ExecAsOriginalUser(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
            '-NoLogo -NoProfile -NonInteractive -Command "$ErrorActionPreference=''Stop''; try { ' +
            '$p=[System.Security.Principal.WindowsPrincipal]::new([System.Security.Principal.WindowsIdentity]::GetCurrent()); ' +
            'if($p.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) { exit 1 } else { exit 0 } ' +
            '} catch { exit 2 }"',
            '', SW_HIDE, ewWaitUntilTerminated, ExitCode) and (ExitCode = 0);
        except
          Log('Original-user token could not be verified; automatic launch remains unavailable.');
        end;
      end;
    end;
    Log('Verified non-administrative original-user launch token: ' + IntToStr(Ord(OriginalLaunchAllowed)));
  end;
  Result := OriginalLaunchAllowed;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and not CanLaunchAsOriginalUser then
    WizardForm.FinishedLabel.Caption := 'Pulse Reader is installed. Open it from the Start menu in your normal Windows session to create your own settings and review the terms.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CompareText(RemoveBackslashUnlessRoot(WizardDirValue), GetInstallationDirectory('')) <> 0 then
    Result := 'Pulse Reader must use the canonical directory for the selected installation scope: ' + GetInstallationDirectory('');
end;

function HasDesktopRuntime10: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  { .NET records every architecture under the 32-bit registry view; x64 is in the key path. }
  if RegGetValueNames(HKLM32, DesktopRuntimeKey, Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if (Copy(Names[I], 1, 5) = '10.0.') and (Pos('-', Names[I]) = 0) then
        Result := True;
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if ExpandConstant('{param:PULSEREADERUPDATE|}') = '1' then
  begin
    if CompareText(ExpandConstant('{param:UPDATEACCEPTEDTERMS|}'), '{#DistributionTermsSha256}') <> 0 then
    begin
      Log('Update installation requires acceptance of the exact new distribution terms.');
      Result := False;
      Exit;
    end;
  end
  else if WizardSilent and (CompareText(ExpandConstant('{param:ACCEPTDISTRIBUTIONTERMS|}'), 'yes') <> 0) then
  begin
    Log('Silent installation requires /ACCEPTDISTRIBUTIONTERMS=yes after reviewing Distribution-Terms.txt.');
    Result := False;
    Exit;
  end;
  #if SelfContained == "0"
  if not HasDesktopRuntime10 then
  begin
    Result := False;
    Log('Required Microsoft .NET 10 Windows Desktop Runtime x64 was not found. Install the runtime or use the self-contained Pulse Reader package.');
    if WizardSilent then Exit;
    if SuppressibleMsgBox('This smaller package requires the Microsoft .NET 10 Windows Desktop Runtime for x64.' + #13#10 + #13#10 +
      'Install that runtime, then run this installer again. You can also use the standard self-contained Pulse Reader installer.' + #13#10 + #13#10 +
      'Open the official .NET 10 download page now? Select Windows Desktop Runtime and Windows x64.',
      mbInformation, MB_YESNO, IDNO) = IDYES then
      ShellExec('open', RuntimeUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
  #endif
end;

function SafePayloadPath(const Path: String): Boolean;
begin
  Result := (Path <> '') and (Pos('..', Path) = 0) and (Pos(':', Path) = 0) and
    (Copy(Path, 1, 1) <> '\') and (Copy(Path, 1, 1) <> '/') and (Pos('/', Path) = 0);
end;

function ListedInPayload(const Path: String; const Files: TArrayOfString): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(Files) - 1 do
    if CompareText(Path, Files[I]) = 0 then Result := True;
end;

function NoReparsePointsOnPath(const FileName: String): Boolean;
var
  Cursor, Parent: String;
  Attributes: LongWord;
begin
  Result := False;
  Cursor := FileName;
  while Cursor <> '' do
  begin
    Attributes := GetFileAttributesW(Cursor);
    if (Attributes = $FFFFFFFF) or ((Attributes and $400) <> 0) then Exit;
    Parent := ExtractFileDir(Cursor);
    if Parent = Cursor then Break;
    Cursor := Parent;
  end;
  Result := True;
end;

function SafeEmptyDirectory(const DirectoryName: String): Boolean;
var
  Path: String;
  Attributes: LongWord;
begin
  Path := ExpandConstant(DirectoryName);
  Attributes := GetFileAttributesW(Path);
  Result := (Attributes <> $FFFFFFFF) and ((Attributes and $10) <> 0) and NoReparsePointsOnPath(Path);
end;

procedure AssertRegularTree(const Root: String);
var
  Entry: TFindRec;
  Child: String;
begin
  if not NoReparsePointsOnPath(Root) then
    RaiseException('Refusing an unsafe shared installation directory: ' + Root);
  if FindFirst(AddBackslash(Root) + '*', Entry) then
  begin
    try
      repeat
        if (Entry.Name <> '.') and (Entry.Name <> '..') then
        begin
          Child := AddBackslash(Root) + Entry.Name;
          if not NoReparsePointsOnPath(Child) then
            RaiseException('Refusing a reparse point in shared installation storage: ' + Child);
          if (Entry.Attributes and $10) <> 0 then AssertRegularTree(Child);
        end;
      until not FindNext(Entry);
    finally FindClose(Entry); end;
  end;
end;

procedure ProtectMachineDirectory(const Root: String);
var
  ExitCode, CreateError: Integer;
  Descriptor: UINT_PTR;
  DescriptorSize: LongWord;
  Attributes: TNativeSecurityAttributes;
  Script, QuotedRoot, QuotedParent, ParentScript, VolumeRoot, VolumeName: String;
begin
  if not NoReparsePointsOnPath(ExtractFileDir(Root)) then
    RaiseException('The machine storage parent is unsafe: ' + Root);
  VolumeRoot := AddBackslash(ExtractFileDrive(Root));
  VolumeName := StringOfChar(#0, 50);
  { A SUBST/network drive can name a deletable backing directory. Exempt DELETE only on an attested local volume root. }
  if (Length(VolumeRoot) <> 3) or (Copy(VolumeRoot, 2, 2) <> ':\') or
    not GetVolumeNameForVolumeMountPointW(VolumeRoot, VolumeName, Length(VolumeName)) or
    (Copy(VolumeName, 1, 11) <> '\\?\Volume{') then
      RaiseException('Machine storage must reside on an attested local drive volume: ' + Root);
  QuotedRoot := Root;
  StringChangeEx(QuotedRoot, '''', '''''', True);
  QuotedParent := ExtractFileDir(Root);
  StringChangeEx(QuotedParent, '''', '''''', True);
  Script := '$ErrorActionPreference=''Stop''; $trusted=@(''S-1-5-18'',''S-1-5-32-544'',' +
    '''S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464''); ' +
    'function Get-PulseReaderEffectiveRights($rights) { $mapped=[int]$rights; ' +
    'if(($mapped -band 268435456) -ne 0) { $mapped=$mapped -bor 2032127 }; ' +
    'if(($mapped -band 1073741824) -ne 0) { $mapped=$mapped -bor 1179926 }; $mapped }; ';
  { Parents may allow creating new children (ProgramData), but cannot allow deleting/redirecting existing children.
    A drive volume root itself cannot be renamed/deleted; its DELETE_CHILD and ACL/owner rights still matter. }
  ParentScript := Script + '$verifiedVolumeRoot=''' + VolumeRoot + '''; ' +
    '$ancestorDanger=[int]([System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles ' +
    '-bor [System.Security.AccessControl.FileSystemRights]::ChangePermissions ' +
    '-bor [System.Security.AccessControl.FileSystemRights]::TakeOwnership); $item=(Get-Item -LiteralPath ''' + QuotedParent + ''' -Force); ' +
    'while($null -ne $item) { $acl=Get-Acl -LiteralPath $item.FullName; ' +
    '$itemPath=[System.IO.Path]::GetFullPath($item.FullName); $isDriveRoot=$itemPath -match ''^[A-Za-z]:\\$'' ' +
    '-and [string]::Equals($itemPath,[System.IO.Path]::GetPathRoot($itemPath),[System.StringComparison]::OrdinalIgnoreCase) ' +
    '-and [string]::Equals($itemPath,$verifiedVolumeRoot,[System.StringComparison]::OrdinalIgnoreCase); ' +
    '$danger=$ancestorDanger; if(-not $isDriveRoot) { $danger=$danger -bor [int][System.Security.AccessControl.FileSystemRights]::Delete }; ' +
    'if($trusted -notcontains $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value) { throw ''Untrusted storage ancestor owner'' }; ' +
    'foreach($rule in $acl.GetAccessRules($true,$true,[System.Security.Principal.SecurityIdentifier])) { ' +
    'if($rule.AccessControlType -eq ''Allow'' -and ($rule.PropagationFlags -band [System.Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 ' +
    '-and ((Get-PulseReaderEffectiveRights $rule.FileSystemRights) -band $danger) -ne 0 ' +
    '-and $trusted -notcontains $rule.IdentityReference.Value) { throw ''A storage ancestor allows ordinary-user deletion'' } }; $item=$item.Parent }';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoLogo -NoProfile -NonInteractive -Command "& { ' + ParentScript + ' }"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode)
    or (ExitCode <> 0) then RaiseException('Machine storage ancestors do not protect existing children: ' + Root);
  Descriptor := 0;
  if not ConvertStringSecurityDescriptorToSecurityDescriptorW(
    'O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;0x1200a9;;;BU)', 1, Descriptor, DescriptorSize) then
    RaiseException('Cannot construct protected machine storage permissions.');
  try
    { Inno 6 uses an x86 installer; Inno 7 is explicitly kept x86 above for this 12-byte Win32 ABI. }
    Attributes.Length := 12; Attributes.SecurityDescriptor := Descriptor; Attributes.InheritHandle := 0;
    { Atomic creation: an ordinary user racing this call is never assigned trusted ownership or permissions. }
    if CreateDirectoryW(Root, Attributes) then CreateError := 0 else CreateError := DLLGetLastError;
    if (CreateError <> 0) and (CreateError <> 183) then RaiseException('Cannot create protected machine storage: ' + Root);
  finally LocalFree(Descriptor); end;
  AssertRegularTree(Root);
  { Existing tree, including a root created by a competitor, is validated read-only. No takeover/ACL reset. }
  Script := Script + '$danger=[int]([System.Security.AccessControl.FileSystemRights]::Write ' +
    '-bor [System.Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [System.Security.AccessControl.FileSystemRights]::Delete ' +
    '-bor [System.Security.AccessControl.FileSystemRights]::ChangePermissions -bor [System.Security.AccessControl.FileSystemRights]::TakeOwnership); ' +
    '$paths=@(Get-Item -LiteralPath ''' + QuotedRoot + ''' -Force)+@(Get-ChildItem -LiteralPath ''' + QuotedRoot + ''' -Recurse -Force); ' +
    'foreach($item in $paths) { $acl=Get-Acl -LiteralPath $item.FullName; ' +
    'if($trusted -notcontains $acl.GetOwner([System.Security.Principal.SecurityIdentifier]).Value) { throw ''Untrusted shared storage owner'' }; ' +
    'foreach($rule in $acl.GetAccessRules($true,$true,[System.Security.Principal.SecurityIdentifier])) { ' +
    'if($rule.AccessControlType -eq ''Allow'' -and ((Get-PulseReaderEffectiveRights $rule.FileSystemRights) -band $danger) -ne 0 -and ' +
    '$trusted -notcontains $rule.IdentityReference.Value) { throw ''Ordinary-user-writable shared storage'' } } }';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoLogo -NoProfile -NonInteractive -Command "& { ' + Script + ' }"', '', SW_HIDE, ewWaitUntilTerminated, ExitCode)
    or (ExitCode <> 0) then RaiseException('Existing shared storage is not trusted. Setup has not changed its ownership or permissions: ' + Root);
end;

procedure FailPayloadCleanup(const Reason: String);
var
  Message: String;
begin
  CleanupFailed := True;
  Message := 'Pulse Reader obsolete payload cleanup failed. ' + Reason + #13#10 +
    'Close programs using the affected file and run Setup again. Cleanup records have not been discarded.';
  Log(Message);
  RaiseException(Message);
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if CleanupFailed then Result := 20;
end;

procedure AppendPreviousFileList(const Files: TArrayOfString);
var
  I, Count: Integer;
begin
  for I := 0 to GetArrayLength(Files) - 1 do
    if Files[I] <> '' then
    begin
      if not SafePayloadPath(Files[I]) then
        FailPayloadCleanup('Unsafe relative path in the previous file list: ' + Files[I]);
      if not ListedInPayload(Files[I], PreviousPayloadFiles) then
      begin
        Count := GetArrayLength(PreviousPayloadFiles);
        SetArrayLength(PreviousPayloadFiles, Count + 1);
        PreviousPayloadFiles[Count] := Files[I];
      end;
    end;
end;

procedure ReadPreviousFileList(const FileName: String);
var
  Files: TArrayOfString;
  Attributes: LongWord;
  ErrorCode: LongInt;
begin
  Attributes := GetFileAttributesW(FileName);
  if Attributes = $FFFFFFFF then
  begin
    ErrorCode := DLLGetLastError;
    if (ErrorCode <> 2) and (ErrorCode <> 3) then
      FailPayloadCleanup('Cannot inspect the previous cleanup record: ' + FileName);
    Exit;
  end;
  if ((Attributes and $10) <> 0) or not NoReparsePointsOnPath(FileName) then
    FailPayloadCleanup('The previous cleanup record is not a regular file: ' + FileName);
  if not LoadStringsFromFile(FileName, Files) then
    FailPayloadCleanup('Cannot read the previous cleanup record: ' + FileName);
  AppendPreviousFileList(Files);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  CurrentPayloadFiles: TArrayOfString;
  CleanupStatePath, ObsoletePath, Scope: String;
  Attributes: LongWord;
  ErrorCode: LongInt;
  I: Integer;
begin
  CleanupStatePath := ExpandConstant('{app}\') + CleanupStateFileName;
  if CurStep = ssInstall then
  begin
    if IsAdminInstallMode then
    begin
      ProtectMachineDirectory(ExpandConstant('{app}'));
      ProtectMachineDirectory(ExpandConstant('{app}\OptionalRuntime'));
      ProtectMachineDirectory(ExpandConstant('{commonappdata}\PulseReader'));
    end;
    SetArrayLength(PreviousPayloadFiles, 0);
    ReadPreviousFileList(ExpandConstant('{app}\payload-files.txt'));
    ReadPreviousFileList(CleanupStatePath);
    if GetArrayLength(PreviousPayloadFiles) > 0 then
    begin
      if not NoReparsePointsOnPath(ExpandConstant('{app}')) then
        FailPayloadCleanup('The installation directory is unsafe for a cleanup record.');
      if not SaveStringsToFile(CleanupStatePath, PreviousPayloadFiles, False) then
        FailPayloadCleanup('Cannot preserve the previous file list before installation: ' + CleanupStatePath);
      Log('Previous payload file list preserved for cleanup retry: ' + CleanupStatePath);
    end;
  end;
  if CurStep = ssPostInstall then
  begin
    if IsAdminInstallMode then Scope := 'machine' else Scope := 'user';
    Attributes := GetFileAttributesW(ExpandConstant('{app}\installation-scope.json'));
    if Attributes <> $FFFFFFFF then
    begin
      if ((Attributes and $10) <> 0) or not NoReparsePointsOnPath(ExpandConstant('{app}\installation-scope.json')) then
        FailPayloadCleanup('The installation scope record is not a regular file.');
    end
    else
    begin
      ErrorCode := DLLGetLastError;
      if (ErrorCode <> 2) and (ErrorCode <> 3) then FailPayloadCleanup('Cannot inspect the installation scope record.');
    end;
    if not NoReparsePointsOnPath(ExpandConstant('{app}')) or
      not SaveStringToFile(ExpandConstant('{app}\installation-scope.json'),
        '{"SchemaVersion":1,"Scope":"' + Scope + '"}', False) then
      FailPayloadCleanup('Cannot record the actual installation scope.');
    if IsAdminInstallMode then
    begin
      AssertRegularTree(ExpandConstant('{app}'));
      if not Exec(ExpandConstant('{sys}\icacls.exe'), '"' + ExpandConstant('{app}') + '" /setowner *S-1-5-32-544 /T /Q',
        '', SW_HIDE, ewWaitUntilTerminated, I) or (I <> 0) then
        FailPayloadCleanup('Cannot set trusted owners on the installed application payload.');
    end;
    if not NoReparsePointsOnPath(ExpandConstant('{app}\payload-files.txt')) or
        not LoadStringsFromFile(ExpandConstant('{app}\payload-files.txt'), CurrentPayloadFiles) then
      FailPayloadCleanup('Cannot read the newly installed payload file list.');
    for I := 0 to GetArrayLength(PreviousPayloadFiles) - 1 do
      if not ListedInPayload(PreviousPayloadFiles[I], CurrentPayloadFiles) then
      begin
        ObsoletePath := ExpandConstant('{app}\') + PreviousPayloadFiles[I];
        Attributes := GetFileAttributesW(ObsoletePath);
        if Attributes = $FFFFFFFF then
        begin
          ErrorCode := DLLGetLastError;
          if (ErrorCode <> 2) and (ErrorCode <> 3) then
            FailPayloadCleanup('Cannot inspect the obsolete file: ' + ObsoletePath);
        end
        else
        begin
          if ((Attributes and $10) <> 0) or not NoReparsePointsOnPath(ObsoletePath) then
            FailPayloadCleanup('Refusing to follow a reparse point or remove a directory: ' + ObsoletePath);
          if not DeleteFile(ObsoletePath) then
            FailPayloadCleanup('Cannot remove the obsolete file: ' + ObsoletePath);
          Log('Removed obsolete payload file: ' + ObsoletePath);
        end;
      end;
    if GetFileAttributesW(CleanupStatePath) <> $FFFFFFFF then
    begin
      if not NoReparsePointsOnPath(CleanupStatePath) or not DeleteFile(CleanupStatePath) then
        FailPayloadCleanup('Cannot clear the completed cleanup record: ' + CleanupStatePath);
    end;
  end;
end;

function StartupPointsToThisApp(const Command: String): Boolean;
var
  ExePath: String;
  I: Integer;
begin
  ExePath := Trim(Command);
  if Copy(ExePath, 1, 1) = '"' then
  begin
    Delete(ExePath, 1, 1);
    I := Pos('"', ExePath);
    if I = 0 then ExePath := '' else ExePath := Copy(ExePath, 1, I - 1);
  end
  else
  begin
    I := Pos(' ', ExePath);
    if I > 0 then ExePath := Copy(ExePath, 1, I - 1);
  end;
  Result := CompareText(ExePath, ExpandConstant('{app}\PulseReader.exe')) = 0;
end;

function InitializeUninstall: Boolean;
var
  Form: TSetupForm;
  Preserve, Dependencies, Everything: TNewRadioButton;
  Heading, RegistryNote: TNewStaticText;
  ContinueButton, CancelButton: TNewButton;
  I: Integer;
  Argument: String;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    Argument := Lowercase(ParamStr(I));
    if (Argument = '/confirmallusersdata') or (Pos('/confirmallusersdata=', Argument) = 1) or
      (Argument = '--confirm-all-users-data') then
    begin
      Log('Obsolete uninstall confirmation parameter. Choose /UNINSTALLDATA=everything explicitly instead.');
      Exit;
    end;
  end;
  UninstallDataChoice := Lowercase(ExpandConstant('{param:UNINSTALLDATA|preserve}'));
  if (UninstallDataChoice <> 'preserve') and (UninstallDataChoice <> 'dependencies') and (UninstallDataChoice <> 'everything') then
  begin
    Log('Unknown /UNINSTALLDATA choice. Use preserve, dependencies or everything.');
    Exit;
  end;
  if UninstallSilent then
  begin
    Result := True;
    Exit;
  end;
  #if Ver >= EncodeVer(6, 6, 0)
    Form := CreateCustomForm(ScaleX(570), ScaleY(235), True, True);
  #else
    Form := CreateCustomForm;
    Form.ClientWidth := ScaleX(570);
    Form.ClientHeight := ScaleY(235);
    Form.KeepSizeX := True;
    Form.KeepSizeY := True;
  #endif
  try
    Form.Caption := 'Uninstall Pulse Reader';
    Form.Position := poScreenCenter;
    Heading := TNewStaticText.Create(Form);
    Heading.Parent := Form; Heading.SetBounds(ScaleX(16), ScaleY(12), Form.ClientWidth - ScaleX(32), ScaleY(38));
    Heading.AutoSize := False; Heading.WordWrap := True;
    if IsAdminInstallMode then Heading.Caption := 'This is an all-users installation. Choose what to remove. Shared downloads can be used by other users.'
    else Heading.Caption := 'Choose what to remove from this Windows account.';
    Preserve := TNewRadioButton.Create(Form);
    Preserve.Parent := Form; Preserve.SetBounds(ScaleX(16), ScaleY(57), Form.ClientWidth - ScaleX(32), ScaleY(24));
    Preserve.Caption := 'Application only: keep all downloaded dependencies, settings and logs'; Preserve.Checked := True;
    Dependencies := TNewRadioButton.Create(Form);
    Dependencies.Parent := Form; Dependencies.SetBounds(ScaleX(16), ScaleY(91), Form.ClientWidth - ScaleX(32), ScaleY(24));
    Dependencies.Caption := 'Application and downloaded dependencies: keep settings and logs';
    Everything := TNewRadioButton.Create(Form);
    Everything.Parent := Form; Everything.SetBounds(ScaleX(16), ScaleY(125), Form.ClientWidth - ScaleX(32), ScaleY(42));
    if IsAdminInstallMode then Everything.Caption := 'Remove all files, including OTHER USERS'' settings, logs and temporary files'
    else Everything.Caption := 'Remove everything, including settings, logs, temporary files and acceptance records';
    RegistryNote := TNewStaticText.Create(Form);
    RegistryNote.Parent := Form; RegistryNote.SetBounds(ScaleX(16), ScaleY(167), Form.ClientWidth - ScaleX(32), ScaleY(20));
    RegistryNote.Caption := 'Logged-out users keep their Windows startup and terms-acceptance records.';
    RegistryNote.Visible := IsAdminInstallMode;
    ContinueButton := TNewButton.Create(Form);
    ContinueButton.Parent := Form; ContinueButton.SetBounds(Form.ClientWidth - ScaleX(16 + 94 + 10 + 94), Form.ClientHeight - ScaleY(16 + 28), ScaleX(94), ScaleY(28));
    ContinueButton.Caption := 'Continue'; ContinueButton.ModalResult := mrOK; ContinueButton.Default := True;
    CancelButton := TNewButton.Create(Form);
    CancelButton.Parent := Form; CancelButton.SetBounds(Form.ClientWidth - ScaleX(16 + 94), Form.ClientHeight - ScaleY(16 + 28), ScaleX(94), ScaleY(28));
    CancelButton.Caption := 'Cancel'; CancelButton.ModalResult := mrCancel; CancelButton.Cancel := True;
    if Form.ShowModal <> mrOK then Exit;
    UninstallDataChoice := 'preserve';
    if Dependencies.Checked then UninstallDataChoice := 'dependencies';
    if Everything.Checked then UninstallDataChoice := 'everything';
    Result := True;
  finally Form.Free; end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Arguments: String;
  ExitCode: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Arguments := '--uninstall-data ' + UninstallDataChoice;
  if UninstallSilent then Arguments := Arguments + ' --quiet';
  { This event precedes payload deletion. Failures stop removal so the helper remains available for retry. }
  if not Exec(ExpandConstant('{app}\PulseReader.exe'), Arguments, ExpandConstant('{app}'), SW_HIDE,
    ewWaitUntilTerminated, ExitCode) or (ExitCode <> 0) then
    RaiseException('Pulse Reader cleanup did not complete (helper exit ' + IntToStr(ExitCode) + '). ' +
      'The application has not been removed. Close affected sessions and retry; requested data may already be partly removed.');
end;
