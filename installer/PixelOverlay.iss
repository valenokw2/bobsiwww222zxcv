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
  WAIT_FAILED = $FFFFFFFF;

var
  StatusPage: TOutputMsgWizardPage;
  VolumePage: TInputOptionWizardPage;
  ProgressPage: TOutputProgressWizardPage;
  VolumePaths: TStringList;
  DetectedMode: String;
  InstalledVersion: String;
  AvailableVersion: String;
  InstalledLocation: String;
  GameBarRunning: Boolean;
  PreflightFailed: Boolean;
  PreflightError: String;
  OperationSkipped: Boolean;

function OpenProcess(dwDesiredAccess: LongWord; bInheritHandle: LongBool; dwProcessId: LongWord): LongWord;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: LongWord; dwMilliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function GetExitCodeProcess(hProcess: LongWord; var lpExitCode: LongWord): LongBool;
  external 'GetExitCodeProcess@kernel32.dll stdcall';
function CloseHandle(hObject: LongWord): LongBool;
  external 'CloseHandle@kernel32.dll stdcall';
procedure Sleep(dwMilliseconds: LongWord);
  external 'Sleep@kernel32.dll stdcall';

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
  Remainder: String;
  VolumeName: String;
  VolumePath: String;
  IsDefault: String;
  FirstSeparator: Integer;
  SecondSeparator: Integer;
  DefaultIndex: Integer;
begin
  Result := False;
  Lines := TStringList.Create;
  try
    if not LoadStringFromFile(FileName, Data) then
      Exit;

    Lines.Text := Data;
    DefaultIndex := -1;
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
        InstalledLocation := Copy(Line, 18, MaxInt)
      else if Pos('GameBarRunning=', Line) = 1 then
        GameBarRunning := Copy(Line, 16, MaxInt) = '1'
      else if Pos('Volume=', Line) = 1 then
      begin
        Data := Copy(Line, 8, MaxInt);
        FirstSeparator := Pos('|', Data);
        if FirstSeparator > 0 then
        begin
          VolumeName := Copy(Data, 1, FirstSeparator - 1);
          Remainder := Copy(Data, FirstSeparator + 1, MaxInt);
          SecondSeparator := Pos('|', Remainder);
          if SecondSeparator > 0 then
          begin
            VolumePath := Copy(Remainder, 1, SecondSeparator - 1);
            IsDefault := Copy(Remainder, SecondSeparator + 1, MaxInt);
            if VolumePath <> '' then
            begin
              VolumePage.Add(VolumeName + '  (' + VolumePath + ')');
              VolumePaths.Add(VolumePath);
              if IsDefault = '1' then
                DefaultIndex := VolumePaths.Count - 1;
            end;
          end;
        end;
      end;
    end;

    if (DefaultIndex >= 0) and (VolumePaths.Count > 0) then
      VolumePage.SelectedValueIndex := DefaultIndex
    else if VolumePaths.Count > 0 then
      VolumePage.SelectedValueIndex := 0;

    Result := (DetectedMode <> '') and (AvailableVersion <> '');
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
  GameBarRunning := False;

  DetectScript := ExpandConstant('{tmp}\Detect-PixelOverlay.ps1');
  OutputPath := ExpandConstant('{tmp}\PixelOverlay-Preflight.txt');
  DeleteFile(OutputPath);
  ExtractTemporaryFile('Detect-PixelOverlay.ps1');

  ProgressPage := CreateOutputProgressPage('Checking your PC', 'Checking the installed version and Windows app package volumes...');
  ProgressPage.Show;
  try
    ProgressPage.SetText('Connecting to the official Pixel Overlay update channel...', '');
    ProgressPage.SetProgress(0, 2);
    Parameters :=
      '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
      '-File "' + DetectScript + '" -OutputPath "' + OutputPath + '"';

    if not RunPowerShell(Parameters, True, ewWaitUntilTerminated, ResultCode) then
      raise Exception.Create('Windows PowerShell could not be started to inspect this PC.');
    if ResultCode <> 0 then
      raise Exception.Create('Could not check the current Pixel Overlay version. Confirm that you are online and try again.');

    ProgressPage.SetText('Reading the installed package and available install volumes...', '');
    ProgressPage.SetProgress(1, 2);

    if not ReadPreflightFile(OutputPath) then
      raise Exception.Create('The installer could not read the version and package-volume information.');

    if (DetectedMode = 'Install') and (VolumePaths.Count = 0) then
      raise Exception.Create('Windows did not report an available MSIX package volume.');
    if not ((DetectedMode = 'Install') or (DetectedMode = 'Update') or
            (DetectedMode = 'Current') or (DetectedMode = 'Newer')) then
      raise Exception.Create('The update channel returned an unsupported installation state.');

    ProgressPage.SetProgress(2, 2);
    ProgressPage.SetText('System check complete.', '');
  except
    PreflightFailed := True;
    PreflightError := GetExceptionMessage;
  finally
    ProgressPage.Hide;
  end;
