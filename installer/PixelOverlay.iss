#define AppVersion GetEnv("PIXEL_OVERLAY_VERSION")
#if AppVersion == ""
  #error "PIXEL_OVERLAY_VERSION must be set before compiling the installer."
#endif

[Setup]
AppId={{8AC4CB70-4D8A-4E24-81AE-7B9E4DD78B6A}
AppName=Pixel Overlay
AppVersion={#AppVersion}
AppVerName=Pixel Overlay {#AppVersion}
AppPublisher=Pixel Overlay
VersionInfoVersion={#AppVersion}
VersionInfoCompany=Pixel Overlay
VersionInfoDescription=Pixel Overlay Setup
VersionInfoProductName=Pixel Overlay
DefaultDirName={tmp}\PixelOverlaySetup
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
Uninstallable=no
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
OutputBaseFilename=PixelOverlay-Setup
SetupLogging=yes
CloseApplications=no
RestartApplications=no

[Files]
Source: "..\scripts\Detect-PixelOverlay.ps1"; Flags: dontcopy
Source: "..\scripts\Install-PixelOverlay.ps1"; Flags: dontcopy
Source: "..\ReleaseAssets\PixelOverlay.cer"; Flags: dontcopy

[Code]
const
  WAIT_OBJECT_0 = $00000000;
  WAIT_TIMEOUT = $00000102;
  PM_REMOVE = 1;
  WM_QUIT = $0012;

type
  TMsg = record
    hwnd: LongWord;
    message: LongWord;
    wParam: LongWord;
    lParam: LongInt;
    time: LongWord;
    ptX: LongInt;
    ptY: LongInt;
  end;

var
  StatusPage: TOutputMsgWizardPage;
  PreflightProgressPage: TOutputProgressWizardPage;
  ProgressPage: TOutputMarqueeProgressWizardPage;
  DetectedMode: String;
  InstalledVersion: String;
  AvailableVersion: String;
  InstalledLocation: String;
  DefaultVolumePath: String;
  GameBarRunning: Boolean;
  PreflightFailed: Boolean;
  PreflightError: String;

function OpenProcess(dwDesiredAccess: LongWord; bInheritHandle: Boolean; dwProcessId: LongWord): LongWord;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: LongWord; dwMilliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function GetExitCodeProcess(hProcess: LongWord; var lpExitCode: LongWord): Boolean;
  external 'GetExitCodeProcess@kernel32.dll stdcall';
function CloseHandle(hObject: LongWord): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
procedure Sleep(dwMilliseconds: LongWord);
  external 'Sleep@kernel32.dll stdcall';
function PeekMessage(var lpMsg: TMsg; hWnd: LongWord; wMsgFilterMin, wMsgFilterMax, wRemoveMsg: LongWord): Boolean;
  external 'PeekMessageA@user32.dll stdcall';
function TranslateMessage(const lpMsg: TMsg): Boolean;
  external 'TranslateMessage@user32.dll stdcall';
function DispatchMessage(const lpMsg: TMsg): LongInt;
  external 'DispatchMessageA@user32.dll stdcall';

procedure PumpWizardMessages;
var
  Msg: TMsg;
begin
  while PeekMessage(Msg, 0, 0, 0, PM_REMOVE) do
  begin
    if Msg.message <> WM_QUIT then
    begin
      TranslateMessage(Msg);
      DispatchMessage(Msg);
    end;
  end;
  WizardForm.Refresh;
end;

function RunPowerShell(const Parameters: String; const AsOriginalUser: Boolean;
  const Wait: TExecWait; var ResultCode: Integer): Boolean;
var
  PowerShellPath: String;
begin
  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  if AsOriginalUser then
    Result := ExecAsOriginalUser(PowerShellPath, Parameters, '', SW_HIDE, Wait, ResultCode)
  else
    Result := Exec(PowerShellPath, Parameters, '', SW_HIDE, Wait, ResultCode);
end;

function ReadPreflightFile(const FileName: String): Boolean;
var
  Lines: TStringList;
  I: Integer;
  Line: String;
  Data: String;
begin
  Result := False;
  Lines := TStringList.Create;
  try
    if not LoadStringFromFile(FileName, Data) then
      Exit;

    Lines.Text := Data;
    for I := 0 to Lines.Count - 1 do
    begin
      Line := Lines[I];
      if Pos('Mode=', Line) = 1 then
        DetectedMode := Copy(Line, 6, MaxInt)
      else if Pos('InstalledVersion=', Line) = 1 then
        InstalledVersion := Copy(Line, 18, MaxInt)
      else if Pos('AvailableVersion=', Line) = 1 then
        AvailableVersion := Copy(Line, 18, MaxInt)
      else if Pos('InstalledLocation=', Line) = 1 then
        InstalledLocation := Copy(Line, 19, MaxInt)
      else if Pos('DefaultVolumePath=', Line) = 1 then
        DefaultVolumePath := Copy(Line, 19, MaxInt)
      else if Pos('GameBarRunning=', Line) = 1 then
        GameBarRunning := Copy(Line, 16, MaxInt) = '1';
    end;

    Result := (DetectedMode <> '') and (AvailableVersion <> '') and
      (DefaultVolumePath <> '');
  finally
    Lines.Free;
  end;
end;

procedure RunPreflight;
var
  DetectScript: String;
  OutputPath: String;
  Parameters: String;
  ResultCode: Integer;
begin
  PreflightFailed := False;
  PreflightError := '';
  DetectedMode := '';
  InstalledVersion := '';
  AvailableVersion := '';
  InstalledLocation := '';
  DefaultVolumePath := '';
  GameBarRunning := False;

  DetectScript := ExpandConstant('{tmp}\Detect-PixelOverlay.ps1');
  OutputPath := ExpandConstant('{tmp}\PixelOverlay-Preflight.txt');
  DeleteFile(OutputPath);
  ExtractTemporaryFile('Detect-PixelOverlay.ps1');

  PreflightProgressPage := CreateOutputProgressPage('Checking your PC', 'Checking the installed version and Windows app package location...');
  PreflightProgressPage.Show;
  try
    try
      PreflightProgressPage.SetText('Connecting to the official Pixel Overlay update channel...', '');
      PreflightProgressPage.SetProgress(0, 2);
      Parameters :=
        '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
        '-File "' + DetectScript + '" -OutputPath "' + OutputPath + '"';

      if not RunPowerShell(Parameters, True, ewWaitUntilTerminated, ResultCode) then
        RaiseException('Windows PowerShell could not be started to inspect this PC.');
      if ResultCode <> 0 then
        RaiseException('Could not check the current Pixel Overlay version. Confirm that you are online and try again.');

      PreflightProgressPage.SetText('Reading the installed package and Windows storage settings...', '');
      PreflightProgressPage.SetProgress(1, 2);

      if not ReadPreflightFile(OutputPath) then
        RaiseException('The installer could not read the version and package-location information.');

      if not ((DetectedMode = 'Install') or (DetectedMode = 'Update') or
              (DetectedMode = 'Current') or (DetectedMode = 'Newer')) then
        RaiseException('The update channel returned an unsupported installation state.');

      PreflightProgressPage.SetProgress(2, 2);
      PreflightProgressPage.SetText('System check complete.', '');
    except
      PreflightFailed := True;
      PreflightError := GetExceptionMessage;
    end;
  finally
    PreflightProgressPage.Hide;
  end;
end;

function BuildStatusMessage: String;
begin
  Result := 'Setup package version: ' + ExpandConstant('{#AppVersion}') + #13#10 +
    'Latest version on the update channel: ' + AvailableVersion + #13#10#13#10;

  if DetectedMode = 'Install' then
    Result := Result + 'Pixel Overlay is not currently installed. Setup will perform a fresh installation.'
  else if DetectedMode = 'Update' then
    Result := Result + 'An older version (' + InstalledVersion + ') is installed. Setup will update it to ' +
      AvailableVersion + ' and preserve the application data.'
  else if DetectedMode = 'Current' then
    Result := Result + 'Pixel Overlay ' + InstalledVersion + ' is already installed and up to date. No package changes are needed.'
  else if DetectedMode = 'Newer' then
    Result := Result + 'The installed version (' + InstalledVersion + ') is newer than the update channel (' +
      AvailableVersion + '). Setup will not downgrade your installation.';

  if InstalledLocation <> '' then
    Result := Result + #13#10#13#10 + 'Current package location:' + #13#10 + InstalledLocation
  else
    Result := Result + #13#10#13#10 + 'Windows default app package volume:' + #13#10 + DefaultVolumePath;

  Result := Result + #13#10#13#10 +
    'Windows protects MSIX package files and controls the exact WindowsApps folder. A custom directory cannot be selected in this setup.';

  if GameBarRunning then
    Result := Result + #13#10#13#10 +
      'Xbox Game Bar appears to be running. Close it before updating so Windows can register the new version cleanly.';
end;

procedure InitializeWizard;
begin
  RunPreflight;

  if PreflightFailed then
    StatusPage := CreateOutputMsgPage(wpWelcome, 'System check', 'Pixel Overlay setup could not continue', PreflightError)
  else
    StatusPage := CreateOutputMsgPage(wpWelcome, 'System check', 'Installation status and location', BuildStatusMessage);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if PreflightFailed and (CurPageID = StatusPage.ID) then
  begin
    Result := False;
    MsgBox('Pixel Overlay setup cannot continue:' + #13#10#13#10 + PreflightError,
      mbError, MB_OK);
  end;
end;

function RunInstallProcess(const Parameters: String; var ExitCode: Integer): Boolean;
var
  ProcessId: Integer;
  ProcessHandle: LongWord;
  WaitResult: LongWord;
  NativeExitCode: LongWord;
begin
  Result := False;
  ExitCode := -1;
  ProcessId := 0;

  if not ExecAsOriginalUser(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters, '', SW_HIDE, ewNoWait, ProcessId) then
  begin
    ExitCode := ProcessId;
    Exit;
  end;

  if ProcessId <= 0 then
    Exit;

  ProcessHandle := OpenProcess($00100400, False, LongWord(ProcessId));
  if ProcessHandle = 0 then
    Exit;

  try
    repeat
      WaitResult := WaitForSingleObject(ProcessHandle, 200);
      PumpWizardMessages;
      if WaitResult = WAIT_OBJECT_0 then
        Break;
      if WaitResult <> WAIT_TIMEOUT then
        Exit;
      Sleep(100);
    until False;

    if not GetExitCodeProcess(ProcessHandle, NativeExitCode) then
      Exit;

    ExitCode := Integer(NativeExitCode);
    Result := (ExitCode = 0);
  finally
    CloseHandle(ProcessHandle);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  TrustParameters: String;
  InstallParameters: String;
  ProcessStarted: Boolean;
begin
  Result := '';

  if PreflightFailed then
  begin
    Result := PreflightError;
    Exit;
  end;

  if (DetectedMode = 'Current') or (DetectedMode = 'Newer') then
  begin
    Log('No package deployment required. Detected state: ' + DetectedMode);
    Exit;
  end;

  ExtractTemporaryFile('Install-PixelOverlay.ps1');
  ExtractTemporaryFile('PixelOverlay.cer');

  ProgressPage := CreateOutputMarqueeProgressPage('Installing Pixel Overlay', 'Preparing the signed package and registering it with Windows...');
  ProgressPage.Show;
  ProgressPage.Animate;
  try
    ProgressPage.SetText('Installing the trusted publisher certificate...', '');

    TrustParameters :=
      '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
      '-File "' + ExpandConstant('{tmp}\Install-PixelOverlay.ps1') + '" ' +
      '-CertificatePath "' + ExpandConstant('{tmp}\PixelOverlay.cer') + '" ' +
      '-TrustCertificateOnly';

    Log('Installing the publisher certificate into Local Computer\Trusted People.');
    if not RunPowerShell(TrustParameters, False, ewWaitUntilTerminated, ResultCode) then
    begin
      Result := 'Windows PowerShell could not be started to trust the Pixel Overlay publisher certificate.';
      Exit;
    end;
    if ResultCode <> 0 then
    begin
      Result := 'Windows could not install the Pixel Overlay publisher certificate (exit code ' +
        IntToStr(ResultCode) + ').' + #13#10 +
        'Check the installer log under the Windows account used for setup.';
      Exit;
    end;

    ProgressPage.SetText('Downloading and registering the signed Pixel Overlay package...', '');

    InstallParameters :=
      '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
      '-File "' + ExpandConstant('{tmp}\Install-PixelOverlay.ps1') + '" ' +
      '-CertificatePath "' + ExpandConstant('{tmp}\PixelOverlay.cer') + '"';

    Log('Installing or updating Pixel Overlay for the original signed-in Windows user.');
    ProcessStarted := RunInstallProcess(InstallParameters, ResultCode);
    if (not ProcessStarted) or (ResultCode <> 0) then
    begin
      Result := 'Pixel Overlay could not be installed (exit code ' + IntToStr(ResultCode) + ').' + #13#10 +
        'The detailed log is available at:' + #13#10 +
        ExpandConstant('{localappdata}\PixelOverlay\Installer.log');
      Exit;
    end;

    ProgressPage.SetText('Installation completed successfully.', '');
    Log('Pixel Overlay installation completed successfully.');
  finally
    ProgressPage.Hide;
  end;
end;
