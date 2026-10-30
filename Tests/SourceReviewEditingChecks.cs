using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;
using BirdPhotoSelector.Controls;

/// <summary>Current pixels remain editable while historical bird coordinates await the user's review.</summary>
internal static class SourceReviewEditingChecks
{
    public static int Run()
    {
        if (Application.Current is null) throw new InvalidOperationException("Initialize App resources before viewer checks.");
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK SOURCE REVIEW " + description);
        }

        string stem = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SourceReviewEditing", Guid.NewGuid().ToString("N"));
        var jpg = new PhotoAsset(stem + ".JPG", PhotoAssetKind.Jpg);
        var raw = new PhotoAsset(stem + ".NEF", PhotoAssetKind.Raw);
        var photo = new PhotoItem { Rating = PhotoRating.Favorite };
        photo.SetAssets(new[] { jpg, raw }, jpg.Id, jpg.Id);
        photo.PixelWidth = 1200; photo.PixelHeight = 800;
        var original = new[]
        {
            new DetectedBird { Number = 17, Box = Box(.12, .2), Species = new("大山雀", "Parus major", .43) },
            new DetectedBird { Number = 29, IsManual = true, Box = Box(.61, .25), Species = new("麻雀", "Passer montanus", .59) }
        };
        photo.SetBirds(original, nextBirdNumber: 50);
        string photoId = photo.Id;
        var historicalFrame = photo.GeometryReference;
        photo.ViewZoom = 5; photo.ViewCenterX = .2; photo.ViewCenterY = .3;
        photo.RemoveAsset(jpg.Id);
        Check(photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && !photo.CanUseBirdGeometry && photo.Bird is null &&
              photo.Id == photoId && photo.Rating == PhotoRating.Favorite && photo.Birds.SequenceEqual(original) &&
              ReferenceEquals(photo.GeometryReference, historicalFrame) && photo.ActivePreviewAssetId == raw.Id,
            "switching from JPG to RAW retains every old bird and species without declaring old coordinates verified for AI");

