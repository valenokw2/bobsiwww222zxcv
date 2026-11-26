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
    public sealed class WindowStateChangedEventArgs : EventArgs
    {
        public string WindowId { get; private set; }
        public WindowStateChangedEventArgs(string windowId)
        {
            WindowId = WindowSlotIds.Normalize(windowId);
        }
    }

    // Persists only serializable data. UI-bound OverlayState instances are created
    // separately on each Game Bar CoreWindow dispatcher.
    public sealed class SettingsStore
    {
        private const string StateFile = "pixel-overlay-state.json";
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _ioGate = new SemaphoreSlim(1, 1);

        private bool _loaded;
        private bool _dirty;
        private string _lastJson = string.Empty;
        private readonly Dictionary<string, WindowInstanceConfig> _windowInstances =
            new Dictionary<string, WindowInstanceConfig>(StringComparer.Ordinal);
        private readonly List<ImageRecord> _library = new List<ImageRecord>();
        private readonly List<OverlayProfile> _profiles = new List<OverlayProfile>();
        private CancellationTokenSource _saveDelay;

        public static SettingsStore Current { get; } = new SettingsStore();
        public event EventHandler StateChanged;
        public event EventHandler<WindowStateChangedEventArgs> WindowStateChanged;

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
                    if (saved != null) ReplaceDataLocked(saved);
                    else EnsureFirstWindowLocked();
                    _lastJson = json ?? string.Empty;
                    _dirty = false;
                    _loaded = true;
                }
            }
            finally { _ioGate.Release(); }
        }

        public PersistedSettings GetSnapshot()
        {
            lock (_sync) return CreateSnapshotLocked();
        }

        // Reads must never recreate a deleted slot.
        public OverlayState GetWindowState(string windowId)
        {
            lock (_sync)
            {
                WindowInstanceConfig config;
                return _windowInstances.TryGetValue(WindowSlotIds.Normalize(windowId), out config) && config != null
                    ? config.ToOverlayState()
                    : new WindowInstanceConfig().ToOverlayState();
            }
        }

        public WindowInstanceConfig GetWindowConfig(string windowId)
        {
            lock (_sync)
            {
                WindowInstanceConfig config;
                return _windowInstances.TryGetValue(WindowSlotIds.Normalize(windowId), out config) && config != null
                    ? config.Clone()
                    : new WindowInstanceConfig();
            }
        }

        public IList<string> GetWindowIds()
        {
            lock (_sync)
                return _windowInstances.Keys.OrderBy(WindowSlotIds.GetIndex).ToList();
        }

        public string CreateWindow(string sourceWindowId, bool cloneSettings)
        {
            string createdId = null;
            lock (_sync)
            {
                if (!_loaded || _windowInstances.Count >= WindowSlotIds.MaxOverlayWindows)
                    return null;

                for (int i = 1; i <= WindowSlotIds.MaxOverlayWindows; i++)
                {
                    var candidate = WindowSlotIds.ForIndex(i);
                    if (_windowInstances.ContainsKey(candidate)) continue;

                    var config = cloneSettings
                        ? GetWindowConfigLocked(sourceWindowId).Clone()
                        : new WindowInstanceConfig();
                    config.IsHidden = false;
                    _windowInstances.Add(candidate, config);
                    createdId = candidate;
                    MarkDirtyLocked();
                    break;
                }
            }

            if (createdId != null)
            {
                PublishChanged();
                PublishWindowChanged(createdId);
            }
            return createdId;
        }

        public bool DeleteWindow(string windowId)
        {
            windowId = WindowSlotIds.Normalize(windowId);
            if (string.Equals(windowId, WindowSlotIds.FirstWindowId, StringComparison.Ordinal))
                return false;

            bool removed;
            lock (_sync)
            {
                if (!_loaded) return false;
                removed = _windowInstances.Remove(windowId);
                if (removed) MarkDirtyLocked();
            }

            if (removed)
            {
                PublishChanged();
                PublishWindowChanged(windowId);
            }
            return removed;
        }

        public void SetWindowHidden(string windowId, bool hidden)
        {
            windowId = WindowSlotIds.Normalize(windowId);
            bool changed = false;
            lock (_sync)
            {
                if (!_loaded) return;
                var config = GetWindowConfigLocked(windowId);
                if (config.IsHidden != hidden)
                {
                    config.IsHidden = hidden;
                    changed = true;
                    MarkDirtyLocked();
                }
            }

            if (changed)
            {
                PublishChanged();
                PublishWindowChanged(windowId);
            }
        }

        public bool IsFavorite(string windowId, string imageId)
        {
            if (string.IsNullOrWhiteSpace(imageId)) return false;
            lock (_sync)
            {
                WindowInstanceConfig config;
                return _windowInstances.TryGetValue(WindowSlotIds.Normalize(windowId), out config) &&
                    config != null && config.FavoriteImageIds.Contains(imageId, StringComparer.Ordinal);
            }
        }

        public void SetFavorite(string windowId, string imageId, bool favorite)
        {
            if (string.IsNullOrWhiteSpace(imageId)) return;
            windowId = WindowSlotIds.Normalize(windowId);
            bool changed = false;
            lock (_sync)
            {
                if (!_loaded) return;
                var config = GetWindowConfigLocked(windowId);
                bool exists = config.FavoriteImageIds.Contains(imageId, StringComparer.Ordinal);
                if (favorite && !exists)
                {
                    config.FavoriteImageIds.Add(imageId);
                    changed = true;
                }
                else if (!favorite && exists)
                {
                    config.FavoriteImageIds.RemoveAll(x => string.Equals(x, imageId, StringComparison.Ordinal));
                    changed = true;
                }
                if (changed) MarkDirtyLocked();
            }

            if (changed)
            {
                PublishChanged();
                PublishWindowChanged(windowId);
            }
        }

        // Compatibility overloads preserve the legacy single-overlay API.
        public void UpdateState(OverlayState state) => UpdateState(WindowSlotIds.FirstWindowId, state);

        public void UpdateState(string windowId, OverlayState state)
        {
            if (state == null) return;
            windowId = WindowSlotIds.Normalize(windowId);
            lock (_sync)
            {
                if (!_loaded) return;
                GetWindowConfigLocked(windowId).Apply(state);
                MarkDirtyLocked();
            }
            PublishChanged();
            PublishWindowChanged(windowId);
        }

        public void AddImageAndSelect(ImageRecord item, OverlayState nextState) =>
            AddImageAndSelect(item, WindowSlotIds.FirstWindowId, nextState);

        public void AddImageAndSelect(ImageRecord item, string windowId, OverlayState nextState)
        {
            if (item == null) return;
            windowId = WindowSlotIds.Normalize(windowId);
            lock (_sync)
            {
                if (!_loaded) return;
                _library.RemoveAll(x => string.Equals(x.Id, item.Id, StringComparison.Ordinal));
                _library.Insert(0, item.Clone());
                var config = GetWindowConfigLocked(windowId);
                config.Apply(nextState);
                config.SelectedImageId = item.Id;
                MarkDirtyLocked();
            }
            PublishChanged();
            PublishWindowChanged(windowId);
        }

        public Task AddImageAndSelectAsync(ImageRecord item, OverlayState nextState) =>
            AddImageAndSelectAsync(item, WindowSlotIds.FirstWindowId, nextState);

        public async Task AddImageAndSelectAsync(ImageRecord item, string windowId, OverlayState nextState)
        {
            AddImageAndSelect(item, windowId, nextState);
            await FlushAsync();
        }

        public void RemoveImage(string imageId)
        {
            if (string.IsNullOrWhiteSpace(imageId)) return;
            var affectedWindows = new List<string>();
            bool changed;
            lock (_sync)
            {
                if (!_loaded) return;
                changed = _library.RemoveAll(x => string.Equals(x.Id, imageId, StringComparison.Ordinal)) > 0;

                foreach (var pair in _windowInstances)
                {
                    bool slotChanged = false;
                    if (string.Equals(pair.Value.SelectedImageId, imageId, StringComparison.Ordinal))
                    {
                        pair.Value.SelectedImageId = null;
                        slotChanged = true;
                    }

                    int removedFavorites = pair.Value.FavoriteImageIds.RemoveAll(
                        x => string.Equals(x, imageId, StringComparison.Ordinal));
                    if (removedFavorites > 0) slotChanged = true;
                    if (slotChanged) affectedWindows.Add(pair.Key);
                }

                changed |= affectedWindows.Count > 0;
                if (changed) MarkDirtyLocked();
            }

            if (changed)
            {
                PublishChanged();
                foreach (var id in affectedWindows) PublishWindowChanged(id);
            }
        }

        public void SaveProfile(OverlayProfile profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.Name)) return;
            lock (_sync)
            {
                if (!_loaded) return;
                int existing = _profiles.FindIndex(x =>
                    string.Equals(x.Name, profile.Name, StringComparison.OrdinalIgnoreCase));
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
                changed = _profiles.RemoveAll(x =>
                    string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
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
                    // Preserve the existing drain-until-clean behavior.
                    if (!_dirty) return;
                }
            }
        }

        private WindowInstanceConfig GetWindowConfigLocked(string windowId)
        {
            windowId = WindowSlotIds.Normalize(windowId);
            WindowInstanceConfig config;
            if (!_windowInstances.TryGetValue(windowId, out config))
            {
                config = new WindowInstanceConfig();
                _windowInstances[windowId] = config;
            }
            return config;
        }

        private void EnsureFirstWindowLocked()
        {
            if (!_windowInstances.ContainsKey(WindowSlotIds.FirstWindowId))
                _windowInstances[WindowSlotIds.FirstWindowId] = new WindowInstanceConfig();
        }

        private void MarkDirtyLocked() { _dirty = true; }

        private void PublishWindowChanged(string windowId)
        {
            WindowStateChanged?.Invoke(this, new WindowStateChangedEventArgs(windowId));
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
            finally { _ioGate.Release(); }
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
            List<string> affectedWindows = new List<string>();
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
                    if (_dirty || string.Equals(json, _lastJson, StringComparison.Ordinal)) return;
                    affectedWindows.AddRange(_windowInstances.Keys);
                    ReplaceDataLocked(saved);
                    affectedWindows.AddRange(_windowInstances.Keys);
                    _lastJson = json;
                    changed = true;
                }
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Settings reload", ex);
            }
            finally { _ioGate.Release(); }

            if (changed)
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
                foreach (var id in affectedWindows.Distinct(StringComparer.Ordinal))
                    PublishWindowChanged(id);
            }
        }

        private PersistedSettings CreateSnapshotLocked()
        {
            EnsureFirstWindowLocked();
            return new PersistedSettings
            {
                State = _windowInstances[WindowSlotIds.FirstWindowId].ToOverlayState(),
                Library = _library.Select(x => x.Clone()).ToList(),
                Profiles = _profiles.Select(x => x.Clone()).ToList(),
                WindowInstances = _windowInstances.ToDictionary(
                    x => x.Key, x => x.Value.Clone(), StringComparer.Ordinal)
            };
        }

        private void ReplaceDataLocked(PersistedSettings saved)
        {
            _windowInstances.Clear();
            if (saved?.WindowInstances != null)
            {
                foreach (var pair in saved.WindowInstances)
                {
                    if (!WindowSlotIds.IsValid(pair.Key) || pair.Value == null) continue;
                    var key = WindowSlotIds.Normalize(pair.Key);
                    if (!_windowInstances.ContainsKey(key))
                        _windowInstances.Add(key, pair.Value.Clone());
                }
            }

            // Migrate a pre-multiwindow config exactly once to overlay-01.
            if (_windowInstances.Count == 0)
                _windowInstances[WindowSlotIds.FirstWindowId] =
                    WindowInstanceConfig.FromOverlayState(saved?.State ?? new OverlayState());

            EnsureFirstWindowLocked();

            _library.Clear();
            if (saved?.Library != null)
                _library.AddRange(saved.Library.Where(x => x != null).Select(x => x.Clone()));

            _profiles.Clear();
            if (saved?.Profiles != null)
                _profiles.AddRange(saved.Profiles.Where(x => x != null).Select(x => x.Clone()));
        }
    }
}