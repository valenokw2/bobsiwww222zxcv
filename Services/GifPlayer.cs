using System;
using System.IO;
using SystemBuffer = System.Buffer;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace PixelOverlay.Services
{
    // Keeps only a bounded compressed copy of the GIF and one composited frame canvas.
    // The source file is released immediately, so the library item can be deleted while playing.
    public sealed class GifPlayer : IDisposable
    {
        private const long MaxCanvasPixels = 8L * 1024L * 1024L;
        private const ulong MaxSourceBytes = 64UL * 1024UL * 1024UL;
        private const uint MaxFrameCount = 10000;
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
        private int _generation;
        private double _speed = 1;
        private bool _playing = true;

        public ImageSource CurrentFrame => _bitmap;
        public event EventHandler FrameChanged;
        public event EventHandler PlaybackFailed;
        public bool IsReady => _decoder != null;

        public GifPlayer() => _timer.Tick += async (s, e) => await AdvanceAsync();

        public async Task OpenAsync(StorageFile file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            var generation = ++_generation;
            Stop();
            DisposeDecoder();
            _disposed = false;

            IRandomAccessStream sourceStream = null;
            InMemoryRandomAccessStream memoryStream = null;
            try
            {
                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size == 0 || properties.Size > MaxSourceBytes)
                    throw new InvalidDataException("GIF files must be between 1 byte and 64 MB.");

                sourceStream = await file.OpenReadAsync();
                memoryStream = new InMemoryRandomAccessStream();
                await RandomAccessStream.CopyAsync(sourceStream, memoryStream);
                sourceStream.Dispose();
                sourceStream = null;
                memoryStream.Seek(0);

                if (!IsCurrent(generation))
                {
                    memoryStream.Dispose();
                    return;
                }

                var decoder = await BitmapDecoder.CreateAsync(memoryStream);
                if (!IsCurrent(generation))
                {
                    memoryStream.Dispose();
                    return;
                }
                if (decoder.FrameCount == 0 || decoder.FrameCount > MaxFrameCount)
                    throw new InvalidDataException("GIF frame count must be between 1 and 10000.");

                var width = (int)decoder.PixelWidth;
                var height = (int)decoder.PixelHeight;
                if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
                    throw new InvalidDataException("GIF dimensions must be between 1 and 8192 pixels.");

                var pixelCount = (long)width * height;
                if (pixelCount > MaxCanvasPixels)
                    throw new InvalidDataException("GIFs larger than 8 megapixels are not supported.");

                _stream = memoryStream;
                memoryStream = null;
                _decoder = decoder;
                _width = width;
                _height = height;
                _canvas = new byte[checked((int)(pixelCount * 4L))];
                _frameIndex = 0;
                _previousDisposal = 0;
                _snapshot = null;

                await RenderCurrentAsync(generation);
                if (IsCurrent(generation)) UpdateSchedule(_speed, _playing);
            }
            catch
            {
                sourceStream?.Dispose();
                memoryStream?.Dispose();
                if (generation == _generation)
                {
                    Stop();
                    DisposeDecoder();
                    _disposed = true;
                }
                throw;
            }
        }

        public void UpdateSchedule(double speed, bool playing)
        {
            _timer.Stop();
            if (double.IsNaN(speed) || double.IsInfinity(speed)) speed = 1;
            _speed = Math.Max(0.25, Math.Min(4, speed));
            _playing = playing;
            if (_decoder == null || _disposed || !_playing || _decoder.FrameCount < 2) return;
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, _lastDelayMs / _speed));
            _timer.Start();
        }

        private async Task AdvanceAsync()
        {
            var generation = _generation;
            if (_loading || _decoder == null || !IsCurrent(generation)) return;
            _timer.Stop();
            try
            {
                _frameIndex = (_frameIndex + 1) % (int)_decoder.FrameCount;
                await RenderCurrentAsync(generation);
                if (IsCurrent(generation)) UpdateSchedule(_speed, _playing);
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

        private async Task RenderCurrentAsync(int generation)
        {
            var decoder = _decoder;
            if (decoder == null || !IsCurrent(generation)) return;
            _loading = true;
            try
            {
                var frame = await decoder.GetFrameAsync((uint)_frameIndex);
                if (!IsCurrent(generation) || !ReferenceEquals(decoder, _decoder) || _canvas == null) return;

                var properties = frame.BitmapProperties;
                int left = await TryReadAsync(properties, "/imgdesc/Left");
                int top = await TryReadAsync(properties, "/imgdesc/Top");
                int disposal = await TryReadAsync(properties, "/grctlext/Disposal");
                int delayCs = await TryReadAsync(properties, "/grctlext/Delay");
                int frameWidth = checked((int)frame.PixelWidth);
                int frameHeight = checked((int)frame.PixelHeight);
                if (frameWidth <= 0 || frameHeight <= 0 || frameWidth > 8192 || frameHeight > 8192 ||
                    (long)frameWidth * frameHeight > MaxCanvasPixels)
                    throw new InvalidDataException("A GIF frame exceeds the 8-megapixel decode limit.");

                var pixels = await frame.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                if (!IsCurrent(generation) || _canvas == null) return;

                var canvas = _canvas;
                var oldSnapshot = _snapshot;
                int previousDisposal = _previousDisposal;
                int previousLeft = _previousLeft;
                int previousTop = _previousTop;
                int previousWidth = _previousWidth;
                int previousHeight = _previousHeight;
                bool firstFrameOfLoop = _frameIndex == 0;
                byte[] framePixels = pixels.DetachPixelData();

                // Disposal, snapshot cloning, and pixel blending are CPU-heavy. Do them away
                // from the CoreWindow dispatcher; only publish the final pixels on the UI thread.
                byte[] restoreSnapshot = await Task.Run(() => PrepareAndBlit(
                    canvas, _width, _height,
                    previousDisposal, previousLeft, previousTop, previousWidth, previousHeight,
                    oldSnapshot, firstFrameOfLoop,
                    disposal, framePixels, frameWidth, frameHeight, left, top));

                if (!IsCurrent(generation) || !ReferenceEquals(canvas, _canvas)) return;
                _snapshot = restoreSnapshot;
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

        private static byte[] PrepareAndBlit(
            byte[] canvas, int canvasWidth, int canvasHeight,
            int previousDisposal, int previousLeft, int previousTop, int previousWidth, int previousHeight,
            byte[] previousSnapshot, bool firstFrameOfLoop,
            int currentDisposal, byte[] pixels, int frameWidth, int frameHeight, int left, int top)
        {
            if (firstFrameOfLoop)
                Array.Clear(canvas, 0, canvas.Length);
            else if (previousDisposal == 2)
                Clear(canvas, canvasWidth, canvasHeight, previousLeft, previousTop, previousWidth, previousHeight);
            else if (previousDisposal == 3 && previousSnapshot != null)
                SystemBuffer.BlockCopy(previousSnapshot, 0, canvas, 0, canvas.Length);

            byte[] restoreSnapshot = currentDisposal == 3 ? (byte[])canvas.Clone() : null;
            Blit(canvas, canvasWidth, canvasHeight, pixels, frameWidth, frameHeight, left, top);
            return restoreSnapshot;
        }

        private static void Clear(byte[] canvas, int canvasWidth, int canvasHeight, int left, int top, int width, int height)
        {
            for (int y = 0; y < height; y++)
            {
                int destinationY = top + y;
                if (destinationY < 0 || destinationY >= canvasHeight) continue;
                int rowStart = Math.Max(0, left);
                int rowEnd = Math.Min(canvasWidth, left + width);
                if (rowEnd <= rowStart) continue;
                Array.Clear(canvas, (destinationY * canvasWidth + rowStart) * 4, (rowEnd - rowStart) * 4);
            }
        }

        private static void Blit(byte[] canvas, int canvasWidth, int canvasHeight, byte[] source,
            int frameWidth, int frameHeight, int left, int top)
        {
            if (source == null || frameWidth <= 0 || frameHeight <= 0) return;
            if ((long)source.Length < (long)frameWidth * frameHeight * 4L)
                throw new InvalidDataException("Decoded GIF frame data is shorter than its declared dimensions.");

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
            return Math.Max(10, Math.Min(60000, centiseconds * 10));
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

        private bool IsCurrent(int generation)
        {
            return !_disposed && generation == _generation;
        }

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