end;

function BuildStatusMessage: String;
begin
  Result := 'Setup package version: ' + ExpandConstant('{#AppVersion}') + #13#10 +
    'Latest version on the update channel: ' + AvailableVersion + #13#10#13#10;

  if DetectedMode = 'Install' then
    Result := Result + 'No existing Pixel Overlay installation was found. A fresh installation will be performed.'
  else if DetectedMode = 'Update' then
    Result := Result + 'An older version (' + InstalledVersion + ') is installed. Setup will update it to ' +
      AvailableVersion + ' and preserve the application data.'
  else if DetectedMode = 'Current' then
    Result := Result + 'Pixel Overlay ' + InstalledVersion + ' is already installed and up to date. No files need to be changed.'
  else if DetectedMode = 'Newer' then
    Result := Result + 'The installed version (' + InstalledVersion + ') is newer than the update channel (' +
      AvailableVersion + '). Setup will not downgrade your installation.';

  if InstalledLocation <> '' then
    Result := Result + #13#10#13#10 + 'Current package location:' + #13#10 + InstalledLocation;

  if DetectedMode = 'Update' then
    Result := Result + #13#10#13#10 + 'Your current Windows-managed install volume will be retained.'
  else if DetectedMode = 'Install' then
    Result := Result + #13#10#13#10 +
      'You can choose a registered Windows app package volume on the next page. Windows manages the package folder itself.';

  if GameBarRunning then
    Result := Result + #13#10#13#10 +
      'Xbox Game Bar appears to be running. Close it before updating so Windows can register the new version cleanly.';

  Result := Result + #13#10#13#10 +
    'Pixel Overlay is installed as an MSIX app. Windows protects its package files and controls the exact folder location.';
end;

procedure InitializeWizard;
var
  I: Integer;
