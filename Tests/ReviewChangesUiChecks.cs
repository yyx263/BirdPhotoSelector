using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BirdPhotoSelector;
using BirdPhotoSelector.Controls;

/// <summary>Checks this review's production UI without RAW decoding, model inference or visible windows.</summary>
internal static class ReviewChangesUiChecks
{
    public static int Run()
    {
        if (Application.Current is null || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Review UI checks require App resources on the STA dispatcher.");
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
            Console.WriteLine("  OK REVIEW UI " + message);
        }

        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.ReviewChangesUiChecks");
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
            string currentJpg = Path.Combine(fixtureRoot, "当前照片.JPG");
            string currentRaw = Path.ChangeExtension(currentJpg, ".NEF");
            WriteJpeg(currentJpg);
            File.WriteAllText(currentRaw, "Opaque paired RAW. The real JPG is the only preview source in this test.");
            var imported = new PhotoImportService().ImportAsync(new[] { currentJpg }, Array.Empty<PhotoItem>())
                .GetAwaiter().GetResult();
            var photo = imported.Added.Single();
            photo.Rating = PhotoRating.Favorite;
            var first = new DetectedBird { Number = 7,
                Box = Box(.12, .2), Species = new("大山雀", "Parus major", .61, Confidence: .91) };
            var second = new DetectedBird { Number = 19, IsManual = true,
                Box = Box(.6, .18), Species = new("麻雀", "Passer montanus", .58, Confidence: .84) };
            photo.SetBirds(new[] { first, second }, selectedIndex: 1, nextBirdNumber: 41);

            PhotoItem Placeholder(string stem, PhotoAssetKind kind, PhotoRating rating)
            {
                string path = Path.Combine(fixtureRoot, stem + (kind == PhotoAssetKind.Jpg ? ".JPG" : ".ARW"));
                if (kind == PhotoAssetKind.Jpg) File.Copy(currentJpg, path);
                else File.WriteAllText(path, "Opaque RAW-only thumbnail-binding fixture; never selected or decoded.");
                return new PhotoItem { FilePath = path, Rating = rating, Thumbnail = photo.Thumbnail,
                    PixelWidth = photo.PixelWidth, PixelHeight = photo.PixelHeight };
            }
            // All three formats are visible in the actual in-group list and in group-cover badges.
            var currentGroup = new PhotoGroup("三种原件格式");
            currentGroup.Photos.Add(photo);
            currentGroup.Photos.Add(Placeholder("组内JPG", PhotoAssetKind.Jpg, PhotoRating.Keep));
            currentGroup.Photos.Add(Placeholder("组内RAW", PhotoAssetKind.Raw, PhotoRating.Reject));
            var jpgGroup = new PhotoGroup(); jpgGroup.Photos.Add(Placeholder("独立JPG", PhotoAssetKind.Jpg, PhotoRating.Keep));
            var rawGroup = new PhotoGroup(); rawGroup.Photos.Add(Placeholder("独立RAW", PhotoAssetKind.Raw, PhotoRating.Reject));

            window = new MainWindow();
            window.Dispatcher.UnhandledException += OnError;
            window.Closed += OnClosed;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            model = (MainViewModel)window.DataContext;
            model.ProjectName = "格式与来源核对界面检查";
            var root = (Grid)window.Content;
            root.Background = window.Background;
            Layout(root, 1560, 860);
            model.SetGroups(new[] { currentGroup, jpgGroup, rawGroup }, photo.Id);
            bool loaded = PumpUntil(() => model.IsPreviewReady || dispatcherError is not null ||
                model.Status.StartsWith("预览失败：", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            if (dispatcherError is not null) throw new InvalidOperationException("Review UI dispatcher failed.", dispatcherError);
            Check(loaded && model.IsPreviewReady && photo.ActivePreviewAsset?.Kind == PhotoAssetKind.Jpg,
                "the production window loads JPG while the selected JPG-only input retains its paired RAW");
            Settle(root);
            Check(window.FindName("IncludeRawCheckBox") is null &&
                  !Descendants(root).OfType<CheckBox>().Any(box => box.Content?.ToString()?.Contains("同时读取", StringComparison.Ordinal) == true),
                "the production window has no RAW import checkbox");
            Check(((TextBlock)window.FindName("MetadataFormat")).Text.Contains("JPG + RAW", StringComparison.Ordinal) &&
                  ((TextBlock)window.FindName("MetadataFormat")).Text.Contains(".NEF", StringComparison.Ordinal) &&
                  ((TextBlock)window.FindName("PreviewSourceText")).Text == "预览：JPG",
                "selecting JPG alone still displays JPG plus RAW in the parameter bar and identifies the JPG preview");
            CheckBadges(root, Check);

            var viewer = (DualPhotoViewer)window.FindName("Viewer");
            var overlay = (Canvas)viewer.FindName("BirdOverlay");
            photo.SetGeometryReference(photo.GeometryReference, PhotoGeometryStatus.NeedsReview);
            viewer.RefreshBirdOverlays();
            Settle(root);
            int reloads = 0;
            void CountReload(PhotoItem? _) => reloads++;
            model.PhotoChanged += CountReload;
            try
            {
                Check(model.CanRate && model.CanEditBirdRegions && overlay.Children.Count == 2 &&
                      ((ToggleButton)viewer.FindName("ManualRegionButton")).IsEnabled && !photo.CanUseBirdGeometry,
                    "a pending source review keeps both old overlays and edit controls without silently confirming AI geometry");
                viewer.SelectBird(0); viewer.SelectBird(1);
                Check(ReferenceEquals(photo.SelectedBird, second) && first.Species?.ScientificName == "Parus major" &&
                      second.Species?.ScientificName == "Passer montanus",
                    "old review overlays remain selectable and retain their existing species results");
                var notice = (TextBlock)window.FindName("GeometryNoticeText");
                Check(notice.Text == "图像来源已变化，请注意核对" && notice.ActualWidth > 0 &&
                      !((TextBlock)window.FindName("OperationStatusText")).Text.Contains("核对", StringComparison.Ordinal) &&
                      !((TextBlock)viewer.FindName("DetectionText")).Text.Contains("核对", StringComparison.Ordinal),
                    "the source-change reminder appears in the top notice without replacing the footer or bird-result text");
                var remove = typeof(MainWindow).GetMethod("RemoveBirdRegion", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Production region-removal action missing.");
                bool removed = (bool)remove.Invoke(window, new object[] { photo, first })!;
                Settle(root);
                Check(removed && photo.Birds.Count == 1 && ReferenceEquals(photo.SelectedBird, second) &&
                      second.Number == 19 && second.DisplayNumber == 1 && second.Species?.ScientificName == "Passer montanus" &&
                      photo.GeometryStatus == PhotoGeometryStatus.NeedsReview,
                    "deleting the first old region through the window preserves the surviving object and species while displaying number one");
                Check(OverlayLabels(overlay).SequenceEqual(new[] { "1" }),
                    "the surviving virtual box immediately follows the result list's compact display number");
                Check(viewer.BeginManualRegionSelection(), "a pending review allows the real manual-selection control to start drawing");
                viewer.CancelManualRegionSelection();
                var added = photo.AddManualRegion(Box(.38, .22));
                added.Species = new("新手动框", "Cyanistes caeruleus", .55, Confidence: .8);
                viewer.SelectBird(photo.SelectedBirdIndex);
                Settle(root);
                Check(photo.Birds.Count == 2 && ReferenceEquals(photo.Birds[0], second) && added.IsManual &&
                      added.DisplayNumber == 2 && second.DisplayNumber == 1 &&
                      second.Species?.ScientificName == "Passer montanus" && photo.GeometryStatus == PhotoGeometryStatus.NeedsReview &&
                      OverlayLabels(overlay).SequenceEqual(new[] { "1", "2" }),
                    "adding a current-image manual region keeps old species and displays continuous labels while the source reminder remains");
                Check(reloads == 0 && model.IsPreviewReady,
                    "manual edits in review state do not reload or temporarily disable the current preview");
            }
            finally { model.PhotoChanged -= CountReload; }

            var emptyTextField = typeof(MainWindow).GetField("EmptyDetectionConfirmationText", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Empty-detection confirmation text missing.");
            string emptyText = (string)emptyTextField.GetRawConstantValue()!;
            var lines = emptyText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Check(lines.Contains("这会移除全部自动框和手动选区。") && lines.Contains("照片、评级和分组保留。"),
                "the clear-regions warning and the preserved-photo/rating/group statement occupy separate confirmation lines");
            Layout(root, 1560, 860); Settle(root);
            CheckBadges(root, Check);
            SaveScreenshot(root, "review-changes.png", 1560, 860);
            Layout(root, 1160, 700); Settle(root);
            CheckBadgeBounds(root, Check);
            var noticeBounds = ((TextBlock)window.FindName("GeometryNoticeText")).TransformToAncestor(root)
                .TransformBounds(new Rect(((TextBlock)window.FindName("GeometryNoticeText")).RenderSize));
            Check(noticeBounds.Right <= 1161 && noticeBounds.Bottom <= 701 && !window.IsVisible,
                "the source reminder remains visible at minimum size without opening a desktop window");
            SaveScreenshot(root, "review-changes-minimum.png", 1160, 700);
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
                        throw new InvalidOperationException("The hidden review window did not close.");
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
                    throw new InvalidOperationException("Unsafe review UI fixture cleanup.");
                Directory.Delete(resolved, recursive: true);
            }
        }
    }

    private static void CheckBadges(FrameworkElement root, Action<bool, string> check)
    {
        foreach (string name in new[] { "PhotoFormatBadge", "GroupCoverFormatBadge" })
        {
            var values = Descendants(root).OfType<TextBlock>().Where(text => text.Name == name).Select(text => text.Text).ToHashSet();
            check(new[] { "J+R", "JPG", "RAW" }.All(values.Contains),
                $"the real {name} bindings show paired, JPG-only and RAW-only formats");
        }
        CheckBadgeBounds(root, check);
    }

    private static void CheckBadgeBounds(FrameworkElement root, Action<bool, string> check)
    {
        var badges = Descendants(root).OfType<TextBlock>().Where(text => text.Name is "PhotoFormatBadge" or "GroupCoverFormatBadge").ToArray();
        bool inside = badges.Length > 0;
        foreach (var badge in badges)
        {
            var border = (Border)VisualTreeHelper.GetParent(badge);
            var imageGrid = (Grid)VisualTreeHelper.GetParent(border);
            var rating = imageGrid.Children.OfType<Border>().Single(item => item != border);
            var bounds = border.TransformToAncestor(imageGrid).TransformBounds(new Rect(border.RenderSize));
            var ratingBounds = rating.TransformToAncestor(imageGrid).TransformBounds(new Rect(rating.RenderSize));
            inside &= badge.ActualWidth > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
                bounds.Right <= imageGrid.ActualWidth + 1 && bounds.Bottom <= imageGrid.ActualHeight + 1 && !bounds.IntersectsWith(ratingBounds);
        }
        check(inside, $"thumbnail format badges stay within their images and separate from rating marks at width {root.ActualWidth:0}");
    }

    private static IEnumerable<string> OverlayLabels(Canvas overlay) => overlay.Children.OfType<Grid>()
        .Select(target => ((TextBlock)target.Children.OfType<Border>().Single().Child).Text);
    private static BirdBox Box(double x, double size) => new() { X = x, Y = .25, Width = size, Height = size, Confidence = .9 };
    private static void WriteJpeg(string path)
    {
        const int width = 720, height = 480;
        var pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int index = (y * width + x) * 3;
            pixels[index] = (byte)(40 + x / 5); pixels[index + 1] = (byte)(70 + y / 4); pixels[index + 2] = (byte)(60 + (x + y) / 8);
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3); image.Freeze();
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
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
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void SaveScreenshot(FrameworkElement root, string name, int width, int height)
    {
        string directory = Path.GetFullPath(Environment.GetEnvironmentVariable("BIRD_UI_SCREENSHOT_DIR") ?? Path.Combine("artifacts", "test-visuals"));
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
}
