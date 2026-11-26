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
        private readonly ObservableCollection<OverlayProfile> _profiles = new ObservableCollection<OverlayProfile>();
        private XboxGameBarWidget _widget;
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
                LibraryList.ItemsSource = _library;
                ProfileList.ItemsSource = _profiles;
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
                SettingsStore.Current.UpdateState(_state);
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
            var previousId = _selected?.Id;
            _applyingStoreSnapshot = true;
            try
            {
                _state.CopyFrom(snapshot.State ?? new OverlayState());

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
                _restoringSelection = true;
                try { LibraryList.SelectedItem = _selected; }
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
                EmptyPreview.Text = "PNG, JPG, JPEG or GIF\nDrop here or import an image";
                EmptyPreview.Visibility = Visibility.Visible;
                UpdatePreviewTransform();
                UpdateControls();
                return;
            }

            try
            {
                var bitmap = await _images.LoadBitmapAsync(item, 1280);
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
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var item = await _images.PickAndImportAsync();
                if (item == null || _cleanedUp) return;
                var nextState = _state.Clone();
                nextState.SelectedImageId = item.Id;
                await SettingsStore.Current.AddImageAndSelectAsync(item, nextState);
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
                var nextState = _state.Clone();
                nextState.SelectedImageId = item.Id;
                await SettingsStore.Current.AddImageAndSelectAsync(item, nextState);
            }
            catch (Exception ex) { ShowError("Cannot import dropped file: " + ex.Message); }
            finally { deferral.Complete(); }
        }

        private void Page_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
                ? DataPackageOperation.Copy : DataPackageOperation.None;
        }

        private async void Library_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                if (_restoringSelection || !(LibraryList.SelectedItem is ImageRecord item)) return;
                _selected = item;
                _state.SelectedImageId = item.Id;
                // The selection handler synchronously updates the store; force the pending
                // debounce to disk before returning to the game or closing Settings.
                await SettingsStore.Current.FlushAsync();
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
