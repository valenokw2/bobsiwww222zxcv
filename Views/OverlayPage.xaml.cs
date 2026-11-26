using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Microsoft.Gaming.XboxGameBar.Input;
using PixelOverlay.Models;
using PixelOverlay.Services;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.System;

namespace PixelOverlay.Views
{
    public sealed partial class OverlayPage : Page
    {
        private const int MaxAnimatedGifPlayers = 2;

        private XboxGameBarWidget _widget;
        private readonly ImageLibraryService _images = new ImageLibraryService();
        private readonly ObservableCollection<OverlayLayer> _layers = new ObservableCollection<OverlayLayer>();
        private readonly Dictionary<string, GifPlayer> _gifPlayers = new Dictionary<string, GifPlayer>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _loadedImageIds = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, BitmapImage> _staticImageCache = new Dictionary<string, BitmapImage>(StringComparer.Ordinal);
        private readonly HashSet<string> _loadingLayers = new HashSet<string>(StringComparer.Ordinal);
        private readonly SemaphoreSlim _refreshGate = new SemaphoreSlim(1, 1);
        private bool _cleanedUp;
        private XboxGameBarHotkeyWatcher _visibilityHotkeyWatcher;

        public OverlayPage()
        {
            InitializeComponent();
            LayerItems.ItemsSource = _layers;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        public OverlayPage(XboxGameBarWidget widget) : this()
        {
            _widget = widget;
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            if (e.Parameter is XboxGameBarWidget widget) _widget = widget;
            base.OnNavigatedTo(e);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await SettingsStore.Current.LoadAsync();
                if (_cleanedUp) return;

                SettingsStore.Current.StateChanged += OnStateChanged;
                if (_widget != null)
                {
                    _widget.RequestedOpacityChanged += OnWidgetStateChanged;
                    _widget.GameBarDisplayModeChanged += OnWidgetStateChanged;
                    _widget.ClickThroughEnabledChanged += OnWidgetStateChanged;
                    _widget.VisibleChanged += OnWidgetStateChanged;
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

            SettingsStore.Current.StateChanged -= OnStateChanged;
            StopVisibilityHotkey();
            if (_widget != null)
            {
                _widget.RequestedOpacityChanged -= OnWidgetStateChanged;
                _widget.GameBarDisplayModeChanged -= OnWidgetStateChanged;
                _widget.ClickThroughEnabledChanged -= OnWidgetStateChanged;
                _widget.VisibleChanged -= OnWidgetStateChanged;
            }

            foreach (var player in _gifPlayers.Values) player.Dispose();
            _gifPlayers.Clear();
            _loadedImageIds.Clear();
            _staticImageCache.Clear();
            _loadingLayers.Clear();
            _layers.Clear();
        }

        private async void OnStateChanged(object sender, EventArgs e)
        {
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
            await _refreshGate.WaitAsync();
            try
            {
                if (_cleanedUp) return;
                var snapshot = SettingsStore.Current.GetSnapshot();
                var state = snapshot.State ?? new OverlayState();

                OverlaySurface.Opacity = _widget == null || state.IgnoreGameBarOpacity
                    ? 1d
                    : _widget.RequestedOpacity;
                UpdateVisibilityHotkey(state.VisibilityHotkeyEnabled);

                SyncLayerCollection(state.Layers);

                // Release hidden or paused GIF decoders first, so later visible layers
                // can claim the freed animation slots in this same refresh pass.
                foreach (var layer in _layers)
                {
                    if (layer.IsVisible && (_widget == null || _widget.Visible) && layer.GifPlaying)
                        continue;
                    if (_gifPlayers.TryGetValue(layer.LayerId, out var inactivePlayer))
                    {
                        inactivePlayer.Dispose();
                        _gifPlayers.Remove(layer.LayerId);
                    }
                }

                PruneStaticImageCache();
                UpdateLayerPositions();
                UpdateHint();

                foreach (var layer in _layers.OrderBy(x => x.ZIndex).ToList())
                {
                    if (_cleanedUp) return;
                    var item = snapshot.Library?.FirstOrDefault(x =>
                        string.Equals(x.Id, layer.ImageId, StringComparison.Ordinal));
                    if (item == null)
                    {
                        RemoveLayerResources(layer.LayerId);
                        layer.ImageSource = null;
                        continue;
                    }

                    bool visible = layer.IsVisible && (_widget == null || _widget.Visible);
                    bool shouldAnimate = visible && layer.GifPlaying;
                    if (!visible) continue;

                    if (_loadedImageIds.TryGetValue(layer.LayerId, out var loadedId) &&
                        string.Equals(loadedId, item.Id, StringComparison.Ordinal))
                    {
                        if (item.IsGif && shouldAnimate && !_gifPlayers.ContainsKey(layer.LayerId))
                            await EnsureGifPlayerAsync(layer, item);
                        continue;
                    }
                    if (_loadingLayers.Contains(layer.LayerId)) continue;

                    await LoadLayerAsync(layer, item, visible);
                }

                UpdateHint();
            }
            finally { _refreshGate.Release(); }
        }

        private void UpdateHint()
        {
            Hint.Visibility = _layers.Any(x => x.IsVisible && x.ImageSource != null)
                ? Visibility.Collapsed : Visibility.Visible;
            Hint.Text = _layers.Count == 0
                ? "Open this widget's settings to add images"
                : _layers.Any(x => x.IsVisible)
                    ? "Images could not be displayed. Check the image library."
                    : "No visible layers. Show a layer in Pixel Overlay Settings.";
        }

        private void SyncLayerCollection(IList<OverlayLayer> source)
        {
            var incoming = source ?? new List<OverlayLayer>();
            var incomingIds = new HashSet<string>(incoming.Where(x => x != null).Select(x => x.LayerId), StringComparer.Ordinal);

            foreach (var current in _layers.ToList())
            {
                if (incomingIds.Contains(current.LayerId)) continue;
                RemoveLayerResources(current.LayerId);
                _layers.Remove(current);
            }

            var ordered = incoming.Where(x => x != null).OrderBy(x => x.ZIndex).ToList();
            for (int index = 0; index < ordered.Count; index++)
            {
                var sourceLayer = ordered[index];
                var target = _layers.FirstOrDefault(x => string.Equals(x.LayerId, sourceLayer.LayerId, StringComparison.Ordinal));
                if (target == null)
                {
                    target = sourceLayer.Clone();
                    _layers.Insert(Math.Min(index, _layers.Count), target);
                }
                else
                {
                    target.CopyFrom(sourceLayer);
                    int currentIndex = _layers.IndexOf(target);
                    if (currentIndex != index) _layers.Move(currentIndex, Math.Min(index, _layers.Count - 1));
                }

                if (_loadedImageIds.TryGetValue(target.LayerId, out var loadedId) &&
                    !string.Equals(loadedId, target.ImageId, StringComparison.Ordinal))
                {
                    RemoveLayerResources(target.LayerId);
                    target.ImageSource = null;
                }
            }
        }

        private async Task LoadLayerAsync(OverlayLayer layer, ImageRecord item, bool visible)
        {
            if (layer == null || item == null || _cleanedUp) return;
            var layerId = layer.LayerId;
            var imageId = item.Id;
            _loadingLayers.Add(layerId);
            try
            {
                // Decode once per unique library item. Repeated layers reuse the same
                // BitmapImage and do not allocate another static texture.
                if (!_staticImageCache.TryGetValue(imageId, out var bitmap))
                {
                    bitmap = await _images.LoadBitmapAsync(item, 1280);
                    if (!IsCurrentLayer(layerId, imageId)) return;
                    _staticImageCache[imageId] = bitmap;
                }
                if (!IsCurrentLayer(layerId, imageId)) return;
                ApplyInitialSize(layer, bitmap.PixelWidth, bitmap.PixelHeight);
                layer.ImageSource = bitmap;
                _loadedImageIds[layerId] = imageId;

                if (item.IsGif && visible && layer.GifPlaying)
                    await EnsureGifPlayerAsync(layer, item);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log(item.IsGif ? "Load GIF layer" : "Load image layer", ex);
                if (_gifPlayers.TryGetValue(layerId, out var failedPlayer))
                {
                    failedPlayer.Dispose();
                    _gifPlayers.Remove(layerId);
                }
                // Keep the decoded first frame as a static fallback when GIF playback fails.
                if (IsCurrentLayer(layerId, imageId) &&
                    _staticImageCache.TryGetValue(imageId, out var fallback))
                {
                    layer.ImageSource = fallback;
                    _loadedImageIds[layerId] = imageId;
                }
            }
            finally { _loadingLayers.Remove(layerId); }
        }

        private async Task EnsureGifPlayerAsync(OverlayLayer layer, ImageRecord item)
        {
            if (_cleanedUp || layer == null || item == null || !item.IsGif ||
                _gifPlayers.ContainsKey(layer.LayerId) || _gifPlayers.Count >= MaxAnimatedGifPlayers ||
                !layer.IsVisible || (_widget != null && !_widget.Visible) || !layer.GifPlaying)
                return;

            var layerId = layer.LayerId;
            var imageId = item.Id;
            GifPlayer player = null;
            try
            {
                var file = await _images.GetFileAsync(item);
                if (!IsCurrentLayer(layerId, imageId) || _cleanedUp) return;

                player = new GifPlayer();
                player.FrameChanged += (s, e) =>
                {
                    if (!_cleanedUp && IsCurrentLayer(layerId, imageId) &&
                        _gifPlayers.TryGetValue(layerId, out var active) && ReferenceEquals(active, player))
                        layer.ImageSource = player.CurrentFrame;
                };
                player.PlaybackFailed += (s, e) =>
                {
                    if (_cleanedUp || !IsCurrentLayer(layerId, imageId)) return;
                    AppDiagnostics.Log("GIF layer playback", new InvalidOperationException("GIF layer playback stopped."));
                    if (_gifPlayers.TryGetValue(layerId, out var failed) && ReferenceEquals(failed, player))
                    {
                        failed.Dispose();
                        _gifPlayers.Remove(layerId);
                        if (_staticImageCache.TryGetValue(imageId, out var fallback))
                            layer.ImageSource = fallback;
                        _ = RefreshSafelyAsync();
                    }
                };

                // Reserve the slot before awaiting decoder initialization so no other
                // layer can race into the same animation budget.
                _gifPlayers[layerId] = player;
                await player.OpenAsync(file, CreatePlaybackState(layer));
                if (!IsCurrentLayer(layerId, imageId) || _cleanedUp ||
                    !layer.IsVisible || (_widget != null && !_widget.Visible) || !layer.GifPlaying)
                {
                    player.Dispose();
                    if (_gifPlayers.TryGetValue(layerId, out var stale) && ReferenceEquals(stale, player))
                        _gifPlayers.Remove(layerId);
                    if (_staticImageCache.TryGetValue(imageId, out var fallback) && IsCurrentLayer(layerId, imageId))
                        layer.ImageSource = fallback;
                    return;
                }

                layer.ImageSource = player.CurrentFrame;
                player.SetVisible(true);
            }
            catch (Exception ex)
            {
                if (player != null)
                {
                    player.Dispose();
                    if (_gifPlayers.TryGetValue(layerId, out var failed) && ReferenceEquals(failed, player))
                        _gifPlayers.Remove(layerId);
                }
                if (IsCurrentLayer(layerId, imageId) &&
                    _staticImageCache.TryGetValue(imageId, out var fallback))
                {
                    layer.ImageSource = fallback;
                    _loadedImageIds[layerId] = imageId;
                }
                AppDiagnostics.Log("Initialize GIF layer", ex);
            }
        }

        private void ApplyInitialSize(OverlayLayer layer, int pixelWidth, int pixelHeight)
        {
            if (layer.Width > 0 && layer.Height > 0) return;
            double width = Math.Max(1, pixelWidth);
            double height = Math.Max(1, pixelHeight);
            double availableWidth = Math.Max(160, OverlaySurface.ActualWidth > 0 ? OverlaySurface.ActualWidth * .8 : 300);
            double availableHeight = Math.Max(120, OverlaySurface.ActualHeight > 0 ? OverlaySurface.ActualHeight * .8 : 300);
            double scale = Math.Min(1d, Math.Min(availableWidth / width, availableHeight / height));
            layer.Width = Math.Max(1, width * scale);
            layer.Height = Math.Max(1, height * scale);
        }

        private void PruneStaticImageCache()
        {
            bool widgetVisible = _widget == null || _widget.Visible;
            var referenced = new HashSet<string>(_layers
                .Where(x => x.IsVisible && widgetVisible && !string.IsNullOrWhiteSpace(x.ImageId))
                .Select(x => x.ImageId), StringComparer.Ordinal);
            foreach (var key in _staticImageCache.Keys.Where(x => !referenced.Contains(x)).ToList())
                _staticImageCache.Remove(key);

            foreach (var layer in _layers)
            {
                if (!layer.IsVisible || !widgetVisible)
                {
                    layer.ImageSource = null;
                    _loadedImageIds.Remove(layer.LayerId);
                }
            }
        }

        private void UpdateLayerPositions()
        {
            foreach (var layer in _layers)
                layer.UpdateCanvasPosition(OverlaySurface.ActualWidth, OverlaySurface.ActualHeight);
        }

        private void OverlaySurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayerPositions();

        private OverlayState CreatePlaybackState(OverlayLayer layer)
        {
            return new OverlayState { GifSpeed = layer.GifSpeed, GifPlaying = layer.GifPlaying };
        }

        private bool IsCurrentLayer(string layerId, string imageId)
        {
            if (_cleanedUp) return false;
            var layer = _layers.FirstOrDefault(x => string.Equals(x.LayerId, layerId, StringComparison.Ordinal));
            return layer != null && string.Equals(layer.ImageId, imageId, StringComparison.Ordinal);
        }

        private void RemoveLayerResources(string layerId)
        {
            if (_gifPlayers.TryGetValue(layerId, out var player))
            {
                player.Dispose();
                _gifPlayers.Remove(layerId);
            }
            _loadedImageIds.Remove(layerId);
            _loadingLayers.Remove(layerId);
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
                var keys = new List<VirtualKey> { VirtualKey.Control, VirtualKey.Menu, VirtualKey.O };
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
    }
}