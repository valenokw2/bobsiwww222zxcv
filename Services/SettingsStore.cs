using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PixelOverlay.Models;
using Windows.Storage;

namespace PixelOverlay.Services
{
    // The store owns only plain data. Each Game Bar CoreWindow builds its own UI-bound
    // OverlayState and ObservableCollections on its own dispatcher.
    public sealed class SettingsStore
    {
        private const string StateFile = "pixel-overlay-state.json";
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _ioGate = new SemaphoreSlim(1, 1);

        private bool _loaded;
        private bool _dirty;
        private string _lastJson = string.Empty;
        private OverlayState _state = new OverlayState();
        private readonly List<ImageRecord> _library = new List<ImageRecord>();
        private readonly List<OverlayProfile> _profiles = new List<OverlayProfile>();
        private CancellationTokenSource _saveDelay;

        public static SettingsStore Current { get; } = new SettingsStore();
        public event EventHandler StateChanged;

        private SettingsStore()
        {
            ApplicationData.Current.DataChanged += OnDataChanged;
        }

        public async Task LoadAsync()
        {
            await _ioGate.WaitAsync();
            try
            {
                lock (_sync)
                {
                    if (_loaded) return;
                }

                PersistedSettings saved = null;
                string json = null;
                try
                {
                    var file = await ApplicationData.Current.LocalFolder.GetFileAsync(StateFile);
                    json = await FileIO.ReadTextAsync(file);
                    var serializer = new DataContractJsonSerializer(typeof(PersistedSettings));
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                        saved = (PersistedSettings)serializer.ReadObject(stream);
                }
                catch (FileNotFoundException) { }
                catch (Exception ex)
                {
                    AppDiagnostics.Log("Settings initial load", ex);
                    throw;
                }

                lock (_sync)
                {
                    if (saved != null)
                        ReplaceDataLocked(saved);
                    _lastJson = json ?? string.Empty;
                    _dirty = false;
                    _loaded = true;
                }
            }
            finally
            {
                _ioGate.Release();
            }
        }

        public PersistedSettings GetSnapshot()
        {
            lock (_sync)
                return CreateSnapshotLocked();
        }

        public void UpdateState(OverlayState state)
        {
            if (state == null) return;
            lock (_sync)
            {
                if (!_loaded) return;
                _state = state.Clone();
                MarkDirtyLocked();
            }
            PublishChanged();
        }

        public bool AddImageAndSelect(ImageRecord item, OverlayState nextState)
        {
            if (item == null) return false;
            bool layerAdded;
            lock (_sync)
            {
                if (!_loaded) return false;
                _library.RemoveAll(x => string.Equals(x.Id, item.Id, StringComparison.Ordinal));
                _library.Insert(0, item.Clone());
                _state = (nextState ?? _state).Clone();
                layerAdded = _state.AddLayer(new OverlayLayer { ImageId = item.Id });
                MarkDirtyLocked();
            }
            PublishChanged();
            return layerAdded;
        }

        public async Task<bool> AddImageAndSelectAsync(ImageRecord item, OverlayState nextState)
        {
            bool layerAdded = AddImageAndSelect(item, nextState);
            await FlushAsync();
            return layerAdded;
        }

        public bool AddLayer(string imageId)
        {
            if (string.IsNullOrWhiteSpace(imageId)) return false;
            bool added;
            lock (_sync)
            {
                if (!_loaded || !_library.Any(x => string.Equals(x.Id, imageId, StringComparison.Ordinal)))
                    return false;
                added = _state.AddLayer(new OverlayLayer { ImageId = imageId });
                if (added) MarkDirtyLocked();
            }
            if (added) PublishChanged();
            return added;
        }

        public bool RemoveLayer(string layerId)
        {
            bool changed;
            lock (_sync)
            {
                if (!_loaded) return false;
                changed = _state.RemoveLayer(layerId);
                if (changed) MarkDirtyLocked();
            }
            if (changed) PublishChanged();
            return changed;
        }

        public bool MoveLayer(string layerId, int direction)
        {
            bool changed;
            lock (_sync)
            {
                if (!_loaded) return false;
                changed = _state.MoveLayer(layerId, direction);
                if (changed) MarkDirtyLocked();
            }
            if (changed) PublishChanged();
            return changed;
        }

        public bool ToggleFavorite(string imageId)
        {
            bool changed = false;
            lock (_sync)
            {
                if (!_loaded) return false;
                var item = _library.FirstOrDefault(x => string.Equals(x.Id, imageId, StringComparison.Ordinal));
                if (item == null) return false;
                item.IsFavorite = !item.IsFavorite;
                changed = true;
                MarkDirtyLocked();
            }
            if (changed) PublishChanged();
            return changed;
        }

        public void RemoveImage(string imageId)
        {
            if (string.IsNullOrWhiteSpace(imageId)) return;
            bool changed;
            lock (_sync)
            {
                if (!_loaded) return;
                changed = _library.RemoveAll(x => string.Equals(x.Id, imageId, StringComparison.Ordinal)) > 0;
                int layerCount = _state.Layers.Count;
                _state.RemoveLayersForImage(imageId);
                if (_state.Layers.Count != layerCount) changed = true;
                if (changed) MarkDirtyLocked();
            }
            if (changed) PublishChanged();
        }

