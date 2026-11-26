using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using PixelOverlay.Models;
using Windows.Storage;
using Windows.Storage.Streams;

namespace PixelOverlay.Services
{
    // Shared, reference-counted GIF stream cache. Entries use stream clones so each
    // BitmapDecoder gets an independent seek position without copying the GIF payload.
    public sealed class AssetCacheService
    {
        public const long MaxCacheBytes = 180L * 1024L * 1024L;
        private const ulong MaxGifBytes = 32UL * 1024UL * 1024UL;

        internal sealed class Entry
        {
            public string Id;
            public InMemoryRandomAccessStream Stream;
            public long Size;
            public int References;
            public LinkedListNode<Entry> Node;
        }

        public sealed class AssetLease : IDisposable
        {
            private AssetCacheService _owner;
            private Entry _entry;
            private IRandomAccessStream _stream;

            internal AssetLease(AssetCacheService owner, Entry entry)
            {
                _owner = owner;
                _entry = entry;
                _stream = entry.Stream.CloneStream();
            }

            public IRandomAccessStream DetachStream()
            {
                var stream = _stream;
                _stream = null;
                return stream;
            }

            public void Dispose()
            {
                var stream = Interlocked.Exchange(ref _stream, null);
                try { stream?.Dispose(); }
                catch (Exception ex) { AppDiagnostics.Log("Dispose cached GIF stream clone", ex); }

                var owner = Interlocked.Exchange(ref _owner, null);
                var entry = Interlocked.Exchange(ref _entry, null);
                if (owner != null && entry != null) owner.Release(entry);
            }
        }

        private static readonly Lazy<AssetCacheService> LazyInstance =
            new Lazy<AssetCacheService>(() => new AssetCacheService());

        public static AssetCacheService Instance => LazyInstance.Value;

        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries =
            new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Task<Entry>> _pending =
            new Dictionary<string, Task<Entry>>(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _lru = new LinkedList<Entry>();
        private long _cachedBytes;

        private AssetCacheService() { }

        public long CachedBytes
        {
            get { lock (_sync) return _cachedBytes; }
        }

        public int EntryCount
        {
            get { lock (_sync) return _entries.Count; }
        }

        public async Task<AssetLease> AcquireGifAsync(ImageRecord item, ImageLibraryService images)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (images == null) throw new ArgumentNullException(nameof(images));
            if (!item.IsGif) throw new InvalidOperationException("Only GIF assets can be acquired from this cache.");

            Task<Entry> pending;
            lock (_sync)
            {
                Entry cached;
                if (_entries.TryGetValue(item.Id, out cached))
                {
                    cached.References++;
                    TouchLocked(cached);
                    return new AssetLease(this, cached);
                }

                if (!_pending.TryGetValue(item.Id, out pending))
                {
                    pending = LoadEntryAsync(item, images);
                    _pending.Add(item.Id, pending);
                }
            }

            Entry loaded;
            try
            {
                loaded = await pending;
            }
            catch
            {
                lock (_sync)
                {
                    Task<Entry> current;
                    if (_pending.TryGetValue(item.Id, out current) && ReferenceEquals(current, pending))
                        _pending.Remove(item.Id);
                }
                throw;
            }

            lock (_sync)
            {
                Task<Entry> current;
                if (_pending.TryGetValue(item.Id, out current) && ReferenceEquals(current, pending))
                    _pending.Remove(item.Id);

                Entry entry;
                if (_entries.TryGetValue(item.Id, out entry))
                {
                    if (!ReferenceEquals(entry, loaded))
                    {
                        try { loaded.Stream.Dispose(); }
                        catch (Exception ex) { AppDiagnostics.Log("Dispose duplicate cached GIF stream", ex); }
                    }
                }
                else
                {
                    entry = loaded;
                    entry.References = 0;
                    entry.Node = _lru.AddFirst(entry);
                    _entries.Add(entry.Id, entry);
                    _cachedBytes += entry.Size;
                }

                entry.References++;
                TouchLocked(entry);
                TrimLocked();
                return new AssetLease(this, entry);
            }
        }

        private static async Task<Entry> LoadEntryAsync(ImageRecord item, ImageLibraryService images)
        {
            var file = await images.GetFileAsync(item);
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size == 0 || properties.Size > MaxGifBytes)
                throw new InvalidOperationException("GIF files must be between 1 byte and 32 MiB.");

            var stream = new InMemoryRandomAccessStream();
            try
            {
                using (var source = await file.OpenAsync(FileAccessMode.Read))
                {
                    await RandomAccessStream.CopyAsync(source, stream);
                }

                if (stream.Size == 0 || stream.Size > MaxGifBytes)
                    throw new InvalidOperationException("GIF files must be between 1 byte and 32 MiB.");

                stream.Seek(0);
                return new Entry
                {
                    Id = item.Id,
                    Stream = stream,
                    Size = (long)stream.Size,
                    References = 0
                };
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        private void Release(Entry entry)
        {
            lock (_sync)
            {
                Entry current;
                if (!_entries.TryGetValue(entry.Id, out current) || !ReferenceEquals(current, entry))
                    return;
                if (entry.References > 0) entry.References--;
                TouchLocked(entry);
                TrimLocked();
            }
        }

        private void TouchLocked(Entry entry)
        {
            if (entry.Node == null) return;
            _lru.Remove(entry.Node);
            _lru.AddFirst(entry.Node);
        }

        private void TrimLocked()
        {
            while (_cachedBytes > MaxCacheBytes)
            {
                var node = _lru.Last;
                while (node != null && node.Value.References > 0) node = node.Previous;
                if (node == null) return;

                var entry = node.Value;
                _lru.Remove(node);
                entry.Node = null;
                _entries.Remove(entry.Id);
                _cachedBytes -= entry.Size;
                try { entry.Stream.Dispose(); }
                catch (Exception ex) { AppDiagnostics.Log("Evict cached GIF stream", ex); }
            }
        }
    }
}