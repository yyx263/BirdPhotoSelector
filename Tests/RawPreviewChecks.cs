using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;
using BirdPhotoSelector.Controls;

/// <summary>Source identity, decode scheduling and unverified-coordinate safeguards.</summary>
internal static class RawPreviewChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool value, string text)
        {
            if (!value) throw new InvalidOperationException(text);
            count++;
            Console.WriteLine("  OK RAW PREVIEW " + text);
        }

        // Called before the first await, while the runner still owns the STA UI thread.
        CheckViewer(Check);
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.RawPreviewChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PhotoItem Placeholder(string name)
            {
                string path = Path.Combine(root, name);
                File.WriteAllText(path, "Generated scheduler fixture only; never native-decoded.");
                return new PhotoItem { FilePath = path, PixelWidth = 4, PixelHeight = 3 };
            }
            var jpg = Placeholder("pair.JPG");
            var raw = Placeholder("pair.NEF");
            var later = Placeholder("later.jpg");
            var pair = new PhotoItem();
            pair.SetAssets(new[] { jpg.ActivePreviewAsset!, raw.ActivePreviewAsset! });
            var calls = new ConcurrentQueue<string>();
            using (var cache = new PhotoPreviewCache(1024, path => { calls.Enqueue(path); return Image(4, 3); }))
            {
                var jpegFrame = await cache.GetAsync(pair);
                pair.SetActivePreviewAsset(raw.ActivePreviewAssetId!);
                var rawFrame = await cache.GetAsync(pair);
                Check(!ReferenceEquals(jpegFrame, rawFrame) && calls.Count == 2 &&
                      calls.Last().Equals(raw.ActivePreviewAsset!.CanonicalPath, StringComparison.OrdinalIgnoreCase),
                    "switching paired preview sources decodes the active RAW path rather than reusing cached JPG pixels");
                Check(ReferenceEquals(rawFrame, await cache.GetAsync(pair)),
                    "a repeated foreground RAW request may reuse its own frozen decoded frame");
                File.AppendAllText(raw.FilePath, "new generated file version");
                Check(!ReferenceEquals(rawFrame, await cache.GetAsync(pair)) && calls.Count == 3,
                    "RAW length changes invalidate the previous version of that RAW frame");
            }

            calls.Clear();
            using (var cache = new PhotoPreviewCache(1024, path => { calls.Enqueue(path); return Image(4, 3); }))
            {
                cache.UpdateNeighborhood(new[] { jpg, raw, later }, jpg);
                await WaitUntil(() => cache.PendingCount == 0);
                Check(calls.Count == 2 && calls.All(path => !path.EndsWith(".NEF", StringComparison.OrdinalIgnoreCase)),
                    "neighbour prefetch skips RAW even when the saved RAW dimensions are already known");
                await cache.GetAsync(raw);
                Check(calls.Count == 3 && calls.Last().EndsWith(".NEF", StringComparison.OrdinalIgnoreCase),
                    "the first RAW decode is scheduled only by an explicit foreground request");
            }

            using (var began = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var cancellation = new CancellationTokenSource())
            {
                int nativeCanceled = 0;
                using var cache = new PhotoPreviewCache(1024, cancellableLoader: (_, token) =>
                {
                    began.Set();
                    if (WaitHandle.WaitAny(new[] { release.WaitHandle, token.WaitHandle }, 5000) == WaitHandle.WaitTimeout)
                        throw new TimeoutException("Synthetic shared decode was never released.");
                    if (token.IsCancellationRequested) { Interlocked.Increment(ref nativeCanceled); token.ThrowIfCancellationRequested(); }
                    return Image(4, 3);
                });
                try
                {
                    var abandoned = cache.GetAsync(raw, cancellation.Token);
                    await WaitUntil(() => began.IsSet);
                    var survivor = cache.GetAsync(raw);
                    cancellation.Cancel();
                    await ExpectCanceled(abandoned);
                    Check(nativeCanceled == 0 && !survivor.IsCompleted,
                        "canceling one RAW observer does not abort a decode still needed by another observer");
                    release.Set();
                    Check((await survivor.WaitAsync(TimeSpan.FromSeconds(5))).IsFrozen,
                        "the surviving observer receives the shared RAW decode result");
                }
                finally { release.Set(); }
            }

            foreach (string action in new[] { "caller", "clear", "trim", "dispose" })
            {
                using var began = new ManualResetEventSlim();
                using var nativeCanceled = new ManualResetEventSlim();
                using var cancellation = new CancellationTokenSource();
                using var cache = new PhotoPreviewCache(1024, cancellableLoader: (path, token) =>
                {
                    if (path.EndsWith(".NEF", StringComparison.OrdinalIgnoreCase))
                    {
                        began.Set();
                        if (!token.WaitHandle.WaitOne(5000)) throw new TimeoutException("RAW cancellation was not forwarded.");
                        nativeCanceled.Set();
                        token.ThrowIfCancellationRequested();
                    }
                    return Image(4, 3);
                });
                var pending = cache.GetAsync(raw, cancellation.Token);
                await WaitUntil(() => began.IsSet);
                if (action == "caller") cancellation.Cancel();
                else if (action == "clear") cache.Clear();
                else if (action == "trim") cache.TrimToPaths(new[] { jpg.FilePath });
                else cache.Dispose();
                try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Stale RAW frame was published."); }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) when (action == "dispose") { }
                await WaitUntil(() => nativeCanceled.IsSet && cache.InFlightCount == 0);
                Check(cache.CachedCount == 0, $"{action} cancellation reaches the in-flight RAW decoder and never caches stale output");
                if (action != "dispose")
                    Check((await cache.GetAsync(jpg).WaitAsync(TimeSpan.FromSeconds(5))).IsFrozen,
                        $"{action} cancellation lets a following JPG foreground request complete");
            }

            var unsafePhoto = ReviewPhoto(Path.Combine(root, "missing-original.jpg"), raw.FilePath);
            var evidence = unsafePhoto.Birds.ToArray();
            int detectCalls = 0, recognizeCalls = 0, changes = 0;
            unsafePhoto.PropertyChanged += (_, _) => Interlocked.Increment(ref changes);
            using (var workflow = new BirdRecognitionWorkflow(
                detectorOverride: (_, _) => { detectCalls++; return new(null, "unexpected"); },
                recognizerOverride: (_, _, _, _) => { recognizeCalls++; return new(null, "unexpected"); }))
            {
                var result = await workflow.RecognizeAsync(new[] { unsafePhoto });
                Check(result.Outcomes.Count == 0 && result.SkippedPhotos == 1 && result.Errors.Count == 1 &&
                      detectCalls == 0 && recognizeCalls == 0 && changes == 0 && unsafePhoto.Birds.SequenceEqual(evidence),
                    "combined recognition skips an unverified source before decoding or running either model and keeps all old evidence");
            }
            using (var species = new BirdSpeciesRecognitionService(Path.Combine(root, "not-installed")))
            {
                var result = await species.RecognizeAsync(new[] { unsafePhoto });
                Check(result.Outcomes.Count == 0 && result.SkippedPhotos == 1 && !result.MissingModel && changes == 0,
                    "species-only batches do not load models or create replacement outcomes for unverified coordinates");
            }

            await CheckGrouping(root, Check);
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe temporary fixture cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
        return count;
    }

    private static void CheckViewer(Action<bool, string> check)
    {
        var photo = ReviewPhoto("review-scene.jpg", "review-scene.NEF");
        photo.ViewZoom = 6; photo.ViewCenterX = .15; photo.ViewCenterY = .2;
        var oldBird = photo.SelectedBird;
        var viewer = new DualPhotoViewer();
        viewer.Measure(new Size(1000, 600)); viewer.Arrange(new Rect(0, 0, 1000, 600)); viewer.UpdateLayout();
        var image = Image(1200, 800);
        viewer.ShowPhoto(photo, image); viewer.UpdateLayout();
        var overlay = (Canvas)viewer.FindName("BirdOverlay");
        var detail = (Image)viewer.FindName("DetailImage");
        var viewport = (Border)viewer.FindName("DetailViewport");
        double fit = Math.Min(viewport.ActualWidth / image.PixelWidth, viewport.ActualHeight / image.PixelHeight);
        check(ReferenceEquals(viewer.CurrentImage, image) && overlay.Children.Count == 0 &&
              Math.Abs(detail.Width / image.PixelWidth - fit) < 1e-6,
            "a reviewed-source preview shows the supplied current pixels fitted to the whole frame without old boxes or saved JPG zoom");
        check(!((ToggleButton)viewer.FindName("ManualRegionButton")).IsEnabled && !viewer.BeginManualRegionSelection() &&
              !viewer.RequestDeleteRegion(photo.SelectedBird!),
            "unverified source coordinates cannot be edited or mixed with a new manual region");
        double previousWidth = detail.Width;
        viewer.SelectBird(1);
        check(photo.SelectedBirdIndex == 1 && detail.Width == previousWidth && photo.Birds[0] == oldBird &&
              ((TextBlock)viewer.FindName("DetectionText")).Text.Contains("待核对", StringComparison.Ordinal),
            "selecting preserved species evidence changes the result selection without navigating to an unverified old frame");
        viewer.RestoreBirdView();
        check(Math.Abs(detail.Width / image.PixelWidth - fit) < 1e-6,
            "restoring the detail view of an unverified source fits the image instead of applying old 70-percent bird geometry");
    }

    private static PhotoItem ReviewPhoto(string jpgPath, string rawPath)
    {
        var photo = new PhotoItem { FilePath = jpgPath, PixelWidth = 1200, PixelHeight = 800, Rating = PhotoRating.Favorite };
        photo.SetBirds(new[]
        {
            new DetectedBird { Number = 4, Box = new() { X = .1, Y = .2, Width = .15, Height = .2, Confidence = .9 },
                Species = new("大山雀", "Parus major", .42) },
            new DetectedBird { Number = 7, IsManual = true, Box = new() { X = .6, Y = .2, Width = .15, Height = .2, Confidence = .9 },
                Species = new("麻雀", "Passer montanus", .55) }
        }, nextBirdNumber: 12);
        // Explicitly installing a new source also works for a renamed project member;
        // no relationship to the old source's coordinate frame is assumed.
        photo.SetAssets(new[] { new PhotoAsset(rawPath, PhotoAssetKind.Raw) });
        return photo;
    }

    private static async Task CheckGrouping(string root, Action<bool, string> check)
    {
        const int width = 160, height = 120;
        var photos = new List<PhotoItem>();
        for (int variant = 0; variant < 2; variant++)
        {
            var pixels = Enumerable.Repeat((byte)100, width * height * 3).ToArray();
            for (int y = 24; y < 84; y++)
                for (int x = 24; x < 104; x++)
                {
                    int offset = (y * width + x) * 3;
                    pixels[offset] = variant == 0 ? (byte)240 : (byte)10;
                    pixels[offset + 1] = variant == 0 ? (byte)10 : (byte)220;
                    pixels[offset + 2] = 40;
                }
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
            string path = Path.Combine(root, $"background-{variant}.jpg");
            var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var photo = new PhotoItem { FilePath = path, PixelWidth = width, PixelHeight = height };
            photo.SetBirds(new[] { new DetectedBird { Box = new() { X = .15, Y = .2, Width = .5, Height = .5, Confidence = .9 },
                Species = new("原结果", "Parus major", .4) } });
            photo.SetGeometryReference(photo.GeometryReference, PhotoGeometryStatus.NeedsReview);
            photos.Add(photo);
        }
        var first = ImageLoader.Load(photos[0].FilePath); var second = ImageLoader.Load(photos[1].FilePath);
        double unmasked = BackgroundSimilarity.Compare(BackgroundSimilarity.Describe(first), BackgroundSimilarity.Describe(second));
        double masked = BackgroundSimilarity.Compare(BackgroundSimilarity.Describe(first, photos[0].SelectedBird!.Box),
            BackgroundSimilarity.Describe(second, photos[1].SelectedBird!.Box));
        check(masked > unmasked + .01, "the grouping fixture distinguishes exclusion of an old bird area from a full-frame comparison");
        using var analysis = new AnalysisService();
        var result = await analysis.AnalyzeAsync(photos, (masked + unmasked) / 2);
        check(result.Groups.Count == 2 && result.Outcomes.All(outcome => outcome.PreservedExistingBirds) &&
              photos.All(photo => photo.Birds.Count == 1 && photo.SelectedBird?.Species is not null &&
                  photo.GeometryStatus == PhotoGeometryStatus.NeedsReview),
            "background grouping ignores unverified old masks but preserves every saved bird and its pending review state");
    }

    private static BitmapSource Image(int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat((byte)110, width * height * 3).ToArray(), width * 3);
        image.Freeze();
        return image;
    }
    private static async Task WaitUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("RAW preview scheduler did not settle.");
            await Task.Delay(5);
        }
    }
    private static async Task ExpectCanceled(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected a canceled RAW observer.");
    }
}