        public void SaveProfile(OverlayProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Name)) return;
            lock (_sync)
            {
                if (!_loaded) return;
                int existing = _profiles.FindIndex(x => string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) _profiles[existing] = profile.Clone();
                else _profiles.Add(profile.Clone());
                MarkDirtyLocked();
            }
            PublishChanged();
        }

        public void RemoveProfile(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            bool changed;
            lock (_sync)
            {
                if (!_loaded) return;
                changed = _profiles.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
                if (changed) MarkDirtyLocked();
            }
            if (changed) PublishChanged();
        }

        public async Task FlushAsync()
        {
            while (true)
            {
                CancellationTokenSource pending;
                lock (_sync)
                {
                    pending = _saveDelay;
                    _saveDelay = null;
                }

                CancelSafely(pending);
                if (!await SaveAsync())
                    throw new IOException("Settings could not be flushed to local storage. See pixel-overlay-diagnostics.log.");

                lock (_sync)
                {
                    // A change can arrive at the end of a save, just before its semaphore
                    // is released. Drain again instead of completing the suspension deferral.
                    if (!_dirty) return;
                }
            }
        }

        private void MarkDirtyLocked()
        {
            _dirty = true;
        }

        private void PublishChanged()
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
            ScheduleSave();
        }

        private void ScheduleSave()
        {
            var next = new CancellationTokenSource();
            CancellationTokenSource previous;
            lock (_sync)
            {
                previous = _saveDelay;
                _saveDelay = next;
            }
            CancelSafely(previous);
            _ = SaveAfterDelayAsync(next);
        }

        private async Task SaveAfterDelayAsync(CancellationTokenSource delay)
        {
            try
            {
                await Task.Delay(250, delay.Token);
                await SaveAsync();
            }
            catch (TaskCanceledException) { }
            catch (Exception ex) { AppDiagnostics.Log("Debounced settings save", ex); }
            finally
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_saveDelay, delay)) _saveDelay = null;
                }
                delay.Dispose();
            }
        }

        private static void CancelSafely(CancellationTokenSource source)
        {
            if (source == null) return;
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        private async Task<bool> SaveAsync()
        {
            await _ioGate.WaitAsync();
            try
            {
                while (true)
                {
                    PersistedSettings snapshot;
                    lock (_sync)
                    {
                        if (!_loaded || !_dirty) break;
                        _dirty = false;
                        snapshot = CreateSnapshotLocked();
                    }

                    string json;
                    var serializer = new DataContractJsonSerializer(typeof(PersistedSettings));
                    using (var stream = new MemoryStream())
                    {
                        serializer.WriteObject(stream, snapshot);
                        json = Encoding.UTF8.GetString(stream.ToArray());
                    }

                    bool alreadySaved;
                    lock (_sync) alreadySaved = string.Equals(json, _lastJson, StringComparison.Ordinal);
                    if (!alreadySaved)
                    {
                        var folder = ApplicationData.Current.LocalFolder;
                        var tempName = StateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        var temp = await folder.CreateFileAsync(tempName, CreationCollisionOption.ReplaceExisting);
                        try
                        {
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
                        }
                        catch
                        {
                            try { await temp.DeleteAsync(); } catch (Exception) { }
                            throw;
                        }
                        lock (_sync) _lastJson = json;
                    }

                    lock (_sync)
                    {
                        if (!_dirty) break;
                    }
                }

                ApplicationData.Current.SignalDataChanged();
                return true;
            }
            catch (Exception ex)
            {
                lock (_sync) _dirty = true;
                AppDiagnostics.Log("Settings save", ex);
                return false;
            }
            finally
            {
                _ioGate.Release();
            }
        }

        private async void OnDataChanged(ApplicationData sender, object args)
        {
            try { await ReloadExternalAsync(); }
            catch (Exception ex) { AppDiagnostics.Log("Cross-process settings notification", ex); }
        }

        private async Task ReloadExternalAsync()
        {
            await _ioGate.WaitAsync();
            bool changed = false;
            try
            {
                lock (_sync)
                {
                    if (!_loaded || _dirty) return;
                }

                string json;
                PersistedSettings saved;
                try
                {
                    var file = await ApplicationData.Current.LocalFolder.GetFileAsync(StateFile);
                    json = await FileIO.ReadTextAsync(file);
                    lock (_sync)
                    {
                        if (_dirty || string.Equals(json, _lastJson, StringComparison.Ordinal)) return;
                    }

                    var serializer = new DataContractJsonSerializer(typeof(PersistedSettings));
                    using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                        saved = (PersistedSettings)serializer.ReadObject(stream);
                }
                catch (FileNotFoundException) { return; }

                lock (_sync)
                {
                    // A local edit may have arrived while the external file was being read.
                    if (_dirty || string.Equals(json, _lastJson, StringComparison.Ordinal)) return;
                    ReplaceDataLocked(saved);
                    _lastJson = json;
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Settings reload", ex);
            }
            finally
            {
                _ioGate.Release();
            }

            if (changed) StateChanged?.Invoke(this, EventArgs.Empty);
        }

        private PersistedSettings CreateSnapshotLocked()
        {
            return new PersistedSettings
            {
                State = _state == null ? new OverlayState() : _state.Clone(),
                Library = _library.Select(x => x.Clone()).ToList(),
                Profiles = _profiles.Select(x => x.Clone()).ToList()
            };
        }

        private void ReplaceDataLocked(PersistedSettings saved)
        {
            _state = saved?.State == null ? new OverlayState() : saved.State.Clone();
            _library.Clear();
            if (saved?.Library != null)
                _library.AddRange(saved.Library.Where(x => x != null).Select(x => x.Clone()));
            _profiles.Clear();
            if (saved?.Profiles != null)
                _profiles.AddRange(saved.Profiles.Where(x => x != null).Select(x => x.Clone()));
        }
    }
}
