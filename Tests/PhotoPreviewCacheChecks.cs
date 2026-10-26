using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

/// <summary>Deterministic scheduler/cache checks. Only generated temporary files and tiny bitmaps are used.</summary>
internal static class PhotoPreviewCacheChecks
{
    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
            Console.WriteLine("  OK CACHE " + message);
        }
        string root = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.CacheChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PhotoItem Photo(string name, int width = 2, int height = 2)
            {
                string path = Path.Combine(root, name + ".jpg");
                File.WriteAllBytes(path, new byte[] { 1, 2 });
                return new PhotoItem { FilePath = path, PixelWidth = width, PixelHeight = height };
            }
            var a = Photo("a"); var b = Photo("b"); var c = Photo("c"); var d = Photo("d"); var e = Photo("e");
            string Name(string path) => Path.GetFileNameWithoutExtension(path).ToLowerInvariant();

            string realPath = Path.Combine(root, "full-resolution.jpg");
            var jpeg = new JpegBitmapEncoder(); jpeg.Frames.Add(BitmapFrame.Create(Image(320, 180)));
            using (var output = File.Create(realPath)) jpeg.Save(output);
            using (var cache = new PhotoPreviewCache(1024 * 1024))
            {
                var image = await cache.GetAsync(new PhotoItem { FilePath = realPath, PixelWidth = 320, PixelHeight = 180 });
                Check(image.IsFrozen && image.PixelWidth == 320 && image.PixelHeight == 180,
                    "the production loader returns a frozen full-resolution JPEG rather than a thumbnail");
            }

            var loads = new ConcurrentDictionary<string, int>();
            using (var cache = new PhotoPreviewCache(32, path => { loads.AddOrUpdate(Name(path), 1, (_, count) => count + 1); return Image(2, 2); }))
            {
                var first = await cache.GetAsync(a);
                await cache.GetAsync(b);
                Check(ReferenceEquals(first, await cache.GetAsync(a)), "a cache hit reuses the exact frozen bitmap object");
                await cache.GetAsync(c);
                Check(cache.CachedBytes == 32 && cache.CachedCount == 2, "decoded frame accounting stays within the byte budget");
                await cache.GetAsync(b);
                Check(loads["b"] == 2 && loads["a"] == 1, "foreground cache eviction follows least-recently-used order");
                Check(first.IsFrozen, "cached previews are frozen for safe UI-thread consumption");
                cache.TrimToPaths(new[] { b.FilePath });
                Check(cache.CachedCount == 1 && cache.CachedBytes == 16, "library trimming releases entries whose paths were removed");
                cache.Clear();
                Check(cache.CachedCount == 0 && cache.CachedBytes == 0, "clearing releases retained cache frames and accounting");
            }

            int versionLoads = 0;
            using (var cache = new PhotoPreviewCache(64, _ => { Interlocked.Increment(ref versionLoads); return Image(2, 2); }))
            {
                var first = await cache.GetAsync(a);
                File.AppendAllText(a.FilePath, "length change");
                var changed = await cache.GetAsync(a);
                Check(!ReferenceEquals(first, changed) && versionLoads == 2 && cache.CachedCount == 1,
                    "file length changes invalidate the old cached version");
                File.SetLastWriteTimeUtc(a.FilePath, File.GetLastWriteTimeUtc(a.FilePath).AddSeconds(10));
                await cache.GetAsync(a);
                Check(versionLoads == 3 && cache.CachedCount == 1, "mtime changes invalidate a same-size cached file");
            }

            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var cancellation = new CancellationTokenSource())
            {
                int calls = 0;
                using var cache = new PhotoPreviewCache(64, _ =>
                {
                    Interlocked.Increment(ref calls); started.Set(); WaitRelease(release); return Image(2, 2);
                });
                try
                {
                    var canceled = cache.GetAsync(a, cancellation.Token);
                    await WaitUntil(() => started.IsSet);
                    var survivor = cache.GetAsync(a);
                    cancellation.Cancel();
                    await ExpectCanceled(canceled);
                    release.Set();
                    var image = await survivor.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(calls == 1 && image.IsFrozen, "concurrent same-file requests share one decode despite one caller canceling");
                }
                finally { release.Set(); }
            }

            var order = new ConcurrentQueue<string>();
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int active = 0, maxActive = 0, mutations = 0;
                a.PropertyChanged += (_, _) => Interlocked.Increment(ref mutations);
                b.PropertyChanged += (_, _) => Interlocked.Increment(ref mutations);
                using var cache = new PhotoPreviewCache(128, path =>
                {
                    int now = Interlocked.Increment(ref active); maxActive = Math.Max(maxActive, now);
                    string name = Name(path); order.Enqueue(name);
                    if (name == "a") { started.Set(); WaitRelease(release); }
                    try { return Image(2, 2); }
                    finally { Interlocked.Decrement(ref active); }
                });
                try
                {
                    cache.UpdateNeighborhood(new[] { a, b }, a);
                    await WaitUntil(() => started.IsSet);
                    var foreground = cache.GetAsync(c);
                    release.Set();
                    await foreground.WaitAsync(TimeSpan.FromSeconds(5));
                    await WaitUntil(() => cache.PendingCount == 0);
                    Check(order.ToArray().SequenceEqual(new[] { "a", "c", "b" }),
                        "foreground work runs before queued preloads after the one existing decode finishes");
                    Check(maxActive == 1 && cache.InFlightCount == 0, "the cache never runs more than one decoder concurrently");
                    Check(mutations == 0, "background prefetch never mutates UI-owned photo models");
                }
                finally { release.Set(); }
            }

            order = new ConcurrentQueue<string>();
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var cache = new PhotoPreviewCache(128, path =>
            {
                string name = Name(path); order.Enqueue(name);
                if (name == "a") { started.Set(); WaitRelease(release); }
                return Image(2, 2);
            }))
            {
                try
                {
                    cache.UpdateNeighborhood(new[] { a, b }, a);
                    await WaitUntil(() => started.IsSet);
                    cache.UpdateNeighborhood(new[] { c }, c);
                    release.Set();
                    await WaitUntil(() => cache.PendingCount == 0);
                    Check(order.ToArray().SequenceEqual(new[] { "a", "c" }) && cache.CachedCount == 1,
                        "navigation drops obsolete queued prefetch and discards its already-running old-generation result");
                }
                finally { release.Set(); }
            }

            order = new ConcurrentQueue<string>();
            using (var cache = new PhotoPreviewCache(48, path => { order.Enqueue(Name(path)); return Image(2, 2); }))
            {
                cache.UpdateNeighborhood(new[] { a, b, c, d, e }, c);
                await WaitUntil(() => cache.PendingCount == 0);
                Check(order.ToArray().SequenceEqual(new[] { "c", "d", "b" }) && cache.CachedBytes == 48,
                    "current and nearest neighbors stay cached when farther preloads would exceed the budget");
                await cache.GetAsync(c); await cache.GetAsync(d); await cache.GetAsync(b);
                Check(order.Count == 3, "farther prefetch does not churn out useful current or adjacent frames");
            }

            var huge = Photo("huge", 10, 10);
            int hugeCalls = 0;
            using (var cache = new PhotoPreviewCache(16, _ => { Interlocked.Increment(ref hugeCalls); return Image(10, 10); }))
            {
                cache.UpdateNeighborhood(new[] { huge }, huge);
                Check(cache.PendingCount == 0 && hugeCalls == 0, "oversized background previews are rejected before decoding");
                var image = await cache.GetAsync(huge);
                await cache.GetAsync(huge);
                Check(image.PixelWidth == 10 && hugeCalls == 2 && cache.CachedBytes == 0,
                    "an oversized foreground frame preserves full resolution while bypassing retained cache storage");
            }

            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var cache = new PhotoPreviewCache(64, _ => { started.Set(); WaitRelease(release); return Image(2, 2); }))
            {
                try
                {
                    var loading = cache.GetAsync(a);
                    await WaitUntil(() => started.IsSet);
                    File.AppendAllText(a.FilePath, "changed during decode");
                    release.Set();
                    await ExpectThrows<IOException>(loading);
                    Check(cache.CachedCount == 0, "a file modified during decode is never published as a valid cached version");
                }
                finally { release.Set(); }
            }

            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int calls = 0;
                using var cache = new PhotoPreviewCache(64, _ =>
                {
                    if (Interlocked.Increment(ref calls) == 1) { started.Set(); WaitRelease(release); }
                    return Image(2, 2);
                });
                try
                {
                    var old = cache.GetAsync(a);
                    await WaitUntil(() => started.IsSet);
                    cache.Clear();
                    await ExpectCanceled(old);
                    var fresh = cache.GetAsync(a);
                    release.Set();
                    await fresh.WaitAsync(TimeSpan.FromSeconds(5));
                    Check(calls == 2 && cache.CachedCount == 1,
                        "clearing an in-flight generation permits a fresh same-file request without reusing its canceled result");
                }
                finally { release.Set(); }
            }

            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var canceled = new CancellationTokenSource())
            {
                var calls = new ConcurrentQueue<string>();
                var cache = new PhotoPreviewCache(64, path =>
                {
                    calls.Enqueue(Name(path)); started.Set(); WaitRelease(release); return Image(2, 2);
                });
                try
                {
                    var active = cache.GetAsync(a);
                    await WaitUntil(() => started.IsSet);
                    var queued = cache.GetAsync(b, canceled.Token);
                    canceled.Cancel();
                    await ExpectCanceled(queued);
                    cache.Dispose();
                    await ExpectThrows<ObjectDisposedException>(active);
                    release.Set();
                    await WaitUntil(() => cache.InFlightCount == 0);
                    Check(calls.ToArray().SequenceEqual(new[] { "a" }) && cache.CachedBytes == 0,
                        "cancellation and disposal stop queued work and release retained storage without waiting on WIC");
                }
                finally { release.Set(); cache.Dispose(); }
            }
            return assertions;
        }
        finally
        {
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.CacheChecks")) + Path.DirectorySeparatorChar;
            string resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
        }
    }

    private static BitmapSource Image(int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        image.Freeze(); return image;
    }
    private static void WaitRelease(ManualResetEventSlim release)
    {
        if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Cache test loader was not released.");
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Cache state did not settle.");
            await Task.Delay(5);
        }
    }
    private static async Task ExpectCanceled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected the preview request to be canceled.");
    }
    private static async Task ExpectThrows<T>(Task task) where T : Exception
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + " from the preview request.");
    }
}
