using System;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using PixelOverlay.Models;
using PixelOverlay.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace PixelOverlay.Views
{
    public sealed partial class OverlayPage : Page
    {
        private XboxGameBarWidget _widget;
        private readonly ImageLibraryService _images = new ImageLibraryService();
        private GifPlayer _gif;
        private string _loadedId;
        private string _loadingId;
        private int _imageLoadGeneration;

        public OverlayPage() { InitializeComponent(); Loaded += OnLoaded; Unloaded += OnUnloaded; }
        public OverlayPage(XboxGameBarWidget widget) : this() { _widget = widget; }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            if (e.Parameter is XboxGameBarWidget widget) _widget = widget;
            base.OnNavigatedTo(e);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            SettingsStore.Current.StateChanged += OnStateChanged;
            if (_widget != null)
            {
                _widget.RequestedOpacityChanged += OnWidgetStateChanged;
                _widget.GameBarDisplayModeChanged += OnWidgetStateChanged;
                _widget.ClickThroughEnabledChanged += OnWidgetStateChanged;
            }
            await RefreshAsync();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SettingsStore.Current.StateChanged -= OnStateChanged;
            if (_widget != null)
            {
                _widget.RequestedOpacityChanged -= OnWidgetStateChanged;
                _widget.GameBarDisplayModeChanged -= OnWidgetStateChanged;
                _widget.ClickThroughEnabledChanged -= OnWidgetStateChanged;
            }
            _imageLoadGeneration++;
            _loadingId = null;
            _loadedId = null;
            _gif?.Dispose();
            _gif = null;
            OverlayImage.Source = null;
        }

        private async void OnStateChanged(object sender, EventArgs e) => await RefreshAsync();
        private async void OnWidgetStateChanged(XboxGameBarWidget sender, object args) => await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => await RefreshAsync());

        private async Task RefreshAsync()
        {
            var state = SettingsStore.Current.State;
            OverlayImage.Opacity = state.Opacity / 100d;
            OverlayTransform.ScaleX = OverlayTransform.ScaleY = state.Scale / 100d;
            OverlayTransform.TranslateX = state.PositionX;
            OverlayTransform.TranslateY = state.PositionY;
            OverlayTransform.Rotation = state.Rotation;
            // RequestedOpacity belongs to the Game Bar surface and only affects this image-only widget.
            OverlaySurface.Opacity = _widget == null ? 1 : _widget.RequestedOpacity / 100d;

            var item = _images.Selected();
            if (item == null)
            {
                _imageLoadGeneration++;
                _loadingId = null;
                _loadedId = null;
                _gif?.Dispose();
                _gif = null;
                OverlayImage.Source = null;
                Hint.Text = "Open this widget's settings to import an image";
                Hint.Visibility = Visibility.Visible;
                return;
            }

            Hint.Visibility = Visibility.Collapsed;
            if (_loadedId == item.Id)
            {
                if (item.IsGif) _gif?.UpdateSchedule();
                return;
            }

            // State changes can arrive while a file is decoding. Do not start duplicate loads
            // for the same selection, but invalidate a load as soon as the selected ID changes.
            if (_loadingId == item.Id) return;

            var generation = ++_imageLoadGeneration;
            _loadingId = item.Id;
            _loadedId = null;
            _gif?.Dispose();
            _gif = null;
            OverlayImage.Source = null;
            Hint.Text = "Loading image...";
            Hint.Visibility = Visibility.Visible;

            try
            {
                if (item.IsGif)
                {
                    var file = await _images.GetFileAsync(item);
                    if (!IsCurrentLoad(generation, item.Id)) return;

                    var player = new GifPlayer();
                    _gif = player;
                    player.FrameChanged += (s, e) =>
                    {
                        if (generation == _imageLoadGeneration && ReferenceEquals(_gif, player))
                            OverlayImage.Source = player.CurrentFrame;
                    };
                    await player.OpenAsync(file);
                    if (!IsCurrentLoad(generation, item.Id))
                    {
                        player.Dispose();
                        return;
                    }
                    OverlayImage.Source = player.CurrentFrame;
                }
                else
                {
                    var bitmap = await _images.LoadBitmapAsync(item);
                    if (!IsCurrentLoad(generation, item.Id)) return;
                    OverlayImage.Source = bitmap;
                }

                _loadedId = item.Id;
                Hint.Visibility = Visibility.Collapsed;
            }
            catch (Exception)
            {
                if (IsCurrentLoad(generation, item.Id))
                {
                    _loadedId = null;
                    _gif?.Dispose();
                    _gif = null;
                    OverlayImage.Source = null;
                    Hint.Text = "Image could not be displayed";
                    Hint.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                if (generation == _imageLoadGeneration && _loadingId == item.Id)
                    _loadingId = null;
            }
        }

        private bool IsCurrentLoad(int generation, string imageId)
        {
            return generation == _imageLoadGeneration && _images.Selected()?.Id == imageId;
        }
    }
}
