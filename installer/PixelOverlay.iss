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
DefaultDirName={tmp}\PixelOverlaySetup
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=yes
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
Source: "..\scripts\Install-PixelOverlay.ps1"; Flags: dontcopy
Source: "..\ReleaseAssets\PixelOverlay.cer"; Flags: dontcopy

[Code]
function InitializeSetup(): Boolean;
begin
  Result := IsWin64;
  if not Result then
    MsgBox('Pixel Overlay requires 64-bit Windows.', mbError, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  PowerShellPath: String;
  TrustParameters: String;
  InstallParameters: String;
begin
  Result := '';
  ExtractTemporaryFile('Install-PixelOverlay.ps1');
  ExtractTemporaryFile('PixelOverlay.cer');

  PowerShellPath := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  TrustParameters :=
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
    '-File "' + ExpandConstant('{tmp}\Install-PixelOverlay.ps1') + '" ' +
    '-CertificatePath "' + ExpandConstant('{tmp}\PixelOverlay.cer') + '" ' +
    '-TrustCertificateOnly';

  Log('Installing the publisher certificate into Local Computer\Trusted People.');
  if not Exec(PowerShellPath, TrustParameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Windows PowerShell could not be started to install the publisher certificate.';
    Exit;
  end;
  if ResultCode <> 0 then
  begin
    Result :=
      'Pixel Overlay could not install its publisher certificate (exit code ' + IntToStr(ResultCode) + ').' + #13#10 + #13#10 +
      'Check the installer log under the Windows account used for setup.';
    Exit;
  end;

  InstallParameters :=
    '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ' +
    '-File "' + ExpandConstant('{tmp}\Install-PixelOverlay.ps1') + '" ' +
    '-CertificatePath "' + ExpandConstant('{tmp}\PixelOverlay.cer') + '"';

  Log('Installing Pixel Overlay for the original signed-in Windows user.');
  if not ExecAsOriginalUser(PowerShellPath, InstallParameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result :=
      'The publisher certificate was installed, but setup could not start the per-user package installation.' + #13#10 + #13#10 +
      'Close this installer and launch it normally from your Windows account. Do not use Run as administrator.';
    Exit;
  end;
  if ResultCode <> 0 then
  begin
    Result :=
      'Pixel Overlay could not be installed (exit code ' + IntToStr(ResultCode) + ').' + #13#10 + #13#10 +
      'The detailed log is available at:' + #13#10 +
      ExpandConstant('{localappdata}\PixelOverlay\Installer.log');
    Exit;
  end;

  Log('Pixel Overlay installation completed successfully.');
end;
