using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BirdPhotoSelector;
using BirdPhotoSelector.Controls;

/// <summary>Hidden production-window integration; dialogs are injected, no real export or model is invoked.</summary>
internal static class SpeciesWindowChecks
{
    public static int Run()
    {
        if (Application.Current is null || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Species window checks require the real App resources on the STA dispatcher.");
        int count = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); count++; Console.WriteLine("  OK SPECIES WINDOW " + message); }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SpeciesWindowChecks");
        string directory = Path.Combine(allowed, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        MainWindow? window = null; MainViewModel? model = null;
        SynchronizationContext? previous = SynchronizationContext.Current;
        Exception? dispatcherError = null; bool closed = false;
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs e) { dispatcherError = e.Exception; e.Handled = true; }
        void OnClosed(object? sender, EventArgs e) => closed = true;
        try
        {
            var image = Image();
            string[] paths = Enumerable.Range(1, 3).Select(i => Path.Combine(directory, $"界面照片-{i}.JPG")).ToArray();
            foreach (string path in paths)
            {
                var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = File.Create(path); encoder.Save(stream);
            }
            var originals = paths.ToDictionary(path => path, File.ReadAllBytes);
            var first = Photo(paths[0], "editing-photo", new[] { Auto(7, .08, "Parus major", .98), Auto(11, .58, "Passer montanus", .95) });
            var second = Photo(paths[1], "manual-photo", new[] { new DetectedBird { Number = 19,
                ManualSpecies = new("鸳鸯", "Aix galericulata"), RecognitionStatus = "人工指定" } });
            second.SetGeometryReference(new("historical-member", Path.Combine(directory, "历史.JPG"), 1000, 800), PhotoGeometryStatus.NeedsReview);
            var unknown = new DetectedBird { Number = 23, Box = Box(.37), RecognitionStatus = "待识别" };
            var low = Auto(31, .7, "Leiothrix lutea", .35);
            var third = Photo(paths[2], "review-photo", new[] { Auto(17, .05, "Parus major", .97), unknown, low });
            var groups = new[] { Group("编辑组", first), Group("人工组", second), Group("待核对组", third) };
            window = new MainWindow(); model = (MainViewModel)window.DataContext;
            window.Dispatcher.UnhandledException += OnError; window.Closed += OnClosed;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            ((DispatcherTimer)Field(window, "_autoSaveTimer")).Stop();
            model.ProjectName = "鸟种人工编辑界面验证";
            var root = (Grid)window.Content; root.Background = window.Background;
            var viewer = (DualPhotoViewer)window.FindName("Viewer");
            var list = (ListBox)window.FindName("SpeciesResultsList");
            var addButton = (Button)window.FindName("AddSpeciesButton");
            var undoButton = (Button)window.FindName("UndoSpeciesButton");
            var recent = (RecentSpeciesHistory)Field(window, "_recentSpecies");
            var history = (SpeciesEditHistory)Field(window, "_speciesEdits");
            void Ready(PhotoItem photo)
            {
                if (!ReferenceEquals(model.CurrentPhoto, photo)) model.LocatePhoto(photo);
                Layout(root);
                if (!PumpUntil(() => model.IsPreviewReady || dispatcherError is not null || model.Status.StartsWith("预览失败：", StringComparison.Ordinal), TimeSpan.FromSeconds(8)))
                    throw new TimeoutException("The actual JPEG preview did not settle for species editing.");
                if (dispatcherError is not null) throw new InvalidOperationException("Species window dispatcher failed.", dispatcherError);
                if (!model.IsPreviewReady) throw new InvalidOperationException(model.Status);
                Settle(root);
            }
            bool Apply(PhotoItem photo, SpeciesIdentity identity, DetectedBird? replaced = null) =>
                Invoke<bool>(window, "ApplyManualSpecies", photo, identity, replaced);
            bool Remove(PhotoItem photo, DetectedBird bird) => Invoke<bool>(window, "RemoveBirdRegion", photo, bird);
            bool Undo() => Invoke<bool>(window, "UndoSpeciesEdit");
            bool Key(Key key, ModifierKeys modifiers) => Invoke<bool>(window, "TryUndoSpeciesKey", key, modifiers);
            bool Confirm(IReadOnlyList<PhotoItem> photos, PhotoFileScope scope, Func<SpeciesExportReminderDialog, bool?> callback) =>
                Invoke<bool>(window, "ConfirmSpeciesBeforeExport", photos, scope, callback);
            int OverlayCount() => ((Canvas)viewer.FindName("BirdOverlay")).Children.OfType<FrameworkElement>().Count(item => item.Tag is DetectedBird);
            void CheckOverlayMatches(string operation)
            {
                var expected = model.CurrentPhoto?.Birds.Where(bird => bird.HasRegion).ToArray() ?? Array.Empty<DetectedBird>();
                var actual = ((Canvas)viewer.FindName("BirdOverlay")).Children.OfType<FrameworkElement>()
                    .Select(item => item.Tag).OfType<DetectedBird>().ToArray();
                Check(actual.Length == expected.Length && actual.All(expected.Contains) && expected.All(actual.Contains),
                    "overlay objects exactly match the current photo's real region objects " + operation);
            }

            model.SetGroups(groups, first.Id); Ready(first); model.Dirty = false;
            Check(addButton.IsEnabled && !undoButton.IsEnabled && addButton.Content?.ToString()?.Contains("手动添加鸟种") == true && OverlayCount() == 2,
                "the real window enables manual species editing after JPEG loading and begins with two automatic overlays");
            string originalRows = Rows(first); int oldHighWater = first.NextBirdNumber;
            var replacement = new SpeciesIdentity("鸳鸯", "Aix galericulata");
            Check(Apply(first, replacement, first.Birds[0]), "the production replacement helper accepts a real selected-photo row");
            CheckOverlayMatches("immediately after replacing an automatic result with a manual label");
            Settle(root);
            var manual = first.Birds[0];
            Check(manual.ManualSpecies == replacement && !manual.HasRegion && manual.Species is null && manual.ConfidenceText == "—" &&
                  manual.SimilarityText == "—" && manual.DisplayNumberText == "手动" && ReferenceEquals(list.SelectedItem, manual) &&
                  first.Birds[1].DisplayNumber == 1 && OverlayCount() == 1 && first.HasManualSpeciesEdits,
                "replacement retains its row, selects a score-free manual tag, removes its old box and compacts the remaining overlay number");
            var container = list.ItemContainerGenerator.ContainerFromItem(manual) as FrameworkElement;
            var rowText = container is null ? Array.Empty<string>() : Descendants(container).OfType<TextBlock>().Select(text => text.Text).ToArray();
            var rowMenu = container is null ? null : Descendants(container).OfType<FrameworkElement>().Select(item => item.ContextMenu).FirstOrDefault(menu => menu is not null);
            Check(rowText.Contains("手动") && rowText.Contains("—") && rowMenu is not null &&
                rowMenu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).SequenceEqual(new[] { "删除鸟种", "更换鸟种" }),
                "production row bindings render the manual marker and empty confidence with both species-editing menu actions");
            Check(undoButton.IsEnabled && undoButton.ToolTip?.ToString()?.Contains("更换") == true && model.Dirty,
                "the undo button reflects the committed replacement and marks the project modified");
            int afterReplacementHighWater = first.NextBirdNumber;
            undoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle(root);
            Check(Rows(first) == originalRows && !first.HasManualSpeciesEdits && OverlayCount() == 2 && !history.CanUndo &&
                first.NextBirdNumber >= afterReplacementHighWater && first.NextBirdNumber > oldHighWater,
                "clicking the actual undo button restores automatic evidence and boxes without reusing allocated identities");

