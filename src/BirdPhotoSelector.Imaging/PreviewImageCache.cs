using System.IO;


namespace BirdPhotoSelector.Imaging;

/// <summary>
/// 显示方向、全分辨率图像的有界预加载缓存。单工作线程防止多张大图同时解码，
/// 前台优先，相同文件共享请求，后台邻居按距离保护，缓存按 LRU 淘汰。
/// 预算至少按四字节/像素计算；解码临时内存、当前显示帧、模型与进程开销另计。
/// 超大前台帧允许显示但不保留，超大后台来源跳过，昂贵来源仅在前台解码。
/// </summary>
public sealed class PreviewImageCache : IDisposable
{
    /// <summary>默认核心预算；现有桌面界面可显式使用更大的预算。</summary>
    public const long DefaultBudgetBytes = 256L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Func<string, CancellationToken, ImageFrame> _loader;
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

    /// <summary>创建单工作线程的有界缓存。解码函数必须返回线程安全的不可变帧，并响应取消。</summary>
    public PreviewImageCache(Func<string, CancellationToken, ImageFrame> loader, long budgetBytes = DefaultBudgetBytes)
    {
        ArgumentNullException.ThrowIfNull(loader);
        if (budgetBytes <= 0) throw new ArgumentOutOfRangeException(nameof(budgetBytes));
        BudgetBytes = budgetBytes; _loader = loader;
        _worker = Task.Run(WorkerAsync);
    }

    /// <summary>最大保留帧内存；不是进程总内存上限。</summary>
    public long BudgetBytes { get; }
    /// <summary>当前缓存中保留帧的计费字节数。</summary>
    public long CachedBytes { get { lock (_gate) return _cachedBytes; } }
    /// <summary>当前可复用的完整帧数量。</summary>
    public int CachedCount { get { lock (_gate) return _cache.Count; } }
    /// <summary>正在解码的数量；该调度器始终最多为一。</summary>
    public int InFlightCount { get { lock (_gate) return _inFlight; } }
    /// <summary>等待或正在处理的去重请求数。</summary>
    public int PendingCount { get { lock (_gate) return _requests.Count; } }

