using System;
using System.IO;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Toolkit.Uwp.UI;
using PixelOverlay.Models;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI.Xaml.Media.Imaging;

namespace PixelOverlay.Services
{
    public sealed class ImageLibraryService
    {
        private const string FolderName = "Library";
        private const int MaxEdge = 8192;
        private const long MaxGifPixels = 8L * 1024L * 1024L;
        private const ulong MaxGifFileBytes = 32UL * 1024UL * 1024UL;
        private const double MaxDecodedPixels = 12d * 1024d * 1024d;
        private const uint DefaultDecodeEdge = 1920;
        private const long MaxExternalImageBytes = 32L * 1024L * 1024L;
        private static readonly HttpClient ExternalHttpClient = new HttpClient();
        private static readonly object ImageCacheSync = new object();
        private static Task ImageCacheInitialization;

        public async Task<ImageRecord> PickAndImportAsync()
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".webp");
            var source = await picker.PickSingleFileAsync();
            if (source == null) return null;
            return await ImportAsync(source);
        }

        public async Task<ImageRecord> ImportAsync(StorageFile source)
        {
            if (source == null) throw new InvalidOperationException("No file was provided.");
            var extension = Path.GetExtension(source.Name).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".gif" && extension != ".webp")
                throw new InvalidOperationException("Only PNG, JPG, JPEG, and GIF files are supported.");

            if (extension == ".gif")
            {
                var sourceProperties = await source.GetBasicPropertiesAsync();
                if (sourceProperties.Size > MaxGifFileBytes)
                    throw new InvalidDataException("GIF files larger than 32 MiB are not supported.");
            }

            var library = await ApplicationData.Current.LocalFolder.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
            var copy = await source.CopyAsync(library, Guid.NewGuid().ToString("N") + extension, NameCollisionOption.GenerateUniqueName);
            try { await ValidateAsync(copy); }
            catch
            {
                try { await copy.DeleteAsync(); } catch (Exception) { }
                throw;
            }
            return new ImageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                StoredName = copy.Name,
                DisplayName = source.DisplayName + extension,
                Extension = extension,
                AddedAt = DateTimeOffset.Now
            };
        }

        public async Task<ImageRecord> ImportFromUriAsync(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("Use a direct HTTPS image URL without embedded credentials.");

            using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
            using (var response = await ExternalHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(mediaType))
                    throw new InvalidDataException("The server did not provide an image Content-Type.");
                if (response.Content.Headers.ContentLength.HasValue &&
                    response.Content.Headers.ContentLength.Value > MaxExternalImageBytes)
                    throw new InvalidDataException("External images larger than 32 MiB are not supported.");

                var contentTypeAllowed = mediaType == "image/png" || mediaType == "image/jpeg" ||
                    mediaType == "image/jpg" || mediaType == "image/pjpeg" ||
                    mediaType == "image/gif" || mediaType == "image/webp";
                if (!contentTypeAllowed)
                    throw new InvalidDataException("The URL does not point to a supported image MIME type.");

                byte[] data;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var memory = new MemoryStream())
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        if (memory.Length + read > MaxExternalImageBytes)
                            throw new InvalidDataException("External images larger than 32 MiB are not supported.");
                        memory.Write(buffer, 0, read);
                    }
                    data = memory.ToArray();
                }

                var extension = DetectImageExtension(data);
                if (extension == null)
                    throw new InvalidDataException("The downloaded data does not contain a supported PNG, JPEG, WEBP or GIF signature.");

                bool typeMatches = (extension == ".png" && mediaType == "image/png") ||
                    ((extension == ".jpg" || extension == ".jpeg") &&
                        (mediaType == "image/jpeg" || mediaType == "image/jpg" || mediaType == "image/pjpeg")) ||
                    (extension == ".gif" && mediaType == "image/gif") ||
                    (extension == ".webp" && mediaType == "image/webp");
                if (!typeMatches)
                    throw new InvalidDataException("The HTTP Content-Type does not match the downloaded image signature.");

                var library = await ApplicationData.Current.LocalFolder.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
                var storedName = Guid.NewGuid().ToString("N") + extension;
                var localFile = await library.CreateFileAsync(storedName, CreationCollisionOption.FailIfExists);
                try
                {
                    await FileIO.WriteBytesAsync(localFile, data);
                    await ValidateAsync(localFile);
                }
                catch
                {
                    try { await localFile.DeleteAsync(); } catch (Exception) { }
                    throw;
                }

                string displayName;
                try { displayName = Path.GetFileName(Uri.UnescapeDataString(uri.LocalPath)); }
                catch (Exception) { displayName = null; }
                if (string.IsNullOrWhiteSpace(displayName)) displayName = "Imported image" + extension;

                return new ImageRecord
                {
                    Id = Guid.NewGuid().ToString("N"),
                    StoredName = storedName,
                    DisplayName = displayName,
                    Extension = extension,
                    AddedAt = DateTimeOffset.Now
                };
            }
        }

        private static string DetectImageExtension(byte[] data)
        {
            if (data == null || data.Length < 12) return null;

            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
                data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
                return ".png";

            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return ".jpg";

            if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' &&
                data[3] == (byte)'8' && (data[4] == (byte)'7' || data[4] == (byte)'9') &&
                data[5] == (byte)'a')
                return ".gif";

            if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F' &&
                data[8] == (byte)'W' && data[9] == (byte)'E' && data[10] == (byte)'B' && data[11] == (byte)'P')
                return ".webp";

            return null;
        }

        public async Task<StorageFile> GetFileAsync(ImageRecord item)
        {
            if (item == null) throw new InvalidOperationException("No image is selected.");
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists);
            return await folder.GetFileAsync(item.StoredName);
        }

        public async Task DeleteAsync(ImageRecord item)
        {
            if (item == null) return;
            try
            {
                var file = await GetFileAsync(item);
                var uri = new Uri(file.Path);
                await file.DeleteAsync();
                try { await ImageCache.Instance.RemoveAsync(new[] { uri }); }
                catch (Exception ex) { AppDiagnostics.Log("Remove cached static image", ex); }
            }
            catch (FileNotFoundException) { }
            SettingsStore.Current.RemoveImage(item.Id);
        }

        public Task<BitmapImage> LoadBitmapAsync(ImageRecord item)
        {
            return LoadBitmapAsync(item, DefaultDecodeEdge);
        }

        public async Task<BitmapImage> LoadBitmapAsync(ImageRecord item, uint maxDecodeEdge)
        {
            if (item == null) return null;
            var file = await GetFileAsync(item);
            try
            {
                await EnsureImageCacheInitializedAsync();
                var cacheUri = new Uri(file.Path);
                var parameters = new List<KeyValuePair<string, object>>
                {
                    new KeyValuePair<string, object>("DecodePixelWidth", (int)Math.Min(1920u, Math.Max(1u, maxDecodeEdge)))
                };
                var cached = ImageCache.Instance.GetFromMemoryCache(cacheUri);
                if (cached != null) return cached;
                return await ImageCache.Instance.GetFromCacheAsync(cacheUri, false, CancellationToken.None, parameters);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Static image cache fallback", ex);
            }

            using (var stream = await file.OpenReadAsync())
            {
                var decoder = await BitmapDecoder.CreateAsync(stream);
                var sourceWidth = decoder.PixelWidth;
                var sourceHeight = decoder.PixelHeight;
                if (sourceWidth == 0 || sourceHeight == 0)
                    throw new InvalidDataException("The image has no pixels.");

                // Decode close to the widget's useful resolution instead of allocating the full
                // source bitmap. A 12-megapixel ceiling also protects against extreme aspect ratios.
                var scale = Math.Min(1d, (double)Math.Max(1u, maxDecodeEdge) / Math.Max(sourceWidth, sourceHeight));
                var sourcePixels = (double)sourceWidth * sourceHeight;
                if (sourcePixels > MaxDecodedPixels)
                    scale = Math.Min(scale, Math.Sqrt(MaxDecodedPixels / sourcePixels));

                var bitmap = new BitmapImage { AutoPlay = false };
                if (sourceWidth >= sourceHeight)
                    bitmap.DecodePixelWidth = (int)Math.Max(1d, Math.Floor(sourceWidth * scale));
                else
                    bitmap.DecodePixelHeight = (int)Math.Max(1d, Math.Floor(sourceHeight * scale));

                stream.Seek(0);
                await bitmap.SetSourceAsync(stream);
                return bitmap;
            }
        }

        private static Task EnsureImageCacheInitializedAsync()
        {
            lock (ImageCacheSync)
            {
                if (ImageCacheInitialization == null)
                {
                    ImageCache.Instance.MaxMemoryCacheCount = 16;
                    ImageCache.Instance.CacheDuration = TimeSpan.FromDays(14);
                    ImageCacheInitialization = ImageCache.Instance.InitializeAsync(
                        ApplicationData.Current.LocalFolder, "StaticImageCache");
                }
                return ImageCacheInitialization;
            }
        }

        private static async Task ValidateAsync(StorageFile file)
        {
            try
            {
                using (var stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    if (decoder.PixelWidth == 0 || decoder.PixelHeight == 0)
                        throw new InvalidDataException("The image has no pixels.");
                    if (decoder.PixelWidth > MaxEdge || decoder.PixelHeight > MaxEdge)
                        throw new InvalidDataException("Images larger than 8192 pixels on a side are not supported.");
                    if (string.Equals(file.FileType, ".gif", StringComparison.OrdinalIgnoreCase))
                    {
                        if (decoder.FrameCount == 0)
                            throw new InvalidDataException("The GIF has no frames.");
                        if ((long)decoder.PixelWidth * decoder.PixelHeight > MaxGifPixels)
                            throw new InvalidDataException("GIFs larger than 8 megapixels are not supported. Resize the GIF and import it again.");
                    }
                }
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex)
            {
                throw new InvalidDataException("The file is damaged or is not a supported image.", ex);
            }
        }
    }
}
