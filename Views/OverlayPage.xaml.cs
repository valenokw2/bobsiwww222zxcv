using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Input;
using PixelOverlay.Models;
using PixelOverlay.Services;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.System;

namespace PixelOverlay.Views
{
    public sealed partial class OverlayPage : Page
    {
        private XboxGameBarWidget _widget;
        private string _windowId = WindowSlotIds.FirstWindowId;
        private AssetCacheService.AssetLease _gifLease;
        private readonly ImageLibraryService _images = new ImageLibraryService();
        private GifPlayer _gif;
        private string _loadedId;
        private string _loadingId;
        private int _imageLoadGeneration;
        private bool _cleanedUp;
        private XboxGameBarHotkeyWatcher _visibilityHotkeyWatcher;

        public OverlayPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public OverlayPage(XboxGameBarWidget widget) : this()
        {
            _widget = widget;
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            if (e.Parameter is WidgetActivationContext context)
            {
                _widget = context.Widget;
                _windowId = WindowSlotIds.Normalize(context.WindowId);
            }
            else if (e.Parameter is XboxGameBarWidget widget)
            {
                _widget = widget;
                _windowId = WindowSlotIds.FirstWindowId;
            }
            base.OnNavigatedTo(e);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await SettingsStore.Current.LoadAsync();
                if (_cleanedUp) return;

                SettingsStore.Current.WindowStateChanged += OnWindowStateChanged;
                if (_widget != null)
                {
                    _widget.RequestedOpacityChanged += OnWidgetStateChanged;
                    _widget.GameBarDisplayModeChanged += OnWidgetStateChanged;
                    _widget.VisibleChanged += OnWidgetStateChanged;
                    _widget.ClickThroughEnabledChanged += OnWidgetStateChanged;
                }
                await RefreshAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Overlay page load", ex); }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Cleanup();
        public void OnWindowClosed() => Cleanup();

        private void Cleanup()
        {
            if (_cleanedUp) return;
            _cleanedUp = true;

            SettingsStore.Current.WindowStateChanged -= OnWindowStateChanged;
            StopVisibilityHotkey();
            if (_widget != null)
            {
                _widget.RequestedOpacityChanged -= OnWidgetStateChanged;
                _widget.GameBarDisplayModeChanged -= OnWidgetStateChanged;
                _widget.VisibleChanged -= OnWidgetStateChanged;
                _widget.ClickThroughEnabledChanged -= OnWidgetStateChanged;
            }

            _imageLoadGeneration++;
            _loadingId = null;
            _loadedId = null;
            _gif?.Dispose();
            _gif = null;
            _gifLease?.Dispose();
            _gifLease = null;
            OverlayImage.Source = null;
        }

        private async void OnWindowStateChanged(object sender, WindowStateChangedEventArgs e)
        {
            if (e == null || !string.Equals(e.WindowId, _windowId, StringComparison.Ordinal)) return;
            try
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => { _ = RefreshSafelyAsync(); });
            }
            catch (Exception ex) { AppDiagnostics.Log("Overlay state dispatch", ex); }
        }

