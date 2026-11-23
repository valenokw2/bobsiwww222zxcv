using System;
using System.IO;
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
    // One decoded frame and one composited canvas. Frames are not retained between cycles.
    public sealed class GifPlayer : IDisposable
    {
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

        public ImageSource CurrentFrame => _bitmap;
        public event EventHandler FrameChanged;
        public bool IsReady => _decoder != null;

        public GifPlayer() => _timer.Tick += async (s, e) => await AdvanceAsync();

        public async Task OpenAsync(StorageFile file)
        {
            Stop();
            DisposeDecoder();
            _disposed = false;
            _stream = await file.OpenAsync(FileAccessMode.Read);
            _decoder = await BitmapDecoder.CreateAsync(_stream);
            if (_decoder.FrameCount == 0) throw new InvalidDataException("The GIF has no frames.");
            _width = (int)_decoder.PixelWidth;
            _height = (int)_decoder.PixelHeight;
            if (_width <= 0 || _height <= 0 || _width > 8192 || _height > 8192)
                throw new InvalidDataException("GIF dimensions must be between 1 and 8192 pixels.");
            _canvas = new byte[(long)_width * _height * 4];
            _frameIndex = 0;
            _previousDisposal = 0;
            await RenderCurrentAsync();
            UpdateSchedule();
        }

        public void UpdateSchedule()
        {
            _timer.Stop();
            if (_decoder == null || _disposed || !SettingsStore.Current.State.GifPlaying || _decoder.FrameCount < 2) return;
            var speed = Math.Max(0.25, SettingsStore.Current.State.GifSpeed);
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, _lastDelayMs / speed));
            _timer.Start();
        }

        private async Task AdvanceAsync()
        {
            if (_loading || _decoder == null || _disposed) return;
            _timer.Stop();
            _frameIndex = (_frameIndex + 1) % (int)_decoder.FrameCount;
            await RenderCurrentAsync();
            if (!_disposed) UpdateSchedule();
        }

        private async Task RenderCurrentAsync()
        {
            if (_decoder == null || _disposed) return;
            _loading = true;
            try
            {
                ApplyPreviousDisposal();
                var frame = await _decoder.GetFrameAsync((uint)_frameIndex);
                var properties = frame.BitmapProperties;
                int left = await TryReadAsync(properties, "/imgdesc/Left");
                int top = await TryReadAsync(properties, "/imgdesc/Top");
                int disposal = await TryReadAsync(properties, "/grctlext/Disposal");
                int delayCs = await TryReadAsync(properties, "/grctlext/Delay");
                int frameWidth = (int)frame.PixelWidth;
                int frameHeight = (int)frame.PixelHeight;

                // Disposal 3 restores the canvas from before this frame was drawn.
                if (disposal == 3) _snapshot = (byte[])_canvas.Clone();

                var pixels = await frame.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                Blit(pixels.DetachPixelData(), frameWidth, frameHeight, left, top);
                PublishCanvas();

                _previousDisposal = disposal;
                _previousLeft = left;
                _previousTop = top;
                _previousWidth = frameWidth;
                _previousHeight = frameHeight;
                _lastDelayMs = DelayToMilliseconds(delayCs);
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

        private void ApplyPreviousDisposal()
        {
            if (_previousDisposal == 2) Clear(_previousLeft, _previousTop, _previousWidth, _previousHeight);
            else if (_previousDisposal == 3 && _snapshot != null) System.System.Buffer.BlockCopy(_snapshot, 0, _canvas, 0, _canvas.Length);
        }

        private void Clear(int left, int top, int width, int height)
        {
            for (int y = 0; y < height; y++)
            {
                int destinationY = top + y;
                if (destinationY < 0 || destinationY >= _height) continue;
                int rowStart = Math.Max(0, left);
                int rowEnd = Math.Min(_width, left + width);
                if (rowEnd <= rowStart) continue;
                Array.Clear(_canvas, (destinationY * _width + rowStart) * 4, (rowEnd - rowStart) * 4);
            }
        }

        private void Blit(byte[] source, int frameWidth, int frameHeight, int left, int top)
        {
            if (source == null || frameWidth <= 0 || frameHeight <= 0) return;
            if (source.Length < frameWidth * frameHeight * 4) return;
            for (int y = 0; y < frameHeight; y++)
            {
                int destinationY = top + y;
                if (destinationY < 0 || destinationY >= _height) continue;
                for (int x = 0; x < frameWidth; x++)
                {
                    int destinationX = left + x;
                    if (destinationX < 0 || destinationX >= _width) continue;
                    int sourceIndex = (y * frameWidth + x) * 4;
                    byte alpha = source[sourceIndex + 3];
                    if (alpha == 0) continue;
                    int destinationIndex = (destinationY * _width + destinationX) * 4;
                    if (alpha == 255)
                    {
                        System.Buffer.BlockCopy(source, sourceIndex, _canvas, destinationIndex, 4);
                        continue;
                    }
                    int inverse = 255 - alpha;
                    _canvas[destinationIndex] = (byte)(source[sourceIndex] + (_canvas[destinationIndex] * inverse) / 255);
                    _canvas[destinationIndex + 1] = (byte)(source[sourceIndex + 1] + (_canvas[destinationIndex + 1] * inverse) / 255);
                    _canvas[destinationIndex + 2] = (byte)(source[sourceIndex + 2] + (_canvas[destinationIndex + 2] * inverse) / 255);
                    _canvas[destinationIndex + 3] = (byte)(alpha + (_canvas[destinationIndex + 3] * inverse) / 255);
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

        // GIF delay is stored in centiseconds. Players treat 0 and 1 as 100 ms.
        public static int DelayToMilliseconds(int centiseconds)
        {
            if (centiseconds <= 1) return 100;
            return centiseconds * 10;
        }

        public void Stop() => _timer.Stop();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            DisposeDecoder();
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
