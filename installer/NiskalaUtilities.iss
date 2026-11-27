#ifndef AppVersion
#error Pass the release version with /DAppVersion=x.y.z; scripts\Build-Release.ps1 does.
#endif
#ifndef Runtime
#define Runtime "win-x64"
#endif
#define Root AddBackslash(SourcePath) + "..\"
#define CertificateFile "niskala-pub.cer"
#ifndef AppSource
#define AppSource Root + "artifacts\release\NiskalaUtilities-v" + AppVersion + "-" + Runtime
#endif
#ifndef CliSource
#define CliSource Root + "artifacts\release\NiskalaUtilities-CLI-v" + AppVersion + "-" + Runtime
#endif
#ifndef OutputDir
#define OutputDir Root + "artifacts\release"
#endif
#ifdef PublisherCertificate
#ifndef PublisherThumbprint
#error Pass the certificate's SHA-1 thumbprint with /DPublisherThumbprint=...; scripts\Build-Release.ps1 does.
#endif
#endif
#if Runtime == "win-arm64"
#define Architecture "arm64"
#else
#define Architecture "x64compatible"
#endif

[Setup]
AppId={{3EC397E4-E199-4B2F-B436-8F7D0FC248B9}
AppName=Niskala Utilities
AppVersion={#AppVersion}
AppVerName=Niskala Utilities {#AppVersion}
AppPublisher=Adriansyah101
AppPublisherURL=https://github.com/Adriansyah101/NiskalaUtilities
AppSupportURL=https://github.com/Adriansyah101/NiskalaUtilities/issues
AppUpdatesURL=https://github.com/Adriansyah101/NiskalaUtilities/releases
AppCopyright=Copyright (c) 2026 Adriansyah101
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=Adriansyah101
VersionInfoDescription=Niskala Utilities Setup
VersionInfoProductName=Niskala Utilities
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (c) 2026 Adriansyah101
DefaultDirName={autopf}\Niskala Utilities
DefaultGroupName=Niskala Utilities
DisableProgramGroupPage=yes
LicenseFile={#Root}LICENSE
SetupIconFile={#Root}assets\niskala.ico
UninstallDisplayIcon={app}\Niskala.exe
UninstallDisplayName=Niskala Utilities
OutputDir={#OutputDir}
OutputBaseFilename=NiskalaUtilities-Setup-v{#AppVersion}-{#Runtime}
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
ArchitecturesAllowed={#Architecture}
ArchitecturesInstallIn64BitMode={#Architecture}
MinVersion=10.0.19041
PrivilegesRequired=admin
CloseApplications=yes
RestartApplications=yes
ChangesEnvironment=yes
WizardStyle=modern
#ifdef SignInstaller
SignTool=niskala
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Components]
Name: "app"; Description: "Niskala Utilities"; Types: full compact custom; Flags: fixed
Name: "cli"; Description: "niskala command-line tool"; Types: full custom

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add the niskala command to PATH"; GroupDescription: "Command line:"; Components: cli; Flags: unchecked

[InstallDelete]
Type: filesandordirs; Name: "{app}\lib"
Type: filesandordirs; Name: "{app}\cli\lib"
Type: files; Name: "{app}\{#CertificateFile}"

[Files]
Source: "{#AppSource}\*"; DestDir: "{app}"; Components: app; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#CliSource}\*"; DestDir: "{app}\cli"; Components: cli; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef PublisherCertificate
Source: "{#PublisherCertificate}"; DestDir: "{tmp}"; DestName: "{#CertificateFile}"; Flags: ignoreversion deleteafterinstall
#endif

[Icons]
Name: "{autoprograms}\Niskala Utilities"; Filename: "{app}\Niskala.exe"
Name: "{autodesktop}\Niskala Utilities"; Filename: "{app}\Niskala.exe"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}\cli"; Tasks: addtopath; Check: NeedsPathEntry(ExpandConstant('{app}\cli'))

[Run]
#ifdef PublisherCertificate
Filename: "{sys}\certutil.exe"; Parameters: "-f -addstore Root ""{tmp}\{#CertificateFile}"""; Flags: runhidden; Check: NeedsTrust('Root'); AfterInstall: RememberTrust('Root'); StatusMsg: "Trusting the publisher certificate..."
Filename: "{sys}\certutil.exe"; Parameters: "-f -addstore TrustedPublisher ""{tmp}\{#CertificateFile}"""; Flags: runhidden; Check: NeedsTrust('TrustedPublisher'); AfterInstall: RememberTrust('TrustedPublisher'); StatusMsg: "Trusting the publisher certificate..."
#endif
Filename: "{app}\Niskala.exe"; Description: "{cm:LaunchProgram,Niskala Utilities}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\Niskala.exe"; Flags: nowait; Check: RestartAfterUpdate

[Code]
const
  EnvironmentKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';
  PublisherKey = 'SOFTWARE\Niskala Utilities';
  TrustedPrefix = 'TrustedCertificate.';
#ifdef PublisherThumbprint
  PublisherThumbprint = '{#PublisherThumbprint}';
#endif

function CertUtil(Parameters: string): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{sys}\certutil.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

#ifdef PublisherThumbprint
function NeedsTrust(Store: string): Boolean;
begin
  Result := not CertUtil('-store ' + Store + ' ' + PublisherThumbprint);
end;

procedure RememberTrust(Store: string);
begin
  if CertUtil('-store ' + Store + ' ' + PublisherThumbprint) then
    RegWriteStringValue(HKEY_LOCAL_MACHINE, PublisherKey, TrustedPrefix + Store, PublisherThumbprint)
  else
    Log('certutil could not add the publisher certificate to LocalMachine\' + Store + '.');
end;
#endif

procedure ForgetTrust(Store: string);
var
  Thumbprint: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, PublisherKey, TrustedPrefix + Store, Thumbprint) then
    Exit;
  if not CertUtil('-delstore ' + Store + ' ' + Thumbprint) then
    Log('certutil could not remove the publisher certificate from LocalMachine\' + Store + '.');
  RegDeleteValue(HKEY_LOCAL_MACHINE, PublisherKey, TrustedPrefix + Store);
end;

function RestartAfterUpdate: Boolean;
begin
  Result := WizardSilent and (Pos('/RESTARTAPPLICATIONS', Uppercase(GetCmdTail)) > 0);
end;

function NeedsPathEntry(Folder: string): Boolean;
var
  Paths: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Paths) then
    Result := True
  else
    Result := Pos(';' + Uppercase(Folder) + ';', ';' + Uppercase(Paths) + ';') = 0;
end;

procedure RemovePathEntry;
var
  Paths, Entry: string;
  Index: Integer;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Paths) then
    Exit;
  Paths := ';' + Paths + ';';
  Entry := ';' + Uppercase(ExpandConstant('{app}\cli')) + ';';
  Index := Pos(Entry, Uppercase(Paths));
  if Index = 0 then
    Exit;
  Delete(Paths, Index, Length(Entry) - 1);
  RegWriteExpandStringValue(HKEY_LOCAL_MACHINE, EnvironmentKey, 'Path', Copy(Paths, 2, Length(Paths) - 2));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;
  RemovePathEntry;
  ForgetTrust('TrustedPublisher');
  ForgetTrust('Root');
  RegDeleteKeyIfEmpty(HKEY_LOCAL_MACHINE, PublisherKey);
end;
