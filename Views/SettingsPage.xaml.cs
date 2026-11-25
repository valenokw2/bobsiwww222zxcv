using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.ComponentModel;
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
        private ObservableCollection<ImageRecord> _viewLibrary;
        private ObservableCollection<OverlayProfile> _viewProfiles;
        private OverlayState _viewState;
        private ImageRecord _selected;
        private bool _dragging;
        private Windows.Foundation.Point _dragStart;
        private double _originX, _originY;
        private int _previewLoadGeneration;
        private bool _restoringSelection;
        private bool _applyingSnapshot;
        private bool _cleaned;
        private bool _showingDialog;
        private int _batchUpdates;
        private long _snapshotRevision = -1;
        private SettingsStore.SettingsSubscription _subscription;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_subscription != null) return;
            _cleaned = false;
            try
            {
                var store = SettingsStore.Current;
                await store.LoadAsync();

                _viewState = new OverlayState();
                _viewLibrary = new ObservableCollection<ImageRecord>();
                _viewProfiles = new ObservableCollection<OverlayProfile>();
                DataContext = _viewState;
                LibraryList.ItemsSource = _viewLibrary;
                ProfileList.ItemsSource = _viewProfiles;

                _viewState.PropertyChanged += ViewState_PropertyChanged;
                _viewLibrary.CollectionChanged += ViewCollections_CollectionChanged;
                _viewProfiles.CollectionChanged += ViewCollections_CollectionChanged;

                _subscription = store.Subscribe(Dispatcher, ApplyStoreSnapshot);
                ApplyStoreSnapshot(store.GetSnapshot());
            }
            catch (Exception ex) { AppDiagnostics.Log("Settings page load", ex); }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e) => Cleanup();

        // Also called by Window.Closed because Game Bar can close a CoreWindow without Unloaded.
        public void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;
            _subscription?.Dispose();
            _subscription = null;
            if (_viewState != null) _viewState.PropertyChanged -= ViewState_PropertyChanged;
            if (_viewLibrary != null) _viewLibrary.CollectionChanged -= ViewCollections_CollectionChanged;
            if (_viewProfiles != null) _viewProfiles.CollectionChanged -= ViewCollections_CollectionChanged;
            _previewLoadGeneration++;
            _dragging = false;
            PreviewImage.Source = null;
        }

        private void ApplyStoreSnapshot(PersistedSettings snapshot)
        {
            if (_cleaned || snapshot == null || snapshot.Revision <= _snapshotRevision || _viewState == null) return;
            _snapshotRevision = snapshot.Revision;
            string previousSelectedId = _selected?.Id;
            _applyingSnapshot = true;
            try
            {
                _viewState.CopyFrom(snapshot.State);
                Replace(_viewLibrary, snapshot.Library);
                Replace(_viewProfiles, snapshot.Profiles);

                _selected = _viewLibrary.FirstOrDefault(x =>
                    x != null && string.Equals(x.Id, _viewState.SelectedImageId, StringComparison.Ordinal));
                _restoringSelection = true;
                try { LibraryList.SelectedItem = _selected; }
                finally { _restoringSelection = false; }
            }
            finally { _applyingSnapshot = false; }

            UpdatePreviewTransform();
            UpdateControls();
            if (!string.Equals(previousSelectedId, _selected?.Id, StringComparison.Ordinal))
                _ = LoadPreviewSafelyAsync();
        }

        private static void Replace<T>(ObservableCollection<T> target, System.Collections.Generic.IEnumerable<T> source)
        {
            target.Clear();
            if (source == null) return;
            foreach (var item in source) target.Add(item);
        }

        private void ViewState_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (!_applyingSnapshot && _batchUpdates == 0) CommitView();
            UpdatePreviewTransform();
            UpdateControls();
        }

        private void ViewCollections_CollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_applyingSnapshot && _batchUpdates == 0) CommitView();
        }

        private void CommitView()
        {
            if (_cleaned || _applyingSnapshot || _viewState == null) return;
            try
            {
                _snapshotRevision = SettingsStore.Current.UpdateSnapshot(new PersistedSettings
                {
                    State = _viewState,
                    Library = _viewLibrary.ToList(),
                    Profiles = _viewProfiles.ToList()
                }, _subscription);
            }
            catch (Exception ex) { AppDiagnostics.Log("Commit settings view", ex); }
        }

        private async Task LoadPreviewSafelyAsync()
        {
            try { await LoadPreviewAsync(); }
            catch (Exception ex) { AppDiagnostics.Log("Preview load", ex); }
        }

        private async Task LoadPreviewAsync()
        {
            var item = _selected;
            var generation = ++_previewLoadGeneration;
            PreviewImage.Source = null;
            if (item == null)
            {
                EmptyPreview.Text = "PNG, JPG, JPEG or GIF" + Environment.NewLine + "Drop here or import an image";
                EmptyPreview.Visibility = Visibility.Visible;
                UpdatePreviewTransform();
                UpdateControls();
                return;
            }

            try
            {
                var bitmap = await _images.LoadBitmapAsync(item, 1280);
                if (_cleaned || generation != _previewLoadGeneration || !ReferenceEquals(_selected, item)) return;
                PreviewImage.Source = bitmap;
                EmptyPreview.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Settings image preview", ex);
                if (_cleaned || generation != _previewLoadGeneration || !ReferenceEquals(_selected, item)) return;
                PreviewImage.Source = null;
                EmptyPreview.Text = "Image could not be opened";
                EmptyPreview.Visibility = Visibility.Visible;
            }
            UpdatePreviewTransform();
            UpdateControls();
        }

        private void UpdatePreviewTransform()
        {
            if (_viewState == null) return;
            PreviewTransform.CenterX = PreviewSurface.ActualWidth / 2d;
            PreviewTransform.CenterY = PreviewSurface.ActualHeight / 2d;
            PreviewSurface.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, PreviewSurface.ActualWidth, PreviewSurface.ActualHeight)
            };
            PreviewImage.Opacity = _viewState.Opacity / 100d;
            PreviewTransform.ScaleX = PreviewTransform.ScaleY = _viewState.Scale / 100d;
            PreviewTransform.TranslateX = _viewState.PositionX;
            PreviewTransform.TranslateY = _viewState.PositionY;
            PreviewTransform.Rotation = _viewState.Rotation;
        }

        private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewTransform();

        private void UpdateControls()
        {
            if (_viewState == null) return;
            GifControls.Visibility = _selected?.IsGif == true ? Visibility.Visible : Visibility.Collapsed;
            PlayPauseButton.Content = _viewState.GifPlaying ? "Pause GIF" : "Play GIF";
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var item = await _images.PickAndImportAsync();
                if (item == null) return;
                _batchUpdates++;
                try
                {
                    _viewLibrary.Insert(0, item);
                    _selected = item;
                    _restoringSelection = true;
                    try { LibraryList.SelectedItem = item; }
                    finally { _restoringSelection = false; }
                    _viewState.SelectedImageId = item.Id;
                }
                finally
                {
                    _batchUpdates--;
                    CommitView();
                }
                await LoadPreviewAsync();
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
                if (file == null) return;
                var item = await _images.ImportAsync(file);
                _batchUpdates++;
                try
                {
                    _viewLibrary.Insert(0, item);
                    _selected = item;
                    _restoringSelection = true;
                    try { LibraryList.SelectedItem = item; }
                    finally { _restoringSelection = false; }
                    _viewState.SelectedImageId = item.Id;
                }
                finally
                {
                    _batchUpdates--;
                    CommitView();
                }
                await LoadPreviewAsync();
            }
            catch (Exception ex) { ShowError("Cannot import dropped file: " + ex.Message); }
            finally { deferral.Complete(); }
        }

        private void Page_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy
                : DataPackageOperation.None;
        }

        private async void Library_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_restoringSelection || !(LibraryList.SelectedItem is ImageRecord item)) return;
                _selected = item;
                _viewState.SelectedImageId = item.Id;
                await LoadPreviewAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Library selection changed", ex); }
        }

        private async void RemoveImage_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is ImageRecord item)) return;
            try
            {
                // GIF decoding now uses a bounded in-memory copy, so deleting the source file
                // does not race an open file handle owned by the overlay window.
                await _images.DeleteAsync(item);
                _batchUpdates++;
                try
                {
                    _viewLibrary.Remove(item);
                    if (string.Equals(_viewState.SelectedImageId, item.Id, StringComparison.Ordinal))
                    {
                        _viewState.SelectedImageId = null;
                        _selected = null;
                        LibraryList.SelectedItem = null;
                    }
                }
                finally
                {
                    _batchUpdates--;
                    CommitView();
                }
                await LoadPreviewAsync();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Remove library image", ex);
                ShowError("Cannot remove this image: " + ex.Message);
            }
        }

        private void Center_Click(object sender, RoutedEventArgs e) => _viewState?.Center();
        private void Reset_Click(object sender, RoutedEventArgs e) => _viewState?.ResetTransform();
        private void ResetRotation_Click(object sender, RoutedEventArgs e) { if (_viewState != null) _viewState.Rotation = 0; }
        private void PlayPause_Click(object sender, RoutedEventArgs e) { if (_viewState != null) _viewState.GifPlaying = !_viewState.GifPlaying; }

        private void Preview_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_viewState == null) return;
            _dragging = true;
            _dragStart = e.GetCurrentPoint(PreviewSurface).Position;
            _originX = _viewState.PositionX;
            _originY = _viewState.PositionY;
            PreviewSurface.CapturePointer(e.Pointer);
        }

        private void Preview_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging || _viewState == null) return;
            var point = e.GetCurrentPoint(PreviewSurface).Position;
            _batchUpdates++;
            try
            {
                _viewState.PositionX = Math.Round(_originX + point.X - _dragStart.X);
                _viewState.PositionY = Math.Round(_originY + point.Y - _dragStart.Y);
            }
            finally
            {
                _batchUpdates--;
                CommitView();
            }
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
            if (string.IsNullOrWhiteSpace(name)) { ShowError("Enter a preset name first."); return; }
            _viewProfiles.Add(_viewState.Snapshot(name));
            ProfileNameBox.Text = string.Empty;
        }

        private void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is OverlayProfile profile) _viewState?.Apply(profile);
        }

        private void RemovePreset_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is OverlayProfile profile) _viewProfiles.Remove(profile);
        }

        private void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private async void ShowError(string message)
        {
            if (_showingDialog || _cleaned) return;
            _showingDialog = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "Pixel Overlay",
                    Content = message,
                    CloseButtonText = "OK"
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Show error dialog", ex); }
            finally { _showingDialog = false; }
        }
    }
}