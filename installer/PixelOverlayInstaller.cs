using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Pixel Overlay Setup")]
[assembly: AssemblyProduct("Pixel Overlay")]
[assembly: AssemblyCompany("Pixel Overlay")]
[assembly: AssemblyDescription("Self-contained Windows installer for Pixel Overlay")]
[assembly: AssemblyVersion("__APP_VERSION__")]
[assembly: AssemblyFileVersion("__APP_VERSION__")]

namespace PixelOverlayInstaller
{
    internal static class Program
    {
        internal const string AppVersion = "__APP_VERSION__";
        internal const string AppInstallerUri = "https://github.com/valenokw2/bobsiwww222zxcv/releases/download/update-channel/PixelOverlay.appinstaller";
        internal const string CertificateThumbprint = "A68A682C3193352F350F0FC6D7A5F8990863E150";

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 2 && String.Equals(args[0], "--trust-certificate", StringComparison.OrdinalIgnoreCase))
                return TrustCertificate(args[1]);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
            return 0;
        }

        private static int TrustCertificate(string path)
        {
            try
            {
                X509Certificate2 certificate = new X509Certificate2(path);
                if (!String.Equals(certificate.Subject, "CN=PixelOverlayDeveloper", StringComparison.OrdinalIgnoreCase) ||
                    !String.Equals(certificate.Thumbprint, CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
                    return 11;

                using (X509Store store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine))
                {
                    store.Open(OpenFlags.ReadWrite);
                    X509Certificate2Collection found = store.Certificates.Find(
                        X509FindType.FindByThumbprint, CertificateThumbprint, false);
                    if (found.Count == 0)
                        store.Add(certificate);
                }
                return 0;
            }
            catch
            {
                return 12;
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly string payloadDirectory;
        private readonly string logPath;
        private readonly string scriptPath;
        private readonly string certificatePath;
        private readonly Label statusTitle;
        private readonly Label statusBody;
        private readonly Label installedValue;
        private readonly Label targetValue;
        private readonly Label footer;
        private readonly ProgressBar progress;
        private readonly Button actionButton;
        private readonly Button cancelButton;
        private bool busy;
        private bool installedSuccessfully;
        private bool closingAllowed;
        private string detectedMode;
        private string installedVersion;
        private string warningMessage;

        internal SetupForm()
        {
            Text = "Pixel Overlay Setup";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = true;
            ClientSize = new Size(650, 455);
            BackColor = Color.FromArgb(248, 250, 252);
            Font = new Font("Segoe UI", 9F);
            ShowIcon = true;

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PixelOverlay", "Installer");
            Directory.CreateDirectory(root);
            logPath = Path.Combine(root, "Setup.log");
            payloadDirectory = Path.Combine(Path.GetTempPath(), "PixelOverlaySetup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(payloadDirectory);

            scriptPath = ExtractResource(
                "PixelOverlayInstaller.Payload.Install-PixelOverlay.ps1",
                "Install-PixelOverlay.ps1");
            certificatePath = ExtractResource(
                "PixelOverlayInstaller.Payload.PixelOverlay.cer",
                "PixelOverlay.cer");
            ExtractResource("PixelOverlayInstaller.Payload.PixelOverlay.msix", "PixelOverlay.msix");
            ExtractResource("PixelOverlayInstaller.Payload.Microsoft.NET.CoreFramework.Debug.2.2.appx",
                "Microsoft.NET.CoreFramework.Debug.2.2.appx");
            ExtractResource("PixelOverlayInstaller.Payload.Microsoft.NET.CoreRuntime.2.2.appx",
                "Microsoft.NET.CoreRuntime.2.2.appx");
            ExtractResource("PixelOverlayInstaller.Payload.Microsoft.VCLibs.x64.14.00.appx",
                "Microsoft.VCLibs.x64.14.00.appx");

            Panel header = new Panel();
            header.Dock = DockStyle.Top;
            header.Height = 116;
            header.BackColor = Color.FromArgb(15, 23, 42);
            Controls.Add(header);

            Label brand = new Label();
            brand.AutoSize = true;
            brand.Text = "PIXEL OVERLAY";
            brand.ForeColor = Color.FromArgb(148, 163, 184);
            brand.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            brand.Location = new Point(34, 22);
            header.Controls.Add(brand);

            Label heading = new Label();
            heading.AutoSize = true;
            heading.Text = "One-file Windows installer";
            heading.ForeColor = Color.White;
            heading.Font = new Font("Segoe UI Semibold", 21F, FontStyle.Bold);
            heading.Location = new Point(31, 47);
            header.Controls.Add(heading);

            Label subheading = new Label();
            subheading.AutoSize = true;
            subheading.Text = "Application, framework dependencies and publisher certificate included";
            subheading.ForeColor = Color.FromArgb(203, 213, 225);
            subheading.Font = new Font("Segoe UI", 9F);
            subheading.Location = new Point(35, 87);
            header.Controls.Add(subheading);

            Label section = new Label();
            section.AutoSize = true;
            section.Text = "Installation overview";
            section.Font = new Font("Segoe UI Semibold", 14F, FontStyle.Bold);
            section.ForeColor = Color.FromArgb(15, 23, 42);
            section.Location = new Point(36, 139);
            Controls.Add(section);

            statusTitle = new Label();
            statusTitle.AutoSize = true;
            statusTitle.Text = "Checking this PC";
            statusTitle.Font = new Font("Segoe UI Semibold", 11F, FontStyle.Bold);
            statusTitle.ForeColor = Color.FromArgb(30, 41, 59);
            statusTitle.Location = new Point(37, 181);
            Controls.Add(statusTitle);

            statusBody = new Label();
            statusBody.AutoSize = false;
            statusBody.Text = "Reading the installed version and Windows compatibility...";
            statusBody.ForeColor = Color.FromArgb(71, 85, 105);
            statusBody.Location = new Point(37, 209);
            statusBody.Size = new Size(570, 48);
            Controls.Add(statusBody);

            Panel versionPanel = new Panel();
            versionPanel.Location = new Point(36, 267);
            versionPanel.Size = new Size(578, 74);
            versionPanel.BackColor = Color.White;
            versionPanel.BorderStyle = BorderStyle.FixedSingle;
            Controls.Add(versionPanel);

            Label installedCaption = new Label();
            installedCaption.AutoSize = true;
            installedCaption.Text = "INSTALLED VERSION";
            installedCaption.Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold);
            installedCaption.ForeColor = Color.FromArgb(100, 116, 139);
            installedCaption.Location = new Point(16, 12);
            versionPanel.Controls.Add(installedCaption);

            installedValue = new Label();
            installedValue.AutoSize = true;
            installedValue.Text = "Detecting...";
            installedValue.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
            installedValue.ForeColor = Color.FromArgb(15, 23, 42);
            installedValue.Location = new Point(16, 35);
            versionPanel.Controls.Add(installedValue);

            Label targetCaption = new Label();
            targetCaption.AutoSize = true;
            targetCaption.Text = "PACKAGE VERSION";
            targetCaption.Font = new Font("Segoe UI Semibold", 8F, FontStyle.Bold);
            targetCaption.ForeColor = Color.FromArgb(100, 116, 139);
            targetCaption.Location = new Point(315, 12);
            versionPanel.Controls.Add(targetCaption);

            targetValue = new Label();
            targetValue.AutoSize = true;
            targetValue.Text = Program.AppVersion;
            targetValue.Font = new Font("Segoe UI Semibold", 10F, FontStyle.Bold);
            targetValue.ForeColor = Color.FromArgb(15, 23, 42);
            targetValue.Location = new Point(315, 35);
            versionPanel.Controls.Add(targetValue);

            progress = new ProgressBar();
            progress.Location = new Point(37, 359);
            progress.Size = new Size(576, 9);
            progress.Style = ProgressBarStyle.Marquee;
            progress.MarqueeAnimationSpeed = 24;
            Controls.Add(progress);

            footer = new Label();
            footer.AutoSize = false;
            footer.Text = "Setup log: " + logPath;
            footer.ForeColor = Color.FromArgb(100, 116, 139);
            footer.Font = new Font("Segoe UI", 7.5F);
            footer.Location = new Point(37, 379);
            footer.Size = new Size(570, 24);
            Controls.Add(footer);

            actionButton = new Button();
            actionButton.Text = "Checking...";
            actionButton.Location = new Point(383, 410);
            actionButton.Size = new Size(135, 32);
            actionButton.Enabled = false;
            actionButton.FlatStyle = FlatStyle.Flat;
            actionButton.FlatAppearance.BorderSize = 0;
            actionButton.BackColor = Color.FromArgb(37, 99, 235);
            actionButton.ForeColor = Color.White;
            actionButton.Font = new Font("Segoe UI Semibold", 9F, FontStyle.Bold);
            actionButton.Click += ActionButtonClick;
            Controls.Add(actionButton);

            cancelButton = new Button();
            cancelButton.Text = "Cancel";
            cancelButton.Location = new Point(526, 410);
            cancelButton.Size = new Size(88, 32);
            cancelButton.FlatStyle = FlatStyle.Flat;
            cancelButton.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            cancelButton.BackColor = Color.White;
            cancelButton.ForeColor = Color.FromArgb(51, 65, 85);
            cancelButton.Click += delegate { Close(); };
            Controls.Add(cancelButton);

            AppendLog("Installer started. Version " + Program.AppVersion + ".");
            Shown += delegate { BeginDetect(); };
            FormClosing += SetupFormClosing;
        }

        private string ExtractResource(string resourceName, string fileName)
        {
            string path = Path.Combine(payloadDirectory, fileName);
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                    throw new InvalidOperationException("Installer payload is incomplete: " + fileName);
                using (FileStream output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                    stream.CopyTo(output);
            }
            return path;
        }

        private void AppendLog(string message)
        {
            try
            {
                File.AppendAllText(logPath,
                    "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        private void SetBusy(bool value, string message)
        {
            busy = value;
            progress.Style = value ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
            if (!value) progress.Value = 0;
            statusBody.Text = message;
            actionButton.Enabled = !value;
            cancelButton.Enabled = !value;
        }

        private void BeginDetect()
        {
            SetBusy(true, "Checking Windows compatibility and the currently installed version...");
            RunPowerShell("Detect", delegate(int exitCode, string output)
            {
                if (exitCode != 0)
                {
                    SetBusy(false, "The system check failed. Review the diagnostic log, then retry.");
                    statusTitle.Text = "System check could not finish";
                    actionButton.Text = "Retry check";
                    actionButton.Enabled = true;
                    detectedMode = "Error";
                    AppendLog("Detection failed: " + output);
                    return;
                }

                string stateLine = FindLine(output, "PIXEL_SETUP_STATE|");
                if (String.IsNullOrEmpty(stateLine))
                {
                    SetBusy(false, "Windows did not return a valid installation state. Review the setup log.");
                    statusTitle.Text = "Could not read installation state";
                    actionButton.Text = "Retry check";
                    actionButton.Enabled = true;
                    detectedMode = "Error";
                    return;
                }

                string[] parts = stateLine.Split('|');
                if (parts.Length < 5)
                {
                    SetBusy(false, "The system check returned incomplete version information.");
                    statusTitle.Text = "Could not read installation state";
                    actionButton.Text = "Retry check";
                    actionButton.Enabled = true;
                    detectedMode = "Error";
                    return;
                }

                detectedMode = parts[1];
                installedVersion = parts[2];
                string build = parts[4];
                installedValue.Text = String.IsNullOrEmpty(installedVersion) ? "Not installed" : installedVersion;

                SetBusy(false, "");
                if (detectedMode == "Install")
                {
                    statusTitle.Text = "Ready for a fresh installation";
                    statusBody.Text = "Pixel Overlay is not installed. The complete package and all dependencies are embedded in this file.";
                    actionButton.Text = "Install";
                    actionButton.Enabled = true;
                }
                else if (detectedMode == "Update")
                {
                    statusTitle.Text = "An update is ready";
                    statusBody.Text = "An older version was detected. Setup will update the app and keep its local data.";
                    actionButton.Text = "Update";
                    actionButton.Enabled = true;
                }
                else if (detectedMode == "Current")
                {
                    statusTitle.Text = "Pixel Overlay is up to date";
                    statusBody.Text = "Version " + installedVersion + " is already installed. No package changes are required.";
                    actionButton.Text = "Close";
                    actionButton.Enabled = true;
                    installedSuccessfully = true;
                }
                else if (detectedMode == "Newer")
                {
                    statusTitle.Text = "A newer version is already installed";
                    statusBody.Text = "Setup will not downgrade your installation.";
                    actionButton.Text = "Close";
                    actionButton.Enabled = true;
                    installedSuccessfully = true;
                }
                else
                {
                    statusTitle.Text = "System check completed";
                    statusBody.Text = "Windows build " + build + ".";
                    actionButton.Text = "Install";
                    actionButton.Enabled = true;
                }
            });
        }

        private void ActionButtonClick(object sender, EventArgs e)
        {
            if (busy) return;
            if (installedSuccessfully)
            {
                closingAllowed = true;
                Close();
                return;
            }
            if (detectedMode == "Error")
            {
                BeginDetect();
                return;
            }
            BeginInstall();
        }

        private void BeginInstall()
        {
            SetBusy(true, "Requesting certificate trust, then installing the signed app package...");
            statusTitle.Text = "Installing Pixel Overlay";
            actionButton.Text = "Installing...";
            actionButton.Enabled = false;

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    EnsureCertificateTrusted();
                    BeginInvoke((MethodInvoker)delegate { RunInstallAttempt(false); });
                }
                catch (Exception ex)
                {
                    AppendLog("Certificate trust failed: " + ex);
                    BeginInvoke((MethodInvoker)delegate
                    {
                        SetBusy(false, ex.Message);
                        statusTitle.Text = "Publisher certificate could not be trusted";
                        actionButton.Text = "Retry";
                        actionButton.Enabled = true;
                        detectedMode = "Error";
                    });
                }
            });
        }

        private void RunInstallAttempt(bool forceApplicationShutdown)
        {
            if (forceApplicationShutdown)
            {
                SetBusy(true, "Closing applications that are holding shared Windows package files...");
                statusTitle.Text = "Retrying installation";
                actionButton.Text = "Retrying...";
            }

            RunPowerShell("Install", delegate(int exitCode, string output)
            {
                string packagesInUseLine = FindLine(output, "PIXEL_SETUP_PACKAGES_IN_USE|");
                if (exitCode != 0 && packagesInUseLine != null && !forceApplicationShutdown)
                {
                    string blockingDetails = packagesInUseLine.Substring("PIXEL_SETUP_PACKAGES_IN_USE|".Length);
                    string prompt =
                        "Windows cannot finish installing Pixel Overlay because shared app components are currently in use.\r\n\r\n" +
                        "Windows reported these packages as busy:\r\n" + blockingDetails + "\r\n\r\n" +
                        "You can close these apps yourself and retry, or let Windows force-close active apps that use the package and its dependencies. " +
                        "Unsaved work in those apps may be lost. Save your work before continuing.\r\n\r\n" +
                        "Force-close the affected apps and retry now?";

                    AppendLog("Package-in-use conflict detected. Asking before force shutdown. " + blockingDetails);
                    DialogResult answer = MessageBox.Show(
                        this,
                        prompt,
                        "Applications are using Windows package files",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);

                    if (answer == DialogResult.Yes)
                    {
                        AppendLog("User approved ForceApplicationShutdown retry.");
                        RunInstallAttempt(true);
                    }
                    else
                    {
                        SetBusy(false, "Installation paused. Save your work, close the listed apps, and select Retry.");
                        statusTitle.Text = "Close the affected apps to continue";
                        actionButton.Text = "Retry";
                        actionButton.Enabled = true;
                        detectedMode = "Error";
                    }
                    return;
                }

                string successLine = FindLine(output, "PIXEL_SETUP_SUCCESS|");
                string warningLine = FindLine(output, "PIXEL_SETUP_WARNING|");
                string failureLine = FindLine(output, "PIXEL_SETUP_FAILURE|");

                SetBusy(false, "");
                if (exitCode == 0 && successLine != null)
                {
                    installedSuccessfully = true;
                    statusTitle.Text = "Pixel Overlay is ready";
                    statusBody.Text = successLine.Substring("PIXEL_SETUP_SUCCESS|".Length);
                    if (!String.IsNullOrEmpty(warningLine))
                        statusBody.Text += Environment.NewLine + warningLine.Substring("PIXEL_SETUP_WARNING|".Length);
                    actionButton.Text = "Done";
                    actionButton.Enabled = true;
                    progress.Value = 100;
                    AppendLog("Installation succeeded.");
                }
                else
                {
                    statusTitle.Text = "Installation could not be completed";
                    statusBody.Text = failureLine != null
                        ? failureLine.Substring("PIXEL_SETUP_FAILURE|".Length)
                        : "PowerShell exited with code " + exitCode + ". Review the setup log for details.";
                    actionButton.Text = "Retry";
                    actionButton.Enabled = true;
                    detectedMode = "Error";
                    AppendLog("Installation failed (exit " + exitCode + "): " + output);
                }
            }, forceApplicationShutdown);
        }

        private void EnsureCertificateTrusted()
        {
            X509Certificate2 certificate = new X509Certificate2(certificatePath);
            if (!String.Equals(certificate.Subject, "CN=PixelOverlayDeveloper", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(certificate.Thumbprint, Program.CertificateThumbprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The embedded publisher certificate failed its identity check.");

            using (X509Store store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine))
            {
                store.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection found = store.Certificates.Find(
                    X509FindType.FindByThumbprint, Program.CertificateThumbprint, false);
                if (found.Count > 0) return;
            }

            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = Application.ExecutablePath;
            start.Arguments = "--trust-certificate " + QuoteArgument(certificatePath);
            start.Verb = "runas";
            start.UseShellExecute = true;
            using (Process process = Process.Start(start))
            {
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("Windows could not add the publisher certificate to Local Computer > Trusted People (exit code " + process.ExitCode + ").");
            }
        }

        private void RunPowerShell(string action, Action<int, string> completed, bool forceApplicationShutdown = false)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                int exitCode = -1;
                StringBuilder output = new StringBuilder();
                Process process = new Process();
                try
                {
                    string args = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                        QuoteArgument(scriptPath) +
                        " -Action " + action +
                        " -PayloadDirectory " + QuoteArgument(payloadDirectory) +
                        " -TargetVersion " + QuoteArgument(Program.AppVersion) +
                        " -AppInstallerUri " + QuoteArgument(Program.AppInstallerUri) +
                        (forceApplicationShutdown ? " -ForceApplicationShutdown" : "");

                    process.StartInfo = new ProcessStartInfo();
                    process.StartInfo.FileName = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.System),
                        @"WindowsPowerShell\v1.0\powershell.exe");
                    process.StartInfo.Arguments = args;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
                    process.StartInfo.StandardErrorEncoding = Encoding.UTF8;

                    process.OutputDataReceived += delegate(object s, DataReceivedEventArgs data)
                    {
                        if (data.Data == null) return;
                        lock (output) output.AppendLine(data.Data);
                        AppendLog(data.Data);
                        if (data.Data.StartsWith("PIXEL_SETUP_PROGRESS|", StringComparison.Ordinal))
                            BeginInvoke((MethodInvoker)delegate { ApplyProgress(data.Data); });
                    };
                    process.ErrorDataReceived += delegate(object s, DataReceivedEventArgs data)
                    {
                        if (data.Data == null) return;
                        lock (output) output.AppendLine(data.Data);
                        AppendLog("PowerShell: " + data.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit();
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                }
                catch (Exception ex)
                {
                    lock (output) output.AppendLine(ex.ToString());
                    AppendLog("PowerShell runner error: " + ex);
                }
                finally
                {
                    process.Dispose();
                }

                string captured;
                lock (output) captured = output.ToString();
                if (!IsDisposed && IsHandleCreated)
                    BeginInvoke((MethodInvoker)delegate { completed(exitCode, captured); });
            });
        }

        private void ApplyProgress(string line)
        {
            string[] parts = line.Split(new char[] { '|' }, 3);
            if (parts.Length == 3)
            {
                int value;
                if (Int32.TryParse(parts[1], out value))
                {
                    progress.Style = ProgressBarStyle.Continuous;
                    progress.Value = Math.Max(0, Math.Min(100, value));
                }
                statusBody.Text = parts[2];
            }
        }

        private static string FindLine(string output, string prefix)
        {
            if (output == null) return null;
            string[] lines = output.Split(new string[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                if (lines[i].StartsWith(prefix, StringComparison.Ordinal))
                    return lines[i];
            }
            return null;
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        }

        private void SetupFormClosing(object sender, FormClosingEventArgs e)
        {
            if (busy && !closingAllowed)
            {
                e.Cancel = true;
                return;
            }
            try
            {
                if (Directory.Exists(payloadDirectory))
                    Directory.Delete(payloadDirectory, true);
            }
            catch { }
        }
    }
}
