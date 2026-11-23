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
            _gif?.Dispose(); _gif = null;
        }
        private async void OnStateChanged(object sender, EventArgs e) => await RefreshAsync();
        private async void OnWidgetStateChanged(XboxGameBarWidget sender, object args) => await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () => await RefreshAsync());

        private async Task RefreshAsync()
        {
            var state = SettingsStore.Current.State;
            OverlayImage.Opacity = state.Opacity / 100d;
            OverlayTransform.ScaleX = OverlayTransform.ScaleY = state.Scale / 100d;
            OverlayTransform.TranslateX = state.PositionX; OverlayTransform.TranslateY = state.PositionY;
            OverlayTransform.Rotation = state.Rotation;
            // RequestedOpacity belongs to the Game Bar surface and only affects this image-only widget.
            OverlaySurface.Opacity = _widget == null ? 1 : _widget.RequestedOpacity / 100d;
            var item = _images.Selected();
            if (item == null) { OverlayImage.Source = null; _gif?.Stop(); _loadedId = null; return; }
            if (_loadedId == item.Id)
            {
                if (item.IsGif) _gif?.UpdateSchedule();
                return;
            }
            _loadedId = item.Id;
            _gif?.Dispose(); _gif = null;
            try
            {
                var file = await _images.GetFileAsync(item);
                if (item.IsGif)
                {
                    _gif = new GifPlayer();
                    _gif.FrameChanged += (s, e) => OverlayImage.Source = _gif.CurrentFrame;
                    await _gif.OpenAsync(file);
                    OverlayImage.Source = _gif.CurrentFrame;
                }
                else OverlayImage.Source = await _images.LoadBitmapAsync(item);
            }
            catch { OverlayImage.Source = null; } // A removed/corrupt cache entry must not crash the pinned overlay.
        }
    }
}
