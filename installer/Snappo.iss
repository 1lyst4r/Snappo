#define MyAppName "Snappo"
#define MyAppVersion "1.2.1"
#define MyAppExeName "Snappo.exe"
#define MyAppPublisher "rei"

[Setup]
AppId={{9E593C9F-4F02-4AA6-A20B-05718750F3B5}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=SnappoSetup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[InstallDelete]
; Left over from older, much bigger builds.
Type: files; Name: "{app}\Snappo.pdb"

[Files]
; Build first with:  dotnet publish -c Release   (from the Snappo project folder)
Source: "..\Snappo\bin\Release\net8.0-windows\win-x64\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#MyAppExeName} /F"; Flags: runhidden; RunOnceId: "KillSnappo"

[Code]
// Snappo uses the shared .NET 8 Desktop Runtime instead of bundling its own copy (that's what keeps it tiny).
// If the runtime isn't installed yet, download Microsoft's official installer and run it.
const
  RuntimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';
  RuntimeFileName = 'windowsdesktop-runtime-8-win-x64.exe';
  StartupRunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';

var
  DownloadPage: TDownloadWizardPage;

function IsDesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          Result := True;
      until Result or not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function RunRuntimeInstaller: String;
var
  ResultCode: Integer;
begin
  Result := '';
  // ShellExec (not Exec) so Windows can show the UAC prompt the runtime installer needs.
  if not ShellExec('', ExpandConstant('{tmp}\' + RuntimeFileName), '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
    Result := 'Couldn''t start the .NET 8 Desktop Runtime installer.'
  else if (ResultCode <> 0) and (ResultCode <> 3010) then   // 3010 = installed, restart recommended
    Result := 'The .NET 8 Desktop Runtime installer failed (code ' + IntToStr(ResultCode) + ').';
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Problem: String;
begin
  Result := True;
  if (CurPageID <> wpReady) or IsDesktopRuntimeInstalled then
    Exit;

  Problem := '';
  DownloadPage.Clear;
  DownloadPage.Add(RuntimeUrl, RuntimeFileName, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      Problem := RunRuntimeInstaller;
    except
      Problem := 'Couldn''t download the .NET 8 Desktop Runtime: ' + GetExceptionMessage;
    end;
  finally
    DownloadPage.Hide;
  end;

  if Problem <> '' then
  begin
    SuppressibleMsgBox(Problem + #13#10#13#10 + 'Snappo needs it to run. You can also get it from https://dotnet.microsoft.com/download/dotnet/8.0', mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;

// Silent installs never show the wizard pages, so handle the runtime here instead.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if WizardSilent and not IsDesktopRuntimeInstalled then
  begin
    try
      DownloadTemporaryFile(RuntimeUrl, RuntimeFileName, '', nil);
      Result := RunRuntimeInstaller;
    except
      Result := 'Couldn''t download the .NET 8 Desktop Runtime: ' + GetExceptionMessage;
    end;
  end;
end;

// "Start Snappo when Windows starts" adds a Run entry; don't leave it pointing at a deleted exe.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, StartupRunKey, '{#MyAppName}');
end;
