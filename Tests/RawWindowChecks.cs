using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BirdPhotoSelector;
using BirdPhotoSelector.Controls;

/// <summary>One real RAW decode through the production window, without showing a desktop window.</summary>
internal static class RawWindowChecks
{
    public static int Run()
    {
        string? samples = Environment.GetEnvironmentVariable("BIRD_RAW_SAMPLES");
        if (string.IsNullOrWhiteSpace(samples))
        {
            Console.WriteLine("SKIP: real RAW window verification requires BIRD_RAW_SAMPLES.");
            return 0;
        }
        if (Application.Current is null || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("RAW window checks require initialized App resources on the STA dispatcher.");
        string path = Path.Combine(Path.GetFullPath(samples), "Canon_EOS_450D_RAW.CR2");
        if (!File.Exists(path)) throw new FileNotFoundException("The recorded Canon 450D CR2 sample is required.", path);
        byte[] originalHash = Hash(path);
        var info = RawImageDecoder.ReadInfo(path);
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
            Console.WriteLine("  OK RAW WINDOW " + message);
        }

        // This is historical metadata only; no JPG is created or removed in the sample directory.
        var jpg = new PhotoAsset(Path.ChangeExtension(path, ".JPG"), PhotoAssetKind.Jpg, "historical-jpg");
        var raw = new PhotoAsset(path, PhotoAssetKind.Raw, "current-raw");
        var photo = new PhotoItem { Id = "raw-window-logical-photo", Rating = PhotoRating.Favorite };
        photo.SetAssets(new[] { jpg, raw }, jpg.Id, jpg.Id);
        photo.PixelWidth = 5000; photo.PixelHeight = 3333;
        var bird = new DetectedBird
        {
            Number = 9, IsManual = true,
            Box = new() { X = .03, Y = .06, Width = .10, Height = .11, Confidence = .91 },
            Species = new("大山雀", "Parus major", .412, Confidence: .83),
            RecognitionStatus = "历史 JPG 手动选区结果"
        };
        photo.SetBirds(new[] { bird }, nextBirdNumber: 41);
        photo.ViewZoom = 4.8; photo.ViewCenterX = .12; photo.ViewCenterY = .19;
        var historicalFrame = photo.GeometryReference;
        photo.RemoveAsset(jpg.Id);
        Check(photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && photo.Assets.Count == 1 &&
              photo.ActivePreviewAssetId == raw.Id && photo.ViewZoom is null,
            "a removed JPG prepares one RAW photo with historical species and an unverified old coordinate frame");

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
            window = new MainWindow();
            window.Dispatcher.UnhandledException += OnError;
            window.Closed += OnClosed;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            model = (MainViewModel)window.DataContext;
            var group = new PhotoGroup("RAW 预览与旧鸟框待核对"); group.Photos.Add(photo);
            var root = (Grid)window.Content;
            root.Background = window.Background;
            model.ProjectName = "RAW 第三步界面验证";
            model.SetGroups(new[] { group });
            Layout(root);
            bool loaded = PumpUntil(() => model.IsPreviewReady || model.Status.StartsWith("预览失败：", StringComparison.Ordinal) ||
                dispatcherError is not null, TimeSpan.FromSeconds(60));
            if (dispatcherError is not null) throw new InvalidOperationException("RAW window dispatcher failed.", dispatcherError);
            Check(loaded && model.IsPreviewReady, "the real MainWindow finishes its asynchronous RAW preview load: " + model.Status);
            Settle(root);
            var viewer = (DualPhotoViewer)window.FindName("Viewer");
            Check(viewer.CurrentImage is BitmapSource { IsFrozen: true } image &&
                  image.PixelWidth == info.PixelWidth && image.PixelHeight == info.PixelHeight && !viewer.IsLoading &&
                  photo.PixelWidth == info.PixelWidth && photo.PixelHeight == info.PixelHeight && photo.Thumbnail is { IsFrozen: true },
                "the production cache/viewer receives a frozen full-resolution RAW image and creates its own thumbnail");
            Check(((TextBlock)window.FindName("MetadataFormat")).Text is { } format && format.Contains("RAW", StringComparison.Ordinal) &&
                  format.Contains(".CR2", StringComparison.Ordinal) && !format.Contains("JPG", StringComparison.Ordinal) &&
                  ((TextBlock)window.FindName("PreviewSourceText")).Text.Contains("RAW", StringComparison.Ordinal) &&
                  ((TextBlock)window.FindName("MetadataCamera")).Text == info.Metadata.Camera,
                "real metadata bindings distinguish RAW CR2, current RAW preview and the decoded camera metadata");
            Check(photo.Id == "raw-window-logical-photo" && photo.Rating == PhotoRating.Favorite && photo.NextBirdNumber == 41 &&
                  ReferenceEquals(photo.SelectedBird, bird) && bird.Species is { ScientificName: "Parus major", Similarity: .412, Confidence: .83 } &&
                  ReferenceEquals(photo.GeometryReference, historicalFrame) && photo.GeometryReference?.AssetId == jpg.Id &&
                  photo.GeometryStatus == PhotoGeometryStatus.NeedsReview,
                "loading RAW keeps logical identity, rating, manual region numbering, species and the original JPG frame");
            Check(model.CanRate && !model.CanEditBirdRegions && !photo.CanUseBirdGeometry && photo.Bird is null &&
                  ((RadioButton)window.FindName("RatingKeep")).IsEnabled &&
                  !((ToggleButton)viewer.FindName("ManualRegionButton")).IsEnabled && !viewer.BeginManualRegionSelection(),
                "a decoded RAW remains rateable while old-coordinate editing and manual region creation are blocked");
            var overlay = (Canvas)viewer.FindName("BirdOverlay");
            Check(overlay.Children.Count == 0 && photo.ViewZoom is { } zoom && Math.Abs(zoom - 1) < 1e-6 &&
                  photo.ViewCenterX == .5 && photo.ViewCenterY == .5,
                "unverified old JPG boxes do not appear as RAW overlays or drive its initial zoom and center");
            viewer.RestoreBirdView();
            Settle(root);
            Check(overlay.Children.Count == 0 && photo.ViewZoom is { } restoredZoom && Math.Abs(restoredZoom - 1) < 1e-6 &&
                  photo.ViewCenterX == .5 && photo.ViewCenterY == .5,
                "restoring the bird view cannot apply historical JPG coordinates to the RAW source");
            var notice = (TextBlock)window.FindName("GeometryNoticeText");
            Check(notice.Visibility == Visibility.Visible && notice.ActualWidth > 0 &&
                  notice.Text.Contains("待核对", StringComparison.Ordinal) && notice.Text.Contains("鸟种结果已保留", StringComparison.Ordinal) &&
                  ((ListBox)window.FindName("SpeciesResultsList")).Items.Contains(bird),
                "the real window visibly explains the changed coordinates and continues listing the saved species");
            ((RadioButton)window.FindName("RatingKeep")).SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            Settle(root);
            Check(photo.Rating == PhotoRating.Keep && model.CurrentRating == PhotoRating.Keep && ReferenceEquals(photo.SelectedBird, bird),
                "the actual RAW rating control updates the photo without replacing its historical species");
            model.CurrentRating = PhotoRating.Favorite;
            Settle(root);
            Check(!window.IsVisible && Application.Current.Windows.Cast<Window>().All(candidate => !candidate.IsVisible),
                "RAW window verification does not open a visible desktop window");
            SaveScreenshot(root);
            Check(Hash(path).SequenceEqual(originalHash), "the complete production RAW preview and rating flow leaves original bytes unchanged");
            return count;
        }
        finally
        {
            try
            {
                if (window is not null)
                {
                    // Cancel any outstanding preview and close through the production asynchronous Closing path.
                    model?.SetGroups(Array.Empty<PhotoGroup>());
                    if (model is not null) { model.IsBusy = false; model.Dirty = false; }
                    window.Close();
                    if (!PumpUntil(() => closed, TimeSpan.FromSeconds(5)))
                        throw new InvalidOperationException("The hidden RAW verification window did not finish closing.");
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
            }
        }
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        if (condition()) return true;
        var frame = new DispatcherFrame();
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (condition() || watch.Elapsed >= timeout) frame.Continue = false; };
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        return condition();
    }
    private static void Layout(FrameworkElement root)
    {
        root.Width = 1560; root.Height = 860;
        root.Measure(new Size(1560, 860)); root.Arrange(new Rect(0, 0, 1560, 860));
        root.UpdateLayout();
    }
    private static void Settle(FrameworkElement root)
    {
        root.UpdateLayout();
        root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.Background);
        root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.SystemIdle);
    }
    private static byte[] Hash(string path)
    { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
    private static void SaveScreenshot(FrameworkElement root)
    {
        string directory = Path.GetFullPath(Environment.GetEnvironmentVariable("BIRD_UI_SCREENSHOT_DIR") ??
            Path.Combine("artifacts", "test-visuals"));
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap(1560, 860, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, "raw-window.png")); encoder.Save(output);
    }
}
