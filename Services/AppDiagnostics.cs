using System;
using System.IO;
using System.Text;
using Windows.Storage;

namespace PixelOverlay.Services
{
    public static class AppDiagnostics
    {
        private static readonly object Sync = new object();
        private const long MaxLogBytes = 1024 * 1024;
        private const int BackupCount = 3;
        private const int MaxMessageCharacters = 64 * 1024;

        public static void Log(string area, Exception exception)
        {
            Log(area, exception == null ? "No exception details were provided." : exception.ToString());
        }

        public static void Log(string area, string message)
        {
            try
            {
                var safeMessage = message ?? string.Empty;
                if (safeMessage.Length > MaxMessageCharacters)
                    safeMessage = safeMessage.Substring(0, MaxMessageCharacters) + Environment.NewLine + "[message truncated]";

                var line = "[" + DateTimeOffset.Now.ToString("O") + "] " + (area ?? "Unknown") +
                    Environment.NewLine + safeMessage + Environment.NewLine + Environment.NewLine;
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "pixel-overlay-diagnostics.log");
                var bytes = new UTF8Encoding(false).GetBytes(line);

                lock (Sync)
                {
                    RotateIfNeeded(path, bytes.Length);
                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                        stream.Write(bytes, 0, bytes.Length);
                }
            }
            catch
            {
                // Diagnostics must never become a second failure path.
            }
        }

        private static void RotateIfNeeded(string path, int incomingBytes)
        {
            if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= MaxLogBytes)
                return;

            var oldest = path + "." + BackupCount;
            if (File.Exists(oldest)) File.Delete(oldest);

            for (var index = BackupCount - 1; index >= 1; index--)
            {
                var source = path + "." + index;
                var destination = path + "." + (index + 1);
                if (File.Exists(source))
                {
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(source, destination);
                }
            }

            var firstBackup = path + ".1";
            if (File.Exists(firstBackup)) File.Delete(firstBackup);
            File.Move(path, firstBackup);
        }
    }
}
