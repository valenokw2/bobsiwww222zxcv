using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using PixelOverlay.Models;
using PixelOverlay.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace PixelOverlay.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly ImageLibraryService _images = new ImageLibraryService();
        private readonly ObservableCollection<ImageRecord> _library = new ObservableCollection<ImageRecord>();
        private readonly ObservableCollection<ImageRecord> _libraryView = new ObservableCollection<ImageRecord>();
        private readonly ObservableCollection<OverlayLayer> _layers = new ObservableCollection<OverlayLayer>();
        private readonly ObservableCollection<OverlayProfile> _profiles = new ObservableCollection<OverlayProfile>();
        private readonly Dictionary<string, ImageSource> _previewImageCache = new Dictionary<string, ImageSource>(StringComparer.Ordinal);
        private XboxGameBarWidget _widget;
        private XboxGameBarWidgetControl _widgetControl;
        private OverlayState _state = new OverlayState();
        private ImageRecord _selectedLibraryItem;
        private bool _dragging;
        private Windows.Foundation.Point _dragStart;
        private double _originX, _originY;
        private int _previewLoadGeneration;
        private bool _restoringLayerSelection;
        private bool _restoringLibrarySelection;
        private bool _applyingStoreSnapshot;
        private bool _cleanedUp;
        private bool _dialogOpen;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            _widget = e.Parameter as XboxGameBarWidget;
            _widgetControl = _widget == null ? null : new XboxGameBarWidgetControl(_widget);
            base.OnNavigatedTo(e);
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await SettingsStore.Current.LoadAsync();
                if (_cleanedUp) return;

                DataContext = _state;
                LibraryList.ItemsSource = _libraryView;
                LayerList.ItemsSource = _layers;
                ProfileList.ItemsSource = _profiles;
                PreviewLayers.ItemsSource = _layers;
                _state.PropertyChanged += LocalState_PropertyChanged;
                SettingsStore.Current.StateChanged += Store_StateChanged;
                await ApplyStoreSnapshotAsync(SettingsStore.Current.GetSnapshot(), true);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Settings page load", ex);
                ShowError("Settings could not be loaded. See pixel-overlay-diagnostics.log.");
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Cleanup();
        public void OnWindowClosed() => Cleanup();

        private void Cleanup()
        {
            if (_cleanedUp) return;
            _cleanedUp = true;
            SettingsStore.Current.StateChanged -= Store_StateChanged;
            _state.PropertyChanged -= LocalState_PropertyChanged;
            _previewLoadGeneration++;
            _dragging = false;
            _layers.Clear();
            _libraryView.Clear();
            _previewImageCache.Clear();
        }

        private void LocalState_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_cleanedUp || _applyingStoreSnapshot) return;
            if (e.PropertyName == nameof(OverlayState.OpacityText) ||
                e.PropertyName == nameof(OverlayState.ScaleText) ||
                e.PropertyName == nameof(OverlayState.RotationText) ||
                e.PropertyName == nameof(OverlayState.GifSpeedText))
                return;

            try
            {
                SettingsStore.Current.UpdateState(_state);
                SyncLayerViews(_state.Layers);
                UpdatePreviewPositions();
                UpdateControls();
            }
            catch (Exception ex) { AppDiagnostics.Log("Settings local state update", ex); }
        }

        private void Store_StateChanged(object sender, EventArgs e)
        {
            if (_cleanedUp) return;
            try
            {
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (!_cleanedUp)
                        _ = ApplyStoreSnapshotSafelyAsync(SettingsStore.Current.GetSnapshot());
                });
            }
            catch (Exception ex) { AppDiagnostics.Log("Settings state dispatch", ex); }
        }

        private async Task ApplyStoreSnapshotSafelyAsync(PersistedSettings snapshot)
        {
            try { await ApplyStoreSnapshotAsync(snapshot, false); }
            catch (Exception ex) { AppDiagnostics.Log("Settings snapshot UI update", ex); }
        }

        private async Task ApplyStoreSnapshotAsync(PersistedSettings snapshot, bool forcePreviewReload)
        {
            if (_cleanedUp || snapshot == null) return;
            var previousLayerIds = string.Join("|", _layers.Select(x => x.LayerId + ":" + x.ImageId));
            _applyingStoreSnapshot = true;
            try
            {
                _state.CopyFrom(snapshot.State ?? new OverlayState());

                if (!LibraryMatches(snapshot.Library))
                {
                    _library.Clear();
                    if (snapshot.Library != null)
                        foreach (var item in snapshot.Library)
                            if (item != null) _library.Add(item.Clone());
                    RefreshLibraryView();
                }

                SyncLayerViews(_state.Layers);

                if (!ProfilesMatch(snapshot.Profiles))
                {
                    _profiles.Clear();
                    if (snapshot.Profiles != null)
                        foreach (var profile in snapshot.Profiles)
                            if (profile != null) _profiles.Add(profile.Clone());
                }

                _selectedLibraryItem = _selectedLibraryItem == null
                    ? null
                    : _library.FirstOrDefault(x => string.Equals(x.Id, _selectedLibraryItem.Id, StringComparison.Ordinal));
                _restoringLibrarySelection = true;
                try { LibraryList.SelectedItem = _selectedLibraryItem; }
                finally { _restoringLibrarySelection = false; }

                _restoringLayerSelection = true;
                try
                {
                    LayerList.SelectedItem = _layers.FirstOrDefault(x =>
                        string.Equals(x.LayerId, _state.SelectedLayerId, StringComparison.Ordinal));
                }
                finally { _restoringLayerSelection = false; }
            }
            finally { _applyingStoreSnapshot = false; }

            UpdatePreviewPositions();
            UpdateControls();
            var currentLayerIds = string.Join("|", _layers.Select(x => x.LayerId + ":" + x.ImageId));
            if (forcePreviewReload || !string.Equals(previousLayerIds, currentLayerIds, StringComparison.Ordinal))
                _previewLoadGeneration++;
            await LoadPreviewLayersAsync();
        }

        private void SyncLayerViews(IList<OverlayLayer> source)
        {
            var old = _layers.ToDictionary(x => x.LayerId, x => x, StringComparer.Ordinal);
            var next = new List<OverlayLayer>();
            foreach (var item in (source ?? new List<OverlayLayer>()).Where(x => x != null).OrderBy(x => x.ZIndex))
            {
                if (old.TryGetValue(item.LayerId, out var view))
                {
                    bool sameImage = string.Equals(view.ImageId, item.ImageId, StringComparison.Ordinal);
                    view.CopyFrom(item);
                    if (!sameImage) view.ImageSource = null;
                }
                else
                    view = item.Clone();

                var record = _library.FirstOrDefault(x => string.Equals(x.Id, item.ImageId, StringComparison.Ordinal));
                view.DisplayName = record == null ? "Missing image" : record.DisplayName;
                next.Add(view);
            }

            _layers.Clear();
            foreach (var item in next) _layers.Add(item);
        }

        private bool LibraryMatches(IList<ImageRecord> source)
        {
            int count = source == null ? 0 : source.Count;
            if (_library.Count != count) return false;
            for (int i = 0; i < count; i++)
            {
                var a = _library[i];
                var b = source[i];
                if (a == null || b == null ||
                    !string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||
                    !string.Equals(a.StoredName, b.StoredName, StringComparison.Ordinal) ||
                    !string.Equals(a.DisplayName, b.DisplayName, StringComparison.Ordinal) ||
                    !string.Equals(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase) ||
                    a.AddedAt != b.AddedAt || a.IsFavorite != b.IsFavorite)
                    return false;
            }
            return true;
        }

        private bool ProfilesMatch(IList<OverlayProfile> source)
        {
            int count = source == null ? 0 : source.Count;
            if (_profiles.Count != count) return false;
            for (int i = 0; i < count; i++)
            {
                var a = _profiles[i];
                var b = source[i];
                if (a == null || b == null || !string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                    a.SelectedLayerId != b.SelectedLayerId || a.HasLayerSnapshot != b.HasLayerSnapshot || a.Layers?.Count != b.Layers?.Count)
                    return false;
                if (a.Layers != null)
                {
                    for (int layerIndex = 0; layerIndex < a.Layers.Count; layerIndex++)
                        if (!LayersMatch(a.Layers[layerIndex], b.Layers[layerIndex])) return false;
                }
                else if (a.Opacity != b.Opacity || a.Scale != b.Scale || a.PositionX != b.PositionX ||
                    a.PositionY != b.PositionY || a.Rotation != b.Rotation ||
                    a.GifSpeed != b.GifSpeed || a.GifPlaying != b.GifPlaying)
                    return false;
            }
            return true;
        }

        private static bool LayersMatch(OverlayLayer a, OverlayLayer b)
        {
            return a != null && b != null &&
                a.LayerId == b.LayerId && a.ImageId == b.ImageId && a.IsVisible == b.IsVisible &&
                a.ZIndex == b.ZIndex && a.Width == b.Width && a.Height == b.Height &&
                a.Opacity == b.Opacity && a.Scale == b.Scale && a.PositionX == b.PositionX &&
                a.PositionY == b.PositionY && a.Rotation == b.Rotation &&
                a.GifSpeed == b.GifSpeed && a.GifPlaying == b.GifPlaying;
        }

        private async Task LoadPreviewLayersAsync()
        {
            var generation = _previewLoadGeneration;
            var referencedImages = new HashSet<string>(_layers
                .Where(x => !string.IsNullOrWhiteSpace(x.ImageId))
                .Select(x => x.ImageId), StringComparer.Ordinal);
            foreach (var cachedId in _previewImageCache.Keys.Where(x => !referencedImages.Contains(x)).ToList())
                _previewImageCache.Remove(cachedId);

            foreach (var layer in _layers.ToList())
            {
                if (_cleanedUp || generation != _previewLoadGeneration) return;
                var item = _library.FirstOrDefault(x => string.Equals(x.Id, layer.ImageId, StringComparison.Ordinal));
                if (item == null)
                {
                    layer.ImageSource = null;
                    layer.DisplayName = "Missing image";
                    continue;
                }

                layer.DisplayName = item.DisplayName;
                if (!layer.IsVisible || layer.ImageSource != null) continue;
                try
                {
                    if (!_previewImageCache.TryGetValue(item.Id, out var bitmapSource))
                    {
                        var bitmap = await _images.LoadBitmapAsync(item, 768);
                        if (_cleanedUp || generation != _previewLoadGeneration ||
                            !_layers.Contains(layer) || !string.Equals(layer.ImageId, item.Id, StringComparison.Ordinal))
                            return;
                        bitmapSource = bitmap;
                        _previewImageCache[item.Id] = bitmapSource;
                    }

                    if (_cleanedUp || generation != _previewLoadGeneration ||
                        !_layers.Contains(layer) || !string.Equals(layer.ImageId, item.Id, StringComparison.Ordinal))
                        return;
                    if (layer.Width <= 0 || layer.Height <= 0)
                    {
                        var dimensions = bitmapSource as Windows.UI.Xaml.Media.Imaging.BitmapImage;
                        double sourceWidth = Math.Max(1, dimensions?.PixelWidth ?? 768);
                        double sourceHeight = Math.Max(1, dimensions?.PixelHeight ?? 768);
                        double fit = Math.Min(1d, Math.Min(240d / sourceWidth, 140d / sourceHeight));
                        layer.Width = Math.Max(1, sourceWidth * fit);
                        layer.Height = Math.Max(1, sourceHeight * fit);
                    }
                    layer.ImageSource = bitmapSource;
                }
                catch (Exception ex)
                {
                    AppDiagnostics.Log("Settings layer preview", ex);
                    if (generation == _previewLoadGeneration) layer.ImageSource = null;
                }
            }

            EmptyPreview.Visibility = _layers.Any(x => x.IsVisible && x.ImageSource != null)
                ? Visibility.Collapsed : Visibility.Visible;
            UpdatePreviewPositions();
        }

        private void UpdatePreviewPositions()
        {
            foreach (var layer in _layers)
                layer.UpdateCanvasPosition(PreviewSurface.ActualWidth, PreviewSurface.ActualHeight);
        }

        private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            PreviewSurface.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
            UpdatePreviewPositions();
        }

        private void RefreshLibraryView()
        {
            var selected = LibraryFilterSelector.SelectedItem as ComboBoxItem;
            string filter = selected?.Tag as string ?? "all";
            IEnumerable<ImageRecord> items = _library;
            if (filter == "favorites") items = items.Where(x => x.IsFavorite);
            else if (filter == "gif") items = items.Where(x => x.IsGif);

            _libraryView.Clear();
            foreach (var item in items) _libraryView.Add(item);
        }

        private void LibraryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_library == null || LibraryList == null) return;
            RefreshLibraryView();
        }

        private void UpdateControls()
        {
            var active = _state.SelectedLayer;
            bool hasLayer = active != null;
            OpacitySlider.IsEnabled = hasLayer;
            ScaleSlider.IsEnabled = hasLayer;
            RotationSlider.IsEnabled = hasLayer;
            PositionXBox.IsEnabled = hasLayer;
            PositionYBox.IsEnabled = hasLayer;
            GifSpeedSlider.IsEnabled = hasLayer && _library.FirstOrDefault(x => x.Id == active.ImageId)?.IsGif == true;
            GifControls.Visibility = hasLayer && _library.FirstOrDefault(x => x.Id == active.ImageId)?.IsGif == true
                ? Visibility.Visible : Visibility.Collapsed;
            bool hasWidget = _widgetControl != null;
            HideOverlayButton.IsEnabled = hasWidget;
            ShowOverlayButton.IsEnabled = hasWidget;
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var item = await _images.PickAndImportAsync();
                if (item == null || _cleanedUp) return;
                bool added = await SettingsStore.Current.AddImageAndSelectAsync(item, _state);
                if (!added) ShowError("The image was imported into the library, but the scene already has 30 layers.");
            }
            catch (Exception ex) { ShowError("Cannot import this image: " + ex.Message); }
        }

        private async void Page_Drop(object sender, DragEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var file = items.OfType<StorageFile>().FirstOrDefault();
                if (file == null || _cleanedUp) return;
                var item = await _images.ImportAsync(file);
                if (_cleanedUp) return;
                bool added = await SettingsStore.Current.AddImageAndSelectAsync(item, _state);
                if (!added) ShowError("The file was imported into the library, but the scene already has 30 layers.");
            }
            catch (Exception ex) { ShowError("Cannot import dropped file: " + ex.Message); }
            finally { deferral.Complete(); }
        }

        private void Page_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy : DataPackageOperation.None;
        }

        private void Library_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringLibrarySelection) return;
            _selectedLibraryItem = LibraryList.SelectedItem as ImageRecord;
            AddLayerButtonEnabled();
        }

        private void AddLayerButtonEnabled()
        {
            // The button is also guarded in its click handler; this method exists to keep
            // selection changes side-effect free.
        }

        private async void AddLayer_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedLibraryItem == null) return;
            try
            {
                if (!SettingsStore.Current.AddLayer(_selectedLibraryItem.Id))
                {
                    ShowError(_state.Layers.Count >= OverlayState.MaxLayers
                        ? "The scene already contains 30 layers."
                        : "The selected library item could not be added.");
                    return;
                }
                await SettingsStore.Current.FlushAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Add layer", ex); ShowError("Could not add layer: " + ex.Message); }
        }

        private async void LayerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringLayerSelection || !(LayerList.SelectedItem is OverlayLayer layer)) return;
            try
            {
                _state.SelectedLayerId = layer.LayerId;
                SettingsStore.Current.UpdateState(_state);
                await SettingsStore.Current.FlushAsync();
                UpdateControls();
            }
            catch (Exception ex) { AppDiagnostics.Log("Select layer", ex); }
        }

        private async void ToggleLayerVisibility_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is OverlayLayer item)) return;
            try
            {
                var layer = _state.Layers.FirstOrDefault(x => x.LayerId == item.LayerId);
                if (layer == null) return;
                layer.IsVisible = !layer.IsVisible;
                SettingsStore.Current.UpdateState(_state);
                await SettingsStore.Current.FlushAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Toggle layer visibility", ex); }
        }

        private async void MoveLayerUp_Click(object sender, RoutedEventArgs e) => await MoveLayerAsync(sender, 1);
        private async void MoveLayerDown_Click(object sender, RoutedEventArgs e) => await MoveLayerAsync(sender, -1);

        private async Task MoveLayerAsync(object sender, int direction)
        {
            if (!(((FrameworkElement)sender).Tag is OverlayLayer item)) return;
            try
            {
                if (SettingsStore.Current.MoveLayer(item.LayerId, direction))
                    await SettingsStore.Current.FlushAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Reorder layer", ex); }
        }

        private async void RemoveLayer_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is OverlayLayer item)) return;
            try
            {
                SettingsStore.Current.RemoveLayer(item.LayerId);
                await SettingsStore.Current.FlushAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Remove layer", ex); }
        }

        private async void ToggleFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is ImageRecord item)) return;
            try
            {
                SettingsStore.Current.ToggleFavorite(item.Id);
                await SettingsStore.Current.FlushAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Toggle image favorite", ex); }
        }

        private async void RemoveImage_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is ImageRecord item)) return;
            try
            {
                await _images.DeleteAsync(item);
                await SettingsStore.Current.FlushAsync();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Remove library image", ex);
                ShowError("Cannot remove this image: " + ex.Message);
            }
        }

        private async void HideOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (_widgetControl == null) return;
            try { await _widgetControl.MinimizeAsync("PixelOverlay"); }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Minimize overlay from settings", ex);
                ShowError("The overlay could not be hidden: " + ex.Message);
            }
        }

        private async void ShowOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (_widgetControl == null) return;
            try { await _widgetControl.RestoreAsync("PixelOverlay"); }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Restore overlay from settings", ex);
                ShowError("The overlay could not be restored: " + ex.Message);
            }
        }

        private void Center_Click(object sender, RoutedEventArgs e) => _state.Center();
        private void Reset_Click(object sender, RoutedEventArgs e) => _state.ResetTransform();
        private void ResetRotation_Click(object sender, RoutedEventArgs e) => _state.Rotation = 0;

        private void Preview_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_state.SelectedLayer == null) return;
            _dragging = true;
            _dragStart = e.GetCurrentPoint(PreviewSurface).Position;
            _originX = _state.PositionX;
            _originY = _state.PositionY;
            PreviewSurface.CapturePointer(e.Pointer);
        }

        private void Preview_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging || _state.SelectedLayer == null) return;
            var point = e.GetCurrentPoint(PreviewSurface).Position;
            _state.PositionX = Math.Round(_originX + point.X - _dragStart.X);
            _state.PositionY = Math.Round(_originY + point.Y - _dragStart.Y);
        }

        private void Preview_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _dragging = false;
            PreviewSurface.ReleasePointerCapture(e.Pointer);
        }

        private void Preview_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => _dragging = false;
        private void Preview_PointerCanceled(object sender, PointerRoutedEventArgs e) => _dragging = false;

        private async void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            var name = ProfileNameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowError("Enter a composition preset name first.");
                return;
            }
            SettingsStore.Current.SaveProfile(_state.Snapshot(name));
            ProfileNameBox.Text = string.Empty;
            try { await SettingsStore.Current.FlushAsync(); }
            catch (Exception ex) { AppDiagnostics.Log("Save composition preset", ex); ShowError("Could not save preset: " + ex.Message); }
        }

        private async void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is OverlayProfile profile)) return;
            try
            {
                _state.Apply(profile);
                SettingsStore.Current.UpdateState(_state);
                await SettingsStore.Current.FlushAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Apply composition preset", ex); ShowError("Could not apply preset: " + ex.Message); }
        }

        private async void RemovePreset_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is OverlayProfile profile)) return;
            SettingsStore.Current.RemoveProfile(profile.Name);
            try { await SettingsStore.Current.FlushAsync(); }
            catch (Exception ex) { AppDiagnostics.Log("Remove composition preset", ex); }
        }

        private void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private async void ShowError(string message)
        {
            if (_dialogOpen || _cleanedUp || XamlRoot == null) return;
            _dialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Pixel Overlay",
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Show error dialog", ex); }
            finally { _dialogOpen = false; }
        }
    }
}