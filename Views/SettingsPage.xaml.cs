using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Gaming.XboxGameBar;
using PixelOverlay.Models;
using PixelOverlay.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.Input;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;

namespace PixelOverlay.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly ImageLibraryService _images = new ImageLibraryService();
        private XboxGameBarWidget _widget;
        private ImageRecord _selected;
        private bool _dragging;
        private Windows.Foundation.Point _dragStart;
        private double _originX, _originY;
        private int _previewLoadGeneration;
        private bool _restoringSelection;

        public SettingsPage()
        {
            InitializeComponent();
            Loaded += OnLoaded; Unloaded += OnUnloaded;
        }
        protected override void OnNavigatedTo(Windows.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            _widget = e.Parameter as XboxGameBarWidget;
            base.OnNavigatedTo(e);
        }
        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                DataContext = SettingsStore.Current.State;
                LibraryList.DataContext = SettingsStore.Current;
                ProfileList.DataContext = SettingsStore.Current;
                SettingsStore.Current.StateChanged += Store_StateChanged;
                await RestoreSelectionAsync();
                UpdateControls();
            }
            catch (Exception ex) { AppDiagnostics.Log("Settings page load", ex); }
        }
        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            SettingsStore.Current.StateChanged -= Store_StateChanged;
            _previewLoadGeneration++;
            _dragging = false;
        }
        private async void Store_StateChanged(object sender, EventArgs e)
        {
            try
            {
                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
                {
                    try { UpdatePreviewTransform(); UpdateControls(); }
                    catch (Exception ex) { AppDiagnostics.Log("Settings state UI update", ex); }
                });
            }
            catch (Exception ex) { AppDiagnostics.Log("Settings state dispatch", ex); }
        }
        private async Task RestoreSelectionAsync()
        {
            _selected = _images.Selected();
            _restoringSelection = true;
            try { LibraryList.SelectedItem = _selected; }
            finally { _restoringSelection = false; }
            await LoadPreviewAsync();
        }
        private async Task LoadPreviewAsync()
        {
            var item = _selected;
            var generation = ++_previewLoadGeneration;
            PreviewImage.Source = null;
            if (item == null)
            {
                EmptyPreview.Text = "PNG, JPG, JPEG or GIF\\nDrop here or import an image";
                EmptyPreview.Visibility = Visibility.Visible;
                UpdatePreviewTransform();
                UpdateControls();
                return;
            }

            try
            {
                var bitmap = await _images.LoadBitmapAsync(item, 1280);
                if (generation != _previewLoadGeneration || !ReferenceEquals(_selected, item)) return;
                PreviewImage.Source = bitmap;
                EmptyPreview.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Settings image preview", ex);
                if (generation != _previewLoadGeneration || !ReferenceEquals(_selected, item)) return;
                PreviewImage.Source = null;
                EmptyPreview.Text = "Image could not be opened";
                EmptyPreview.Visibility = Visibility.Visible;
            }
            UpdatePreviewTransform();
            UpdateControls();
        }
        private void UpdatePreviewTransform()
        {
            PreviewTransform.CenterX = PreviewSurface.ActualWidth / 2d;
            PreviewTransform.CenterY = PreviewSurface.ActualHeight / 2d;
            var state = SettingsStore.Current.State;
            PreviewImage.Opacity = state.Opacity / 100d;
            PreviewTransform.ScaleX = PreviewTransform.ScaleY = state.Scale / 100d;
            PreviewTransform.TranslateX = state.PositionX; PreviewTransform.TranslateY = state.PositionY;
            PreviewTransform.Rotation = state.Rotation;
        }
        private void PreviewSurface_SizeChanged(object sender, SizeChangedEventArgs e) => UpdatePreviewTransform();

        private void UpdateControls()
        {
            GifControls.Visibility = _selected?.IsGif == true ? Visibility.Visible : Visibility.Collapsed;
            PlayPauseButton.Content = SettingsStore.Current.State.GifPlaying ? "Pause GIF" : "Play GIF";
        }
        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var item = await _images.PickAndImportAsync();
                if (item == null) return;
                SettingsStore.Current.Library.Insert(0, item);
                SettingsStore.Current.State.SelectedImageId = item.Id;
                SettingsStore.Current.NotifyChanged();
                await RestoreSelectionAsync();
            }
            catch (Exception ex) { ShowError("Cannot import this image: " + ex.Message); }
        }
        private async void Page_Drop(object sender, DragEventArgs e)
        {
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var file = items.OfType<StorageFile>().FirstOrDefault();
                if (file == null) return;
                var item = await _images.ImportAsync(file);
                SettingsStore.Current.Library.Insert(0, item); SettingsStore.Current.State.SelectedImageId = item.Id; SettingsStore.Current.NotifyChanged();
                await RestoreSelectionAsync();
            }
            catch (Exception ex) { ShowError("Cannot import dropped file: " + ex.Message); }
        }
        private void Page_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        }
        private async void Library_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringSelection || !(LibraryList.SelectedItem is ImageRecord item)) return;
            _selected = item; SettingsStore.Current.State.SelectedImageId = item.Id; SettingsStore.Current.NotifyChanged();
            await LoadPreviewAsync();
        }
        private async void RemoveImage_Click(object sender, RoutedEventArgs e)
        {
            if (!(((FrameworkElement)sender).Tag is ImageRecord item)) return;
            try
            {
                await _images.DeleteAsync(item);
                await RestoreSelectionAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Remove library image", ex); ShowError("Cannot remove this image: " + ex.Message); }
        }
        private void Center_Click(object sender, RoutedEventArgs e) => SettingsStore.Current.State.Center();
        private void Reset_Click(object sender, RoutedEventArgs e) => SettingsStore.Current.State.ResetTransform();
        private void ResetRotation_Click(object sender, RoutedEventArgs e) => SettingsStore.Current.State.Rotation = 0;
        private void PlayPause_Click(object sender, RoutedEventArgs e) => SettingsStore.Current.State.GifPlaying = !SettingsStore.Current.State.GifPlaying;

        private void Preview_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _dragging = true; _dragStart = e.GetCurrentPoint(PreviewSurface).Position;
            _originX = SettingsStore.Current.State.PositionX; _originY = SettingsStore.Current.State.PositionY;
            PreviewSurface.CapturePointer(e.Pointer);
        }
        private void Preview_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_dragging) return;
            var point = e.GetCurrentPoint(PreviewSurface).Position;
            SettingsStore.Current.State.PositionX = Math.Round(_originX + point.X - _dragStart.X);
            SettingsStore.Current.State.PositionY = Math.Round(_originY + point.Y - _dragStart.Y);
        }
        private void Preview_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _dragging = false; PreviewSurface.ReleasePointerCapture(e.Pointer);
        }
        private void SavePreset_Click(object sender, RoutedEventArgs e)
        {
            var name = ProfileNameBox.Text?.Trim();
            if (string.IsNullOrWhiteSpace(name)) { ShowError("Enter a preset name first."); return; }
            SettingsStore.Current.Profiles.Add(SettingsStore.Current.State.Snapshot(name)); ProfileNameBox.Text = string.Empty; SettingsStore.Current.NotifyChanged();
        }
        private void ApplyPreset_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is OverlayProfile profile) SettingsStore.Current.State.Apply(profile);
        }
        private void RemovePreset_Click(object sender, RoutedEventArgs e)
        {
            if (((FrameworkElement)sender).Tag is OverlayProfile profile) { SettingsStore.Current.Profiles.Remove(profile); SettingsStore.Current.NotifyChanged(); }
        }
        private void Profile_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private async void ShowError(string message)
        {
            try
            {
                var dialog = new ContentDialog { Title = "Pixel Overlay", Content = message, CloseButtonText = "OK" };
                await dialog.ShowAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Show error dialog", ex); }
        }
    }
}
