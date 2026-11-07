using BirdPhotoSelector.Imaging;
using System.Collections.Concurrent;

/// <summary>可直接在 Windows/macOS/Linux 运行的核心验证；不引用 WPF、模型或原生 RAW 解码器。</summary>
internal static class Program
{
    private static int _checks;

    /// <summary>仅生成临时目录和小图像；所有失败都会以非零退出码报告。</summary>
    private static async Task<int> Main()
    {
        string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.Imaging"));
        string root = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PixelChecks();
            BgraChecks();
            await CacheChecks(root);
            CanceledNeighborhood(root);
            var references = typeof(ImageFrame).Assembly.GetReferencedAssemblies();
            Check(!references.Any(a => a.Name is "PresentationCore" or "PresentationFramework" or "WindowsBase" or "System.Drawing.Common"),
                "core assembly has no desktop graphics dependency");
            Console.WriteLine($"PASS: {_checks} portable imaging assertions on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != tempRoot || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
                throw new InvalidOperationException("Refusing to clean a directory outside the generated check root.");
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>显示布局改动不能改变公共 RGB 像素、八方向、固定缩放、所有权或取消语义。</summary>
    private static void BgraChecks()
    {
        byte[] bytes = [0,0,255,255,255,0,0,255,0,255,0,255,255,255,255,255];
        var frame = new Bgra32ImageFrame(2,2,bytes); bytes[2] = 0;
        byte[] rgb = [255,0,0,0,0,255,0,255,0,255,255,255];
        Check(frame.CopyRgb24().SequenceEqual(rgb), "BGRA constructor owns pixels and projects exact RGB channels");
        var copy = frame.CopyRgb24(); copy[0] = 0; Check(frame.CopyRgb24()[0] == 255, "BGRA reads never expose writable retained pixels");
        Check(frame.CopyRgb24(new PixelRect(1,0,1,2)).SequenceEqual(new byte[] {0,0,255,255,255,255}), "BGRA crops project only requested rows in RGB order");
        Check(frame.RetainedBytes == 16 && ReferenceEquals(frame,frame.Resize(2,2)), "BGRA retained budget and unchanged resize remain exact");
        Check(frame.Resize(1,1).CopyRgb24().SequenceEqual(new byte[] {128,128,128}), "BGRA fixed bilinear reduction agrees with RGB gold sample");
        var reference = new Rgb24ImageFrame(2,2,rgb);
        for (int orientation=1;orientation<=8;orientation++)
            Check(ImageResampling.Orient(frame,orientation) is Bgra32ImageFrame oriented && oriented.CopyRgb24().SequenceEqual(ImageResampling.Orient(reference,orientation).CopyRgb24()), "BGRA preserves layout and correct RGB for EXIF " + orientation);
        Reject<ArgumentException>(()=>new Bgra32ImageFrame(2,2,new byte[15]), "BGRA rejects wrong byte length");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Reject<OperationCanceledException>(()=>frame.CopyRgb24(canceled.Token), "BGRA read observes cancellation");
    }

    /// <summary>取消的旧邻域即使完成了文件查询也不能排队，避免快速切图后错误预取旧照片。</summary>
    private static void CanceledNeighborhood(string root)
    {
        string path=Path.Combine(root,"neighbor.jpg"); File.WriteAllText(path,"generated metadata fixture");
        var source=new PreviewSource("neighbor",path,2,2); int calls=0;
        using var cache=new PreviewImageCache((_,_)=> { Interlocked.Increment(ref calls); return new Rgb24ImageFrame(2,2,new byte[12]); });
        using var first=new CancellationTokenSource(); first.Cancel();
        cache.UpdateNeighborhood([source],source,first.Token);
        Check(cache.PendingCount==0 && calls==0,"pre-canceled neighborhood does not schedule decoding");
        using var midway=new CancellationTokenSource();
        cache.UpdateNeighborhood(new CancelDuringLookup(source,midway),source,midway.Token);
        Check(midway.IsCancellationRequested && cache.PendingCount==0 && calls==0,"neighborhood canceled during metadata lookup cannot commit a stale queue");
    }
    /// <summary>在候选读取时模拟用户又切了图，检查公开取消语义，不读取缓存内部字段。</summary>
    private sealed class CancelDuringLookup(PreviewSource source,CancellationTokenSource cancellation) : IReadOnlyList<PreviewSource>
    {
        private int _reads; public int Count=>1;
        public PreviewSource this[int index] { get { if(index!=0)throw new ArgumentOutOfRangeException(nameof(index)); if(++_reads==2)cancellation.Cancel(); return source; } }
        public IEnumerator<PreviewSource> GetEnumerator() { yield return this[0]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
    }

    /// <summary>验证像素所有权、RGB 通道、裁切边界、插值结果、溢出和取消，避免仅镜像接口实现。</summary>
    private static void PixelChecks()
    {
        byte[] original = { 255, 0, 0, 0, 0, 255, 0, 255, 0, 255, 255, 255 };
        var image = new Rgb24ImageFrame(2, 2, original);
        original[0] = 0;
        Check(image.CopyRgb24()[0] == 255, "constructor owns an independent immutable RGB copy");
        var copy = image.CopyRgb24(); copy[0] = 0;
        Check(image.CopyRgb24()[0] == 255, "reading pixels cannot mutate the cached frame");
        Check(image.CopyRgb24(new PixelRect(1, 0, 1, 2)).SequenceEqual(new byte[] { 0, 0, 255, 255, 255, 255 }),
            "rectangular copies retain RGB channel order and top-down row order");
        Check(image.RetainedBytes == 16 && ReferenceEquals(image, image.Resize(2, 2)), "budget is conservative and an unchanged resize shares the image");
        Check(image.Resize(1, 1).CopyRgb24().SequenceEqual(new byte[] { 128, 128, 128 }), "pixel-center bilinear reduction averages all four corners");
        var bigger = image.Resize(4, 4).CopyRgb24();
        Check(bigger.Take(3).SequenceEqual(new byte[] { 255, 0, 0 }) && bigger.TakeLast(3).All(p => p == 255),
            "upscaling clamps edges without introducing black borders");
        Reject<ArgumentOutOfRangeException>(() => image.CopyRgb24(new PixelRect(-1, 0, 1, 1)), "negative crop rejected");
        Reject<ArgumentOutOfRangeException>(() => image.CopyRgb24(new PixelRect(1, 1, int.MaxValue, 1)), "overflowing crop rejected before allocation");
        Reject<ArgumentOutOfRangeException>(() => image.CopyRgb24(new PixelRect(0, 0, 0, 1)), "empty crop rejected");
        Reject<ArgumentOutOfRangeException>(() => new Rgb24ImageFrame(0, 1, new byte[3]), "invalid image dimensions rejected");
        Reject<ArgumentException>(() => new Rgb24ImageFrame(2, 1, new byte[3]), "misaligned pixel payload rejected");
        Reject<OverflowException>(() => new Rgb24ImageFrame(int.MaxValue, 2, Array.Empty<byte>()), "allocation size overflow rejected");
        Reject<ArgumentOutOfRangeException>(() => image.Resize(0, 1), "invalid output size rejected");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Reject<OperationCanceledException>(() => image.CopyRgb24(canceled.Token), "pixel reading honors pre-cancellation");
        Reject<OperationCanceledException>(() => image.Resize(1, 1, canceled.Token), "resampling honors pre-cancellation");
        Check(PhotoImageFormats.IsJpeg("BIRD.JPEG") && PhotoImageFormats.IsRaw("bird.NEF") &&
              !PhotoImageFormats.IsRaw("bird.png") && !PhotoImageFormats.IsJpeg(null), "shared format rules are case-insensitive without accepting unsupported inputs");
    }

    /// <summary>真实调度线程上的缓存验证，使用闸门明确控制竞争顺序，不依赖任意延时猜测。</summary>
    private static async Task CacheChecks(string root)
    {
        PreviewSource Source(string name, bool raw = false, int width = 2)
        {
            string path = Path.Combine(root, name); File.WriteAllText(path, "generated bytes");
            return new(name, path, width, 2, raw);
        }
        var a = Source("a.jpg"); var b = Source("b.jpg"); var c = Source("c.jpg");
        var loads = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        ImageFrame Decode(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); loads.AddOrUpdate(Path.GetFileName(path), 1, (_, count) => count + 1);
            return new Rgb24ImageFrame(2, 2, new byte[12]);
        }
        Reject<ArgumentOutOfRangeException>(() => new PreviewImageCache(Decode, 0), "zero cache budget rejected");
        using (var cache = new PreviewImageCache(Decode, 32))
        {
            var first = await cache.GetAsync(a); await cache.GetAsync(b);
            Check(ReferenceEquals(first, await cache.GetAsync(a)), "cache shares exact immutable frame on hit");
            await cache.GetAsync(c); await cache.GetAsync(b);
            Check(loads["a.jpg"] == 1 && loads["b.jpg"] == 2 && cache.CachedBytes == 32 && cache.CachedCount == 2,
                "foreground LRU eviction obeys byte budget");
            File.AppendAllText(b.Path, "new version");
            var changed = await cache.GetAsync(b);
            Check(loads["b.jpg"] == 3 && cache.CachedCount == 2, "file length changes invalidate old cache version");
            File.SetLastWriteTimeUtc(b.Path, File.GetLastWriteTimeUtc(b.Path).AddSeconds(5));
            Check(!ReferenceEquals(changed, await cache.GetAsync(b)), "same-length timestamp changes invalidate old cache version");
            cache.TrimToPaths(new[] { b.Path });
            Check(cache.CachedCount == 1 && cache.CachedBytes == 16, "trimming removed sources releases cached bytes");
            cache.Clear(); Check(cache.CachedCount == 0 && cache.CachedBytes == 0, "clear releases every retained image");
        }
        var huge = Source("huge.jpg", width: 10);
        int hugeLoads = 0;
        using (var cache = new PreviewImageCache((_, _) => { hugeLoads++; return new Rgb24ImageFrame(10, 2, new byte[60]); }, 16))
        {
            Check((await cache.GetAsync(huge)).PixelWidth == 10, "oversized foreground image still displays");
            await cache.GetAsync(huge);
            Check(hugeLoads == 2 && cache.CachedCount == 0, "oversized image is not retained above budget");
        }
        using (var started = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        using (var canceled = new CancellationTokenSource())
        {
            int calls = 0;
            using var cache = new PreviewImageCache((_, token) =>
            {
                calls++; started.Set();
                if (!release.Wait(TimeSpan.FromSeconds(5), token)) throw new TimeoutException();
                return new Rgb24ImageFrame(2, 2, new byte[12]);
            }, 32);
            try
            {
                var abandoned = cache.GetAsync(a, canceled.Token);
                await Until(() => started.IsSet);
                var survivor = cache.GetAsync(a);
                canceled.Cancel(); await Canceled(abandoned);
                release.Set(); await survivor.WaitAsync(TimeSpan.FromSeconds(5));
                Check(calls == 1, "one caller canceling does not cancel a shared image needed by another");
            }
            finally { release.Set(); }
        }
        var raw = Source("expensive.NEF", raw: true);
        using (var started = new ManualResetEventSlim())
        using (var canceled = new CancellationTokenSource())
        {
            using var cache = new PreviewImageCache((_, token) =>
            {
                started.Set(); token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)); token.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }, 32);
            var pending = cache.GetAsync(raw, canceled.Token);
            await Until(() => started.IsSet); canceled.Cancel(); await Canceled(pending);
            await Until(() => cache.InFlightCount == 0);
            Check(cache.CachedCount == 0, "last foreground-only observer canceling aborts its expensive decode");
        }
        var order = new ConcurrentQueue<string>();
        using (var cache = new PreviewImageCache((path, token) => { order.Enqueue(Path.GetFileName(path).ToLowerInvariant()); return Decode(path, token); }, 128))
        {
            cache.UpdateNeighborhood(new[] { a, raw, b, c }, b);
            await Until(() => cache.PendingCount == 0);
            Check(order.SequenceEqual(new[] { "b.jpg", "c.jpg", "a.jpg" }), "neighbors are ordered by distance and expensive sources never prefetch");
        }
        using (var started = new ManualResetEventSlim())
        using (var release = new ManualResetEventSlim())
        {
            var cache = new PreviewImageCache((_, _) =>
            { started.Set(); if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(); return new Rgb24ImageFrame(2, 2, new byte[12]); }, 32);
            try
            {
                var pending = cache.GetAsync(a); await Until(() => started.IsSet);
                cache.Dispose();
                bool disposed = false;
                try { await pending; } catch (ObjectDisposedException) { disposed = true; }
                Check(disposed && cache.CachedBytes == 0, "dispose completes waiting callers without blocking for the decoder");
                release.Set(); await Until(() => cache.InFlightCount == 0);
                Check(cache.CachedCount == 0, "late decoder completion cannot repopulate a disposed cache");
            }
            finally { release.Set(); cache.Dispose(); }
        }
        if (!OperatingSystem.IsWindows())
        {
            var upper = Source("CASE.jpg"); var lower = Source("case.jpg"); int calls = 0;
            using var cache = new PreviewImageCache((_, _) => { calls++; return new Rgb24ImageFrame(2, 2, new byte[12]); }, 64);
            await cache.GetAsync(upper); await cache.GetAsync(lower);
            Check(calls == 2, "Unix cache does not merge differently cased paths");
        }
    }

    /// <summary>带截止时间等待可观察状态，失败时不会让验证进程无限挂起。</summary>
    private static async Task Until(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    /// <summary>断言异步请求确实被取消。</summary>
    private static async Task Canceled(Task task)
    {
        bool canceled = false;
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "asynchronous observer receives cancellation");
    }
    /// <summary>断言无效输入抛出指定异常。</summary>
    private static void Reject<T>(Action action, string message) where T : Exception
    {
        bool rejected = false;
        try { action(); } catch (T) { rejected = true; }
        Check(rejected, message);
    }
    /// <summary>记录具有用户可见含义的断言，不依赖外部测试框架。</summary>
    private static void Check(bool passed, string message)
    {
        if (!passed) throw new InvalidOperationException(message);
        _checks++; Console.WriteLine("  OK IMAGING " + message);
    }
}
