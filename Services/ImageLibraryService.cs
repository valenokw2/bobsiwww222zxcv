using System;
using System.IO;
using System.Linq;
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
        private const double MaxDecodedPixels = 12d * 1024d * 1024d;
        private const uint DefaultDecodeEdge = 4096;

        public async Task<ImageRecord> PickAndImportAsync()
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".gif");
            var source = await picker.PickSingleFileAsync();
            if (source == null) return null;
            return await ImportAsync(source);
        }

        public async Task<ImageRecord> ImportAsync(StorageFile source)
        {
            if (source == null) throw new InvalidOperationException("No file was provided.");
            var extension = Path.GetExtension(source.Name).ToLowerInvariant();
            if (extension != ".png" && extension != ".jpg" && extension != ".jpeg" && extension != ".gif")
                throw new InvalidOperationException("Only PNG, JPG, JPEG, and GIF files are supported.");

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

        public ImageRecord Selected() => SettingsStore.Current.Library.FirstOrDefault(x => x.Id == SettingsStore.Current.State.SelectedImageId);

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
