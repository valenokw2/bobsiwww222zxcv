using System;
using System.Linq;
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
        private bool _cleaned;
        private long _snapshotRevision = -1;
        private PersistedSettings _snapshot;
        private SettingsStore.SettingsSubscription _subscription;

        public OverlayPage() { InitializeComponent(); Loaded += OnLoaded; Unloaded += OnUnloaded; }
        public OverlayPage(XboxGameBarWidget widget) : this() { _widget = widget; }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            if (e.Parameter is XboxGameBarWidget widget) _widget = widget;
            base.OnNavigatedTo(e);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_subscription != null) return;
            _cleaned = false;
            try
            {
                await SettingsStore.Current.LoadAsync();
                _subscription = SettingsStore.Current.Subscribe(Dispatcher, OnStoreSnapshotChanged);
                ApplyInitialSnapshot(SettingsStore.Current.GetSnapshot());

                if (_widget != null)
                {
                    _widget.RequestedOpacityChanged += OnWidgetStateChanged;
                    _widget.GameBarDisplayModeChanged += OnWidgetStateChanged;
                    _widget.ClickThroughEnabledChanged += OnWidgetStateChanged;
                }

                await RefreshSafelyAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Overlay page load", ex); }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Cleanup();

        // Called both by Unloaded and Window.Closed. It is intentionally idempotent.
        public void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;

            _subscription?.Dispose();
            _subscription = null;
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

        private void ApplyInitialSnapshot(PersistedSettings snapshot)
        {
            if (snapshot == null) return;
            _snapshot = snapshot;
            _snapshotRevision = snapshot.Revision;
        }

        private void OnStoreSnapshotChanged(PersistedSettings snapshot)
        {
            if (_cleaned || snapshot == null || snapshot.Revision <= _snapshotRevision) return;
            _snapshot = snapshot;
            _snapshotRevision = snapshot.Revision;
            _ = RefreshSafelyAsync();
        }

        private async void OnWidgetStateChanged(XboxGameBarWidget sender, object args)
        {
            try
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (!_cleaned) _ = RefreshSafelyAsync();
                });
            }
            catch (Exception ex) { AppDiagnostics.Log("Game Bar widget state update", ex); }
        }

        private async Task RefreshSafelyAsync()
        {
            try
            {
                if (!_cleaned && _snapshot != null) await RefreshAsync(_snapshot);
            }
            catch (Exception ex) { AppDiagnostics.Log("Overlay refresh", ex); }
        }

        private async Task RefreshAsync(PersistedSettings snapshot)
        {
            if (_cleaned || snapshot == null) return;
            var state = snapshot.State ?? new OverlayState();

            UpdateTransformCenter();
            OverlayImage.Opacity = state.Opacity / 100d;
            OverlayTransform.ScaleX = OverlayTransform.ScaleY = state.Scale / 100d;
            OverlayTransform.TranslateX = state.PositionX;
            OverlayTransform.TranslateY = state.PositionY;
            OverlayTransform.Rotation = state.Rotation;
            OverlaySurface.Opacity = _widget == null ? 1 : _widget.RequestedOpacity / 100d;

            var item = snapshot.Library?.FirstOrDefault(x =>
                x != null && string.Equals(x.Id, state.SelectedImageId, StringComparison.Ordinal));
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
                if (item.IsGif) _gif?.UpdateSchedule(state.GifSpeed, state.GifPlaying);
                return;
            }

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
                        if (!_cleaned && generation == _imageLoadGeneration && ReferenceEquals(_gif, player))
                            OverlayImage.Source = player.CurrentFrame;
                    };
                    player.PlaybackFailed += (s, e) =>
                    {
                        if (!_cleaned && generation == _imageLoadGeneration && ReferenceEquals(_gif, player))
                        {
                            Hint.Text = "GIF playback stopped because a frame could not be decoded";
                            Hint.Visibility = Visibility.Visible;
                        }
                    };
                    await player.OpenAsync(file);
                    if (!IsCurrentLoad(generation, item.Id))
                    {
                        player.Dispose();
                        return;
                    }
                    OverlayImage.Source = player.CurrentFrame;
                    player.UpdateSchedule(state.GifSpeed, state.GifPlaying);
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
            catch (Exception ex)
            {
                AppDiagnostics.Log(item.IsGif ? "Overlay GIF load" : "Overlay image load", ex);
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

        private void OverlaySurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTransformCenter();

        private void UpdateTransformCenter()
        {
            OverlayTransform.CenterX = OverlaySurface.ActualWidth / 2d;
            OverlayTransform.CenterY = OverlaySurface.ActualHeight / 2d;
        }

        private bool IsCurrentLoad(int generation, string imageId)
        {
            return !_cleaned && generation == _imageLoadGeneration && _snapshot?.State?.SelectedImageId == imageId;
        }
    }
}