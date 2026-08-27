; RXDK for Visual Studio - installer.
;
; One-stop setup so first use of the extension needs zero prerequisite steps:
;   1. Ensures the .NET 8 Desktop Runtime (the RXDK engine is framework-dependent net8).
;   2. Stages the RXDK engine to C:\ProgramData\RXDK\engine.
;   3. Runs the engine's install-zig / install-tools / install-sdk / install-docs verbs so
;      Zig, the host tools, the SDK and the docs are all present before VS opens. The XDK
;      sample suite (install-samples) is optional -- an opt-out task checkbox, checked by default.
;   4. Installs the RXDK "Xbox" MSBuild platform into every VS 2022+ install (with the
;      RxdkPlatform.version stamp the extension checks) so Xbox projects load and build.
;   5. Adds the MSVC v143 C++ build tools if they're missing (via the VS Installer).
;   6. Installs the extension VSIX into VS.
;
; The extension can also be installed standalone from the VS Marketplace; in that case its
; in-tool-window "Install Prerequisites" button does all of the above. When this installer has
; already done it, that button hides itself (it re-uses the same detection).
;
; Build with installer\build-installer.ps1 (stages the payload, then runs ISCC).

#define MyAppName "RXDK for Visual Studio"
#ifndef MyAppVersion
  #define MyAppVersion "1.1.2"
#endif
#define MyAppPublisher "Team Resurgent"
#define MyAppURL "https://github.com/Team-Resurgent/RXDK-VS20XX"
#define MyAppId "RXDK-VisualStudio"
#define ExtensionId "RxdkVs.Package.a069146d-e49e-4913-92cc-339495d0cd21"
#define VsixFileName "rxdk-vs.vsix"

#ifndef PayloadDir
  #define PayloadDir "payload"
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "out"
#endif
#ifndef InstallerOutputBaseName
  #define InstallerOutputBaseName "RXDK-VS-Setup"
#endif

