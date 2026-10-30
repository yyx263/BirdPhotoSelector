using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;
using BirdPhotoSelector.Controls;

/// <summary>Unframed manual species share result selection, never the region coordinate system.</summary>
internal static class ManualLabelViewerChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK MANUAL LABEL VIEWER " + description);
        }
        var image = BitmapSource.Create(1200, 800, 96, 96, PixelFormats.Rgb24, null, new byte[1200 * 800 * 3], 1200 * 3);
        image.Freeze();
        var firstLabel = Label(5, "白鹭", "Egretta garzetta");
        var ai = new DetectedBird { Number = 10, Box = Box(.2), Species = new("欧亚大山雀", "Parus major", .7) };
        // Even stale valid geometry must not make an explicit manual species assignment drawable.
        var middleLabel = new DetectedBird
        {
            Number = 6, IsManual = true, Box = Box(.8),
            ManualSpecies = new("红嘴蓝鹊", "Urocissa erythroryncha")
        };
        var region = new DetectedBird { Number = 11, IsManual = true, Box = Box(.6), Species = new("麻雀", "Passer montanus", .6) };
        var photo = new PhotoItem
        {
            FilePath = "mixed-manual-labels.jpg", PixelWidth = 1200, PixelHeight = 800,
            ViewZoom = 3, ViewCenterX = .6, ViewCenterY = .4
        };
        photo.SetBirds(new[] { firstLabel, ai, middleLabel, region }, selectedIndex: 2);
        var viewer = new DualPhotoViewer();
        Layout(viewer);
        int viewEvents = 0, selectionEvents = 0;
        viewer.ViewChanged += (_, _) => viewEvents++;
        viewer.BirdSelectionChanged += (_, _) => selectionEvents++;
        viewer.ShowPhoto(photo, image);
        Layout(viewer);
        var overlay = (Canvas)viewer.FindName("BirdOverlay");
        var detail = (Image)viewer.FindName("DetailImage");
        var footer = (TextBlock)viewer.FindName("DetectionText");
        Check(photo.Birds.Count == 4 && overlay.Children.Count == 2 &&
              overlay.Children.OfType<Grid>().Select(target => target.Tag).SequenceEqual(new object[] { ai, region }),
            "mixed result rows draw only actual regions, including skipping a manual identity with stale geometry");
        Check(overlay.Children.OfType<Grid>().Select(target =>
                ((TextBlock)target.Children.OfType<Border>().Single().Child).Text).SequenceEqual(new[] { "01", "02" }) &&
              firstLabel.DisplayNumberText == "手动" && middleLabel.DisplayNumberText == "手动",
            "visible region labels remain contiguous and manual rows do not consume a region number");
        Check(footer.Text == "人工指定 · 红嘴蓝鹊" && photo.SelectedBirdIndex == 2 && viewEvents == 0,
            "an unframed selected result displays its manual species without reporting a framing edit");
        Click((Grid)overlay.Children[1]);
        Check(photo.SelectedBirdIndex == 3 && ReferenceEquals(photo.SelectedBird, region) && selectionEvents == 1,
            "clicking a region selects its original result index past interleaved manual rows");

        var framedView = View(photo, detail);
        int beforeManualSelection = viewEvents;
        viewer.SelectBird(0);
        Check(ReferenceEquals(photo.SelectedBird, firstLabel) && selectionEvents == 2 &&
              SameView(framedView, View(photo, detail)) && viewEvents == beforeManualSelection && footer.Text == "人工指定 · 白鹭",
            "selecting an unframed manual row preserves zoom, pan and image placement");
        photo.Birds.Insert(0, Label(30, "绿背山雀", "Parus monticolus"));
        viewer.RefreshBirdOverlays();
        Click((Grid)overlay.Children[0]);
        Check(photo.SelectedBirdIndex == 2 && ReferenceEquals(photo.SelectedBird, ai) && selectionEvents == 3,
            "adding a manual row before existing regions does not shift overlay selection to the wrong result");
        BirdRegionDeleteEventArgs? deletion = null;
        viewer.DeleteRegionRequested += (_, request) => deletion = request;
        var menu = ((Grid)overlay.Children[0]).ContextMenu!;
        menu.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(deletion is not null && ReferenceEquals(deletion.Photo, photo) && ReferenceEquals(deletion.Bird, ai) &&
              menu.Items.OfType<MenuItem>().Single().Header?.ToString() == "删除选区",
            "a region menu still requests deletion of its exact result object among manual rows");

        viewer.SelectBird(photo.Birds.IndexOf(middleLabel));
        var manualView = View(photo, detail);
        viewer.ShowLoading();
        viewer.SelectBird(photo.Birds.IndexOf(region));
        Check(ReferenceEquals(photo.SelectedBird, middleLabel) && SameView(manualView, View(photo, detail)) && viewer.IsLoading,
            "loading retains the selected manual result and blocks region selection against the previous frame");
        viewer.ShowPhoto(photo, image);
        viewer.RestoreBirdView();
        Check(Math.Abs(photo.ViewZoom!.Value - 1) < 1e-9 && overlay.Children.Count == 2 && photo.SelectedBird == middleLabel,
            "explicit restore on an unframed result fits the photo without inventing a region or changing the result");

        // Mirror the production ApplyManualSpecies path: mutate this same PhotoItem,
        // then SelectBird without reloading pixels or explicitly refreshing overlays.
        var edits = new SpeciesEditHistory();
        var beforeEditView = View(photo, detail);
        int beforeEditEvents = viewEvents;
        var appended = edits.Add(photo, new("鸳鸯", "Aix galericulata"));
        viewer.SelectBird(photo.SelectedBirdIndex);
        Check(ReferenceEquals(photo.SelectedBird, appended) && overlay.Children.Count == 2 &&
              overlay.Children.OfType<Grid>().Select(target => target.Tag).SequenceEqual(new object[] { ai, region }) &&
              SameView(beforeEditView, View(photo, detail)) && ReferenceEquals(viewer.CurrentImage, image) && viewEvents == beforeEditEvents,
            "same-photo manual add refreshes existing overlays without reloading or changing the detail view");
        var firstReplacement = edits.Replace(photo, ai, new("白鹭", "Egretta garzetta"));
        viewer.SelectBird(photo.SelectedBirdIndex);
        Check(ReferenceEquals(photo.SelectedBird, firstReplacement) && !firstReplacement.HasRegion && overlay.Children.Count == 1 &&
              ReferenceEquals(((Grid)overlay.Children[0]).Tag, region) && region.DisplayNumber == 1 &&
              ((TextBlock)((Grid)overlay.Children[0]).Children.OfType<Border>().Single().Child).Text == "01",
            "same-photo AI replacement removes its overlay and renumbers the surviving box without ShowPhoto");
        var lastReplacement = edits.Replace(photo, region, new("麻雀", "Passer montanus"));
        viewer.SelectBird(photo.SelectedBirdIndex);
        Check(ReferenceEquals(photo.SelectedBird, lastReplacement) && photo.Birds.All(bird => !bird.HasRegion) &&
              overlay.Children.Count == 0 && SameView(beforeEditView, View(photo, detail)) &&
              ReferenceEquals(viewer.CurrentImage, image) && viewEvents == beforeEditEvents && footer.Text == "人工指定 · 麻雀",
            "replacing the last region atomically clears all old boxes while retaining pixels and manual framing");
        viewer.SelectBird(photo.Birds.IndexOf(firstReplacement));
        Check(overlay.Children.Count == 0 && photo.SelectedBird == firstReplacement && footer.Text == "人工指定 · 白鹭",
            "subsequent selection among manual-only results cannot resurrect removed region overlays");

        var onlyLabel = new PhotoItem { FilePath = "manual-label-only.jpg" };
        onlyLabel.SetBirds(new[] { Label(40, "白鹭", "Egretta garzetta") });
        var boxedPhoto = new PhotoItem { FilePath = "region-before-manual-label.jpg" };
        var separateRegion = new DetectedBird { Number = 50, Box = Box(.4) };
        boxedPhoto.SetBirds(new[] { separateRegion });
        viewer.ShowPhoto(boxedPhoto, image);
        Check(overlay.Children.Count == 1 && ReferenceEquals(((Grid)overlay.Children[0]).Tag, separateRegion),
            "switching from converted manual-only results to another photo draws only that photo's region");
        viewer.ShowPhoto(onlyLabel, image);
        Check(overlay.Children.Count == 0 && footer.Text == "人工指定 · 白鹭" &&
              Math.Abs(onlyLabel.ViewZoom!.Value - 1) < 1e-9 &&
              ((System.Windows.Controls.Primitives.ToggleButton)viewer.FindName("ManualRegionButton")).IsEnabled,
            "a manual-only photo remains fully viewable and can acquire a real hand-drawn region later");
        viewer.ShowPhoto(boxedPhoto, image);
        Check(overlay.Children.Count == 1 && ReferenceEquals(((Grid)overlay.Children[0]).Tag, separateRegion) &&
              !ReferenceEquals(((Grid)overlay.Children[0]).Tag, ai) && !ReferenceEquals(((Grid)overlay.Children[0]).Tag, region),
            "returning from a manual-only photo restores the correct source overlays without reviving deleted boxes");
        viewer.ShowPhoto(null);
        return count;
    }

    private static DetectedBird Label(int number, string chinese, string scientific) => new()
    { Number = number, IsManual = true, ManualSpecies = new(chinese, scientific) };
    private static BirdBox Box(double x) => new() { X = x, Y = .3, Width = .15, Height = .2, Confidence = .9 };
    private static void Click(Grid target) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,
        Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = target });
    private static double[] View(PhotoItem photo, Image image) => new[]
    { photo.ViewZoom!.Value, photo.ViewCenterX!.Value, photo.ViewCenterY!.Value, image.Width, image.Height, Canvas.GetLeft(image), Canvas.GetTop(image) };
    private static bool SameView(double[] first, double[] second) => first.Zip(second).All(pair => Math.Abs(pair.First - pair.Second) < 1e-9);
    private static void Layout(FrameworkElement viewer)
    { viewer.Measure(new Size(1200, 700)); viewer.Arrange(new Rect(0, 0, 1200, 700)); viewer.UpdateLayout(); }
}
