using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace PixelOverlay.Diagnostics
{
    /// <summary>
    /// Writes runtime diagnostics to the app's LocalState folder.
    /// The logger is deliberately fail-safe: diagnostics must never crash the widget.
    /// </summary>
    internal static class WidgetDiagnostics
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private const string FileName = "pixeloverlay-diagnostics.log";

        public static async Task LogAsync(string message, Exception exception = null)
        {
            var line = DateTime.UtcNow.ToString("o") + " [T" +
                       Environment.CurrentManagedThreadId + "] " + message;
            if (exception != null) line += Environment.NewLine + exception;

            Debug.WriteLine("[PixelOverlay] " + line);
            try
            {
                await Gate.WaitAsync();
                try
                {
                    var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                        FileName, CreationCollisionOption.OpenIfExists);
                    await FileIO.AppendTextAsync(file, line + Environment.NewLine);
                }
                finally { Gate.Release(); }
            }
            catch (Exception logException)
            {
                Debug.WriteLine("[PixelOverlay] Diagnostic write failed: " + logException);
            }
        }
    }
}
