using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PixelOverlay.Models;
using Windows.Storage;
using Windows.UI.Core;

namespace PixelOverlay.Services
{
    // This store contains only detached data. UI-bound objects and collections belong to
    // individual Game Bar windows and are updated through each window's own dispatcher.
    public sealed class SettingsStore
    {
        private const string StateFile = "pixel-overlay-state.json";
        private readonly object _sync = new object();
        private readonly SemaphoreSlim _loadGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _reloadGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _saveGate = new SemaphoreSlim(1, 1);
        private readonly List<SettingsSubscription> _subscriptions = new List<SettingsSubscription>();
        private PersistedSettings _data = new PersistedSettings();
        private bool _loaded;
        private long _revision;
        private long _savedRevision;
        private string _lastJson = string.Empty;
        private CancellationTokenSource _saveDelay;

        public static SettingsStore Current { get; } = new SettingsStore();

        private SettingsStore()
        {
            ApplicationData.Current.DataChanged += OnDataChanged;
        }

        public sealed class SettingsSubscription : IDisposable
        {
            private SettingsStore _owner;
            internal readonly CoreDispatcher Dispatcher;
            internal readonly Action<PersistedSettings> Callback;

            internal SettingsSubscription(SettingsStore owner, CoreDispatcher dispatcher, Action<PersistedSettings> callback)
            {
                _owner = owner;
                Dispatcher = dispatcher;
                Callback = callback;
            }

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner != null) owner.Unsubscribe(this);
            }
        }

        public async Task LoadAsync()
        {
            await _loadGate.WaitAsync();
            try
            {
                if (_loaded) return;

                PersistedSettings loaded = null;
                string json = null;
                try
                {
                    var file = await ApplicationData.Current.LocalFolder.GetFileAsync(StateFile);
                    json = await FileIO.ReadTextAsync(file);
                    loaded = Deserialize(json);
                }
                catch (FileNotFoundException)
                {
                    loaded = new PersistedSettings();
                }
                catch (Exception ex)
                {
                    AppDiagnostics.Log("Initial settings load", ex);
                    loaded = new PersistedSettings();
                }

                lock (_sync)
                {
                    _data = Normalize(loaded);
                    _lastJson = json ?? string.Empty;
                    _revision = 0;
                    _savedRevision = 0;
                    _loaded = true;
                }
            }
            finally
            {
                _loadGate.Release();
            }
        }

        public PersistedSettings GetSnapshot()
        {
            lock (_sync) return Clone(_data, _revision);
        }

        public SettingsSubscription Subscribe(CoreDispatcher dispatcher, Action<PersistedSettings> callback)
        {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            if (callback == null) throw new ArgumentNullException(nameof(callback));

            var subscription = new SettingsSubscription(this, dispatcher, callback);
            lock (_sync) _subscriptions.Add(subscription);
            return subscription;
        }

        public long UpdateSnapshot(PersistedSettings snapshot, SettingsSubscription source)
        {
            if (snapshot == null) return -1;

            long revision;
            lock (_sync)
            {
                if (!_loaded) return -1;
                _data = Normalize(Clone(snapshot, 0));
                revision = ++_revision;
            }

            PublishSnapshot(source);
            ScheduleSave();
            return revision;
        }

        public Task FlushAsync()
        {
            CancellationTokenSource delay;
            lock (_sync) delay = _saveDelay;
            if (delay != null)
            {
                try { delay.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            return SaveAsync();
        }

        private void Unsubscribe(SettingsSubscription subscription)
        {
            lock (_sync) _subscriptions.Remove(subscription);
        }

        private void PublishSnapshot(SettingsSubscription source)
        {
            PersistedSettings snapshot;
            SettingsSubscription[] targets;
            lock (_sync)
            {
                snapshot = Clone(_data, _revision);
                targets = _subscriptions.ToArray();
            }

            foreach (var target in targets)
            {
                if (ReferenceEquals(target, source)) continue;
                DispatchSnapshot(target, snapshot);
            }
        }

        private async void DispatchSnapshot(SettingsSubscription target, PersistedSettings snapshot)
        {
            try
            {
                if (target.Dispatcher.HasThreadAccess)
                {
                    target.Callback(Clone(snapshot, snapshot.Revision));
                    return;
                }

                await target.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    target.Callback(Clone(snapshot, snapshot.Revision));
                });
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Dispatch settings to window", ex);
            }
        }

        private void ScheduleSave()
        {
            CancellationTokenSource previous;
            CancellationTokenSource current = new CancellationTokenSource();
            lock (_sync)
            {
                previous = _saveDelay;
                _saveDelay = current;
            }

            if (previous != null)
            {
                try { previous.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            _ = SaveAfterDelayAsync(current);
        }

        private async Task SaveAfterDelayAsync(CancellationTokenSource delay)
        {
            try
            {
                await Task.Delay(180, delay.Token);
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

        private async Task SaveAsync()
        {
            await _saveGate.WaitAsync();
            bool wrote = false;
            try
            {
                while (true)
                {
                    PersistedSettings snapshot;
                    long targetRevision;
                    lock (_sync)
                    {
                        if (!_loaded || _savedRevision >= _revision) break;
                        snapshot = Clone(_data, _revision);
                        targetRevision = _revision;
                    }

                    string json = Serialize(snapshot);
                    var folder = ApplicationData.Current.LocalFolder;
                    var tempName = StateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    var temp = await folder.CreateFileAsync(tempName, CreationCollisionOption.FailIfExists);
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

                    lock (_sync)
                    {
                        _lastJson = json;
                        if (targetRevision > _savedRevision) _savedRevision = targetRevision;
                        wrote = true;
                        if (_savedRevision >= _revision) break;
                    }
                }
            }
            finally
            {
                _saveGate.Release();
            }

            if (wrote)
            {
                try { ApplicationData.Current.SignalDataChanged(); }
                catch (Exception ex) { AppDiagnostics.Log("Signal settings changed", ex); }
            }
        }

        private async void OnDataChanged(ApplicationData sender, object args)
        {
            try
            {
                await LoadAsync();
                await ReloadFromDiskAsync();
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Cross-process settings notification", ex);
            }
        }

        private async Task ReloadFromDiskAsync()
        {
            await _reloadGate.WaitAsync();
            try
            {
                string json;
                PersistedSettings loaded;
                try
                {
                    var file = await ApplicationData.Current.LocalFolder.GetFileAsync(StateFile);
                    json = await FileIO.ReadTextAsync(file);
                    loaded = Deserialize(json);
                }
                catch (FileNotFoundException) { return; }

                lock (_sync)
                {
                    // Do not overwrite local, not-yet-persisted edits with another process' update.
                    if (_revision != _savedRevision || string.Equals(json, _lastJson, StringComparison.Ordinal))
                        return;

                    _data = Normalize(loaded);
                    _lastJson = json;
                    _revision++;
                    _savedRevision = _revision;
                }

                PublishSnapshot(null);
            }
            catch (Exception ex)
            {
                AppDiagnostics.Log("Settings reload", ex);
            }
            finally
            {
                _reloadGate.Release();
            }
        }

        private static PersistedSettings Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new PersistedSettings();
            var serializer = new DataContractJsonSerializer(typeof(PersistedSettings));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (PersistedSettings)serializer.ReadObject(stream);
        }

        private static string Serialize(PersistedSettings settings)
        {
            var serializer = new DataContractJsonSerializer(typeof(PersistedSettings));
            using (var stream = new MemoryStream())
            {
                serializer.WriteObject(stream, settings);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        private static PersistedSettings Normalize(PersistedSettings value)
        {
            value = value ?? new PersistedSettings();
            if (value.State == null) value.State = new OverlayState();
            if (value.Library == null) value.Library = new List<ImageRecord>();
            if (value.Profiles == null) value.Profiles = new List<OverlayProfile>();
            return value;
        }

        private static PersistedSettings Clone(PersistedSettings value, long revision)
        {
            value = Normalize(value);
            var clone = new PersistedSettings
            {
                State = Clone(value.State),
                Library = new List<ImageRecord>(),
                Profiles = new List<OverlayProfile>(),
                Revision = revision
            };

            foreach (var item in value.Library)
            {
                if (item == null) continue;
                clone.Library.Add(new ImageRecord
                {
                    Id = item.Id,
                    StoredName = item.StoredName,
                    DisplayName = item.DisplayName,
                    Extension = item.Extension,
                    AddedAt = item.AddedAt
                });
            }

            foreach (var profile in value.Profiles)
            {
                if (profile == null) continue;
                clone.Profiles.Add(new OverlayProfile
                {
                    Name = profile.Name,
                    Opacity = profile.Opacity,
                    Scale = profile.Scale,
                    PositionX = profile.PositionX,
                    PositionY = profile.PositionY,
                    Rotation = profile.Rotation,
                    GifSpeed = profile.GifSpeed,
                    GifPlaying = profile.GifPlaying
                });
            }

            return clone;
        }

        private static OverlayState Clone(OverlayState value)
        {
            value = value ?? new OverlayState();
            return new OverlayState
            {
                SelectedImageId = value.SelectedImageId,
                Opacity = value.Opacity,
                Scale = value.Scale,
                PositionX = value.PositionX,
                PositionY = value.PositionY,
                Rotation = value.Rotation,
                GifSpeed = value.GifSpeed,
                GifPlaying = value.GifPlaying
            };
        }
    }
}