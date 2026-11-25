using System;
using System.IO;
using System.Text;
using Windows.Storage;

namespace PixelOverlay.Services
{
    public static class AppDiagnostics
    {
        private static readonly object Sync = new object();

        public static void Log(string area, Exception exception)
        {
            Log(area, exception == null ? "No exception details were provided." : exception.ToString());
        }

        public static void Log(string area, string message)
        {
            try
            {
                var line = "[" + DateTimeOffset.Now.ToString("O") + "] " + (area ?? "Unknown") +
                    Environment.NewLine + (message ?? string.Empty) +
                    Environment.NewLine + Environment.NewLine;
                var path = Path.Combine(ApplicationData.Current.LocalFolder.Path, "pixel-overlay-diagnostics.log");
                lock (Sync)
                    File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never become a second failure path.
            }
        }
    }
}