begin
  VolumePaths := TStringList.Create;
  StatusPage := nil;
  VolumePage := nil;
  OperationSkipped := False;

  RunPreflight;

  if PreflightFailed then
    StatusPage := CreateOutputMsgPage(wpWelcome, 'System check', 'Pixel Overlay setup could not continue', PreflightError)
  else
    StatusPage := CreateOutputMsgPage(wpWelcome, 'System check', 'Installation status', BuildStatusMessage);

  VolumePage := CreateInputOptionPage(StatusPage.ID,
    'Installation drive',
    'Choose where Windows should store Pixel Overlay',
    'Select one of the package volumes already registered with Windows. The physical WindowsApps folder remains protected and is managed by Windows.',
    True, False);

  if not PreflightFailed then
  begin
    if DetectedMode = 'Install' then
    begin
      { Volume choices were populated from the signed-in user's Windows Appx volume list. }
      if VolumePage.Values.Count = 0 then
      begin
        PreflightFailed := True;
        PreflightError := 'No online Windows app package volume is available for a new installation.';
      end;
    end;
  end;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (VolumePage <> nil) and (PageID = VolumePage.ID) and
     (DetectedMode <> 'Install') then
    Result := True;
  if PreflightFailed and (VolumePage <> nil) and (PageID = VolumePage.ID) then
    Result := True;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if PreflightFailed then
  begin
    Result := False;
    MsgBox('Pixel Overlay setup cannot continue:' + #13#10#13#10 + PreflightError,
      mbError, MB_OK);
    Exit;
  end;

  if (VolumePage <> nil) and (CurPageID = VolumePage.ID) and
     (VolumePaths.Count > 0) and
     ((VolumePage.SelectedValueIndex < 0) or
      (VolumePage.SelectedValueIndex >= VolumePaths.Count)) then
  begin
    Result := False;
    MsgBox('Select a Windows app package volume before continuing.', mbInformation, MB_OK);
  end;
end;

function RunInstallProcess(const Parameters, ProgressFile: String; var ExitCode: Integer): Boolean;
var
  ProcessId: Integer;
  ProcessHandle: LongWord;
  WaitResult: LongWord;
  NativeExitCode: LongWord;
  ProgressText: String;
  Separator: Integer;
  Percent: Integer;
  Status: String;
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
  begin
    ExitCode := -1;
    Exit;
  end;

  ProcessHandle := OpenProcess($00100400, False, LongWord(ProcessId));
  if ProcessHandle = 0 then
  begin
    ExitCode := -1;
    Exit;
  end;

  try
    repeat
      WaitResult := WaitForSingleObject(ProcessHandle, 250);

      if LoadStringFromFile(ProgressFile, ProgressText) then
      begin
        Separator := Pos('|', ProgressText);
        if Separator > 0 then
        begin
          Percent := StrToIntDef(Copy(ProgressText, 1, Separator - 1), 0);
          Status := Copy(ProgressText, Separator + 1, MaxInt);
          if Percent < 0 then Percent := 0;
          if Percent > 100 then Percent := 100;
          ProgressPage.SetText(Status, '');
          ProgressPage.SetProgress(Percent, 100);
        end;
      end;

      WizardForm.Update;
      if WaitResult = WAIT_OBJECT_0 then
        Break;
      if WaitResult <> WAIT_TIMEOUT then
      begin
        ExitCode := -1;
        Exit;
      end;
      Sleep(100);
    until False;

    if not GetExitCodeProcess(ProcessHandle, NativeExitCode) then
    begin
      ExitCode := -1;
      Exit;
    end;

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
  ProgressFile: String;
  SelectedVolumePath: String;
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
    OperationSkipped := True;
    Log('No package deployment required. Detected state: ' + DetectedMode);
    Exit;
  end;

  ExtractTemporaryFile('Install-PixelOverlay.ps1');
  ExtractTemporaryFile('PixelOverlay.cer');

  ProgressFile := ExpandConstant('{tmp}\PixelOverlay-Install-Progress.txt');
  DeleteFile(ProgressFile);

  ProgressPage := CreateOutputProgressPage('Installing Pixel Overlay', 'Preparing the signed package and registering it with Windows...');
  ProgressPage.Show;
  try
    ProgressPage.SetText('Installing the trusted publisher certificate...', '');
    ProgressPage.SetProgress(5, 100);

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
        IntToStr(ResultCode) + ').' + #13#10#13#10 +
        'Check the installer log under the Windows account used for setup.';
      Exit;
    end;

    ProgressPage.SetText('Starting the per-user app installation...', '');
    ProgressPage.SetProgress(12, 100);

    InstallParameters :=
      '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
      '-File "' + ExpandConstant('{tmp}\Install-PixelOverlay.ps1') + '" ' +
      '-CertificatePath "' + ExpandConstant('{tmp}\PixelOverlay.cer') + '" ' +
      '-ProgressFile "' + ProgressFile + '"';

    if DetectedMode = 'Install' then
    begin
      SelectedVolumePath := VolumePaths[VolumePage.SelectedValueIndex];
      InstallParameters := InstallParameters + ' -VolumePath "' + SelectedVolumePath + '"';
    end;

    Log('Installing or updating Pixel Overlay for the original signed-in Windows user.');
    ProcessStarted := RunInstallProcess(InstallParameters, ProgressFile, ResultCode);
    if not ProcessStarted then
    begin
      Result := 'Pixel Overlay could not be installed (exit code ' + IntToStr(ResultCode) + ').' +
        #13#10#13#10 + 'The detailed log is available at:' + #13#10 +
        ExpandConstant('{localappdata}\PixelOverlay\Installer.log');
      Exit;
    end;

    ProgressPage.SetText('Installation completed successfully.', '');
    ProgressPage.SetProgress(100, 100);
    Log('Pixel Overlay installation completed successfully.');
  finally
    ProgressPage.Hide;
  end;
end;

procedure DeinitializeSetup;
begin
  if VolumePaths <> nil then
    VolumePaths.Free;
end;
