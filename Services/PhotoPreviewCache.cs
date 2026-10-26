using System.IO;
using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

/// <summary>
/// A full-resolution, EXIF-oriented preview cache. Its byte budget accounts for retained output frames,
/// charged at least four bytes per pixel; WIC buffers and process overhead are outside this budget. The viewer may
/// retain the same frame and a single decoder may hold an additional frame. An oversized foreground
/// frame is displayed without retaining it in this cache; oversized background frames are skipped.
/// </summary>
public sealed class PhotoPreviewCache : IDisposable
{
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Func<string, BitmapSource> _loader;
    private readonly Dictionary<FileKey, CacheEntry> _cache = new();
    private readonly LinkedList<FileKey> _lru = new();
    private readonly Dictionary<FileKey, Request> _requests = new();
    private readonly Queue<Request> _foreground = new();
    private readonly Queue<Request> _prefetch = new();
    private readonly Dictionary<FileKey, int> _neighborhoodRanks = new();
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _worker;
    private long _cachedBytes, _generation;
    private FileKey? _currentKey;
    private int _inFlight;
    private bool _disposed;

    public PhotoPreviewCache(long budgetBytes = DefaultBudgetBytes, Func<string, BitmapSource>? loader = null)
    {
        if (budgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        BudgetBytes = budgetBytes;
        _loader = loader ?? (path => ImageLoader.Load(path));
        _worker = Task.Run(WorkerAsync);
    }

    public long BudgetBytes { get; }
    public long CachedBytes { get { lock (_gate) return _cachedBytes; } }
    public int CachedCount { get { lock (_gate) return _cache.Count; } }
    public int InFlightCount { get { lock (_gate) return _inFlight; } }
    public int PendingCount { get { lock (_gate) return _requests.Count; } }

    public Task<BitmapSource> GetAsync(PhotoItem photo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<BitmapSource>(cancellationToken);
        FileKey key;
        try { key = ReadKey(photo.FilePath); }
        catch (Exception ex) { return Task.FromException<BitmapSource>(ex); }
        Task<BitmapSource> result;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _currentKey = key;
            RemoveOldVersions(key);
            if (_cache.TryGetValue(key, out var entry))
            {
                Touch(entry);
                return Task.FromResult(entry.Image);
            }
            if (!_requests.TryGetValue(key, out var request) || request.Invalidated)
            {
                request = new Request(key, EstimatedBytes(photo.PixelWidth, photo.PixelHeight));
                _requests[key] = request;
            }
            var waiter = new Waiter(cancellationToken);
            request.Waiters.Add(waiter);
            result = waiter.Completion.Task;
            if (!request.ForegroundQueued && !request.InFlight)
            {
                request.ForegroundQueued = true;
                _foreground.Enqueue(request);
            }
        }
        Wake();
        return result;
    }

