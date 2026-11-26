using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using Muxc = Microsoft.UI.Xaml.Controls;
using PixelOverlay.Models;
using PixelOverlay.Services;
using PixelOverlay.Services.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.System;

namespace PixelOverlay.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly ImageLibraryService _images = new ImageLibraryService();
        private readonly ObservableCollection<ImageRecord> _library = new ObservableCollection<ImageRecord>();
        private readonly ObservableCollection<ImageRecord> _libraryView = new ObservableCollection<ImageRecord>();
        private readonly ObservableCollection<string> _windowSlots = new ObservableCollection<string>();
        private readonly ObservableCollection<MediaSearchResult> _mediaResults = new ObservableCollection<MediaSearchResult>();
        private readonly ObservableCollection<OverlayProfile> _profiles = new ObservableCollection<OverlayProfile>();
        private XboxGameBarWidget _widget;
        private string _windowId = WindowSlotIds.FirstWindowId;
        private bool _updatingWindowSlots;
        private bool _updatingLibraryView;
        private IMediaSearchProvider _giphyProvider;
        private XboxGameBarWidgetControl _widgetControl;
        private OverlayState _state = new OverlayState();
        private ImageRecord _selected;
        private bool _dragging;
        private Windows.Foundation.Point _dragStart;
        private double _originX, _originY;
        private int _previewLoadGeneration;
        private bool _restoringSelection;
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
                ProfileList.ItemsSource = _profiles;
                GiphyResultsList.ItemsSource = _mediaResults;
                _state.PropertyChanged += LocalState_PropertyChanged;
                SettingsStore.Current.StateChanged += Store_StateChanged;
                RefreshWindowSlots();
                GiphyApiKeyBox.Text = ApplicationData.Current.LocalSettings.Values["GiphyApiKey"] as string ?? string.Empty;
                if (RootNavigation.MenuItems.Count > 0)
                    RootNavigation.SelectedItem = RootNavigation.MenuItems[0];
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
        }

        private void LocalState_PropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (_cleanedUp || _applyingStoreSnapshot) return;
            // Derived text labels do not change persisted state; ignore their notifications.
            if (e.PropertyName == nameof(OverlayState.OpacityText) ||
                e.PropertyName == nameof(OverlayState.ScaleText) ||
                e.PropertyName == nameof(OverlayState.RotationText) ||
                e.PropertyName == nameof(OverlayState.GifSpeedText))
                return;
            try
            {
                SettingsStore.Current.UpdateState(_windowId, _state);
                UpdatePreviewTransform();
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
                    {
                        RefreshWindowSlots();
                        _ = ApplyStoreSnapshotSafelyAsync(SettingsStore.Current.GetSnapshot());
                    }
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
            var previousId = _selected?.Id;
            _applyingStoreSnapshot = true;
            try
            {
                _state.CopyFrom(SettingsStore.Current.GetWindowState(_windowId));

                // Slider changes are frequent. Keep existing XAML item containers and
                // selection stable unless the underlying library actually changed.
                if (!LibraryMatches(snapshot.Library))
                {
                    _library.Clear();
                    if (snapshot.Library != null)
                        foreach (var item in snapshot.Library)
                            if (item != null) _library.Add(item.Clone());
                }

                if (!ProfilesMatch(snapshot.Profiles))
                {
                    _profiles.Clear();
                    if (snapshot.Profiles != null)
                        foreach (var profile in snapshot.Profiles)
                            if (profile != null) _profiles.Add(profile.Clone());
                }

                _selected = _library.FirstOrDefault(x =>
                    string.Equals(x.Id, _state.SelectedImageId, StringComparison.Ordinal));
                RefreshLibraryFilter();
                _restoringSelection = true;
                try { LibraryList.SelectedItem = _libraryView.FirstOrDefault(x => string.Equals(x.Id, _selected?.Id, StringComparison.Ordinal)); }
                finally { _restoringSelection = false; }
            }
            finally { _applyingStoreSnapshot = false; }

            UpdatePreviewTransform();
            UpdateControls();
            if (forcePreviewReload || !string.Equals(previousId, _selected?.Id, StringComparison.Ordinal))
                await LoadPreviewAsync();
        }

        private bool LibraryMatches(IList<ImageRecord> source)
        {
            int sourceCount = source == null ? 0 : source.Count;
            if (_library.Count != sourceCount) return false;

            for (int i = 0; i < sourceCount; i++)
            {
                var left = _library[i];
                var right = source[i];
                if (left == null || right == null) return false;
                if (!string.Equals(left.Id, right.Id, StringComparison.Ordinal) ||
                    !string.Equals(left.StoredName, right.StoredName, StringComparison.Ordinal) ||
                    !string.Equals(left.DisplayName, right.DisplayName, StringComparison.Ordinal) ||
                    !string.Equals(left.Extension, right.Extension, StringComparison.OrdinalIgnoreCase) ||
                    left.AddedAt != right.AddedAt)
                    return false;
            }

            return true;
        }

        private bool ProfilesMatch(IList<OverlayProfile> source)
        {
            int sourceCount = source == null ? 0 : source.Count;
            if (_profiles.Count != sourceCount) return false;

            for (int i = 0; i < sourceCount; i++)
            {
                var left = _profiles[i];
                var right = source[i];
                if (left == null || right == null) return false;
                if (!string.Equals(left.Name, right.Name, StringComparison.Ordinal) ||
                    left.Opacity != right.Opacity ||
                    left.Scale != right.Scale ||
                    left.PositionX != right.PositionX ||
                    left.PositionY != right.PositionY ||
                    left.Rotation != right.Rotation ||
                    left.GifSpeed != right.GifSpeed ||
                    left.GifPlaying != right.GifPlaying)
                    return false;
            }

            return true;
        }

        private async Task LoadPreviewAsync()
        {
            var item = _selected;
            var generation = ++_previewLoadGeneration;
            PreviewImage.Source = null;
            if (item == null)
            {
                EmptyPreview.Text = "PNG, JPG, JPEG, WEBP or GIF\nDrop here or import an image";
                EmptyPreview.Visibility = Visibility.Visible;
                UpdatePreviewTransform();
                UpdateControls();
                return;
            }

            try
            {
                var bitmap = await _images.LoadBitmapAsync(item, 1920);
                if (_cleanedUp || generation != _previewLoadGeneration ||
                    !string.Equals(_selected?.Id, item.Id, StringComparison.Ordinal)) return;
                PreviewImage.Source = bitmap;
                EmptyPreview.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Settings image preview", ex);
                if (_cleanedUp || generation != _previewLoadGeneration ||
                    !string.Equals(_selected?.Id, item.Id, StringComparison.Ordinal)) return;
                PreviewImage.Source = null;
                EmptyPreview.Text = "Image could not be opened";
                EmptyPreview.Visibility = Visibility.Visible;
            }

            UpdatePreviewTransform();
            UpdateControls();
        }

        private void UpdatePreviewTransform()
        {
            if (PreviewTransform == null || PreviewSurface == null) return;
            PreviewTransform.CenterX = PreviewSurface.ActualWidth / 2d;
            PreviewTransform.CenterY = PreviewSurface.ActualHeight / 2d;
            PreviewImage.Opacity = _state.Opacity / 100d;
            PreviewTransform.ScaleX = PreviewTransform.ScaleY = _state.Scale / 100d;
            PreviewTransform.TranslateX = _state.PositionX;
            PreviewTransform.TranslateY = _state.PositionY;
            PreviewTransform.Rotation = _state.Rotation;
        }

        private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            PreviewSurface.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };
            UpdatePreviewTransform();
        }

        private void UpdateControls()
        {
            if (GifControls == null || PlayPauseButton == null) return;
            GifControls.Visibility = _selected?.IsGif == true ? Visibility.Visible : Visibility.Collapsed;
            PlayPauseButton.Content = _state.GifPlaying ? "Pause GIF" : "Play GIF";
            HideOverlayButton.IsEnabled = _widgetControl != null;
            ShowOverlayButton.IsEnabled = _widgetControl != null;
            WindowSlotSelector.IsEnabled = _windowSlots.Count > 0;
            WindowSummaryText.Text = _selected == null ? "No image selected" : "Image: " + _selected.DisplayName;
            OverlayWindowLabel.Text = "Editing " + _windowId;
            var config = SettingsStore.Current.GetWindowConfig(_windowId);
            WindowVisibilityStatusText.Text = config.IsHidden ? "Visibility: hidden" : "Visibility: visible";
            ClickThroughStatusText.Text = _widget == null ? "Settings opened outside Game Bar." :
                "Settings widget click-through: " + (_widget.ClickThroughEnabled ? "enabled" : "disabled");
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var item = await _images.PickAndImportAsync();
                if (item == null || _cleanedUp) return;
                var nextState = _state.Clone();
                nextState.SelectedImageId = item.Id;
                await SettingsStore.Current.AddImageAndSelectAsync(item, _windowId, nextState);
            }
            catch (Exception ex) { ShowError("Cannot import this image: " + ex.Message); }
        }

        private async void Page_Drop(object sender, DragEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                ImageRecord item = null;
                if (e.DataView.Contains(StandardDataFormats.StorageItems))
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    var file = items.OfType<StorageFile>().FirstOrDefault();
                    if (file != null) item = await _images.ImportAsync(file);
                }
                else
                {
                    Uri uri = null;
                    if (e.DataView.Contains(StandardDataFormats.WebLink))
                        uri = await e.DataView.GetWebLinkAsync();
                    else if (e.DataView.Contains(StandardDataFormats.Uri))
                        uri = await e.DataView.GetUriAsync();
                    if (uri != null) item = await _images.ImportFromUriAsync(uri);
                }

                if (item == null || _cleanedUp) return;
                var nextState = _state.Clone();
                nextState.SelectedImageId = item.Id;
                await SettingsStore.Current.AddImageAndSelectAsync(item, _windowId, nextState);
            }
            catch (Exception ex) { ShowError("Cannot import dropped item: " + ex.Message); }
            finally { deferral.Complete(); }
        }

        private void Page_DragOver(object sender, DragEventArgs e)
        {
            bool supported = e.DataView.Contains(StandardDataFormats.StorageItems) ||
                e.DataView.Contains(StandardDataFormats.WebLink) ||
                e.DataView.Contains(StandardDataFormats.Uri);
            e.AcceptedOperation = supported ? DataPackageOperation.Copy : DataPackageOperation.None;
        }

        private async void Library_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_restoringSelection || _updatingLibraryView || _applyingStoreSnapshot || !(LibraryList.SelectedItem is ImageRecord item)) return;
                _selected = item;
                _state.SelectedImageId = item.Id;
                SettingsStore.Current.UpdateState(_windowId, _state);
                // Force the pending debounce to disk before returning to the game or closing Settings.
                await SettingsStore.Current.FlushAsync();
                RefreshLibraryFilter();
                await LoadPreviewAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Library selection changed", ex); }
        }

        private async void RemoveImage_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is ImageRecord item)) return;
            try
            {
                await _images.DeleteAsync(item);
                if (!_cleanedUp)
                    await ApplyStoreSnapshotAsync(SettingsStore.Current.GetSnapshot(), false);
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
            try
            {
                await _widgetControl.MinimizeAsync(WindowSlotIds.GetExtensionId(_windowId));
                SettingsStore.Current.SetWindowHidden(_windowId, true);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Minimize overlay from settings", ex);
                ShowError("The overlay could not be hidden: " + ex.Message);
            }
        }

        private async void ShowOverlay_Click(object sender, RoutedEventArgs e)
        {
            if (_widgetControl == null) return;
            try
            {
                await _widgetControl.RestoreAsync(WindowSlotIds.GetExtensionId(_windowId));
                SettingsStore.Current.SetWindowHidden(_windowId, false);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Restore overlay from settings", ex);
                ShowError("The overlay could not be restored: " + ex.Message);
            }
        }

        private void RefreshWindowSlots()
        {
            if (_cleanedUp || WindowSlotSelector == null) return;
            var ids = SettingsStore.Current.GetWindowIds().ToList();
            if (!ids.Contains(WindowSlotIds.FirstWindowId, StringComparer.Ordinal))
                ids.Insert(0, WindowSlotIds.FirstWindowId);
            if (_windowSlots.SequenceEqual(ids, StringComparer.Ordinal)) return;

            _updatingWindowSlots = true;
            try
            {
                _windowSlots.Clear();
                foreach (var id in ids) _windowSlots.Add(id);
                WindowSlotSelector.ItemsSource = _windowSlots;
                if (!_windowSlots.Contains(_windowId))
                    _windowId = WindowSlotIds.FirstWindowId;
                WindowSlotSelector.SelectedItem = _windowId;
            }
            finally { _updatingWindowSlots = false; }
        }

        private async void WindowSlotSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingWindowSlots || !(WindowSlotSelector.SelectedItem is string selected) ||
                !WindowSlotIds.IsValid(selected) || string.Equals(selected, _windowId, StringComparison.Ordinal))
                return;

            _windowId = selected;
            _selected = null;
            try { await ApplyStoreSnapshotAsync(SettingsStore.Current.GetSnapshot(), true); }
            catch (Exception ex) { AppDiagnostics.Log("Switch overlay slot", ex); }
        }

        private async void CreateWindow_Click(object sender, RoutedEventArgs e)
        {
            var created = SettingsStore.Current.CreateWindow(_windowId, false);
            if (created == null)
            {
                ShowError("The maximum of 32 overlay windows has been reached.");
                return;
            }
            RefreshWindowSlots();
            WindowSlotSelector.SelectedItem = created;
            _windowId = created;
            await ApplyStoreSnapshotAsync(SettingsStore.Current.GetSnapshot(), true);
        }

        private async void CloneWindow_Click(object sender, RoutedEventArgs e)
        {
            var created = SettingsStore.Current.CreateWindow(_windowId, true);
            if (created == null)
            {
                ShowError("The maximum of 32 overlay windows has been reached.");
                return;
            }
            RefreshWindowSlots();
            WindowSlotSelector.SelectedItem = created;
            _windowId = created;
            await ApplyStoreSnapshotAsync(SettingsStore.Current.GetSnapshot(), true);
        }

        private async void DeleteWindow_Click(object sender, RoutedEventArgs e)
        {
            if (string.Equals(_windowId, WindowSlotIds.FirstWindowId, StringComparison.Ordinal))
            {
                ShowError("The primary overlay-01 slot cannot be deleted.");
                return;
            }

            var extensionId = WindowSlotIds.GetExtensionId(_windowId);
            try
            {
                if (_widgetControl != null)
                    await _widgetControl.CloseAsync(extensionId);
                if (!SettingsStore.Current.DeleteWindow(_windowId))
                {
                    ShowError("The selected overlay slot could not be deleted.");
                    return;
                }
                _windowId = WindowSlotIds.FirstWindowId;
                RefreshWindowSlots();
                WindowSlotSelector.SelectedItem = _windowId;
                await ApplyStoreSnapshotAsync(SettingsStore.Current.GetSnapshot(), true);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Delete overlay slot", ex);
                ShowError("The selected overlay could not be closed or deleted: " + ex.Message);
            }
        }

        private async void OpenWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_widgetControl == null) return;
            try
            {
                await _widgetControl.ActivateAsync(WindowSlotIds.GetExtensionId(_windowId));
                SettingsStore.Current.SetWindowHidden(_windowId, false);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Open overlay slot", ex);
                ShowError("The selected overlay could not be opened: " + ex.Message);
            }
        }

        private async void CloseWindow_Click(object sender, RoutedEventArgs e)
        {
            if (_widgetControl == null) return;
            try
            {
                await _widgetControl.CloseAsync(WindowSlotIds.GetExtensionId(_windowId));
                SettingsStore.Current.SetWindowHidden(_windowId, true);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Close overlay slot", ex);
                ShowError("The selected overlay could not be closed: " + ex.Message);
            }
        }

        private void NavigationView_SelectionChanged(Muxc.NavigationView sender, Muxc.NavigationViewSelectionChangedEventArgs args)
        {
            var item = args.SelectedItem as Muxc.NavigationViewItem;
            var section = item?.Tag as string ?? "windows";
            WindowsSection.Visibility = section == "windows" ? Visibility.Visible : Visibility.Collapsed;
            LibrarySection.Visibility = section == "library" ? Visibility.Visible : Visibility.Collapsed;
            OverlaySection.Visibility = section == "overlay" ? Visibility.Visible : Visibility.Collapsed;
            MediaSection.Visibility = section == "media" ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RefreshLibraryFilter()
        {
            if (_cleanedUp || LibraryList == null) return;
            var query = (LibrarySearchBox.Text ?? string.Empty).Trim();
            var selectedFilter = LibraryFilterSelector.SelectedItem as ComboBoxItem;
            var filter = selectedFilter?.Tag as string ?? "all";
            var config = SettingsStore.Current.GetWindowConfig(_windowId);

            _updatingLibraryView = true;
            try
            {
                _libraryView.Clear();
                foreach (var image in _library)
                {
                    if (image == null) continue;
                    if (query.Length > 0 && (image.DisplayName ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    if (filter == "favorites" && !SettingsStore.Current.IsFavorite(_windowId, image.Id))
                        continue;
                    if (filter == "gif" && !image.IsGif)
                        continue;
                    if (filter == "current" && !string.Equals(config.SelectedImageId, image.Id, StringComparison.Ordinal))
                        continue;
                    _libraryView.Add(image);
                }
                LibraryCountText.Text = _libraryView.Count.ToString() + " images";
            }
            finally { _updatingLibraryView = false; }
        }

        private void LibrarySearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshLibraryFilter();
        private void LibraryFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshLibraryFilter();

        private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is ImageRecord item)) return;
            bool favorite = SettingsStore.Current.IsFavorite(_windowId, item.Id);
            SettingsStore.Current.SetFavorite(_windowId, item.Id, !favorite);
            RefreshLibraryFilter();
        }

        private async void ApplyLibrarySelection_Click(object sender, RoutedEventArgs e)
        {
            if (!(LibraryList.SelectedItem is ImageRecord item))
            {
                ShowError("Select an image from the library first.");
                return;
            }
            _selected = item;
            _state.SelectedImageId = item.Id;
            try
            {
                SettingsStore.Current.UpdateState(_windowId, _state);
                await SettingsStore.Current.FlushAsync();
                await LoadPreviewAsync();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Apply library image to window", ex);
                ShowError("The selected image could not be applied: " + ex.Message);
            }
        }

        private async void ImportUrl_Click(object sender, RoutedEventArgs e)
        {
            if (!Uri.TryCreate(ImageUrlBox.Text?.Trim(), UriKind.Absolute, out var uri))
            {
                ShowError("Enter a valid HTTPS image URL.");
                return;
            }
            try
            {
                var item = await _images.ImportFromUriAsync(uri);
                if (item == null || _cleanedUp) return;
                var nextState = _state.Clone();
                nextState.SelectedImageId = item.Id;
                await SettingsStore.Current.AddImageAndSelectAsync(item, _windowId, nextState);
                ImageUrlBox.Text = string.Empty;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Import direct image URL", ex);
                ShowError("The URL could not be imported: " + ex.Message);
            }
        }

        private void SaveGiphyKey_Click(object sender, RoutedEventArgs e)
        {
            var key = GiphyApiKeyBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(key))
            {
                ApplicationData.Current.LocalSettings.Values.Remove("GiphyApiKey");
                _giphyProvider = null;
                ShowError("GIPHY API key cleared.");
                return;
            }
            if (key.Length > 256)
            {
                ShowError("The API key is too long.");
                return;
            }
            ApplicationData.Current.LocalSettings.Values["GiphyApiKey"] = key;
            _giphyProvider = new GiphySearchProvider(key);
        }

        private IMediaSearchProvider GetGiphyProvider()
        {
            if (_giphyProvider != null) return _giphyProvider;
            var key = ApplicationData.Current.LocalSettings.Values["GiphyApiKey"] as string;
            if (string.IsNullOrWhiteSpace(key)) return null;
            _giphyProvider = new GiphySearchProvider(key);
            return _giphyProvider;
        }

        private async void GiphySearch_Click(object sender, RoutedEventArgs e) =>
            await QueryGiphyAsync(false);

        private async void GiphyTrending_Click(object sender, RoutedEventArgs e) =>
            await QueryGiphyAsync(true);

        private async Task QueryGiphyAsync(bool trending)
        {
            var provider = GetGiphyProvider();
            if (provider == null)
            {
                ShowError("Enter and save a GIPHY API key first.");
                return;
            }
            try
            {
                var results = trending
                    ? await provider.GetTrendingAsync(20)
                    : await provider.SearchAsync(GiphyQueryBox.Text, 20);

                _mediaResults.Clear();
                foreach (var result in results) _mediaResults.Add(result);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("GIPHY search", ex);
                ShowError("GIPHY search failed: " + ex.Message);
            }
        }

        private async void OpenMediaResult_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is MediaSearchResult result) ||
                !Uri.TryCreate(result.PageUrl, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps ||
                !(string.Equals(uri.Host, "giphy.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".giphy.com", StringComparison.OrdinalIgnoreCase)))
                return;

            try { await Launcher.LaunchUriAsync(uri); }
            catch (Exception ex) { AppDiagnostics.Log("Open GIPHY result", ex); }
        }

        private void Center_Click(object sender, RoutedEventArgs e) => _state.Center();
        private void Reset_Click(object sender, RoutedEventArgs e) => _state.ResetTransform();
        private void ResetRotation_Click(object sender, RoutedEventArgs e) => _state.Rotation = 0;
        private void PlayPause_Click(object sender, RoutedEventArgs e) => _state.GifPlaying = !_state.GifPlaying;

        private void Preview_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _dragging = true;
            _dragStart = e.GetCurrentPoint(PreviewSurface).Position;
            _originX = _state.PositionX;
            _originY = _state.PositionY;
            PreviewSurface.CapturePointer(e.Pointer);
        }

        private void Preview_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
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

        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            var name = ProfileNameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                ShowError("Enter a preset name first.");
                return;
            }
            SettingsStore.Current.SaveProfile(_state.Snapshot(name));
            ProfileNameBox.Text = string.Empty;
        }

        private void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is OverlayProfile profile)
                _state.Apply(profile);
        }

        private void RemovePreset_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is OverlayProfile profile)
                SettingsStore.Current.RemoveProfile(profile.Name);
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
