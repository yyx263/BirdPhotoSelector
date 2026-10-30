using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BirdPhotoSelector;
using BirdPhotoSelector.Controls;

internal static class MainWorkflowUpdateChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); count++; Console.WriteLine("  OK UPDATE UI " + message); }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.MainWorkflowUpdateChecks");
        string directory = Directory.CreateDirectory(Path.Combine(allowed, Guid.NewGuid().ToString("N"))).FullName;
        var previous = SynchronizationContext.Current;
        MainWindow? window = null;
        bool closed = false;
        try
        {
            var image = BitmapSource.Create(120, 80, 96, 96, PixelFormats.Gray8, null,
                Enumerable.Repeat((byte)128, 120 * 80).ToArray(), 120); image.Freeze();
            string path = Path.Combine(directory, "reset.JPG");
            var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(path)) encoder.Save(output);
            byte[] original = File.ReadAllBytes(path);
            var photo = new PhotoItem { FilePath = path, Rating = PhotoRating.Favorite, PixelWidth = 120, PixelHeight = 80 };
            photo.SetBirds(new[] { new DetectedBird { Number = 3,
                Box = new() { X = .2, Y = .2, Width = .3, Height = .3, Confidence = .9 },
                Species = new("欧亚大山雀", "Parus major", .7, Confidence: .98) } });
            var group = new PhotoGroup("清空识别测试"); group.Photos.Add(photo);
            window = new MainWindow(); window.Closed += (_, _) => closed = true;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            ((DispatcherTimer)Field(window, "_autoSaveTimer")).Stop();
            var model = (MainViewModel)window.DataContext;
            var root = (FrameworkElement)window.Content;
            var viewer = (DualPhotoViewer)window.FindName("Viewer");
            var list = (ListBox)window.FindName("SpeciesResultsList");
            var button = (Button)window.FindName("ClearRecognitionResultsButton");
            var recent = (RecentSpeciesHistory)Field(window, "_recentSpecies");
            recent.Use(new("白鹭", "Egretta garzetta"));
            model.SetGroups(new[] { group });
            Layout(root);
            Pump(() => model.IsPreviewReady, "the generated JPEG preview did not load");
            Invoke<bool>(window, "ApplyManualSpecies", photo, new SpeciesIdentity("鸳鸯", "Aix galericulata"), null);
            Settle(root); int recentCount = recent.Items.Count, highWater = photo.NextBirdNumber;
            var retainedImage = viewer.CurrentImage;
            Check(button.IsEnabled && photo.Birds.Count == 2 && photo.HasManualSpeciesEdits,
                "the production clear button is enabled for a photo with automatic and manual results");
            model.Dirty = false;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle(root);
            Check(photo.Birds.Count == 0 && list.Items.Count == 0 && !photo.HasManualSpeciesEdits &&
                  photo.DetectionStatus == "待识别" && photo.ViewCenterX == .5 && photo.ViewCenterY == .5 && model.Dirty &&
                  ((Canvas)viewer.FindName("BirdOverlay")).Children.OfType<FrameworkElement>().All(item => item.Tag is not DetectedBird),
                "clicking clear removes actual rows, bird overlays and manual protection, then marks the project changed");
            Check(ReferenceEquals(viewer.CurrentImage, retainedImage) && photo.Rating == PhotoRating.Favorite &&
                  group.Photos.Single() == photo && recent.Items.Count == recentCount && photo.NextBirdNumber == highWater &&
                  File.ReadAllBytes(path).SequenceEqual(original),
                "clearing retains pixels, original bytes, rating, group, recent choices and stable number high water");
            Check(Invoke<bool>(window, "UndoSpeciesEdit"), "the clear action is undoable through the production undo branch");
            Settle(root);
            Check(photo.Birds.Count == 2 && photo.HasManualSpeciesEdits && list.Items.Count == 2 &&
                  photo.Birds.Any(bird => bird.HasRegion) && photo.Birds.Any(bird => bird.ManualSpecies is not null),
                "undo restores the complete automatic and manual result set and its protection flag");
            model.IsBusy = true; Settle(root);
            Check(!button.IsEnabled && !Invoke<bool>(window, "ClearRecognitionResults") && photo.Birds.Count == 2,
                "a busy operation blocks the button and the underlying clear helper");
            model.IsBusy = false;

            var conflict = new ExportConflict(path, Path.Combine(directory, "target", "reset.JPG"), original.Length, original.Length + 10);
            foreach (var (action, buttonName) in new[] { (ExportConflictAction.Overwrite, "OverwriteButton"),
                         (ExportConflictAction.Rename, "RenameButton"), (ExportConflictAction.Cancel, "CancelButton") })
            {
                var decisionTask = Invoke<Task<ExportConflictDecision>>(window, "ResolveExportConflict", conflict,
                    CancellationToken.None, (Func<ExportConflictDialog, bool?>)(dialog =>
                    {
                        Check(dialog.Dispatcher.CheckAccess() && dialog.Decision.Action == ExportConflictAction.Cancel &&
                              ((CheckBox)dialog.FindName("ApplyToRemainingBox")).IsChecked == false &&
                              ((TextBlock)dialog.FindName("SourcePathText")).Text == path,
                            "the production resolver opens each new conflict on the UI dispatcher with ask-every-time defaults");
                        ((CheckBox)dialog.FindName("ApplyToRemainingBox")).IsChecked = true;
                        ((Button)dialog.FindName(buttonName)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        return action != ExportConflictAction.Cancel;
                    }));
                Pump(() => decisionTask.IsCompleted, "conflict resolver did not complete");
                var decision = decisionTask.GetAwaiter().GetResult();
                Check(decision.Action == action && decision.ApplyToRemaining == (action != ExportConflictAction.Cancel),
                    "actual conflict buttons flow through the production resolver: " + action);
            }
            var canceled = new CancellationTokenSource(); canceled.Cancel(); bool askedAfterCancel = false;
            var canceledTask = Invoke<Task<ExportConflictDecision>>(window, "ResolveExportConflict", conflict, canceled.Token,
                (Func<ExportConflictDialog, bool?>)(_ => { askedAfterCancel = true; return true; }));
            Pump(() => canceledTask.IsCompleted, "canceled conflict resolver did not complete");
            Check(canceledTask.GetAwaiter().GetResult().Action == ExportConflictAction.Cancel && !askedAfterCancel,
                "an already canceled export never presents another conflict dialog");
            canceled.Dispose();
            var protectedDialog = new ExportConflictDialog(conflict with { CanOverwrite = false, Explanation = "工程原件受保护" });
            var protectedSurface = (Grid)protectedDialog.Content; protectedSurface.Background = protectedDialog.Background;
            Layout(protectedSurface, 684, 440);
            Check(!((Button)protectedDialog.FindName("OverwriteButton")).IsEnabled &&
                  ((TextBlock)protectedDialog.FindName("ProtectionText")).Visibility == Visibility.Visible,
                "the conflict dialog clearly blocks overwriting a protected original");
            foreach (string name in new[] { "OverwriteButton", "RenameButton", "CancelButton", "ApplyToRemainingBox" })
            {
                var control = (FrameworkElement)protectedDialog.FindName(name);
                Rect bounds = control.TransformToAncestor(protectedSurface).TransformBounds(new Rect(control.RenderSize));
                Check(bounds.Top >= 0 && bounds.Bottom <= 440 && bounds.Left >= 0 && bounds.Right <= 684,
                    "the conflict choice remains fully visible: " + name);
            }
            Save(protectedSurface, "export-conflict-1.8.2.png", 684, 440);
            protectedDialog.Close();

            string helpText = (string)(typeof(MainWindow).GetField("UsageHelpText", BindingFlags.Static | BindingFlags.NonPublic)?.GetRawConstantValue()
                ?? throw new InvalidOperationException("Missing usage help."));
            var help = new HelpDialog(helpText);
            var helpSurface = (Grid)help.Content; helpSurface.Background = help.Background;
            Layout(helpSurface, 790, 600);
            var link = (Hyperlink)help.FindName("RepositoryLink");
            Check(link.NavigateUri.AbsoluteUri == "https://github.com/yyx263/BirdPhotoSelector" &&
                  string.Concat(link.Inlines.OfType<Run>().Select(run => run.Text)) == link.NavigateUri.AbsoluteUri,
                "help contains the exact clickable GitHub URL");
            Check(((TextBlock)help.FindName("HelpText")).Text.Contains("清空识别结果") && helpText.Contains("重命名"),
                "help explains recognition clearing and export conflict choices");
            Check(Descendants(helpSurface).OfType<ScrollViewer>().Any(scroll => scroll.ScrollableHeight > 0),
                "long help instructions remain scrollable inside the help window");
            Save(helpSurface, "help-1.8.2.png", 790, 600);
            Check(!help.IsVisible && !window.IsVisible, "the integration checks do not open a window or launch a browser");
            help.Close();
            return count;
        }
        finally
        {
            if (window is not null)
            {
                var model = (MainViewModel)window.DataContext;
                model.SetGroups(Array.Empty<PhotoGroup>()); model.IsBusy = false; model.Dirty = false;
                window.Close(); Pump(() => closed, "the hidden update-check window did not close");
            }
            SynchronizationContext.SetSynchronizationContext(previous);
            if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe update fixture cleanup.");
            Directory.Delete(directory, true);
        }
    }

    private static object Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance)
        ?? throw new InvalidOperationException("Missing field: " + name);
    private static T Invoke<T>(object instance, string name, params object?[] args) =>
        (T)(instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(instance, args)
            ?? throw new InvalidOperationException("Missing helper: " + name));
    private static void Layout(FrameworkElement root, int width = 1560, int height = 860)
    { root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout(); }
    private static void Settle(FrameworkElement root)
    { root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.Background); root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.SystemIdle); }
    private static void Pump(Func<bool> condition, string failure)
    {
        var watch = Stopwatch.StartNew(); var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || watch.Elapsed > TimeSpan.FromSeconds(8)) frame.Continue = false; };
        try { if (!condition()) { timer.Start(); Dispatcher.PushFrame(frame); } } finally { timer.Stop(); }
        if (!condition()) throw new InvalidOperationException(failure);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject item)
    {
        yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(item, i))) yield return child;
    }
    private static void Save(FrameworkElement content, string name, int width, int height)
    {
        string? directory = Environment.GetEnvironmentVariable("BIRD_UI_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name)); encoder.Save(stream);
    }
}