        private async void OnWidgetStateChanged(XboxGameBarWidget sender, object args)
        {
            try
            {
                _gif?.SetVisible(sender != null && sender.Visible);
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => { _ = RefreshSafelyAsync(); });
            }
            catch (Exception ex) { AppDiagnostics.Log("Game Bar widget state update", ex); }
        }

        private async Task RefreshSafelyAsync()
        {
            try { await RefreshAsync(); }
            catch (Exception ex) { AppDiagnostics.Log("Overlay refresh", ex); }
        }

        private async Task RefreshAsync()
        {
            if (_cleanedUp) return;

            UpdateTransformCenter();
            var snapshot = SettingsStore.Current.GetSnapshot();
            var state = SettingsStore.Current.GetWindowState(_windowId);

            OverlayImage.Opacity = state.Opacity / 100d;
            UpdateVisibilityHotkey(state.VisibilityHotkeyEnabled);
            OverlayTransform.ScaleX = OverlayTransform.ScaleY = state.Scale / 100d;
            OverlayTransform.TranslateX = state.PositionX;
            OverlayTransform.TranslateY = state.PositionY;
            OverlayTransform.Rotation = state.Rotation;

            // The Xbox Game Bar sample applies RequestedOpacity directly to a XAML Opacity
            // property. Do not divide the SDK value by 100 here.
            OverlaySurface.Opacity = _widget == null || state.IgnoreGameBarOpacity
                ? 1d
                : _widget.RequestedOpacity;

            var item = snapshot.Library?.FirstOrDefault(x =>
                string.Equals(x.Id, state.SelectedImageId, StringComparison.Ordinal));

            if (item == null)
            {
                _imageLoadGeneration++;
                _loadingId = null;
                _loadedId = null;
                _gif?.Dispose();
                _gif = null;
                _gifLease?.Dispose();
                _gifLease = null;
                OverlayImage.Source = null;
                Hint.Text = "Open this widget's settings to import an image";
                Hint.Visibility = Visibility.Visible;
                return;
            }

            Hint.Visibility = Visibility.Collapsed;
            if (string.Equals(_loadedId, item.Id, StringComparison.Ordinal))
            {
                if (item.IsGif)
                {
                    _gif?.UpdateSchedule(state);
                    _gif?.SetVisible(_widget == null || _widget.Visible);
                }
                return;
            }

            // A transform update during decode must not start a duplicate load. A new image
            // selection increments the generation and invalidates the previous asynchronous load.
            if (string.Equals(_loadingId, item.Id, StringComparison.Ordinal)) return;

            var generation = ++_imageLoadGeneration;
            _loadingId = item.Id;
            _loadedId = null;
            _gif?.Dispose();
            _gif = null;
            _gifLease?.Dispose();
            _gifLease = null;
            OverlayImage.Source = null;
            Hint.Text = "Loading image...";
            Hint.Visibility = Visibility.Visible;

            AssetCacheService.AssetLease pendingLease = null;
            try
            {
                if (item.IsGif)
                {
                    pendingLease = await AssetCacheService.Instance.AcquireGifAsync(item, _images);
                    if (!IsCurrentLoad(generation, item.Id))
                    {
                        pendingLease.Dispose();
                        pendingLease = null;
                        return;
                    }

                    var player = new GifPlayer();
                    _gif = player;
                    player.FrameChanged += (s, e) =>
                    {
                        if (!_cleanedUp && generation == _imageLoadGeneration &&
                            ReferenceEquals(_gif, player))
                            OverlayImage.Source = player.CurrentFrame;
                    };
                    player.PlaybackFailed += (s, e) =>
                    {
                        if (!_cleanedUp && generation == _imageLoadGeneration &&
                            ReferenceEquals(_gif, player))
                        {
                            Hint.Text = "GIF playback stopped because a frame could not be decoded";
                            Hint.Visibility = Visibility.Visible;
                        }
                    };

                    await player.OpenAsync(pendingLease.DetachStream(), state);
                    if (!IsCurrentLoad(generation, item.Id))
                    {
                        player.Dispose();
                        pendingLease.Dispose();
                        pendingLease = null;
                        return;
                    }
                    _gifLease = pendingLease;
                    pendingLease = null;
                    // The selected GIF may have finished decoding after the user changed
                    // playback speed or pressed Pause. Apply the latest shared snapshot.
                    player.UpdateSchedule(SettingsStore.Current.GetWindowState(_windowId));
                    player.SetVisible(_widget == null || _widget.Visible);
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
            catch (Exception ex)
            {
                pendingLease?.Dispose();
                pendingLease = null;
                AppDiagnostics.Log(item.IsGif ? "Overlay GIF load" : "Overlay image load", ex);
                if (IsCurrentLoad(generation, item.Id))
                {
                    _loadedId = null;
                    _gif?.Dispose();
                    _gif = null;
                    _gifLease?.Dispose();
                    _gifLease = null;
                    OverlayImage.Source = null;
                    Hint.Text = "Image could not be displayed";
                    Hint.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                if (generation == _imageLoadGeneration &&
                    string.Equals(_loadingId, item.Id, StringComparison.Ordinal))
                    _loadingId = null;
            }
        }

        private void UpdateVisibilityHotkey(bool enabled)
        {
            if (_widget == null || _cleanedUp) return;

            if (!enabled)
            {
                StopVisibilityHotkey();
                return;
            }

            if (_visibilityHotkeyWatcher != null) return;
            try
            {
                var keys = new List<VirtualKey>
                {
                    VirtualKey.Control,
                    VirtualKey.Menu, // Alt
                    VirtualKey.O
                };
                var watcher = XboxGameBarHotkeyWatcher.CreateWatcher(_widget, keys);
                watcher.HotkeySetStateChanged += OnVisibilityHotkeyChanged;
                watcher.Start();
                _visibilityHotkeyWatcher = watcher;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Start overlay visibility hotkey", ex);
                StopVisibilityHotkey();
            }
        }

        private void StopVisibilityHotkey()
        {
            var watcher = _visibilityHotkeyWatcher;
            _visibilityHotkeyWatcher = null;
            if (watcher == null) return;
            try
            {
                watcher.HotkeySetStateChanged -= OnVisibilityHotkeyChanged;
                watcher.Stop();
            }
            catch (Exception ex) { AppDiagnostics.Log("Stop overlay visibility hotkey", ex); }
        }

        private async void OnVisibilityHotkeyChanged(XboxGameBarHotkeyWatcher sender, HotkeySetStateChangedArgs args)
        {
            if (!args.HotkeySetDown || _cleanedUp) return;
            try
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => { _ = ToggleWidgetVisibilityAsync(); });
            }
            catch (Exception ex) { AppDiagnostics.Log("Overlay visibility hotkey dispatch", ex); }
        }

        private async Task ToggleWidgetVisibilityAsync()
        {
            if (_cleanedUp || _widget == null) return;
            try
            {
                if (_widget.WindowState == XboxGameBarWidgetWindowState.Minimized)
                    await _widget.RestoreAsync();
                else
                    await _widget.MinimizeAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Toggle overlay visibility", ex); }
        }

        private void OverlaySurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTransformCenter();

        private void UpdateTransformCenter()
        {
            OverlayTransform.CenterX = OverlaySurface.ActualWidth / 2d;
            OverlayTransform.CenterY = OverlaySurface.ActualHeight / 2d;
        }

        private bool IsCurrentLoad(int generation, string imageId)
        {
            if (_cleanedUp || generation != _imageLoadGeneration) return false;
            var current = SettingsStore.Current.GetWindowState(_windowId);
            return string.Equals(current?.SelectedImageId, imageId, StringComparison.Ordinal);
        }
    }
}