            var added = new SpeciesIdentity("红嘴相思鸟", "Leiothrix lutea");
            Check(Apply(first, added), "the production add helper appends another species without asking for a box"); Settle(root);
            CheckOverlayMatches("after appending a manual label without creating another box");
            Check(first.Birds.Count == 3 && first.Birds[^1].ManualSpecies == added && ReferenceEquals(list.SelectedItem, first.Birds[^1]) && OverlayCount() == 2,
                "an appended manual species remains selected while existing automatic boxes are unchanged");
            string beforeWrongKeys = Rows(first);
            Check(!Key(System.Windows.Input.Key.Z, ModifierKeys.None) && !Key(System.Windows.Input.Key.A, ModifierKeys.Control) &&
                !Key(System.Windows.Input.Key.Z, ModifierKeys.Control | ModifierKeys.Shift) && Rows(first) == beforeWrongKeys,
                "the production shortcut branch ignores plain Z and other modifier combinations");
            Check(Key(System.Windows.Input.Key.Z, ModifierKeys.Control), "Ctrl+Z enters the same production undo branch used by the real key handler");
            Settle(root);
            Check(first.Birds.Count == 2 && !first.HasManualSpeciesEdits && OverlayCount() == 2,
                "Ctrl+Z removes the newly appended label and restores the prior overlay state");