#define DotNetReleaseMetadataUrl "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/8.0/releases.json"
#define DotNetDesktopRuntimeFileName "windowsdesktop-runtime-win-x64.exe"
#define Vc143Component "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\RXDK\VisualStudio
DisableDirPage=yes
DisableProgramGroupPage=yes
UsePreviousAppDir=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename={#InstallerOutputBaseName}
SetupIconFile=Icon.ico
WizardImageFile=WizardImage.bmp
WizardSmallImageFile=WizardSmallImage.bmp
WizardStyle=modern
UninstallDisplayIcon={app}\Icon.ico
UninstallDisplayName={#MyAppName}
Compression=lzma2
SolidCompression=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; The XDK sample suite is the one large, optional component -- offer it as an
; opt-out checkbox (checked by default) rather than always pulling the full repo.
Name: "samples"; Description: "Install the RXDK sample suite (large download)"; GroupDescription: "Optional components:"

[Files]
; The RXDK engine (Rxdk.Cli/Rxdk.Dap + net8 closure) -> the location the extension resolves.
Source: "{#PayloadDir}\engine\*"; DestDir: "{commonappdata}\RXDK\engine"; Flags: ignoreversion recursesubdirs createallsubdirs
; The custom Xbox MSBuild platform, staged here and copied into each VS install in code.
Source: "{#PayloadDir}\platform\*"; DestDir: "{app}\platform"; Flags: ignoreversion recursesubdirs createallsubdirs
; The extension VSIX, installed via VSIXInstaller in [Run].
Source: "{#PayloadDir}\{#VsixFileName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "Icon.ico"; DestDir: "{app}"; Flags: ignoreversion

[Run]
Filename: "{code:GetVsixInstaller}"; Parameters: """{app}\{#VsixFileName}"" /quiet"; StatusMsg: "Installing the RXDK extension into Visual Studio..."; Flags: waituntilterminated runhidden; Check: HasVsixInstaller

[Code]
var
  DotNetDownloadPage: TDownloadWizardPage;
  ProgressPage: TOutputProgressWizardPage;
  VsixInstallerPath: String;

const
  ProgramDataRxdk = '{commonappdata}\RXDK';

{ ---------- small helpers ---------- }

function GetVsixInstaller(Param: String): String;
begin
  Result := VsixInstallerPath;
end;

function HasVsixInstaller: Boolean;
begin
  Result := (VsixInstallerPath <> '') and FileExists(VsixInstallerPath);
end;

{ Run a command, capturing its stdout lines by redirecting to a temp file. }
function RunCapture(const Exe, Args: String; var Lines: TArrayOfString): Boolean;
var
  OutFile: String;
  Code: Integer;
  Cmd: String;
begin
  Result := False;
  SetArrayLength(Lines, 0);
  OutFile := ExpandConstant('{tmp}\rxdk-capture.txt');
  DeleteFile(OutFile);
  Cmd := '/C ""' + Exe + '" ' + Args + ' > "' + OutFile + '" 2>nul"';
  if not Exec(ExpandConstant('{cmd}'), Cmd, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Exit;
  if FileExists(OutFile) then
    Result := LoadStringsFromFile(OutFile, Lines);
end;

{ ---------- VSIXInstaller / Visual Studio discovery ---------- }

function VsWherePath: String;
begin
  Result := ExpandConstant('{pf32}\Microsoft Visual Studio\Installer\vswhere.exe');
end;

{ Locate VSIXInstaller.exe under any installed VS instance. }
function FindVsixInstaller: String;
var
  Lines: TArrayOfString;
  I: Integer;
  Candidate: String;
begin
  Result := '';
  if not FileExists(VsWherePath) then Exit;
  if not RunCapture(VsWherePath, '-all -prerelease -property installationPath', Lines) then Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Trim(Lines[I]) = '' then Continue;
    Candidate := AddBackslash(Trim(Lines[I])) + 'Common7\IDE\VSIXInstaller.exe';
    if FileExists(Candidate) then
    begin
      Result := Candidate;
      Exit;
    end;
  end;
end;

{ Each VS install's VCTargetsPath\...\Platforms\Xbox destination (dirs that ship an x64 platform). }
function FindXboxPlatformDests(var Dests: TArrayOfString): Integer;
var
  Installs: TArrayOfString;
  I: Integer;
  VcRoot, ToolsetRoot: String;
  FR: TFindRec;
begin
  SetArrayLength(Dests, 0);
  if not FileExists(VsWherePath) then begin Result := 0; Exit; end;
  if not RunCapture(VsWherePath, '-all -prerelease -property installationPath', Installs) then begin Result := 0; Exit; end;
  for I := 0 to GetArrayLength(Installs) - 1 do
  begin
    if Trim(Installs[I]) = '' then Continue;
    VcRoot := AddBackslash(Trim(Installs[I])) + 'MSBuild\Microsoft\VC';
    if not DirExists(VcRoot) then Continue;
    if FindFirst(AddBackslash(VcRoot) + 'v1*', FR) then
    begin
      try
        repeat
          if (FR.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          begin
            ToolsetRoot := AddBackslash(VcRoot) + FR.Name;
            if DirExists(AddBackslash(ToolsetRoot) + 'Platforms\x64') then
            begin
              SetArrayLength(Dests, GetArrayLength(Dests) + 1);
              Dests[GetArrayLength(Dests) - 1] := AddBackslash(ToolsetRoot) + 'Platforms\Xbox';
            end;
          end;
        until not FindNext(FR);
      finally
        FindClose(FR);
      end;
    end;
  end;
  Result := GetArrayLength(Dests);
end;

function HasVc143: Boolean;
var
  Lines: TArrayOfString;
begin
  Result := False;
  if not FileExists(VsWherePath) then Exit;
  if RunCapture(VsWherePath, '-latest -requires {#Vc143Component} -property installationPath', Lines) then
    Result := (GetArrayLength(Lines) > 0) and (Trim(Lines[0]) <> '');
end;

{ ---------- .NET 8 runtime ---------- }

function HasDotNet8: Boolean;
var
  FR: TFindRec;
  Base: String;
begin
  Result := False;
  Base := ExpandConstant('{pf}\dotnet\shared\Microsoft.NETCore.App');
  if not DirExists(Base) then Exit;
  if FindFirst(AddBackslash(Base) + '8.*', FR) then
  begin
    try
      Result := True;
    finally
      FindClose(FR);
    end;
  end;
end;

function DownloadFileToTmp(const Url, BaseFileName: String): Boolean;
begin
  DotNetDownloadPage.Clear;
  DotNetDownloadPage.Add(Url, BaseFileName, '');
  DotNetDownloadPage.Show;
  try
    try
      DotNetDownloadPage.Download;
      Result := True;
    except
      Log('Download failed for ' + Url + ': ' + GetExceptionMessage);
      Result := False;
    end;
  finally
    DotNetDownloadPage.Hide;
  end;
end;

function ExtractQuotedJsonValue(const Content: AnsiString; StartPos: Integer): String;
var I: Integer;
begin
  Result := '';
  I := StartPos;
  while (I <= Length(Content)) and (Content[I] <> '"') do Inc(I);
  if I > Length(Content) then Exit;
  Inc(I);
  while (I <= Length(Content)) and (Content[I] <> '"') do begin Result := Result + Content[I]; Inc(I); end;
end;

function ResolveDotNetDesktopRuntimeUrl: String;
var
  JsonPath: String;
  Content, Segment: AnsiString;
  NamePos, UrlKeyPos, UrlStart: Integer;
begin
  JsonPath := ExpandConstant('{tmp}\dotnet-release-metadata.json');
  if not DownloadFileToTmp('{#DotNetReleaseMetadataUrl}', 'dotnet-release-metadata.json') then
    RaiseException('Failed to download .NET release metadata.');
  if not LoadStringFromFile(JsonPath, Content) then
    RaiseException('Failed to read .NET release metadata.');
  NamePos := Pos('{#DotNetDesktopRuntimeFileName}', Content);
  if NamePos = 0 then
    RaiseException('Could not find the .NET Desktop Runtime installer in release metadata.');
  Segment := Copy(Content, NamePos, 512);
  UrlKeyPos := Pos('"url"', Segment);
  if UrlKeyPos = 0 then
    RaiseException('Could not resolve the .NET Desktop Runtime download URL.');
  UrlStart := NamePos + UrlKeyPos + Length('"url"') - 1;
  while (UrlStart <= Length(Content)) and ((Content[UrlStart] = ':') or (Content[UrlStart] = ' ') or (Content[UrlStart] = #9)) do
    Inc(UrlStart);
  Result := ExtractQuotedJsonValue(Content, UrlStart);
  if Result = '' then RaiseException('Could not parse the .NET Desktop Runtime download URL.');
  Log('Resolved .NET Desktop Runtime URL: ' + Result);
end;

procedure EnsureDotNet8;
var
  Url, InstallerPath: String;
  Code: Integer;
begin
  if HasDotNet8 then
  begin
    Log('.NET 8 runtime already present.');
    Exit;
  end;
  ProgressPage.SetText('Installing the .NET 8 Desktop Runtime...', '');
  Url := ResolveDotNetDesktopRuntimeUrl;
  if not DownloadFileToTmp(Url, '{#DotNetDesktopRuntimeFileName}') then
    RaiseException('Failed to download the .NET 8 Desktop Runtime.');
  InstallerPath := ExpandConstant('{tmp}\{#DotNetDesktopRuntimeFileName}');
  if not Exec(InstallerPath, '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    RaiseException('The .NET 8 Desktop Runtime installer failed to launch.');
  if not HasDotNet8 then
    Log('WARNING: .NET 8 still not detected after install (exit ' + IntToStr(Code) + ').');
end;

{ ---------- prerequisite orchestration (post-install) ---------- }

function EngineCli: String;
begin
  Result := ExpandConstant(ProgramDataRxdk + '\engine\Rxdk.Cli.exe');
end;

procedure RunEngineVerb(const Verb: String);
var Code: Integer;
begin
  if not FileExists(EngineCli) then Exit;
  ProgressPage.SetText('RXDK setup: ' + Verb + '...', '');
  Exec(EngineCli, Verb + ' --max-version {#MyAppVersion}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('engine ' + Verb + ' exit ' + IntToStr(Code));
end;

procedure InstallXboxPlatform;
var
  Dests: TArrayOfString;
  I, Code: Integer;
  Src, Dest: String;
begin
  Src := ExpandConstant('{app}\platform');
  if not DirExists(Src) then Exit;
  if FindXboxPlatformDests(Dests) = 0 then
  begin
    Log('No VS C++ toolset found - Xbox platform not installed (v143 install may add one).');
    Exit;
  end;
  for I := 0 to GetArrayLength(Dests) - 1 do
  begin
    Dest := Dests[I];
    ProgressPage.SetText('Installing the Xbox build platform...', Dest);
    ForceDirectories(Dest);
    Exec(ExpandConstant('{cmd}'), '/C robocopy "' + Src + '" "' + Dest + '" /E /NFL /NDL /NJH /NJS /R:1 /W:1 >nul', '', SW_HIDE, ewWaitUntilTerminated, Code);
    SaveStringToFile(AddBackslash(Dest) + 'RxdkPlatform.version', '{#MyAppVersion}', False);
  end;
end;

procedure InstallVc143IfMissing;
var
  Installer, Args, InstallPath: String;
  Lines: TArrayOfString;
  Code: Integer;
begin
  if HasVc143 then
  begin
    Log('MSVC v143 already present.');
    Exit;
  end;
  Installer := ExpandConstant('{pf32}\Microsoft Visual Studio\Installer\vs_installer.exe');
  if not FileExists(Installer) then
  begin
    Log('vs_installer.exe not found - skipping v143.');
    Exit;
  end;
  InstallPath := '';
  if RunCapture(VsWherePath, '-latest -property installationPath', Lines) and (GetArrayLength(Lines) > 0) then
    InstallPath := Trim(Lines[0]);
  Args := 'modify --add {#Vc143Component} --quiet --norestart';
  if InstallPath <> '' then
    Args := 'modify --installPath "' + InstallPath + '" --add {#Vc143Component} --quiet --norestart';
  ProgressPage.SetText('Adding the MSVC v143 C++ build tools (this can take a while)...', '');
  Exec(Installer, Args, '', SW_SHOW, ewWaitUntilTerminated, Code);
  Log('vs_installer modify exit ' + IntToStr(Code));
end;

{ ---------- Inno wizard hooks ---------- }

procedure InitializeWizard;
begin
  DotNetDownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DotNetDownloadPage.ShowBaseNameInsteadOfUrl := True;
  ProgressPage := CreateOutputProgressPage('Setting up RXDK', 'Installing prerequisites and the Visual Studio extension...');
  VsixInstallerPath := FindVsixInstaller;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    ProgressPage.Show;
    try
      EnsureDotNet8;
      InstallXboxPlatform;
      RunEngineVerb('install-zig');
      RunEngineVerb('install-tools');
      RunEngineVerb('install-sdk');
      RunEngineVerb('install-docs');
      if WizardIsTaskSelected('samples') then
        RunEngineVerb('install-samples');
      InstallVc143IfMissing;
    finally
      ProgressPage.Hide;
    end;
    { The VSIX itself is installed by the [Run] entry after this step. }
  end;
end;

{ ---------- uninstall ---------- }

procedure RemoveXboxPlatform;
var
  Dests: TArrayOfString;
  I: Integer;
begin
  if FindXboxPlatformDests(Dests) = 0 then Exit;
  for I := 0 to GetArrayLength(Dests) - 1 do
    if DirExists(Dests[I]) then DelTree(Dests[I], True, True, True);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Vsix: String;
  Code: Integer;
begin
  if CurUninstallStep = usUninstall then
  begin
    Vsix := FindVsixInstaller;
    if (Vsix <> '') and FileExists(Vsix) then
      Exec(Vsix, '/uninstall:{#ExtensionId} /quiet', '', SW_HIDE, ewWaitUntilTerminated, Code);
    RemoveXboxPlatform;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    { Remove the machine-wide RXDK data the installer populated. }
    if DirExists(ExpandConstant(ProgramDataRxdk)) then
      DelTree(ExpandConstant(ProgramDataRxdk), True, True, True);
  end;
end;
