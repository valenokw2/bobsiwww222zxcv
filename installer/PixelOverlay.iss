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
WizardStyle=modern dynamic windows11
WizardSizePercent=130,120
WizardResizable=yes
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
  StatusPage: TWizardPage;
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
  Lines: TArrayOfString;
  I: Integer;
  Line: String;
begin
  Result := False;
  if not LoadStringsFromFile(FileName, Lines) then
    Exit;

  for I := 0 to GetArrayLength(Lines) - 1 do
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
end;

function ReadPreflightError(const FileName: String): String;
var
  Lines: TArrayOfString;
  I: Integer;
begin
  Result := '';
  if not LoadStringsFromFile(FileName, Lines) then
    Exit;

  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    if Result <> '' then
      Result := Result + #13#10;
    Result := Result + Lines[I];
  end;
end;

procedure RunPreflight;
var
  DetectScript: String;
  OutputPath: String;
  ErrorPath: String;
  ErrorDetails: String;
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
  OutputPath := ExpandConstant('{localappdata}\PixelOverlay\Preflight-Result.txt');
  ErrorPath := ExpandConstant('{localappdata}\PixelOverlay\Preflight-Error.txt');
  DeleteFile(OutputPath);
  DeleteFile(ErrorPath);
  ExtractTemporaryFile('Detect-PixelOverlay.ps1');

  PreflightProgressPage := CreateOutputProgressPage('Checking your PC', 'Checking the installed version and Windows app package location...');
  PreflightProgressPage.Show;
  try
    try
      PreflightProgressPage.SetText('Connecting to the official Pixel Overlay update channel...', '');
      PreflightProgressPage.SetProgress(0, 2);
      Parameters :=
        '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
        '-File "' + DetectScript + '" -OutputPath "' + OutputPath + '" ' +
        '-ErrorPath "' + ErrorPath + '"';

      if not RunPowerShell(Parameters, True, ewWaitUntilTerminated, ResultCode) then
        RaiseException('Windows PowerShell could not be started to inspect this PC.');
      if ResultCode <> 0 then
      begin
        ErrorDetails := ReadPreflightError(ErrorPath);
        if ErrorDetails <> '' then
          RaiseException('The version check failed:' + #13#10 + ErrorDetails + #13#10#13#10 +
            'Diagnostic log: ' + ExpandConstant('{localappdata}\PixelOverlay\Preflight.log'))
        else
          RaiseException('Could not check the current Pixel Overlay version. Check your internet connection and see:' + #13#10 +
            ExpandConstant('{localappdata}\PixelOverlay\Preflight.log'));
      end;

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

procedure AddStatusText(Page: TWizardPage; const Caption: String;
  Top, Height, FontSize: Integer; Bold, Wrap: Boolean);
var
  TextControl: TNewStaticText;
begin
  TextControl := TNewStaticText.Create(Page);
  TextControl.Parent := Page.Surface;
  TextControl.Left := 0;
  TextControl.Top := ScaleY(Top);
  TextControl.Width := Page.SurfaceWidth;
  TextControl.Height := ScaleY(Height);
  TextControl.AutoSize := False;
  TextControl.WordWrap := Wrap;
  TextControl.Caption := Caption;
  TextControl.Font.Size := FontSize;
  if Bold then
    TextControl.Font.Style := TextControl.Font.Style + [fsBold];
  TextControl.Anchors := [akLeft, akTop, akRight];
end;

procedure AddStatusRow(Page: TWizardPage; const Caption, Value: String; Top: Integer);
var
  NameLabel: TNewStaticText;
  ValueLabel: TNewStaticText;
  ValueLeft: Integer;
begin
  ValueLeft := ScaleX(156);

  NameLabel := TNewStaticText.Create(Page);
  NameLabel.Parent := Page.Surface;
  NameLabel.Left := ScaleX(12);
  NameLabel.Top := ScaleY(Top);
  NameLabel.Width := ValueLeft - ScaleX(20);
  NameLabel.Height := ScaleY(34);
  NameLabel.AutoSize := False;
  NameLabel.WordWrap := True;
  NameLabel.Caption := Caption;
  NameLabel.Font.Style := NameLabel.Font.Style + [fsBold];
  NameLabel.Anchors := [akLeft, akTop];

  ValueLabel := TNewStaticText.Create(Page);
  ValueLabel.Parent := Page.Surface;
  ValueLabel.Left := ValueLeft;
  ValueLabel.Top := ScaleY(Top);
  ValueLabel.Width := Page.SurfaceWidth - ValueLeft - ScaleX(12);
  ValueLabel.Height := ScaleY(34);
  ValueLabel.AutoSize := False;
  ValueLabel.WordWrap := True;
  ValueLabel.Caption := Value;
  ValueLabel.Anchors := [akLeft, akTop, akRight];
end;

procedure CreateStatusPage;
var
  Heading: String;
  InstalledText: String;
  LocationText: String;
  IntroText: String;
begin
  StatusPage := CreateCustomPage(wpWelcome, 'Installation overview',
    'Review the detected installation state before continuing.');

  if PreflightFailed then
  begin
    AddStatusText(StatusPage, 'The system check could not be completed',
      8, 32, 15, True, False);
    AddStatusText(StatusPage, 'Setup has not changed your Pixel Overlay installation. The check stopped with this error:',
      48, 42, 9, False, True);
    AddStatusText(StatusPage, PreflightError,
      96, 155, 9, False, True);
    AddStatusText(StatusPage, 'Diagnostic log: ' +
      ExpandConstant('{localappdata}\PixelOverlay\Preflight.log'),
      258, 32, 8, False, True);
    Exit;
  end;

  InstalledText := InstalledVersion;
  if InstalledText = '' then
    InstalledText := 'Not installed';

  LocationText := InstalledLocation;
  if LocationText = '' then
    LocationText := DefaultVolumePath;

  if DetectedMode = 'Install' then
  begin
    Heading := 'Ready for a fresh installation';
    IntroText := 'Pixel Overlay is not installed for this Windows account. Setup will register the latest signed package.';
  end
  else if DetectedMode = 'Update' then
  begin
    Heading := 'An update is ready';
    IntroText := 'An older version was detected. Setup will update Pixel Overlay and preserve its application data.';
  end
  else if DetectedMode = 'Current' then
  begin
    Heading := 'Pixel Overlay is up to date';
    IntroText := 'The latest version is already installed. Continuing will not replace or reinstall the package.';
  end
  else
  begin
    Heading := 'A newer version is already installed';
    IntroText := 'Setup will not downgrade your current installation.';
  end;

  AddStatusText(StatusPage, Heading, 8, 32, 16, True, False);
  AddStatusText(StatusPage, IntroText, 48, 42, 9, False, True);

  AddStatusRow(StatusPage, 'INSTALLED VERSION', InstalledText, 108);
  AddStatusRow(StatusPage, 'LATEST VERSION', AvailableVersion, 148);
  AddStatusRow(StatusPage, 'PACKAGE LOCATION', LocationText, 188);

  AddStatusText(StatusPage,
    'Windows manages the protected WindowsApps directory for MSIX packages. A custom installation folder cannot be selected here.',
    238, 48, 8, False, True);

  if GameBarRunning then
    AddStatusText(StatusPage,
      'Xbox Game Bar appears to be running. Close it before updating to avoid package registration conflicts.',
      292, 44, 8, True, True)
  else
    AddStatusText(StatusPage,
      'The setup is ready. Select Next to continue.',
      292, 30, 8, False, False);
end;

procedure InitializeWizard;
begin
  RunPreflight;
  CreateStatusPage;
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