            Apply(first, added); Settle(root);
            string beforeBusy = Rows(first); int recentBeforeBusy = recent.Items.Count;
            model.IsBusy = true; Settle(root);
            Check(!addButton.IsEnabled && !undoButton.IsEnabled && !list.IsEnabled &&
                  !Apply(first, replacement) && !Remove(first, first.Birds[0]) && !Undo() && !Key(System.Windows.Input.Key.Z, ModifierKeys.Control) &&
                  Rows(first) == beforeBusy && recent.Items.Count == recentBeforeBusy,
                "busy-state bindings and production helpers block adding, replacing, deleting and undoing species");
            model.IsBusy = false; Settle(root); Undo(); Settle(root);
            Check(Remove(first, first.Birds[0]), "the production delete helper removes a selected-photo automatic result"); Settle(root);
            CheckOverlayMatches("after deleting a real region through the window");
            Check(first.Birds.Count == 1 && first.Birds[0].DisplayNumber == 1 && OverlayCount() == 1 && first.HasManualSpeciesEdits,
                "deleting a bird removes its associated box and renumbers the survivor");
            Undo(); Settle(root);
            Check(first.Birds.Count == 2 && OverlayCount() == 2 && !first.HasManualSpeciesEdits,
                "undo of deletion restores both bird results and the original manual-edit protection flag");

            for (int i = 0; i < 12; i++)
            {
                var photo = i % 2 == 0 ? first : second; Ready(photo);
                Check(Apply(photo, new("人工鸟种" + i, "Testus species" + i)), "manual species can be added across photos in the same project");
                Settle(root);
            }
            Ready(second); Apply(second, new("最近重复项", " TESTUS SPECIES5 ")); Settle(root);
            Check(recent.Items.Count == 10 && recent.Items[0].Key == "TESTUS SPECIES5" && recent.Items.Select(item => item.Key).Distinct().Count() == 10,
                "the real window shares a ten-item deduplicated MRU across photos and moves a reused scientific name to the front");
            int secondCount = second.Birds.Count, firstCount = first.Birds.Count;
            Ready(first); Check(Undo() && ReferenceEquals(model.CurrentPhoto, second), "undo locates the previous edit made on a different photo"); Ready(second);
            Check(ReferenceEquals(model.CurrentPhoto, second) && second.Birds.Count == secondCount - 1 && first.Birds.Count == firstCount && recent.Items.Count == 10,
                "cross-photo undo locates the edited photo while retaining other results and recent-choice suggestions");