        var viewer = new DualPhotoViewer();
        Layout(viewer);
        var image = Image();
        viewer.ShowPhoto(photo, image); viewer.UpdateLayout();
        var overlay = (Canvas)viewer.FindName("BirdOverlay");
        var detail = (Image)viewer.FindName("DetailImage");
        var viewport = (Border)viewer.FindName("DetailViewport");
        var label = (TextBlock)viewer.FindName("DetectionText");
        double fit = Math.Min(viewport.ActualWidth / image.PixelWidth, viewport.ActualHeight / image.PixelHeight);
        Check(overlay.Children.Count == 2 && ReferenceEquals(viewer.CurrentImage, image) && photo.ViewZoom == 1 &&
              Math.Abs(detail.Width / image.PixelWidth - fit) < 1e-6,
            "the changed-source preview keeps both old boxes visible while initially fitting current RAW pixels");
        Check(label.Text.Contains("大山雀", StringComparison.Ordinal) && !label.Text.Contains("待核对", StringComparison.Ordinal) &&
              !label.Text.Contains("来源已变化", StringComparison.Ordinal),
            "the viewer footer retains ordinary selected-bird information and leaves the source notice to the window header");
        Check(((ToggleButton)viewer.FindName("ManualRegionButton")).IsEnabled && viewer.BeginManualRegionSelection(),
            "manual region selection remains enabled on a changed-source preview");
        viewer.CancelManualRegionSelection();
        int viewChanges = 0;
        viewer.ViewChanged += (_, _) => viewChanges++;
        viewport.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 240)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewport });
        var zoomed = (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY);
        var other = new PhotoItem { FilePath = stem + "-other.JPG" };
        viewer.ShowPhoto(other, image);
        viewer.ShowPhoto(photo, image);
        Check(Math.Abs(photo.ViewZoom!.Value - zoomed.Item1!.Value) < 1e-9 &&
              Math.Abs(photo.ViewCenterX!.Value - zoomed.Item2!.Value) < 1e-9 &&
              Math.Abs(photo.ViewCenterY!.Value - zoomed.Item3!.Value) < 1e-9 && photo.ViewZoom > 1 && viewChanges == 1 &&
              photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && !photo.CanUseBirdGeometry,
            "current-source wheel zoom survives navigating away and back without confirming old bird geometry or reporting a new edit");
        photo.ViewZoom = 3; photo.ViewCenterX = .65; photo.ViewCenterY = .42;
        viewer.ShowPhoto(photo, image);
        viewer.ShowPhoto(other, image);
        viewer.ShowPhoto(photo, image);
        Check(Math.Abs(photo.ViewZoom!.Value - 3) < 1e-9 && photo.ViewCenterX == .65 && photo.ViewCenterY == .42 && viewChanges == 1 &&
              ReferenceEquals(photo.GeometryReference, historicalFrame),
            "a saved current-source pan and zoom restore while the historical bird reference remains unchanged");
        viewer.SelectBird(1);
        Check(ReferenceEquals(photo.SelectedBird, original[1]) && photo.ViewZoom > 1 &&
              photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && !photo.CanUseBirdGeometry,
            "clicking a retained bird explicitly focuses its box without clearing the source-change state");
        double focusedWidth = detail.Width;
        viewer.RestoreBirdView();
        Check(detail.Width == focusedWidth && label.Text.Contains("手动选区2", StringComparison.Ordinal),
            "an explicit restore uses the retained box and shows the contiguous display number");

        viewer.FitToFrame();
        DetectedBird? added = null;
        var newSpecies = new SpeciesMatch("红嘴蓝鹊", "Urocissa erythroryncha", .68);
        viewer.ManualRegionSelected += (_, proposal) =>
        {
            if (!ReferenceEquals(proposal.Photo, photo)) throw new InvalidOperationException("Manual proposal targeted the wrong photo.");
            added = photo.AddManualRegion(proposal.Box);
            added.Species = newSpecies;
            viewer.SelectBird(photo.SelectedBirdIndex);
        };
        double left = Canvas.GetLeft(detail), top = Canvas.GetTop(detail);
        var start = new Point(left + detail.Width * .3, top + detail.Height * .3);
        var end = new Point(left + detail.Width * .6, top + detail.Height * .6);
        Check(viewer.BeginManualRegionSelection() && viewer.CompleteManualRegionSelection(start, end) &&
              added is { IsManual: true, Box.IsValid: true } && added.Species == newSpecies && photo.Birds.Count == 3,
            "a newly drawn region maps to the displayed RAW image and can receive its own recognition result");
        Check(photo.Birds[0] == original[0] && photo.Birds[1] == original[1] &&
              original[0].Species?.ScientificName == "Parus major" && original[1].Species?.ScientificName == "Passer montanus" &&
              photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && ReferenceEquals(photo.GeometryReference, historicalFrame) &&
              photo.Id == photoId && photo.Rating == PhotoRating.Favorite,
            "adding a current-image manual result preserves both historical species, identity, rating and source notice");

        viewer.DeleteRegionRequested += (_, request) =>
        {
            if (!ReferenceEquals(request.Photo, photo)) throw new InvalidOperationException("Delete targeted the wrong photo.");
            photo.RemoveBird(request.Bird);
            viewer.RefreshBirdOverlays();
        };
        Check(viewer.RequestDeleteRegion(original[0]) && photo.Birds.Count == 2 && !photo.Birds.Contains(original[0]) &&
              ReferenceEquals(photo.SelectedBird, added) && photo.Birds.Select(bird => bird.DisplayNumber).SequenceEqual(new[] { 1, 2 }),
            "an existing old box can be deleted while preserving the selected new result and compacting display numbers");
        Check(overlay.Children.Count == 2 && photo.Birds[0] == original[1] && original[1].Species?.ScientificName == "Passer montanus" &&
              photo.GeometryStatus == PhotoGeometryStatus.NeedsReview,
            "deleting one old box keeps the remaining original species and its visible box");

        viewer.ShowLoading();
        Check(!viewer.BeginManualRegionSelection() && !viewer.RequestDeleteRegion(original[1]) && photo.Birds.Count == 2,
            "retained loading frames still block edits until the new source actually finishes loading");
        viewer.ShowPhoto(photo, image); viewer.UpdateLayout();
        Check(viewer.BeginManualRegionSelection() && viewer.RequestDeleteRegion(original[1]) &&
              photo.Birds.Count == 1 && ReferenceEquals(photo.SelectedBird, added) && added!.DisplayNumber == 1,
            "after loading, historical regions can still be edited and the last visible region is numbered one");
        viewer.CancelManualRegionSelection();
        Check(viewer.RequestDeleteRegion(added!) && photo.Birds.Count == 0 && overlay.Children.Count == 0 &&
              photo.SelectedBird is null && !photo.HasManualSpeciesEdits && photo.DetectionStatus == "待识别" &&
              photo.GeometryStatus == PhotoGeometryStatus.Unspecified && photo.GeometryReference is null &&
              photo.ViewZoom is null && photo.ViewCenterX is null && photo.ViewCenterY is null,
            "deleting the final result clears stale geometry and manual protection so the current source can be recognized again");
        Check(photo.Id == photoId && photo.Rating == PhotoRating.Favorite && photo.NextBirdNumber == 51 &&
              photo.Assets.Count == 1 && photo.ActivePreviewAssetId == raw.Id && photo.Assets.Single().Id == raw.Id,
            "deleting all results preserves logical identity, rating, the current RAW member and the numbering high-water mark");
        viewer.ShowPhoto(null);
        return count;
    }

    private static BirdBox Box(double x, double y) => new() { X = x, Y = y, Width = .16, Height = .2, Confidence = .9 };
    private static void Layout(FrameworkElement viewer)
    {
        viewer.Measure(new Size(1200, 700)); viewer.Arrange(new Rect(0, 0, 1200, 700)); viewer.UpdateLayout();
    }
    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(1200, 800, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat((byte)90, 1200 * 800 * 3).ToArray(), 1200 * 3);
        image.Freeze(); return image;
    }
}