    /// <summary>前台读取；相同来源共享解码，每个等待者可独立取消，命中时复用同一图像帧。</summary>
    public Task<ImageFrame> GetAsync(PreviewSource photo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<ImageFrame>(cancellationToken);
        FileKey key;
        try { key = ReadKey(SourcePath(photo)); }
        catch (Exception ex) { return Task.FromException<ImageFrame>(ex); }
        Task<ImageFrame> result;
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
                request = new Request(key, EstimatedBytes(photo.PixelWidth, photo.PixelHeight), photo.ForegroundOnly);
                _requests[key] = request;
            }
            var waiter = new Waiter(cancellationToken);
            request.Waiters.Add(waiter);
            waiter.ArmCancellation(() => CancelAbandonedDecode(request));
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
    /// <summary>按当前位置、后一张、前一张、后两张、前两张更新预取队列；RAW 不进入后台队列。</summary>
    public void UpdateNeighborhood(IReadOnlyList<PreviewSource> photos, PreviewSource current)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ArgumentNullException.ThrowIfNull(current);
        int index = -1;
        for (int i = 0; i < photos.Count; i++)
            if (ReferenceEquals(photos[i], current) || photos[i].Id == current.Id) { index = i; break; }
        var candidates = new List<(FileKey Key, long Bytes)>();
        FileKey? selectedKey = null;
        try { selectedKey = ReadKey(SourcePath(current)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
        if (index >= 0)
            foreach (int offset in new[] { 0, 1, -1, 2, -2 })
            {
                int candidate = index + offset;
                if (candidate < 0 || candidate >= photos.Count) continue;
                var photo = photos[candidate];
                // RAW decoding is deliberately foreground-only, even when dimensions are
                // already known. Merely browsing a neighbour must not unpack another RAW.
                if (photo.ForegroundOnly) continue;
                long bytes = EstimatedBytes(photo.PixelWidth, photo.PixelHeight);
                // Unknown dimensions must first be read by an explicit foreground request.
                if (bytes <= 0 || bytes > BudgetBytes) continue;
                try { candidates.Add((ReadKey(SourcePath(photo)), bytes)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
            }
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _generation++;
            _prefetch.Clear();
            _neighborhoodRanks.Clear();
            _currentKey = selectedKey;
            for (int rank = 0; rank < candidates.Count; rank++)
            {
                var candidate = candidates[rank];
                if (!_neighborhoodRanks.TryAdd(candidate.Key, rank)) continue;
                if (rank == 0 && index >= 0 && candidate.Key.Path == CanonicalPath(SourcePath(current))) _currentKey = candidate.Key;
                RemoveOldVersions(candidate.Key);
                if (_cache.ContainsKey(candidate.Key)) continue;
                if (!_requests.TryGetValue(candidate.Key, out var request) || request.Invalidated)
                {
                    request = new Request(candidate.Key, candidate.Bytes, foregroundOnly: false);
                    _requests[candidate.Key] = request;
                }
                request.PrefetchGeneration = _generation;
                if (!request.InFlight) _prefetch.Enqueue(request);
            }
            RemoveObsoleteQueuedRequests();
        }
        Wake();
    }

    /// <summary>清除已从照片库移除的来源；正在解码的旧结果作废，避免重新进入缓存。</summary>
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
                request.CancelDecode();
                canceled.AddRange(request.Waiters);
                if (!request.InFlight) { request.Completed = true; _requests.Remove(request.Key); request.Dispose(); }
            }
        }
        foreach (var waiter in canceled) { waiter.Cancel(); waiter.Dispose(); }
    }

    /// <summary>清空帧和排队请求，并作废正在解码的结果；不会等待解码线程结束。</summary>
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
            foreach (var request in _requests.Values)
            {
                request.Invalidated = true;
                request.CancelDecode();
                if (!request.InFlight) { request.Completed = true; request.Dispose(); }
            }
            foreach (var key in _requests.Where(pair => !pair.Value.InFlight).Select(pair => pair.Key).ToArray()) _requests.Remove(key);
        }
        foreach (var waiter in canceled) { waiter.Cancel(); waiter.Dispose(); }
    }

    /// <summary>唯一工作线程：先处理前台，再处理当前一代的相邻请求；关闭时由令牌退出。</summary>
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

    /// <summary>在锁内选取仍有效的请求并预留空间；远处预取不能挤掉更近的缓存帧。</summary>
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

    /// <summary>在锁外解码，前后验证文件版本；锁内入库后再完成等待者，防止回调重入。</summary>
    private void Process(Request request)
    {
        ImageFrame? image = null;
        long bytes = 0;
        Exception? error = null;
        try
        {
            if (ReadKey(request.Key.Path) != request.Key) throw new IOException("照片在等待预览时已被修改，请重新选择。");
            request.DecodeCancellation.Token.ThrowIfCancellationRequested();
            image = _loader(request.Key.Path, request.DecodeCancellation.Token);
            if (image is null) throw new InvalidDataException("预览解码器没有返回图像。");
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
        request.Dispose();
    }

    // A background candidate may replace stale/farther frames, never a useful nearer neighbor.
    /// <summary>按 LRU 腾出预算；后台请求额外保护当前照片和距离更近的邻居。</summary>
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

    /// <summary>命中后提升到最近使用端；仅在缓存锁内调用。</summary>
    private void Touch(CacheEntry entry) { _lru.Remove(entry.Node); _lru.AddFirst(entry.Node); }
    /// <summary>同步移除字典、LRU 节点和字节计数；仅在锁内调用。</summary>
    private void RemoveEntry(FileKey key)
    {
        if (!_cache.Remove(key, out var entry)) return;
        _lru.Remove(entry.Node); _cachedBytes -= entry.Bytes;
    }
    /// <summary>同一路径的长度或修改时间发生变化时移除旧版本。</summary>
    private void RemoveOldVersions(FileKey current)
    {
        foreach (var key in _cache.Keys.Where(key => key.Path == current.Path && key != current).ToArray()) RemoveEntry(key);
    }
    /// <summary>释放已经失去等待者和邻居优先级的排队请求。</summary>
    private void FinishUnused(Request request)
    {
        request.Completed = true;
        _requests.Remove(request.Key);
        foreach (var waiter in request.Waiters) waiter.Dispose();
        request.Waiters.Clear();
        request.Dispose();
    }
    /// <summary>切图后丢弃上一代的无用预取，保留仍有人等待的前台请求。</summary>
    private void RemoveObsoleteQueuedRequests()
    {
        foreach (var request in _requests.Values.Where(request => !request.InFlight && !request.HasWaiters &&
            request.PrefetchGeneration != _generation).ToArray()) FinishUnused(request);
    }
    /// <summary>合并唤醒信号；重复通知或关闭期间的通知无需报错。</summary>
    private void Wake() { try { _wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { } }
    /// <summary>读取不可变快照中的实际来源路径。</summary>
    private static string SourcePath(PreviewSource photo) => photo.Path;
    /// <summary>昂贵来源失去最后一个等待者时取消；其他等待者仍存在则保留共享解码。</summary>
    private void CancelAbandonedDecode(Request request)
    {
        if (!request.ForegroundOnly) return;
        lock (_gate)
        {
            // One canceled observer must not cancel a shared decode still awaited by another.
            if (request.Completed || request.HasWaiters) return;
            request.Invalidated = true;
            request.CancelDecode();
            if (!request.InFlight)
            {
                if (_requests.TryGetValue(request.Key, out var registered) && ReferenceEquals(registered, request))
                    _requests.Remove(request.Key);
                request.Completed = true;
                request.Dispose();
            }
        }
    }
    // Windows retains v1 case-insensitive keys. Unix keeps case, so distinct files never collapse.
    /// <summary>按当前平台构造缓存键；Unix 不将大小写不同的路径合并。</summary>
    private static string CanonicalPath(string path) => FilePaths.Key(path);
    /// <summary>路径、长度、UTC 修改时间共同组成文件版本，不跨版本复用像素。</summary>
    private static FileKey ReadKey(string path)
    {
        string canonical = CanonicalPath(path);
        var file = new FileInfo(canonical);
        if (!file.Exists) throw new FileNotFoundException("照片文件不存在。", path);
        return new FileKey(canonical, file.Length, file.LastWriteTimeUtc.Ticks);
    }
    /// <summary>使用完整尺寸预估预算；未知尺寸禁止预取，大图仍可由前台单独显示。</summary>
    private static long EstimatedBytes(int width, int height)
    {
        if (width <= 0 || height <= 0) return 0;
        long pixels = (long)width * height;
        return pixels > long.MaxValue / 4 ? long.MaxValue : pixels * 4;
    }
    /// <summary>由后端报告保留内存；同时强制每像素至少四字节，避免 RGB24 低估预算。</summary>
    private static long DecodedBytes(ImageFrame image) => Math.Max(image.RetainedBytes,
        checked((long)image.PixelWidth * image.PixelHeight * 4));

    /// <summary>立即拒绝新请求并结束等待者；解码退出后释放调度资源，不阻塞 UI 关闭。</summary>
    public void Dispose()
    {
        List<Waiter> waiters;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            waiters = _requests.Values.SelectMany(request => request.Waiters).ToList();
            foreach (var request in _requests.Values)
            {
                request.Invalidated = true;
                request.CancelDecode();
                if (!request.InFlight) request.Dispose();
            }
            _requests.Clear(); _foreground.Clear(); _prefetch.Clear();
            _cache.Clear(); _lru.Clear(); _neighborhoodRanks.Clear(); _cachedBytes = 0;
        }
        foreach (var waiter in waiters)
        {
            waiter.Completion.TrySetException(new ObjectDisposedException(nameof(PreviewImageCache)));
            waiter.Dispose();
        }
        _shutdown.Cancel();
        _ = _worker.ContinueWith(_ => { _wake.Dispose(); _shutdown.Dispose(); }, TaskScheduler.Default);
    }

    /// <summary>文件版本键，不包含可变的照片对象或任何 UI 引用。</summary>
    private readonly record struct FileKey(string Path, long Length, long WriteTicks);
    /// <summary>缓存帧及其 LRU 节点；所有更新都受同一把锁保护。</summary>
    private sealed record CacheEntry(ImageFrame Image, long Bytes, LinkedListNode<FileKey> Node);
    /// <summary>一次共享解码的状态；不在完成后保留 Task 结果，以免阻止淘汰帧释放。</summary>
    private sealed class Request(FileKey key, long estimatedBytes, bool foregroundOnly) : IDisposable
    {
        public FileKey Key { get; } = key;
        public bool ForegroundOnly { get; } = foregroundOnly;
        public long EstimatedBytes { get; } = estimatedBytes;
        public List<Waiter> Waiters { get; } = new();
        public CancellationTokenSource DecodeCancellation { get; } = new();
        public long PrefetchGeneration { get; set; } = -1;
        public bool ForegroundQueued { get; set; }
        public bool InFlight { get; set; }
        public bool Completed { get; set; }
        public bool Invalidated { get; set; }
        public bool HasWaiters => Waiters.Any(waiter => !waiter.Completion.Task.IsCompleted);
        /// <summary>取消共享解码；与清空、关闭并发时允许已经释放。</summary>
        public void CancelDecode() { try { DecodeCancellation.Cancel(); } catch (ObjectDisposedException) { } }
        /// <summary>解码已退出或请求未执行时释放其专用取消源。</summary>
        public void Dispose() => DecodeCancellation.Dispose();
    }
    /// <summary>单个调用方的取消和结果通道，不拥有共享解码的生命周期。</summary>
    private sealed class Waiter : IDisposable
    {
        private CancellationTokenRegistration _registration;
        private readonly CancellationToken _token;
        public TaskCompletionSource<ImageFrame> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>保存调用方令牌；登记回调推迟到等待者已加入共享请求之后。</summary>
        public Waiter(CancellationToken token)
        {
            _token = token;
        }
        /// <summary>先结束此等待者，再判断是否应取消已经无人使用的昂贵解码。</summary>
        public void ArmCancellation(Action afterCancel) => _registration = _token.Register(() =>
        {
            Completion.TrySetCanceled(_token);
            afterCancel();
        });
        /// <summary>来源被移除或请求作废时，以取消状态结束此调用方。</summary>
        public void Cancel() => Completion.TrySetCanceled(_token.IsCancellationRequested ? _token : new CancellationToken(true));
        // Cleanup can run under the cache gate. Unregister never waits for a callback
        // that may itself be waiting to acquire that gate.
        /// <summary>使用非阻塞注销，避免锁内清理与正在等待缓存锁的取消回调互相等待。</summary>
        public void Dispose() => _registration.Unregister();
    }
}