            int reminders = 0;
            Check(Confirm(new[] { second }, PhotoFileScope.All, _ => { reminders++; throw new InvalidOperationException("Manual tags must not prompt."); }) && reminders == 0,
                "manual tags do not produce a confidence reminder even when the photo retains a geometry-review notice");
            Check(Confirm(new[] { third }, PhotoFileScope.RawOnly, _ => throw new InvalidOperationException("No RAW is in scope.")),
                "export reminder inspection respects the selected file-member scope");
            Ready(first); first.SetGeometryReference(new("old-frame", Path.Combine(directory, "old.JPG"), 720, 480), PhotoGeometryStatus.NeedsReview);
            Check(Confirm(new[] { first }, PhotoFileScope.All, _ => throw new InvalidOperationException("Geometry is not a confidence reason.")),
                "geometry review alone does not turn otherwise identified high-confidence regions into species warnings");
            string reviewRows = Rows(third); int selectionBeforeContinue = third.SelectedBirdIndex;
            Check(Confirm(new[] { first, second, third }, PhotoFileScope.All, dialog =>
            {
                reminders++;
                bool readable = !dialog.IsVisible && ((TextBlock)dialog.FindName("DetailsText")).Text.Contains(third.FileName) &&
                    ((TextBlock)dialog.FindName("SummaryText")).Text.Contains("低置信度");
                dialog.Close(); Check(readable, "the injected real reminder describes the affected photo and confidence reason without opening a modal window");
                return true;
            }) && ReferenceEquals(model.CurrentPhoto, first) && Rows(third) == reviewRows && third.SelectedBirdIndex == selectionBeforeContinue,
                "continuing past the export reminder never edits results or navigates away from the current photo");
            Check(!Confirm(new[] { first, second, third }, PhotoFileScope.All, dialog => { dialog.Close(); return false; }),
                "return-to-review cancels the export flow"); Ready(third);
            Check(ReferenceEquals(model.CurrentPhoto, third) && ReferenceEquals(third.SelectedBird, unknown) && ReferenceEquals(list.SelectedItem, unknown) && Rows(third) == reviewRows,
                "return-to-review crosses groups and selects the first unidentified frame without rewriting recognition evidence");
            Remove(third, unknown); Settle(root);
            Check(!Confirm(new[] { third }, PhotoFileScope.All, dialog => { dialog.Close(); return false; }),
                "a remaining low-confidence AI frame still offers return-to-review"); Settle(root);
            Check(ReferenceEquals(third.SelectedBird, low) && ReferenceEquals(list.SelectedItem, low) && low.Species?.Confidence == .35,
                "return-to-review selects the actual low-confidence frame after the unidentified row is removed");
            Apply(third, added); Settle(root);
            CheckOverlayMatches("when a selected manual row coexists with two valid automatic regions");
            Check(third.Birds.Count == 3 && third.Birds.Count(bird => bird.HasRegion) == 2 && list.Items.Count == 3,
                "scrolling to the bottom manual row does not hide or delete other automatic results from the data model");
            var retainedImage = viewer.CurrentImage;
            var retainedView = (third.ViewZoom, third.ViewCenterX, third.ViewCenterY);
            foreach (var region in third.Birds.Where(bird => bird.HasRegion).ToArray())
            {
                Check(Apply(third, replacement, region), "an existing remaining region can be replaced with a manual species");
                CheckOverlayMatches("immediately after replacing a remaining real region");
                Settle(root);
            }
            Check(third.Birds.Count == 3 && third.Birds.All(bird => !bird.HasRegion && bird.ManualSpecies is not null) &&
                  OverlayCount() == 0 && ReferenceEquals(viewer.CurrentImage, retainedImage) &&
                  (third.ViewZoom, third.ViewCenterX, third.ViewCenterY) == retainedView,
                "an all-manual result list has zero leftover bird overlays while retaining the current image and viewport");
            SaveScreenshot(root);
            Check(!window.IsVisible && originals.All(pair => File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                "all species-window checks remain hidden and leave original generated JPEG bytes unchanged");
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
                    if (!PumpUntil(() => closed, TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Hidden species window did not finish closing.");
                }
            }
            finally
            {
                if (window is not null) { window.Dispatcher.UnhandledException -= OnError; window.Closed -= OnClosed; }
                SynchronizationContext.SetSynchronizationContext(previous);
                if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Unsafe species-window fixture cleanup.");
                Directory.Delete(directory, true);
            }
        }
    }

    private static T Invoke<T>(MainWindow window, string name, params object?[] args)
    {
        var method = typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing production species hook: " + name);
        return (T)(method.Invoke(window, args) ?? throw new InvalidOperationException(name + " returned null."));
    }
    private static object Field(MainWindow window, string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
        ?? throw new InvalidOperationException("Missing production species field: " + name);
    private static PhotoItem Photo(string path, string id, IEnumerable<DetectedBird> birds)
    {
        var photo = new PhotoItem { FilePath = path, Id = id, Rating = PhotoRating.Keep, PixelWidth = 720, PixelHeight = 480,
            Metadata = new() { Camera = "离屏验证相机", CapturedAt = "2026-10-30 10:00:00" } };
        photo.SetBirds(birds, nextBirdNumber: 50); return photo;
    }
    private static PhotoGroup Group(string label, PhotoItem photo) { var group = new PhotoGroup(label); group.Photos.Add(photo); return group; }
    private static DetectedBird Auto(int number, double x, string scientific, double confidence) => new()
    { Number = number, Box = Box(x), Species = new(scientific == "Parus major" ? "欧亚大山雀" : "麻雀", scientific, .7, Confidence: confidence), RecognitionStatus = "已识别" };
    private static BirdBox Box(double x) => new() { X = x, Y = .25, Width = .22, Height = .35, Confidence = .9 };
    private static string Rows(PhotoItem photo) => JsonSerializer.Serialize(new
    {
        photo.Id, photo.Rating, photo.HasManualSpeciesEdits,
        Birds = photo.Birds.Select(bird => new { bird.Number, bird.HasRegion, bird.IsManual, bird.ManualSpecies, bird.Box, bird.Species, bird.RecognitionStatus }).ToArray()
    });
    private static IEnumerable<DependencyObject> Descendants(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(node, i))) yield return child;
    }
    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        if (condition()) return true;
        var frame = new DispatcherFrame(); var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) => { if (condition() || watch.Elapsed >= timeout) frame.Continue = false; };
        try { timer.Start(); Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        return condition();
    }
    private static void Layout(FrameworkElement root)
    { root.Width = 1560; root.Height = 900; root.Measure(new(1560, 900)); root.Arrange(new(0, 0, 1560, 900)); root.UpdateLayout(); }
    private static void Settle(FrameworkElement root)
    { root.UpdateLayout(); root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.Background); root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.SystemIdle); }
    private static BitmapSource Image()
    {
        var pixels = new byte[720 * 480 * 3];
        for (int y = 0; y < 480; y++) for (int x = 0; x < 720; x++)
        { int i = (y * 720 + x) * 3; pixels[i] = (byte)(40 + x / 5); pixels[i + 1] = (byte)(70 + y / 4); pixels[i + 2] = (byte)(60 + (x + y) / 8); }
        var bitmap = BitmapSource.Create(720, 480, 96, 96, PixelFormats.Rgb24, null, pixels, 720 * 3); bitmap.Freeze(); return bitmap;
    }
    private static void SaveScreenshot(FrameworkElement root)
    {
        string directory = Path.GetFullPath(Environment.GetEnvironmentVariable("BIRD_UI_SCREENSHOT_DIR") ?? Path.Combine("artifacts", "test-visuals"));
        Directory.CreateDirectory(directory);
        var image = new RenderTargetBitmap(1560, 900, 96, 96, PixelFormats.Pbgra32); image.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(Path.Combine(directory, "species-manual-window.png")); encoder.Save(output);
    }
}
