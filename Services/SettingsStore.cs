using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PixelOverlay.Models;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;

namespace PixelOverlay.Services
{
    // Local JSON only. Game Bar runs the overlay and the settings widget in separate processes,
    // so a save also signals ApplicationData.DataChanged for the other process.
    public sealed class SettingsStore
    {
        private const string StateFile = "pixel-overlay-state.json";
        private bool _loaded;
        private bool _saving;
        private bool _dirty;
        private bool _reloading;
        private string _lastJson = string.Empty;
        private CancellationTokenSource _saveDelay;
        private Task _pending = Task.CompletedTask;

        public static SettingsStore Current { get; } = new SettingsStore();
        public OverlayState State { get; private set; } = new OverlayState();
        public ObservableCollection<ImageRecord> Library { get; } = new ObservableCollection<ImageRecord>();
        public ObservableCollection<OverlayProfile> Profiles { get; } = new ObservableCollection<OverlayProfile>();
        public event EventHandler StateChanged;

        private SettingsStore()
        {
            ApplicationData.Current.DataChanged += OnDataChanged;
        }

        public async Task LoadAsync()
        {
            if (_loaded) return;
            _loaded = true;
            await ReloadAsync();
        }

        public void NotifyChanged()
        {
            if (_reloading) return;
            StateChanged?.Invoke(this, EventArgs.Empty);
            _dirty = true;
            ScheduleSave();
        }

        public Task FlushAsync()
        {
            _saveDelay?.Cancel();
            _dirty = true;
            return SaveAsync();
        }

        private void ScheduleSave()
        {
            _saveDelay?.Cancel();
            var delay = new CancellationTokenSource();
            _saveDelay = delay;
            var dispatcher = Window.Current?.Dispatcher;
            _ = SaveAfterDelayAsync(delay.Token, dispatcher);
        }

        private async Task SaveAfterDelayAsync(CancellationToken token, CoreDispatcher dispatcher)
        {
            try
            {
                await Task.Delay(120, token);
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { _ = SaveAsync(); });
                else
                    await SaveAsync();
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) { AppDiagnostics.Log("Debounced settings save", ex); }
        }

        private Task SaveAsync()
        {
            if (!_loaded) return Task.CompletedTask;
            if (_saving) return _pending;
            _pending = SaveCoreAsync();
            return _pending;
        }

        private async Task SaveCoreAsync()
        {
            _saving = true;
            try
            {
                do
                {
                    _dirty = false;
                    var snapshot = new PersistedSettings
                    {
                        State = State,
                        Library = new List<ImageRecord>(Library),
                        Profiles = new List<OverlayProfile>(Profiles)
                    };
                    var serializer = new DataContractJsonSerializer(typeof(PersistedSettings));
                    string json;
                    using (var stream = new MemoryStream())
                    {
                        serializer.WriteObject(stream, snapshot);
                        json = Encoding.UTF8.GetString(stream.ToArray());
                    }
                    if (json == _lastJson) continue;
                    var folder = ApplicationData.Current.LocalFolder;
                    var temp = await folder.CreateFileAsync(StateFile + "." + Guid.NewGuid().ToString("N") + ".tmp", CreationCollisionOption.ReplaceExisting);
                    await FileIO.WriteTextAsync(temp, json);
                    try
                    {
                        var destination = await folder.GetFileAsync(StateFile);
                        await temp.MoveAndReplaceAsync(destination);
                    }
                    catch (FileNotFoundException)
                    {
                        await temp.MoveAsync(folder, StateFile, NameCollisionOption.ReplaceExisting);
                    }
                    _lastJson = json;
                }
                while (_dirty);
                ApplicationData.Current.SignalDataChanged();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Settings save", ex);
                // Keep the app usable after a storage failure. A later user change will retry.
                _dirty = false;
            }
            finally
            {
                _saving = false;
                if (_dirty) _ = SaveAsync();
            }
        }

        private async void OnDataChanged(ApplicationData sender, object args)
        {
            try
            {
                var dispatcher = Window.Current?.Dispatcher
                    ?? Windows.ApplicationModel.Core.CoreApplication.MainView?.CoreWindow?.Dispatcher;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => { _ = ReloadAsync(); });
                    return;
                }
                await ReloadAsync();
            }
            catch (Exception ex) { AppDiagnostics.Log("Cross-process settings notification", ex); }
        }

        private async Task ReloadAsync()
        {
            PersistedSettings saved = null;
            string json = null;
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(StateFile);
                json = await FileIO.ReadTextAsync(file);
                if (json == _lastJson) return;
                var serializer = new DataContractJsonSerializer(typeof(PersistedSettings));
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    saved = (PersistedSettings)serializer.ReadObject(stream);
            }
            catch (FileNotFoundException) { return; }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Settings reload", ex);
                return;
            }

            _reloading = true;
            try
            {
                State.CopyFrom(saved?.State ?? new OverlayState());
                Replace(Library, saved?.Library);
                Replace(Profiles, saved?.Profiles);
                _lastJson = json ?? string.Empty;
            }
            finally { _reloading = false; }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private static void Replace<T>(ObservableCollection<T> target, IList<T> source)
        {
            target.Clear();
            if (source == null) return;
            foreach (var item in source) target.Add(item);
        }
    }
}
