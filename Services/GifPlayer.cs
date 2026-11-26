using System;
using System.IO;
using SystemBuffer = System.Buffer;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using PixelOverlay.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace PixelOverlay.Services
{
    // Decodes one frame at a time. The decoder uses an in-memory stream so deleting an
    // imported GIF is never blocked by an open handle to the user's library file.
    public sealed class GifPlayer : IDisposable
    {
        private const long MaxCanvasPixels = 8L * 1024L * 1024L;
        private const long MaxFramePixels = 8L * 1024L * 1024L;
        private const ulong MaxGifFileBytes = 32UL * 1024UL * 1024UL;

        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private BitmapDecoder _decoder;
        private IRandomAccessStream _stream;
        private WriteableBitmap _bitmap;
        private byte[] _canvas;
        private byte[] _snapshot;
        private int _width;
        private int _height;
        private int _frameIndex;
        private int _lastDelayMs = 100;
        private int _previousDisposal;
        private int _previousLeft;
        private int _previousTop;
        private int _previousWidth;
        private int _previousHeight;
        private bool _loading;
        private bool _disposed;
        private bool _isPlaying = true;
        private bool _isVisible = true;
        private double _playbackSpeed = 1;
        private int _generation;

        public ImageSource CurrentFrame => _bitmap;
        public event EventHandler FrameChanged;
        public bool IsReady => _decoder != null;
        public event EventHandler PlaybackFailed;

        public GifPlayer() => _timer.Tick += async (s, e) => await AdvanceAsync();

        public async Task OpenAsync(StorageFile file, OverlayState playbackState)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size == 0 || properties.Size > MaxGifFileBytes)
                throw new InvalidDataException("GIF files must be between 1 byte and 32 MiB.");

            var buffer = await FileIO.ReadBufferAsync(file);
            if (buffer.Length == 0 || buffer.Length > MaxGifFileBytes)
                throw new InvalidDataException("GIF files must be between 1 byte and 32 MiB.");

            var memoryStream = new InMemoryRandomAccessStream();
            try
            {
                using (var writer = new DataWriter(memoryStream))
                {
                    writer.WriteBuffer(buffer);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                memoryStream.Seek(0);
                await OpenAsync(memoryStream, playbackState);
                memoryStream = null;
            }
            finally
            {
                memoryStream?.Dispose();
            }
        }

        // Takes ownership of the supplied stream. AssetCacheService provides one clone
        // per window, while the shared master stream remains reference-counted by its lease.
        public async Task OpenAsync(IRandomAccessStream source, OverlayState playbackState)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (_disposed) throw new ObjectDisposedException(nameof(GifPlayer));
            var generation = ++_generation;
            Stop();
            DisposeDecoder();
            try
            {
                if (source.Size == 0 || source.Size > MaxGifFileBytes)
                    throw new InvalidDataException("GIF files must be between 1 byte and 32 MiB.");

                source.Seek(0);
                if (!IsCurrent(generation)) return;
                var decoder = await BitmapDecoder.CreateAsync(source);
                if (!IsCurrent(generation)) return;
                if (decoder.FrameCount == 0) throw new InvalidDataException("The GIF has no frames.");

                var width = checked((int)decoder.PixelWidth);
                var height = checked((int)decoder.PixelHeight);
                if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                    throw new InvalidDataException("GIF dimensions must be between 1 and 8192 pixels.");

                var pixelCount = (long)width * height;
                if (pixelCount > MaxCanvasPixels)
                    throw new InvalidDataException("GIFs larger than 8 megapixels are not supported.");

                _stream = source;
                source = null;
                _decoder = decoder;
                _width = width;
                _height = height;
                _canvas = new byte[checked((int)(pixelCount * 4L))];
                _frameIndex = 0;
                _previousDisposal = 0;

                await RenderCurrentAsync(generation);
                if (IsCurrent(generation)) UpdateSchedule(playbackState);
            }
            catch
            {
                if (generation == _generation)
                {
                    Stop();
                    DisposeDecoder();
                    _disposed = true;
                }
                throw;
            }
            finally
            {
                source?.Dispose();
            }
        }

        public void UpdateSchedule(OverlayState playbackState)
        {
            _isPlaying = playbackState != null && playbackState.GifPlaying;
            if (playbackState != null)
                _playbackSpeed = Math.Max(0.25, Math.Min(4, playbackState.GifSpeed));
            ScheduleNextFrame();
        }

        private void ScheduleNextFrame()
        {
            _timer.Stop();
            // A settings change can arrive while a frame is being decoded.
            // Keep the timer stopped until the in-flight frame finishes.
            if (_decoder == null || _disposed || _loading || !_isPlaying || !_isVisible || _decoder.FrameCount < 2) return;
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, _lastDelayMs / _playbackSpeed));
            _timer.Start();
        }

        private async Task AdvanceAsync()
        {
            var generation = _generation;
            if (_loading || _decoder == null || !IsCurrent(generation)) return;
            _timer.Stop();
            try
            {
                int nextFrame = (_frameIndex + 1) % (int)_decoder.FrameCount;
                bool startNewCycle = nextFrame == 0;
                _frameIndex = nextFrame;
                await RenderCurrentAsync(generation, startNewCycle);
                if (IsCurrent(generation)) ScheduleNextFrame();
            }
            catch (Exception ex)
            {
                if (!IsCurrent(generation)) return;
                Stop();
                AppDiagnostics.Log("GIF playback", ex);
                try { PlaybackFailed?.Invoke(this, EventArgs.Empty); }
                catch (Exception handlerException) { AppDiagnostics.Log("GIF playback failure handler", handlerException); }
            }
        }

        private async Task RenderCurrentAsync(int generation, bool startNewCycle = false)
        {
            var decoder = _decoder;
            var canvas = _canvas;
            if (decoder == null || canvas == null || !IsCurrent(generation)) return;
            _loading = true;
            try
            {
                var frame = await decoder.GetFrameAsync((uint)_frameIndex);
                if (!IsCurrent(generation) || !ReferenceEquals(decoder, _decoder) || !ReferenceEquals(canvas, _canvas)) return;

                var properties = frame.BitmapProperties;
                int left = await TryReadAsync(properties, "/imgdesc/Left");
                int top = await TryReadAsync(properties, "/imgdesc/Top");
                int disposal = await TryReadAsync(properties, "/grctlext/Disposal");
                int delayCs = await TryReadAsync(properties, "/grctlext/Delay");
                int frameWidth = checked((int)frame.PixelWidth);
                int frameHeight = checked((int)frame.PixelHeight);
                long framePixels = (long)frameWidth * frameHeight;

                if (frameWidth <= 0 || frameHeight <= 0 || framePixels > MaxFramePixels)
                    throw new InvalidDataException("The GIF contains a frame larger than 8 megapixels.");
                if (!IsCurrent(generation) || !ReferenceEquals(canvas, _canvas)) return;

                var pixels = await frame.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                byte[] source = pixels.DetachPixelData();
                if (!IsCurrent(generation) || !ReferenceEquals(canvas, _canvas)) return;
                if ((long)source.Length < framePixels * 4L)
                    throw new InvalidDataException("The GIF decoder returned an incomplete frame buffer.");

                int previousDisposal = startNewCycle ? 0 : _previousDisposal;
                int previousLeft = _previousLeft;
                int previousTop = _previousTop;
                int previousWidth = _previousWidth;
                int previousHeight = _previousHeight;
                byte[] nextSnapshot = startNewCycle ? null : _snapshot;
                int canvasWidth = _width;
                int canvasHeight = _height;

                // Pixel blending and disposal snapshots are CPU work. Do not block the XAML dispatcher.
                await Task.Run(() =>
                {
                    if (startNewCycle)
                        Array.Clear(canvas, 0, canvas.Length);
                    else
                        ApplyPreviousDisposal(canvas, nextSnapshot, previousDisposal,
                            previousLeft, previousTop, previousWidth, previousHeight, canvasWidth, canvasHeight);
                    if (disposal == 3) nextSnapshot = (byte[])canvas.Clone();
                    Blit(canvas, source, frameWidth, frameHeight, left, top, canvasWidth, canvasHeight);
                });

                if (!IsCurrent(generation) || !ReferenceEquals(canvas, _canvas)) return;
                _snapshot = nextSnapshot;
                _previousDisposal = disposal;
                _previousLeft = left;
                _previousTop = top;
                _previousWidth = frameWidth;
                _previousHeight = frameHeight;
                _lastDelayMs = DelayToMilliseconds(delayCs);
                PublishCanvas();
                FrameChanged?.Invoke(this, EventArgs.Empty);
            }
            finally { _loading = false; }
        }

        private void PublishCanvas()
        {
            if (_bitmap == null || _bitmap.PixelWidth != _width || _bitmap.PixelHeight != _height)
                _bitmap = new WriteableBitmap(_width, _height);
            using (var output = _bitmap.PixelBuffer.AsStream())
            {
                output.Position = 0;
                output.Write(_canvas, 0, _canvas.Length);
            }
            _bitmap.Invalidate();
        }

        private static void ApplyPreviousDisposal(byte[] canvas, byte[] snapshot, int disposal,
            int left, int top, int width, int height, int canvasWidth, int canvasHeight)
        {
            if (disposal == 2)
                Clear(canvas, left, top, width, height, canvasWidth, canvasHeight);
            else if (disposal == 3 && snapshot != null)
                SystemBuffer.BlockCopy(snapshot, 0, canvas, 0, canvas.Length);
        }

        private static void Clear(byte[] canvas, int left, int top, int width, int height, int canvasWidth, int canvasHeight)
        {
            for (int y = 0; y < height; y++)
            {
                int destinationY = top + y;
                if (destinationY < 0 || destinationY >= canvasHeight) continue;
                int rowStart = Math.Max(0, left);
                int rowEnd = (int)Math.Min(canvasWidth, (long)left + width);
                if (rowEnd <= rowStart) continue;
                Array.Clear(canvas, (destinationY * canvasWidth + rowStart) * 4, (rowEnd - rowStart) * 4);
            }
        }

        private static void Blit(byte[] canvas, byte[] source, int frameWidth, int frameHeight,
            int left, int top, int canvasWidth, int canvasHeight)
        {
            for (int y = 0; y < frameHeight; y++)
            {
                int destinationY = top + y;
                if (destinationY < 0 || destinationY >= canvasHeight) continue;
                for (int x = 0; x < frameWidth; x++)
                {
                    int destinationX = left + x;
                    if (destinationX < 0 || destinationX >= canvasWidth) continue;
                    int sourceIndex = (y * frameWidth + x) * 4;
                    byte alpha = source[sourceIndex + 3];
                    if (alpha == 0) continue;
                    int destinationIndex = (destinationY * canvasWidth + destinationX) * 4;
                    if (alpha == 255)
                    {
                        SystemBuffer.BlockCopy(source, sourceIndex, canvas, destinationIndex, 4);
                        continue;
                    }
                    int inverse = 255 - alpha;
                    canvas[destinationIndex] = (byte)(source[sourceIndex] + (canvas[destinationIndex] * inverse) / 255);
                    canvas[destinationIndex + 1] = (byte)(source[sourceIndex + 1] + (canvas[destinationIndex + 1] * inverse) / 255);
                    canvas[destinationIndex + 2] = (byte)(source[sourceIndex + 2] + (canvas[destinationIndex + 2] * inverse) / 255);
                    canvas[destinationIndex + 3] = (byte)(alpha + (canvas[destinationIndex + 3] * inverse) / 255);
                }
            }
        }

        private static async Task<int> TryReadAsync(BitmapPropertiesView properties, string key)
        {
            try
            {
                var set = await properties.GetPropertiesAsync(new[] { key });
                if (set != null && set.TryGetValue(key, out var value) && value?.Value != null)
                    return Convert.ToInt32(value.Value);
            }
            catch (Exception) { }
            return 0;
        }

        public static int DelayToMilliseconds(int centiseconds)
        {
            if (centiseconds <= 1) return 100;
            return Math.Max(10, centiseconds * 10);
        }

        public void SetVisible(bool visible)
        {
            if (_disposed || _isVisible == visible) return;
            _isVisible = visible;
            if (!_isVisible)
                _timer.Stop();
            else
                ScheduleNextFrame();
        }

        public void Stop() => _timer.Stop();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _generation++;
            Stop();
            DisposeDecoder();
        }

        private bool IsCurrent(int generation) => !_disposed && generation == _generation;

        private void DisposeDecoder()
        {
            _decoder = null;
            _stream?.Dispose();
            _stream = null;
            _bitmap = null;
            _canvas = null;
            _snapshot = null;
            _frameIndex = 0;
            _previousDisposal = 0;
        }
    }
}
