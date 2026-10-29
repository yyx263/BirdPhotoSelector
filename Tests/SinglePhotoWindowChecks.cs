using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BirdPhotoSelector;
using BirdPhotoSelector.Controls;

/// <summary>Exercises both confirmations and explicit one-photo replacement through the production window.</summary>
internal static class SinglePhotoWindowChecks
{
    public static int Run()
    {
        if (Application.Current is null || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Single-photo window checks require initialized App resources on the STA dispatcher.");
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
            Console.WriteLine("  OK SINGLE WINDOW " + message);
        }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SinglePhotoWindowChecks");
        string fixtureRoot = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        MainWindow? window = null;
        MainViewModel? model = null;
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Exception? dispatcherError = null;
        bool closed = false;
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
        { dispatcherError = e.Exception; e.Handled = true; }
        void OnClosed(object? sender, EventArgs e) => closed = true;
        try
        {
            string firstPath = Path.Combine(fixtureRoot, "当前照片.JPG");
            string secondPath = Path.Combine(fixtureRoot, "另一张照片.JPG");
            var image = Image();
            var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(firstPath)) encoder.Save(output);
            File.Copy(firstPath, secondPath);
            byte[] firstBytes = File.ReadAllBytes(firstPath), secondBytes = File.ReadAllBytes(secondPath);
            var replacementSpecies = new SpeciesMatch("欧亚大山雀", "Parus major", .72, Confidence: .94);
            window = new MainWindow();
            window.Dispatcher.UnhandledException += OnError;
            window.Closed += OnClosed;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            model = (MainViewModel)window.DataContext;
            model.ProjectName = "单张重新识别界面验证";
            var root = (Grid)window.Content; root.Background = window.Background;
            var viewer = (DualPhotoViewer)window.FindName("Viewer");
            var button = (Button)window.FindName("ReidentifyCurrentPhotoButton");
            var method = typeof(MainWindow).GetMethod("ReidentifyCurrentPhoto", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The production one-photo method is required.");
            Task Invoke(Func<bool> replace, Func<bool> empty, Func<SinglePhotoRecognitionService> create) =>
                (Task)(method.Invoke(window, new object?[] { replace, empty, create })
                    ?? throw new InvalidOperationException("The production one-photo method must return a Task."));
            void Complete(Task task)
            {
                bool completed = PumpUntil(() => task.IsCompleted || dispatcherError is not null, TimeSpan.FromSeconds(15));
                if (dispatcherError is not null) throw new InvalidOperationException("Single-photo window dispatcher failed.", dispatcherError);
                if (!completed) throw new TimeoutException("Single-photo window operation did not complete.");
                task.GetAwaiter().GetResult();
                if (!PumpUntil(() => model.IsPreviewReady || model.Status.StartsWith("预览失败：", StringComparison.Ordinal), TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Single-photo preview did not settle after operation completion.");
                Settle(root);
            }
            (PhotoItem Current, PhotoItem Other, PhotoGroup Group) Prepare(bool needsReview)
            {
                var current = Photo(firstPath, "current-logical-photo");
                var other = Photo(secondPath, "other-logical-photo"); other.Rating = PhotoRating.Keep;
                if (needsReview)
                    current.SetGeometryReference(new("prior-preview-member", Path.Combine(fixtureRoot, "历史来源.JPG"), 1000, 800),
                        PhotoGeometryStatus.NeedsReview);
                var group = new PhotoGroup("原有连拍组"); group.Photos.Add(current); group.Photos.Add(other);
                model.SetGroups(new[] { group }, current.Id);
                Layout(root, 1560, 860);
                if (!PumpUntil(() => model.IsPreviewReady || dispatcherError is not null, TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The generated JPEG preview did not become ready.");
                if (dispatcherError is not null) throw dispatcherError;
                viewer.ShowPhoto(current, image); model.IsPreviewReady = true;
                Settle(root); model.Dirty = false;
                return (current, other, group);
            }

            var initial = Prepare(needsReview: false);
            Check(button.Content?.ToString() == "重新识别本张" && button.IsEnabled && model.CanReidentifyCurrentPhoto &&
                  button.ToolTip?.ToString() is { } tooltip && tooltip.Contains("手动选区", StringComparison.Ordinal) &&
                  tooltip.Contains("成功后", StringComparison.Ordinal) && tooltip.Contains("取消", StringComparison.Ordinal),
                "the current-photo button is enabled after preview and explicitly describes replacing manual regions only on success");
            foreach (var size in new[] { new Size(1560, 860), new Size(1160, 760) })
            {
                Layout(root, size.Width, size.Height); Settle(root);
                var bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
                Check(button.ActualWidth > 75 && button.ActualHeight > 12 && bounds.Left >= 0 && bounds.Right <= size.Width &&
                      bounds.Top >= 0 && bounds.Bottom <= size.Height,
                    $"the single-photo action remains visible within the {size.Width:0} by {size.Height:0} production window");
            }
            Layout(root, 1560, 860); Settle(root);
            var original = State(initial.Current); var originalBirds = initial.Current.Birds.ToArray();
            int factories = 0, emptyConfirmations = 0;
            Complete(Invoke(() => false, () => { emptyConfirmations++; return true; }, () =>
            { factories++; return new SinglePhotoRecognitionService(); }));
            Check(factories == 0 && emptyConfirmations == 0 && !model.IsBusy && !model.Dirty &&
                  Unchanged(initial.Current, original, originalBirds),
                "rejecting the initial replacement confirmation never starts inference or changes old selections and species");

            int recognitions = 0;
            Complete(Invoke(() => true, () => { emptyConfirmations++; return false; }, () =>
                new SinglePhotoRecognitionService(detectorOverride: (_, _) => new(null, "正常完成，未检测到鸟"),
                    recognizerOverride: (_, _, _, _) => { recognitions++; return new(replacementSpecies, "unexpected"); })));
            Check(emptyConfirmations == 1 && recognitions == 0 && !model.IsBusy && !model.Dirty &&
                  Unchanged(initial.Current, original, originalBirds) && model.Status.Contains("已保留", StringComparison.Ordinal),
                "rejecting the second zero-detection confirmation keeps every old automatic/manual result and saved view");

            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var clearTask = Invoke(() => true, () => true, () => new SinglePhotoRecognitionService(
                    detectorOverride: (_, token) => { started.Set(); release.Wait(token); return new(null, "正常空结果"); }));
                bool disabledWhileWorking = false;
                try
                {
                    bool working = PumpUntil(() => started.IsSet || dispatcherError is not null, TimeSpan.FromSeconds(5));
                    Settle(root);
                    disabledWhileWorking = working && model.IsBusy && !button.IsEnabled && !model.CanReidentifyCurrentPhoto;
                }
                finally { release.Set(); }
                Complete(clearTask);
                Check(disabledWhileWorking,
                    "the real operation disables the rebuild button while the current-photo detector is still running");
            }
            Check(initial.Current.Birds.Count == 0 && initial.Current.NextBirdNumber == 37 && model.Dirty &&
                  initial.Current.Rating == PhotoRating.Favorite && initial.Current.Id == "current-logical-photo" &&
                  initial.Group.Photos.Count == 2 && initial.Group.Photos[0] == initial.Current &&
                  initial.Current.GeometryStatus == PhotoGeometryStatus.Current && initial.Current.CanUseBirdGeometry,
                "approved empty detection clears all old regions through the window while keeping photo, rating, group and numbering history");
            Check(button.IsEnabled && model.CanReidentifyCurrentPhoto && !model.IsBusy,
                "the action becomes available again after an approved empty replacement");

            var pending = Prepare(needsReview: true);
            var oldRegions = pending.Current.Birds.ToArray();
            var otherState = State(pending.Other); var otherRegions = pending.Other.Birds.ToArray();
            Check(pending.Current.GeometryStatus == PhotoGeometryStatus.NeedsReview && !pending.Current.CanUseBirdGeometry &&
                  button.IsEnabled && model.CanReidentifyCurrentPhoto && !model.CanEditBirdRegions,
                "an explicitly pending old coordinate frame can use the rebuild button while ordinary region editing stays blocked");
            Complete(Invoke(() => true, () => throw new InvalidOperationException("Nonempty success must not ask to clear."), () =>
                new SinglePhotoRecognitionService(detectorOverride: (_, _) =>
                    new(Box(.1, .15), "定位两只鸟") { Birds = new[] { Box(.1, .15), Box(.55, .3) } },
                    recognizerOverride: (_, _, _, _) => new(replacementSpecies, "本张重新识别成功"))));
            Check(pending.Current.Birds.Count == 2 && pending.Current.Birds.All(bird => !bird.IsManual && bird.Species == replacementSpecies) &&
                  !pending.Current.Birds.Intersect(oldRegions).Any() && pending.Current.Birds.Select(bird => bird.Number).SequenceEqual(new[] { 37, 38 }) &&
                  pending.Current.NextBirdNumber == 39 && pending.Current.SelectedBird?.Number == 37,
                "successful production-window rebuilding replaces all old manual/automatic regions with complete newly numbered results");
            Check(pending.Current.GeometryStatus == PhotoGeometryStatus.Current && pending.Current.CanUseBirdGeometry &&
                  pending.Current.GeometryReference?.AssetId == pending.Current.ActivePreviewAssetId &&
                  pending.Current.Rating == PhotoRating.Favorite && pending.Current.Id == "current-logical-photo" &&
                  pending.Group.Photos.SequenceEqual(new[] { pending.Current, pending.Other }) && model.PhotoCount == 2 &&
                  Unchanged(pending.Other, otherState, otherRegions),
                "the current-source frame is confirmed while the photo's rating/group and every other photo remain unchanged");
            Check(model.CanEditBirdRegions && model.CanReidentifyCurrentPhoto &&
                  ((TextBlock)window.FindName("GeometryNoticeText")).Text.Length == 0 &&
                  ((Canvas)viewer.FindName("BirdOverlay")).Children.Count > 0 &&
                  ((ListBox)window.FindName("SpeciesResultsList")).Items.Count == 2,
                "completed rebuild removes the review notice and restores overlays, region editing and current species bindings");
            SaveScreenshot(root);
            Check(!window.IsVisible && File.ReadAllBytes(firstPath).SequenceEqual(firstBytes) && File.ReadAllBytes(secondPath).SequenceEqual(secondBytes),
                "hidden single-photo UI verification never opens a visible desktop window or rewrites its generated photo bytes");
            return count;
        }
        finally
        {
            try
            {
                if (window is not null)
                {
                    model?.SetGroups(Array.Empty<PhotoGroup>());
                    if (model is not null) { model.IsBusy = false; model.Dirty = false; }
                    window.Close();
                    if (!PumpUntil(() => closed, TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("The hidden single-photo window did not finish closing.");
                }
            }
            finally
            {
                if (window is not null)
                {
                    window.Dispatcher.UnhandledException -= OnError;
                    window.Closed -= OnClosed;
                }
                SynchronizationContext.SetSynchronizationContext(previousContext);
                string resolved = Path.GetFullPath(fixtureRoot);
                if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsafe single-photo UI cleanup path.");
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    private static PhotoItem Photo(string path, string id)
    {
        var photo = new PhotoItem { Id = id, FilePath = path, Rating = PhotoRating.Favorite,
            PixelWidth = 720, PixelHeight = 480, Metadata = new PhotoMetadata { Camera = "界面测试相机", CapturedAt = "2026-10-30 09:00:00" } };
        photo.SetBirds(new[]
        {
            new DetectedBird { Number = 3, Box = Box(.1, .15), Species = new("旧自动结果", "Parus major", .4), RecognitionStatus = "old auto" },
            new DetectedBird { Number = 8, IsManual = true, Box = Box(.55, .2), Species = new("旧手动结果", "Passer montanus", .5), RecognitionStatus = "old manual" }
        }, selectedIndex: 1, nextBirdNumber: 37);
        photo.ViewZoom = 1.7; photo.ViewCenterX = .6; photo.ViewCenterY = .4;
        return photo;
    }
    private static BirdBox Box(double x, double size) => new() { X = x, Y = .2, Width = size, Height = size, Confidence = .91 };
    private static BitmapSource Image()
    {
        const int width = 720, height = 480;
        var pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int index = (y * width + x) * 3;
            pixels[index] = (byte)(45 + x / 5); pixels[index + 1] = (byte)(75 + y / 4); pixels[index + 2] = (byte)(55 + (x + y) / 8);
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3); image.Freeze(); return image;
    }
    private static string State(PhotoItem photo) => JsonSerializer.Serialize(new
    {
        photo.Id, photo.FilePath, photo.Rating, photo.NextBirdNumber, photo.SelectedBirdIndex,
        Birds = photo.Birds.Select(bird => new { bird.Number, bird.IsManual, bird.Box, bird.Species, bird.RecognitionStatus }).ToArray(),
        photo.GeometryStatus, photo.GeometryReference, photo.DetectionStatus, photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY
    });
    private static bool Unchanged(PhotoItem photo, string state, DetectedBird[] objects) => State(photo) == state &&
        photo.Birds.Count == objects.Length && photo.Birds.Zip(objects).All(pair => ReferenceEquals(pair.First, pair.Second));
    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        if (condition()) return true;
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) => { if (condition() || watch.Elapsed >= timeout) frame.Continue = false; };
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        return condition();
    }
    private static void Layout(FrameworkElement root, double width, double height)
    { root.Width = width; root.Height = height; root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Settle(FrameworkElement root)
    {
        root.UpdateLayout(); root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.Background);
        root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.SystemIdle);
    }
    private static void SaveScreenshot(FrameworkElement root)
    {
        string directory = Path.GetFullPath(Environment.GetEnvironmentVariable("BIRD_UI_SCREENSHOT_DIR") ?? Path.Combine("artifacts", "test-visuals"));
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap(1560, 860, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, "single-photo-rebuild.png")); encoder.Save(output);
    }
}