    /// <summary>Prefetches current, next, previous, next + 2, previous - 2 across the flattened library.</summary>
    public void UpdateNeighborhood(IReadOnlyList<PhotoItem> photos, PhotoItem current)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(current);
        int index = -1;
        for (int i = 0; i < photos.Count; i++)
            if (ReferenceEquals(photos[i], current) || photos[i].Id == current.Id) { index = i; break; }
        var candidates = new List<(FileKey Key, long Bytes)>();
        if (index >= 0)
            foreach (int offset in new[] { 0, 1, -1, 2, -2 })
            {
                int candidate = index + offset;
                if (candidate < 0 || candidate >= photos.Count) continue;
                var photo = photos[candidate];
                long bytes = EstimatedBytes(photo.PixelWidth, photo.PixelHeight);
                // Unknown dimensions must first be read by an explicit foreground request.
                if (bytes <= 0 || bytes > BudgetBytes) continue;
                try { candidates.Add((ReadKey(photo.FilePath), bytes)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _generation++;
            _prefetch.Clear();
            _neighborhoodRanks.Clear();
            _currentKey = null;
            for (int rank = 0; rank < candidates.Count; rank++)
            {
                var candidate = candidates[rank];
                if (!_neighborhoodRanks.TryAdd(candidate.Key, rank)) continue;
                if (rank == 0 && index >= 0 && candidate.Key.Path == CanonicalPath(current.FilePath)) _currentKey = candidate.Key;
                RemoveOldVersions(candidate.Key);
                if (_cache.ContainsKey(candidate.Key)) continue;
                if (!_requests.TryGetValue(candidate.Key, out var request) || request.Invalidated)
                {
                    request = new Request(candidate.Key, candidate.Bytes);
                    _requests[candidate.Key] = request;
                }
                request.PrefetchGeneration = _generation;
                if (!request.InFlight) _prefetch.Enqueue(request);
            }
            RemoveObsoleteQueuedRequests();
        }
        Wake();
    }

    public void TrimToPaths(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var allowed = new HashSet<string>(paths.Select(CanonicalPath), StringComparer.Ordinal);
        List<Waiter> canceled = new();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var key in _cache.Keys.Where(key => !allowed.Contains(key.Path)).ToArray()) RemoveEntry(key);
            foreach (var key in _neighborhoodRanks.Keys.Where(key => !allowed.Contains(key.Path)).ToArray()) _neighborhoodRanks.Remove(key);
            if (_currentKey is { } current && !allowed.Contains(current.Path)) _currentKey = null;
            foreach (var request in _requests.Values.Where(request => !allowed.Contains(request.Key.Path)).ToArray())
            {
                request.Invalidated = true;
                canceled.AddRange(request.Waiters);
                if (!request.InFlight) { request.Completed = true; _requests.Remove(request.Key); }
            }
        }
        foreach (var waiter in canceled) { waiter.Cancel(); waiter.Dispose(); }
    }

    public void Clear()
    {
        List<Waiter> canceled;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _generation++;
            _currentKey = null;
            _neighborhoodRanks.Clear();
            _cache.Clear(); _lru.Clear(); _cachedBytes = 0;
            _foreground.Clear(); _prefetch.Clear();
            canceled = _requests.Values.SelectMany(request => request.Waiters).ToList();
            foreach (var request in _requests.Values) { request.Invalidated = true; if (!request.InFlight) request.Completed = true; }
            foreach (var key in _requests.Where(pair => !pair.Value.InFlight).Select(pair => pair.Key).ToArray()) _requests.Remove(key);
        }
        foreach (var waiter in canceled) { waiter.Cancel(); waiter.Dispose(); }
    }

    private async Task WorkerAsync()
    {
        try
        {
            while (true)
            {
                await _wake.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                Request? request;
                while ((request = TakeNext()) is not null) Process(request);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    private Request? TakeNext()
    {
        lock (_gate)
        {
            if (_disposed) return null;
            while (_foreground.Count > 0 || _prefetch.Count > 0)
            {
                var request = _foreground.Count > 0 ? _foreground.Dequeue() : _prefetch.Dequeue();
                request.ForegroundQueued = false;
                if (request.Completed || request.InFlight || request.Invalidated) continue;
                bool foreground = request.HasWaiters;
                bool wanted = request.PrefetchGeneration == _generation && _neighborhoodRanks.ContainsKey(request.Key);
                if (!foreground && !wanted) { FinishUnused(request); continue; }
                if (!foreground && (request.EstimatedBytes <= 0 || request.EstimatedBytes > BudgetBytes))
                { FinishUnused(request); continue; }
                long reserve = Math.Min(request.EstimatedBytes, BudgetBytes);
                if (reserve > 0 && !MakeSpace(reserve, foreground ? null : request.Key))
                { FinishUnused(request); continue; }
                request.InFlight = true;
                _inFlight = 1;
                return request;
            }
            return null;
        }
    }

    private void Process(Request request)
    {
        BitmapSource? image = null;
        long bytes = 0;
        Exception? error = null;
        try
        {
            if (ReadKey(request.Key.Path) != request.Key) throw new IOException("照片在等待预览时已被修改，请重新选择。");
            image = _loader(request.Key.Path);
            if (image is null) throw new InvalidDataException("预览解码器没有返回图像。");
            if (!image.IsFrozen) image.Freeze();
            if (ReadKey(request.Key.Path) != request.Key) throw new IOException("照片在载入预览时已被修改，请重新选择。");
            bytes = DecodedBytes(image);
        }
        catch (Exception ex) { error = ex; }

        List<Waiter> waiters;
        bool discard;
        lock (_gate)
        {
            _inFlight = 0;
            request.InFlight = false;
            request.Completed = true;
            if (_requests.TryGetValue(request.Key, out var registered) && ReferenceEquals(registered, request)) _requests.Remove(request.Key);
            waiters = request.Waiters.ToList();
            bool foreground = request.HasWaiters;
            // Stale queue nodes must not retain completed Task results after an LRU eviction.
            request.Waiters.Clear();
            bool wanted = request.PrefetchGeneration == _generation && _neighborhoodRanks.ContainsKey(request.Key);
            discard = _disposed || request.Invalidated || (!foreground && !wanted);
            if (!discard && error is null && image is not null)
            {
                if (bytes <= BudgetBytes && MakeSpace(bytes, foreground ? null : request.Key))
                {
                    var node = _lru.AddFirst(request.Key);
                    _cache.Add(request.Key, new CacheEntry(image, bytes, node));
                    _cachedBytes += bytes;
                }
            }
        }
        foreach (var waiter in waiters)
        {
            if (discard) waiter.Cancel();
            else if (error is not null) waiter.Completion.TrySetException(error);
            else waiter.Completion.TrySetResult(image!);
            waiter.Dispose();
        }
    }

    // A background candidate may replace stale/farther frames, never a useful nearer neighbor.
    private bool MakeSpace(long incoming, FileKey? backgroundKey)
    {
        int rank = backgroundKey is { } candidate && _neighborhoodRanks.TryGetValue(candidate, out int r) ? r : int.MaxValue;
        while (_cachedBytes > BudgetBytes - incoming)
        {
            var node = _lru.Last;
            while (node is not null && backgroundKey is not null &&
                (node.Value == _currentKey || _neighborhoodRanks.TryGetValue(node.Value, out int existingRank) && existingRank <= rank))
                node = node.Previous;
            if (node is null) return false;
            RemoveEntry(node.Value);
        }
        return true;
    }

    private void Touch(CacheEntry entry) { _lru.Remove(entry.Node); _lru.AddFirst(entry.Node); }
    private void RemoveEntry(FileKey key)
    {
        if (!_cache.Remove(key, out var entry)) return;
        _lru.Remove(entry.Node); _cachedBytes -= entry.Bytes;
    }
    private void RemoveOldVersions(FileKey current)
    {
        foreach (var key in _cache.Keys.Where(key => key.Path == current.Path && key != current).ToArray()) RemoveEntry(key);
    }
    private void FinishUnused(Request request)
    {
        request.Completed = true;
        _requests.Remove(request.Key);
        foreach (var waiter in request.Waiters) waiter.Dispose();
        request.Waiters.Clear();
    }
    private void RemoveObsoleteQueuedRequests()
    {
        foreach (var request in _requests.Values.Where(request => !request.InFlight && !request.HasWaiters &&
            request.PrefetchGeneration != _generation).ToArray()) FinishUnused(request);
    }
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }
    private static string CanonicalPath(string path) => Path.GetFullPath(path).ToUpperInvariant();
    private static FileKey ReadKey(string path)
    {
        string canonical = CanonicalPath(path);
        var file = new FileInfo(canonical);
        if (!file.Exists) throw new FileNotFoundException("照片文件不存在。", path);
        return new FileKey(canonical, file.Length, file.LastWriteTimeUtc.Ticks);
    }
    private static long EstimatedBytes(int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        long pixels = (long)width * height;
        return pixels > long.MaxValue / 4 ? long.MaxValue : pixels * 4;
    }
    private static long DecodedBytes(BitmapSource image)
    {
        long bits = Math.Max(32, image.Format.BitsPerPixel);
        long stride = checked(((long)image.PixelWidth * bits + 31) / 32 * 4);
        return checked(stride * image.PixelHeight);
    }

    public void Dispose()
    {
        List<Waiter> waiters;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            waiters = _requests.Values.SelectMany(request => request.Waiters).ToList();
            foreach (var request in _requests.Values) request.Invalidated = true;
            _requests.Clear(); _foreground.Clear(); _prefetch.Clear();
            _cache.Clear(); _lru.Clear(); _neighborhoodRanks.Clear(); _cachedBytes = 0;
        }
        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(new ObjectDisposedException(nameof(PhotoPreviewCache)));
            waiter.Dispose();
        }
        _shutdown.Cancel();
        _ = _worker.ContinueWith(_ => { _wake.Dispose(); _shutdown.Dispose(); }, TaskScheduler.Default);
    }

    private readonly record struct FileKey(string Path, long Length, long WriteTicks);
    private sealed record CacheEntry(BitmapSource Image, long Bytes, LinkedListNode<FileKey> Node);
    private sealed class Request(FileKey key, long estimatedBytes)
    {
        public FileKey Key { get; } = key;
        public long EstimatedBytes { get; } = estimatedBytes;
        public List<Waiter> Waiters { get; } = new();
        public long PrefetchGeneration { get; set; } = -1;
        public bool ForegroundQueued { get; set; }
        public bool InFlight { get; set; }
        public bool Completed { get; set; }
        public bool Invalidated { get; set; }
        public bool HasWaiters => Waiters.Any(waiter => !waiter.Completion.Task.IsCompleted);
    }
    private sealed class Waiter : IDisposable
    {
        private readonly CancellationTokenRegistration _registration;
        private readonly CancellationToken _token;
        public TaskCompletionSource<BitmapSource> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Waiter(CancellationToken token)
        {
            _token = token;
            _registration = token.Register(() => Completion.TrySetCanceled(token));
        }
        public void Cancel() => Completion.TrySetCanceled(_token.IsCancellationRequested ? _token : new CancellationToken(true));
        public void Dispose() => _registration.Dispose();
    }
}
