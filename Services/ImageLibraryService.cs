using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
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
        private const uint DefaultDecodeEdge = 4096;
        private const long MaxRemoteImageBytes = 32L * 1024L * 1024L;
        private const int MaxRedirects = 5;

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
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" &&
                extension != ".gif" && extension != ".webp")
                throw new InvalidOperationException("Only PNG, JPG, JPEG, GIF, and WEBP files are supported.");

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

        public async Task<ImageRecord> ImportFromUrlAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.Length > 4096)
                throw new InvalidOperationException("Enter a valid HTTPS image URL.");

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var currentUri))
                throw new InvalidOperationException("The image URL is not valid.");
            ValidateRemoteUri(currentUri);

            byte[] payload = null;
            string mediaType = null;
            Uri finalUri = currentUri;

            using (var handler = new HttpClientHandler { AllowAutoRedirect = false })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) })
            {
                for (int redirect = 0; redirect <= MaxRedirects; redirect++)
                {
                    ValidateRemoteUri(currentUri);
                    HttpResponseMessage response;
                    using (var request = new HttpRequestMessage(HttpMethod.Get, currentUri))
                        response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                    using (response)
                    {
                        int statusCode = (int)response.StatusCode;
                        if (statusCode >= 300 && statusCode < 400)
                        {
                            if (redirect == MaxRedirects || response.Headers.Location == null)
                                throw new InvalidDataException("The image server redirected too many times or returned an invalid redirect.");

                            currentUri = response.Headers.Location.IsAbsoluteUri
                                ? response.Headers.Location
                                : new Uri(currentUri, response.Headers.Location);
                            continue;
                        }

                        response.EnsureSuccessStatusCode();
                        mediaType = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
                        if (!string.IsNullOrWhiteSpace(mediaType) &&
                            mediaType != "application/octet-stream" &&
                            mediaType != "binary/octet-stream" &&
                            !mediaType.StartsWith("image/", StringComparison.Ordinal))
                            throw new InvalidDataException("The URL did not return an image content type.");

                        var contentLength = response.Content.Headers.ContentLength;
                        if (contentLength.HasValue && contentLength.Value > MaxRemoteImageBytes)
                            throw new InvalidDataException("Remote images larger than 32 MiB are not supported.");

                        using (var input = await response.Content.ReadAsStreamAsync())
                        using (var output = new MemoryStream())
                        {
                            var buffer = new byte[81920];
                            int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                if (output.Length + read > MaxRemoteImageBytes)
                                    throw new InvalidDataException("Remote images larger than 32 MiB are not supported.");
                                output.Write(buffer, 0, read);
                            }
                            payload = output.ToArray();
                        }

                        finalUri = currentUri;
                        break;
                    }
                }
            }

            var extension = DetectImageExtension(payload);
            ValidateImageContentType(mediaType, extension);

            var library = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                FolderName, CreationCollisionOption.OpenIfExists);
            var copy = await library.CreateFileAsync(
                Guid.NewGuid().ToString("N") + extension,
                CreationCollisionOption.GenerateUniqueName);
            try
            {
                await FileIO.WriteBytesAsync(copy, payload);
                await ValidateAsync(copy);
            }
            catch
            {
                try { await copy.DeleteAsync(); } catch (Exception) { }
                throw;
            }

            var displayName = Path.GetFileName(finalUri.LocalPath);
            if (string.IsNullOrWhiteSpace(displayName) ||
                !string.Equals(Path.GetExtension(displayName), extension, StringComparison.OrdinalIgnoreCase))
                displayName = "Downloaded image" + extension;

            return new ImageRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                StoredName = copy.Name,
                DisplayName = displayName,
                Extension = extension,
                AddedAt = DateTimeOffset.Now
            };
        }

        private static void ValidateRemoteUri(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
                throw new InvalidOperationException("Only HTTPS image URLs without embedded credentials are supported.");
        }

        private static string DetectImageExtension(byte[] data)
        {
            if (data == null || data.Length < 12)
                throw new InvalidDataException("The downloaded file is empty or too small to be an image.");

            if (data.Length >= 8 &&
                data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47 &&
                data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
                return ".png";

            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
                return ".jpg";

            if (data[0] == (byte)'G' && data[1] == (byte)'I' && data[2] == (byte)'F' &&
                data[3] == (byte)'8' && (data[4] == (byte)'7' || data[4] == (byte)'9') &&
                data[5] == (byte)'a')
                return ".gif";

            if (data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' &&
                data[3] == (byte)'F' && data[8] == (byte)'W' && data[9] == (byte)'E' &&
                data[10] == (byte)'B' && data[11] == (byte)'P')
                return ".webp";

            throw new InvalidDataException("The downloaded file signature is not PNG, JPEG, GIF, or WEBP.");
        }

        private static void ValidateImageContentType(string mediaType, string extension)
        {
            if (string.IsNullOrWhiteSpace(mediaType) ||
                mediaType == "application/octet-stream" ||
                mediaType == "binary/octet-stream")
                return;

            bool matches =
                (extension == ".png" && (mediaType == "image/png" || mediaType == "image/x-png")) ||
                (extension == ".jpg" && (mediaType == "image/jpeg" || mediaType == "image/jpg" || mediaType == "image/pjpeg")) ||
                (extension == ".gif" && mediaType == "image/gif") ||
                (extension == ".webp" && mediaType == "image/webp");

            if (!matches)
                throw new InvalidDataException("The image content type does not match the downloaded file signature.");
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
                await file.DeleteAsync();
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
