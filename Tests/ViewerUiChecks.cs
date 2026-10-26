using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;
using BirdPhotoSelector.Controls;

/// <summary>
/// Exercises real WPF layout and routed input without creating a visible desktop window.
/// Run from the test application's STA entry thread before awaiting service tests.
/// </summary>
internal static class ViewerUiChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK UI " + description);
        }

        var viewer = new DualPhotoViewer();
        Layout(viewer, 1000, 600);
        var detail = (Border)viewer.FindName("DetailViewport");
        var full = (Border)viewer.FindName("FullViewport");
        var detailImage = (Image)viewer.FindName("DetailImage");
        var fullImage = (Image)viewer.FindName("FullImage");
        var viewportRectangle = (System.Windows.Shapes.Rectangle)viewer.FindName("ViewportRectangle");
        var viewportOutline = (System.Windows.Shapes.Rectangle)viewer.FindName("ViewportOutline");
        var reset = (Button)viewer.FindName("RestoreButton");
        var fit = (Button)viewer.FindName("FitButton");
        double PixelScale() => detailImage.Width / ((BitmapSource)detailImage.Source).PixelWidth;
        double NaturalFit() => Math.Min(detail.ActualWidth / ((BitmapSource)detailImage.Source).PixelWidth,
                                        detail.ActualHeight / ((BitmapSource)detailImage.Source).PixelHeight);
        bool DetailStaysInsideImage()
        {
            double left = Canvas.GetLeft(detailImage), top = Canvas.GetTop(detailImage);
            bool horizontal = detailImage.Width <= detail.ActualWidth + .00001
                ? Near(left, (detail.ActualWidth - detailImage.Width) / 2)
                : left <= .00001 && left + detailImage.Width >= detail.ActualWidth - .00001;
            bool vertical = detailImage.Height <= detail.ActualHeight + .00001
                ? Near(top, (detail.ActualHeight - detailImage.Height) / 2)
                : top <= .00001 && top + detailImage.Height >= detail.ActualHeight - .00001;
            return horizontal && vertical;
        }
        bool LocatorStaysInsidePhoto()
        {
            var source = (BitmapSource)fullImage.Source;
            double scale = Math.Min(full.ActualWidth / source.PixelWidth, full.ActualHeight / source.PixelHeight);
            var photoBounds = new Rect((full.ActualWidth - source.PixelWidth * scale) / 2,
                                       (full.ActualHeight - source.PixelHeight * scale) / 2,
                                       source.PixelWidth * scale, source.PixelHeight * scale);
            foreach (var locator in new[] { viewportRectangle, viewportOutline })
            {
                double halfStroke = locator.StrokeThickness / 2;
                if (Canvas.GetLeft(locator) - halfStroke < photoBounds.Left - .00001 ||
                    Canvas.GetTop(locator) - halfStroke < photoBounds.Top - .00001 ||
                    Canvas.GetLeft(locator) + locator.Width + halfStroke > photoBounds.Right + .00001 ||
                    Canvas.GetTop(locator) + locator.Height + halfStroke > photoBounds.Bottom + .00001) return false;
            }
            return ((Canvas)viewer.FindName("FullOverlay")).Clip is RectangleGeometry clip && clip.Rect == photoBounds;
        }
        MouseWheelEventArgs RaiseWheel(int delta)
        {
            var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
            {
                RoutedEvent = Mouse.PreviewMouseWheelEvent,
                Source = detail
            };
            detail.RaiseEvent(args);
            return args;
        }
        var bitmap = CreatePreviewBitmap();
        var photo = new PhotoItem
        {
            FilePath = "offscreen-fixture.jpg",
            Bird = new BirdBox { X = .02, Y = .1, Width = .15, Height = .2, Confidence = .95 }
        };

        int changes = 0;
        viewer.ViewChanged += (_, _) => changes++;
        viewer.ShowPhoto(photo, bitmap);
        Layout(viewer, 1000, 600);
        Check(ReferenceEquals(viewer.CurrentImage, bitmap) && ReferenceEquals(fullImage.Source, detailImage.Source),
            "both panes display the same oriented source bitmap");
        Check(changes == 0, "loading and initial layout do not report user edits");
        Check(photo.ViewCenterX > .095 && Near(Canvas.GetLeft(detailImage), 0) && Near(photo.ViewCenterY!.Value, .2),
            "an edge bird stops at the image boundary instead of introducing a blank margin");
        double widthFraction = photo.Bird.Width * detailImage.Width / detail.ActualWidth;
        double heightFraction = photo.Bird.Height * detailImage.Height / detail.ActualHeight;
        Check(Near(Math.Max(widthFraction, heightFraction), .7), "whole-bird auto zoom occupies 70% of the viewport when within scale limits");
        Check(DetailStaysInsideImage(), "auto bird framing covers the viewport without showing beyond magnified image edges");

        // Verify the white frame describes only the visible source image, excluding any dark margin.
        double fullScale = Math.Min(full.ActualWidth / bitmap.PixelWidth, full.ActualHeight / bitmap.PixelHeight);
        double intersectionRight = Math.Min(1, photo.ViewCenterX!.Value + detail.ActualWidth / detailImage.Width / 2);
        Check(Near(viewportRectangle.Width, intersectionRight * bitmap.PixelWidth * fullScale - viewportRectangle.StrokeThickness) &&
              LocatorStaysInsidePhoto(),
            "white viewport frame and contrast stroke remain fully inside the fitted photo");
        Check(viewer.IsDetailArea(detailImage) && viewer.IsDetailArea(detail) &&
              !viewer.IsDetailArea(fullImage) && !viewer.IsDetailArea(reset) && !viewer.IsDetailArea(fit),
            "outer wheel routing can distinguish detail pixels from full image and footer");

        Layout(viewer, 940, 530);
        Check(changes == 0, "resizing does not mark the project as manually edited");

        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        {
            RoutedEvent = Mouse.PreviewMouseWheelEvent,
            Source = detail
        };
        double zoomBeforeWheel = photo.ViewZoom!.Value;
        detail.RaiseEvent(wheel);
        Check(wheel.Handled && photo.ViewZoom > zoomBeforeWheel && changes == 1,
            "detail wheel zooms the current photo, consumes the event, and records a user edit");
        viewer.RestoreBirdView();
        Check(changes == 2 && Near(Canvas.GetLeft(detailImage), 0) && DetailStaysInsideImage(),
            "restore returns to the bounded edge-bird view and records a user edit");

        photo.ViewZoom = 4.2;
        photo.ViewCenterX = .8;
        photo.ViewCenterY = .7;
        viewer.ShowPhoto(photo, bitmap);
        Check(Near(photo.ViewZoom!.Value, 4.2) && photo.ViewCenterX == .8 && photo.ViewCenterY == .7 && changes == 2,
            "navigation restores manual zoom and center without reporting an edit");

        var tinyBirdPhoto = new PhotoItem
        {
            FilePath = "tiny-bird-fixture.jpg",
            Bird = new BirdBox { X = .495, Y = .495, Width = .005, Height = .005, Confidence = .9 }
        };
        viewer.ShowPhoto(tinyBirdPhoto, bitmap);
        Check(Near(PixelScale(), 2) &&
              Math.Max(.005 * detailImage.Width / detail.ActualWidth, .005 * detailImage.Height / detail.ActualHeight) < .7,
            "tiny birds respect the 200% pixel limit even when the 70% target would require more zoom");

        var largeBirdPhoto = new PhotoItem
        {
            FilePath = "large-bird-fixture.jpg",
            Bird = new BirdBox { X = .025, Y = .025, Width = .95, Height = .95, Confidence = .9 }
        };
        viewer.ShowPhoto(largeBirdPhoto, bitmap);
        Check(Near(PixelScale(), NaturalFit()) && Near(largeBirdPhoto.ViewZoom!.Value, 1),
            "a bird covering most of the photo never shrinks the detail image below full-image fit");

        var oldSavedPhoto = new PhotoItem
        {
            FilePath = "legacy-view-fixture.jpg", ViewZoom = 512, ViewCenterX = .8, ViewCenterY = .7
        };
        int beforeProgrammaticChanges = changes;
        viewer.ShowPhoto(oldSavedPhoto, bitmap);
        Check(Near(PixelScale(), 2) && changes == beforeProgrammaticChanges,
            "legacy 512× relative views clamp to 200% without marking the project as user-edited");
        Layout(viewer, 2000, 1200);
        Check(Near(PixelScale(), 2) && changes == beforeProgrammaticChanges && DetailStaysInsideImage() && LocatorStaysInsidePhoto(),
            "enlarging the viewport re-clamps persisted relative zoom to the 200% pixel ceiling");
        Layout(viewer, 940, 530);
        oldSavedPhoto.ViewZoom = .05;
        viewer.ShowPhoto(oldSavedPhoto, bitmap);
        Check(Near(PixelScale(), NaturalFit()) && oldSavedPhoto.ViewCenterX == .5 && oldSavedPhoto.ViewCenterY == .5 &&
              changes == beforeProgrammaticChanges && DetailStaysInsideImage() && LocatorStaysInsidePhoto(),
            "legacy views below fit are raised to the minimum and recentered on every fitted axis");

        oldSavedPhoto.ViewZoom = 3;
        oldSavedPhoto.ViewCenterX = .7;
        oldSavedPhoto.ViewCenterY = .65;
        viewer.ShowPhoto(oldSavedPhoto, bitmap);
        fit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Near(PixelScale(), NaturalFit()) && oldSavedPhoto.ViewCenterX == .5 && oldSavedPhoto.ViewCenterY == .5 &&
              changes == beforeProgrammaticChanges + 1,
            "fit action recenters the full photo at minimum allowed scale and records a deliberate edit");
        Check(fit.TransformToAncestor(viewer).Transform(new Point()).X < reset.TransformToAncestor(viewer).Transform(new Point()).X,
            "fit-to-frame button sits to the left of bird restore");

        var anchorPhoto = new PhotoItem
        {
            FilePath = "cursor-anchor-fixture.jpg", ViewZoom = 3, ViewCenterX = .5, ViewCenterY = .5
        };
        viewer.ShowPhoto(anchorPhoto, bitmap);
        var cursor = Mouse.GetPosition(detail);
        double sourceX = anchorPhoto.ViewCenterX!.Value * bitmap.PixelWidth + (cursor.X - detail.ActualWidth / 2) / PixelScale();
        double sourceY = anchorPhoto.ViewCenterY!.Value * bitmap.PixelHeight + (cursor.Y - detail.ActualHeight / 2) / PixelScale();
        RaiseWheel(120);
        double anchoredX = anchorPhoto.ViewCenterX!.Value * bitmap.PixelWidth + (cursor.X - detail.ActualWidth / 2) / PixelScale();
        double anchoredY = anchorPhoto.ViewCenterY!.Value * bitmap.PixelHeight + (cursor.Y - detail.ActualHeight / 2) / PixelScale();
        Check(Near(sourceX, anchoredX) && Near(sourceY, anchoredY), "wheel zoom preserves the image point under the mouse cursor");
        Check(RaiseWheel(12000).Handled && Near(PixelScale(), 2), "repeated zoom-in stops at exactly 200% original pixels");
        int atMaximumChanges = changes;
        RaiseWheel(12000);
        Check(changes == atMaximumChanges && Near(PixelScale(), 2), "zooming beyond 200% neither moves the view nor reports an edit");
        Check(RaiseWheel(-12000).Handled && Near(PixelScale(), NaturalFit()),
            "repeated zoom-out stops at full-image fit instead of shrinking further");
        Check(anchorPhoto.ViewCenterX == .5 && anchorPhoto.ViewCenterY == .5 &&
              DetailStaysInsideImage() && LocatorStaysInsidePhoto(),
            "zooming out releases the cursor anchor at image bounds and recenters the fitted axes");

        var extremePanPhoto = new PhotoItem
        {
            FilePath = "extreme-pan-fixture.jpg", ViewZoom = 4, ViewCenterX = 1_000_000, ViewCenterY = -1_000_000
        };
        int beforeBoundedLoads = changes;
        viewer.ShowPhoto(extremePanPhoto, bitmap);
        Check(Near(Canvas.GetLeft(detailImage), detail.ActualWidth - detailImage.Width) &&
              Near(Canvas.GetTop(detailImage), 0) && DetailStaysInsideImage() && LocatorStaysInsidePhoto(),
            "huge legacy pan offsets clamp to the right and top image edges");
        extremePanPhoto.ViewCenterX = -1_000_000;
        extremePanPhoto.ViewCenterY = 1_000_000;
        viewer.ShowPhoto(extremePanPhoto, bitmap);
        Check(Near(Canvas.GetLeft(detailImage), 0) &&
              Near(Canvas.GetTop(detailImage), detail.ActualHeight - detailImage.Height) &&
              DetailStaysInsideImage() && LocatorStaysInsidePhoto() && changes == beforeBoundedLoads,
            "opposite pan outliers clamp to left and bottom edges without reporting a user edit");
        Layout(viewer, 1400, 400);
        Check(DetailStaysInsideImage() && LocatorStaysInsidePhoto(),
            "resizing a panned view keeps both image coverage and white viewport strokes in bounds");

        Layout(viewer, 940, 1400);
        var mixedAxisPhoto = new PhotoItem
        {
            FilePath = "mixed-axis-pan-fixture.jpg", ViewZoom = 2, ViewCenterX = 1_000_000, ViewCenterY = -1_000_000
        };
        viewer.ShowPhoto(mixedAxisPhoto, bitmap);
        Check(detailImage.Width > detail.ActualWidth && detailImage.Height < detail.ActualHeight &&
              Near(Canvas.GetLeft(detailImage), detail.ActualWidth - detailImage.Width) && mixedAxisPhoto.ViewCenterY == .5 &&
              DetailStaysInsideImage() && LocatorStaysInsidePhoto(),
            "an image smaller on one axis stays centered there while its larger axis clamps panning");
        viewer.FitToFrame();
        Check(mixedAxisPhoto.ViewCenterX == .5 && mixedAxisPhoto.ViewCenterY == .5 &&
              DetailStaysInsideImage() && LocatorStaysInsidePhoto(),
            "fit-to-frame prevents dragging into extra empty space on either fitted axis");
        Layout(viewer, 940, 530);

        var smallBitmap = BitmapSource.Create(10, 6, 96, 96, PixelFormats.Rgb24, null, new byte[10 * 6 * 3], 10 * 3);
        smallBitmap.Freeze();
        var smallPhoto = new PhotoItem
        {
            FilePath = "tiny-original-fixture.jpg", ViewZoom = 512, ViewCenterX = -1_000_000, ViewCenterY = 1_000_000
        };
        viewer.ShowPhoto(smallPhoto, smallBitmap);
        Check(NaturalFit() > 2 && Near(PixelScale(), 2) && smallPhoto.ViewCenterX == .5 && smallPhoto.ViewCenterY == .5 &&
              DetailStaysInsideImage() && LocatorStaysInsidePhoto(),
            "tiny originals are centered at 200% when fitting them would exceed the absolute ceiling");
        int beforeTinyWheel = changes;
        RaiseWheel(12000); RaiseWheel(-12000);
        viewer.FitToFrame();
        Check(Near(PixelScale(), 2) && changes == beforeTinyWheel,
            "tiny originals have a stable 200% fit with no spurious wheel or fit edits");

        Layout(viewer, 600, 400);
        var fitBounds = fit.TransformToAncestor(viewer).TransformBounds(new Rect(fit.RenderSize));
        var resetBounds = reset.TransformToAncestor(viewer).TransformBounds(new Rect(reset.RenderSize));
        Check(fitBounds.Right <= resetBounds.Left && resetBounds.Right <= viewer.ActualWidth &&
              fit.ActualWidth >= 50 && reset.ActualWidth >= 70,
            "both compact footer actions remain visible in a narrow dual-view control");

        // Produce an optional standalone screenshot for visual review of the actual WPF control.
        string? outputDirectory = Environment.GetEnvironmentVariable("BIRD_UI_SCREENSHOT_DIR");
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
            var illustratedPhoto = new PhotoItem
            {
                FilePath = "Illustrated preview fixture.jpg",
                Bird = new BirdBox { X = .31, Y = .25, Width = .28, Height = .42, Confidence = .96 }
            };
            viewer.ShowPhoto(illustratedPhoto, bitmap);
            Layout(viewer, 1200, 650);
            var screenshot = new RenderTargetBitmap(1200, 650, 96, 96, PixelFormats.Pbgra32);
            screenshot.Render(viewer);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(screenshot));
            using var output = File.Create(Path.Combine(outputDirectory, "viewer-offscreen.png"));
            encoder.Save(output);
        }

        var loadingPhoto = new PhotoItem
        {
            FilePath = "retained-frame-fixture.jpg", ViewZoom = 2, ViewCenterX = .6, ViewCenterY = .4
        };
        viewer.ShowPhoto(loadingPhoto, bitmap);
        var retainedImage = viewer.CurrentImage;
        var retainedFullSource = fullImage.Source;
        var retainedDetailSource = detailImage.Source;
        var retainedFrame = new Rect(Canvas.GetLeft(viewportRectangle), Canvas.GetTop(viewportRectangle),
                                     viewportRectangle.Width, viewportRectangle.Height);
        var retainedImageBounds = new Rect(Canvas.GetLeft(detailImage), Canvas.GetTop(detailImage),
                                           detailImage.Width, detailImage.Height);
        double? retainedZoom = loadingPhoto.ViewZoom;
        double? retainedCenterX = loadingPhoto.ViewCenterX;
        double? retainedCenterY = loadingPhoto.ViewCenterY;
        int beforeLoadingChanges = changes;
        viewer.ShowLoading();
        Check(viewer.IsLoading && ReferenceEquals(viewer.CurrentImage, retainedImage) &&
              ReferenceEquals(fullImage.Source, retainedFullSource) && ReferenceEquals(detailImage.Source, retainedDetailSource) &&
              retainedFrame == new Rect(Canvas.GetLeft(viewportRectangle), Canvas.GetTop(viewportRectangle),
                                        viewportRectangle.Width, viewportRectangle.Height) &&
              retainedImageBounds == new Rect(Canvas.GetLeft(detailImage), Canvas.GetTop(detailImage), detailImage.Width, detailImage.Height),
            "loading retains the exact previous bitmap, viewport frame, and image geometry without a blank flash");
        Check(!fit.IsEnabled && !reset.IsEnabled && ((TextBlock)viewer.FindName("DetectionText")).Text == "正在载入照片…",
            "retained-frame loading shows subtle status and disables view actions");
        var blockedWheel = RaiseWheel(120);
        viewer.FitToFrame();
        viewer.RestoreBirdView();
        var blockedMouseDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = detail
        };
        detail.RaiseEvent(blockedMouseDown);
        Check(blockedWheel.Handled && blockedMouseDown.Handled && !detail.IsMouseCaptured &&
              loadingPhoto.ViewZoom == retainedZoom && loadingPhoto.ViewCenterX == retainedCenterX &&
              loadingPhoto.ViewCenterY == retainedCenterY && changes == beforeLoadingChanges,
            "loading consumes wheel and drag input while fit and bird restore leave the previous photo unmodified");

        viewer.ShowLoadError("测试照片不存在");
        RaiseWheel(120);
        viewer.FitToFrame();
        viewer.RestoreBirdView();
        Check(!viewer.IsLoading && ReferenceEquals(viewer.CurrentImage, retainedImage) &&
              ReferenceEquals(fullImage.Source, retainedFullSource) && ReferenceEquals(detailImage.Source, retainedDetailSource) &&
              !fit.IsEnabled && !reset.IsEnabled && loadingPhoto.ViewZoom == retainedZoom &&
              loadingPhoto.ViewCenterX == retainedCenterX && loadingPhoto.ViewCenterY == retainedCenterY &&
              ((TextBlock)viewer.FindName("DetectionText")).Text.Contains("测试照片不存在", StringComparison.Ordinal) &&
              changes == beforeLoadingChanges,
            "a load error keeps the previous frame and clear error status while old-photo interactions remain blocked");

        viewer.ShowLoading();
        var replacementBitmap = new CroppedBitmap(bitmap, new Int32Rect(0, 0, 1000, 700));
        replacementBitmap.Freeze();
        var replacementPhoto = new PhotoItem { FilePath = "replacement-fixture.jpg" };
        viewer.ShowPhoto(replacementPhoto, replacementBitmap);
        Check(!viewer.IsLoading && ReferenceEquals(viewer.CurrentImage, replacementBitmap) &&
              ReferenceEquals(fullImage.Source, replacementBitmap) && ReferenceEquals(detailImage.Source, replacementBitmap) &&
              fit.IsEnabled && reset.IsEnabled && changes == beforeLoadingChanges,
            "successful decode atomically replaces both retained sources and re-enables the new photo without a user edit");
        double replacementScale = PixelScale();
        RaiseWheel(120);
        Check(PixelScale() > replacementScale && replacementPhoto.ViewZoom > 1 && changes == beforeLoadingChanges + 1,
            "successful replacement re-enables wheel interaction for the new photo only");

        viewer.ShowPhoto(null);
        Check(!viewer.IsLoading && viewer.CurrentImage is null && !reset.IsEnabled && !fit.IsEnabled && viewportRectangle.Visibility == Visibility.Collapsed,
            "empty session clears image, locator frame, and both view actions");
        viewer.ShowLoading();
        Check(viewer.IsLoading && viewer.CurrentImage is null && fullImage.Source is null && detailImage.Source is null &&
              ((TextBlock)viewer.FindName("EmptyTitle")).Text == "正在载入照片",
            "first-photo loading shows the loading empty state when there is no previous frame");
        viewer.ShowLoadError("无法解码照片");
        Check(!viewer.IsLoading && viewer.CurrentImage is null &&
              ((TextBlock)viewer.FindName("EmptyTitle")).Text == "照片载入失败" &&
              ((TextBlock)viewer.FindName("EmptyMessage")).Text == "无法解码照片",
            "first-photo failure presents a readable error without inventing an old frame");
        viewer.ShowPhoto(null);
        count += RunMultiBirdChecks(bitmap);
        count += RunManualRegionChecks(bitmap);
        return count;
    }

    private static int RunMultiBirdChecks(BitmapSource bitmap)
    {
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK UI MULTIBIRD " + description);
        }
        var viewer = new DualPhotoViewer();
        Layout(viewer, 1200, 650);
        var overlay = (Canvas)viewer.FindName("BirdOverlay");
        var full = (Border)viewer.FindName("FullViewport");
        var detail = (Border)viewer.FindName("DetailViewport");
        var detailImage = (Image)viewer.FindName("DetailImage");
        var locator = (System.Windows.Shapes.Rectangle)viewer.FindName("ViewportRectangle");
        var photo = new PhotoItem { FilePath = "multi-bird-fixture.jpg" };
        photo.SetBirds(new[]
        {
            new DetectedBird { Box = new BirdBox { X = .12, Y = .1, Width = .3, Height = .4, Confidence = .98 } },
            new DetectedBird { Box = new BirdBox { X = .74, Y = .72, Width = .2, Height = .22, Confidence = .92 } },
            new DetectedBird { Box = new BirdBox { X = .998, Y = .998, Width = .002, Height = .002, Confidence = .81 } }
        });
        int selectedEvents = 0, viewEvents = 0;
        viewer.BirdSelectionChanged += (_, _) => selectedEvents++;
        viewer.ViewChanged += (_, _) => viewEvents++;
        viewer.ShowPhoto(photo, bitmap);
        Layout(viewer, 1200, 650);

        Rect PhotoBounds()
        {
            double scale = Math.Min(full.ActualWidth / bitmap.PixelWidth, full.ActualHeight / bitmap.PixelHeight);
            return new Rect((full.ActualWidth - bitmap.PixelWidth * scale) / 2,
                            (full.ActualHeight - bitmap.PixelHeight * scale) / 2,
                            bitmap.PixelWidth * scale, bitmap.PixelHeight * scale);
        }
        bool BoxesAndLabelsStayInsidePhoto()
        {
            var bounds = PhotoBounds();
            if (overlay.Clip is not RectangleGeometry clip || clip.Rect != bounds) return false;
            foreach (var target in overlay.Children.OfType<Grid>())
            {
                var boxBounds = new Rect(Canvas.GetLeft(target), Canvas.GetTop(target), target.Width, target.Height);
                var label = target.Children.OfType<Border>().Single();
                Rect labelBounds = label.TransformToAncestor(overlay).TransformBounds(new Rect(label.RenderSize));
                if (boxBounds.Left < bounds.Left - .00001 || boxBounds.Top < bounds.Top - .00001 ||
                    boxBounds.Right > bounds.Right + .00001 || boxBounds.Bottom > bounds.Bottom + .00001 ||
                    labelBounds.Left < bounds.Left - .00001 || labelBounds.Top < bounds.Top - .00001 ||
                    labelBounds.Right > bounds.Right + .00001 || labelBounds.Bottom > bounds.Bottom + .00001)
                {
                    Console.WriteLine($"  BIRD OVERLAY BOUNDS photo={bounds} box={boxBounds} label={labelBounds}");
                    return false;
                }
            }
            return true;
        }
        double BirdFraction()
        {
            var box = photo.Bird!;
            return Math.Max(box.Width * detailImage.Width / detail.ActualWidth,
                            box.Height * detailImage.Height / detail.ActualHeight);
        }
        MouseButtonEventArgs Click(Grid target)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent, Source = target };
            target.RaiseEvent(args);
            return args;
        }

        Check(overlay.Children.Count == 3 && overlay.Children.OfType<Grid>().Select(target =>
                ((TextBlock)target.Children.OfType<Border>().Single().Child).Text).SequenceEqual(new[] { "1", "2", "3" }),
            "every detected bird has a numbered overlay while the largest first bird remains selected by default");
        Check(photo.SelectedBirdIndex == 0 && Near(BirdFraction(), .7) && selectedEvents == 0 && viewEvents == 0,
            "opening multiple detections initializes the first bird view without reporting a user edit or selection");
        Check(overlay.Children.OfType<Grid>().All(target => target.Children.OfType<System.Windows.Shapes.Rectangle>()
                .Single() is { StrokeDashArray.Count: 2 } rectangle && rectangle.Stroke is SolidColorBrush { Color.A: <= 140 }) &&
              locator.Visibility == Visibility.Visible && BoxesAndLabelsStayInsidePhoto(),
            "faint dashed detection boxes, tiny edge-bird numbers and white viewport are all clipped inside the actual photo");

        var secondClick = Click((Grid)overlay.Children[1]);
        Layout(viewer, 1200, 650);
        Check(secondClick.Handled && photo.SelectedBirdIndex == 1 && ReferenceEquals(photo.Bird, photo.Birds[1].Box) &&
              selectedEvents == 1 && Near(BirdFraction(), .7) && detailImage.Width / bitmap.PixelWidth <= 2 &&
              Canvas.GetLeft(detailImage) <= .00001 && Canvas.GetTop(detailImage) <= .00001,
            "clicking a numbered box selects that bird and restores its bounded 70% detail view");
        Check(((TextBlock)viewer.FindName("DetectionText")).Text.StartsWith("第2只鸟", StringComparison.Ordinal) &&
              ((SolidColorBrush)((Grid)overlay.Children[1]).Children.OfType<System.Windows.Shapes.Rectangle>().Single().Stroke).Color.A >
              ((SolidColorBrush)((Grid)overlay.Children[0]).Children.OfType<System.Windows.Shapes.Rectangle>().Single().Stroke).Color.A,
            "the selected number and detection footer track the active bird with a stronger outline");

        viewer.FitToFrame();
        viewer.SelectBird(1);
        Check(selectedEvents == 1 && Near(BirdFraction(), .7),
            "selecting the same bird restores its framing without emitting a duplicate selection event");
        viewer.SelectBird(-1); viewer.SelectBird(3);
        Check(photo.SelectedBirdIndex == 1 && selectedEvents == 1,
            "invalid bird indices cannot alter the active detection");
        var viewBeforeRefresh = (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY);
        int eventsBeforeRefresh = viewEvents;
        photo.Birds[1].Species = new SpeciesMatch("大山雀", "Parus major", .412);
        viewer.RefreshBirdOverlays();
        Layout(viewer, 1200, 650);
        Check(((Grid)overlay.Children[1]).ToolTip.ToString()!.Contains("大山雀", StringComparison.Ordinal) &&
              ((TextBlock)viewer.FindName("DetectionText")).Text == "第2只鸟 · 大山雀" &&
              viewBeforeRefresh == (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY) &&
              selectedEvents == 1 && viewEvents == eventsBeforeRefresh,
            "recognition refresh updates bird tooltip text without changing framing or emitting edits");
        Layout(viewer, 600, 400);
        Check(BoxesAndLabelsStayInsidePhoto() && selectedEvents == 1 && viewEvents == eventsBeforeRefresh,
            "overlay boxes and number chips remain correctly placed after narrow-window resizing");

        var retainedOverlay = overlay.Children[0];
        var retainedView = (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY);
        int beforeLoadingViewEvents = viewEvents;
        viewer.ShowLoading();
        viewer.SelectBird(0);
        viewer.RefreshBirdOverlays();
        Click((Grid)overlay.Children[0]);
        Check(ReferenceEquals(overlay.Children[0], retainedOverlay) && !overlay.IsHitTestVisible &&
              photo.SelectedBirdIndex == 1 && retainedView == (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY) &&
              selectedEvents == 1 && viewEvents == beforeLoadingViewEvents,
            "loading retains the exact bird overlays and blocks both pointer and API selection of the previous photo");
        viewer.ShowLoadError("下一张照片损坏");
        viewer.SelectBird(0);
        Check(ReferenceEquals(overlay.Children[0], retainedOverlay) && photo.SelectedBirdIndex == 1 && selectedEvents == 1,
            "load failure keeps previous detections visible and blocked");

        var legacy = new PhotoItem
        {
            FilePath = "legacy-single-bird.jpg",
            Bird = new BirdBox { X = .31, Y = .25, Width = .28, Height = .42, Confidence = .96 }
        };
        viewer.ShowPhoto(legacy, bitmap);
        Layout(viewer, 1200, 650);
        Check(overlay.IsHitTestVisible && overlay.Children.Count == 1 && selectedEvents == 1 &&
              ((TextBlock)((Grid)overlay.Children[0]).Children.OfType<Border>().Single().Child).Text == "1",
            "successful replacement restores interaction and legacy single-bird fixtures receive one numbered box");
        viewer.ShowPhoto(null);
        Check(overlay.Children.Count == 0 && overlay.Clip is null,
            "clearing the session removes every detected-bird overlay");
        return count;
    }

    private static bool Near(double left, double right) => Math.Abs(left - right) < .00001;

    private static int RunManualRegionChecks(BitmapSource bitmap)
    {
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK UI REGION " + description);
        }
        var viewer = new DualPhotoViewer();
        Layout(viewer, 1000, 600);
        var detail = (Border)viewer.FindName("DetailViewport");
        var image = (Image)viewer.FindName("DetailImage");
        var button = (System.Windows.Controls.Primitives.ToggleButton)viewer.FindName("ManualRegionButton");
        var draft = (System.Windows.Shapes.Rectangle)viewer.FindName("ManualRegionRectangle");
        var overlay = (Canvas)viewer.FindName("BirdOverlay");
        var photo = new PhotoItem
        {
            FilePath = "manual-region-pan-zoom.jpg", ViewZoom = 3, ViewCenterX = .65, ViewCenterY = .4
        };
        var proposals = new List<ManualRegionEventArgs>();
        var deletions = new List<BirdRegionDeleteEventArgs>();
        int viewEvents = 0;
        viewer.ManualRegionSelected += (_, args) => proposals.Add(args);
        viewer.DeleteRegionRequested += (_, args) => deletions.Add(args);
        viewer.ViewChanged += (_, _) => viewEvents++;
        viewer.ShowPhoto(photo, bitmap);
        Check(button.IsEnabled && !viewer.IsManualRegionSelectionActive && viewer.BeginManualRegionSelection() &&
              button.IsChecked == true && detail.Cursor == Cursors.Cross,
            "an explicit header toggle enables drawing while ordinary viewing starts outside selection mode");
        var start = new Point(80, 60); var end = new Point(300, 240);
        var expected = new BirdBox
        {
            X = (start.X - Canvas.GetLeft(image)) / image.Width,
            Y = (start.Y - Canvas.GetTop(image)) / image.Height,
            Width = (end.X - start.X) / image.Width, Height = (end.Y - start.Y) / image.Height
        };
        var viewBefore = (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY);
        Check(viewer.CompleteManualRegionSelection(start, end) && proposals.Count == 1 &&
              ReferenceEquals(proposals[0].Photo, photo) && proposals[0].Box.IsValid && proposals[0].Box.Confidence == 0 &&
              Near(proposals[0].Box.X, expected.X) && Near(proposals[0].Box.Y, expected.Y) &&
              Near(proposals[0].Box.Width, expected.Width) && Near(proposals[0].Box.Height, expected.Height),
            "detail coordinates map through current zoom and pan to a normalized full-image region without detector confidence");
        Check(!viewer.IsManualRegionSelectionActive && button.IsChecked == false && detail.Cursor == Cursors.Hand &&
              draft.Visibility == Visibility.Collapsed && photo.Birds.Count == 0 && viewEvents == 0 &&
              viewBefore == (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY),
            "commit returns to normal panning and emits a proposal without mutating the model or persisted view");
        Check(!viewer.CompleteManualRegionSelection(start, end) && proposals.Count == 1,
            "a drawing completion outside explicit selection mode cannot create an accidental region");

        viewer.BeginManualRegionSelection();
        Check(!viewer.CompleteManualRegionSelection(start, new Point(start.X + 4, start.Y + 4)) &&
              !viewer.CompleteManualRegionSelection(start, start) &&
              !viewer.CompleteManualRegionSelection(new Point(-20, -20), end) &&
              !viewer.CompleteManualRegionSelection(new Point(double.NaN, 0), end) && proposals.Count == 1,
            "tiny, empty, off-image-start and non-finite drags create no region");
        Check(!viewer.HandleSelectionKey(Key.A) && viewer.IsManualRegionSelectionActive &&
              viewer.HandleSelectionKey(Key.Escape) && !viewer.IsManualRegionSelectionActive &&
              !viewer.HandleSelectionKey(Key.Escape) && proposals.Count == 1,
            "the shared Escape handler cancels selection without consuming unrelated keys or emitting a proposal");
        viewer.BeginManualRegionSelection();
        Check(viewer.CompleteManualRegionSelection(end, start) && proposals.Count == 2 &&
              Near(proposals[^1].Box.X, expected.X) && Near(proposals[^1].Box.Y, expected.Y) &&
              Near(proposals[^1].Box.Width, expected.Width) && Near(proposals[^1].Box.Height, expected.Height),
            "reverse-direction drags preserve the same normalized rectangle");
        viewer.BeginManualRegionSelection();
        Check(viewer.CompleteManualRegionSelection(start, new Point(10000, 10000)) &&
              Near(proposals[^1].Box.X + proposals[^1].Box.Width,
                   (detail.ActualWidth - Canvas.GetLeft(image)) / image.Width) &&
              Near(proposals[^1].Box.Y + proposals[^1].Box.Height,
                   (detail.ActualHeight - Canvas.GetTop(image)) / image.Height) && proposals[^1].Box.IsValid,
            "a drag beyond the viewport is clipped to the actually visible part of the zoomed photo");

        viewer.BeginManualRegionSelection();
        double zoomBeforeManualWheel = photo.ViewZoom!.Value;
        int proposalsBeforeWheel = proposals.Count;
        var manualWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
        { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = detail };
        detail.RaiseEvent(manualWheel);
        Check(manualWheel.Handled && photo.ViewZoom > zoomBeforeManualWheel && viewer.IsManualRegionSelectionActive &&
              button.IsChecked == true && proposals.Count == proposalsBeforeWheel,
            "wheel zoom remains available while selection is armed without creating a region");
        viewer.HandleSelectionKey(Key.Escape);

        Layout(viewer, 1000, 900);
        viewer.FitToFrame();
        double imageTop = Canvas.GetTop(image);
        viewer.BeginManualRegionSelection();
        Check(imageTop > 20 && !viewer.CompleteManualRegionSelection(new Point(30, 10), new Point(300, 300)),
            "letterbox margins cannot become the start of a manual image region");
        Check(viewer.CompleteManualRegionSelection(new Point(30, imageTop + 20), new Point(10000, 10000)) &&
              Near(proposals[^1].Box.X + proposals[^1].Box.Width, 1) &&
              Near(proposals[^1].Box.Y + proposals[^1].Box.Height, 1),
            "fit-view selection clamps its far edges to original image bounds rather than black margins");
        Layout(viewer, 1000, 600);
        var tinyBitmap = BitmapSource.Create(10, 6, 96, 96, PixelFormats.Rgb24, null, new byte[180], 30);
        tinyBitmap.Freeze();
        viewer.ShowPhoto(new PhotoItem { FilePath = "manual-region-tiny.jpg" }, tinyBitmap);
        var tinyStart = new Point(Canvas.GetLeft(image) + 2, Canvas.GetTop(image) + 2);
        viewer.BeginManualRegionSelection();
        int beforeTiny = proposals.Count;
        Check(!viewer.CompleteManualRegionSelection(tinyStart, new Point(tinyStart.X + 7.9, tinyStart.Y + 8)) &&
              proposals.Count == beforeTiny &&
              viewer.CompleteManualRegionSelection(tinyStart, new Point(tinyStart.X + 8, tinyStart.Y + 8)) &&
              Near(proposals[^1].Box.Width * tinyBitmap.PixelWidth, 4) &&
              Near(proposals[^1].Box.Height * tinyBitmap.PixelHeight, 4),
            "the minimum accepts four source pixels at 200% and rejects smaller display drags");

        var automatic = new DetectedBird { Number = 5, Box = new BirdBox { X = .1, Y = .15, Width = .3, Height = .35, Confidence = .92 } };
        var manual = new DetectedBird { Number = 9, IsManual = true, Box = new BirdBox { X = .6, Y = .5, Width = .2, Height = .25 } };
        photo.SetBirds(new[] { automatic, manual });
        viewer.ShowPhoto(photo, bitmap);
        Layout(viewer, 1000, 600);
        var automaticTarget = (Grid)overlay.Children[0];
        var manualTarget = (Grid)overlay.Children[1];
        var automaticDelete = automaticTarget.ContextMenu!.Items.OfType<MenuItem>().Single();
        var manualDelete = manualTarget.ContextMenu!.Items.OfType<MenuItem>().Single();
        Check(ReferenceEquals(automaticTarget.Tag, automatic) && ReferenceEquals(manualTarget.Tag, manual) &&
              automaticDelete.Header?.ToString() == "删除选区" && manualDelete.Header?.ToString() == "删除选区" &&
              manualTarget.ToolTip.ToString()!.Contains("手动选区", StringComparison.Ordinal),
            "automatic and manual boxes both expose deletion and retain exact object identity and stable number labels");
        automaticDelete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(deletions.Count == 1 && ReferenceEquals(deletions[^1].Photo, photo) &&
              ReferenceEquals(deletions[^1].Bird, automatic) && photo.Birds.Count == 2,
            "automatic-box deletion is a request for the exact bird and leaves mutation to the parent");
        photo.RemoveBird(automatic);
        manualDelete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(deletions.Count == 2 && ReferenceEquals(deletions[^1].Bird, manual) &&
              manual.Number == 9 && photo.SelectedBird == manual,
            "an existing menu still targets the same manual object after a prior deletion shifts collection indices");
        automaticDelete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(deletions.Count == 2 && !viewer.RequestDeleteRegion(automatic),
            "a menu for an already removed object cannot delete its replacement at the old index");
        viewer.RefreshBirdOverlays();
        Check(((TextBlock)((Grid)overlay.Children[0]).Children.OfType<Border>().Single().Child).Text == "9" &&
              ((TextBlock)viewer.FindName("DetectionText")).Text.StartsWith("手动选区9", StringComparison.Ordinal) &&
              !((TextBlock)viewer.FindName("DetectionText")).Text.Contains('%'),
            "number gaps remain visible and an unclassified manual box never displays detector confidence");
        manual.Number = 10009;
        viewer.RefreshBirdOverlays();
        Layout(viewer, 1000, 600);
        var highNumberChip = ((Grid)overlay.Children[0]).Children.OfType<Border>().Single();
        var highNumberText = (TextBlock)highNumberChip.Child;
        Check(highNumberText.Text == "10009" && highNumberChip.ActualWidth >= highNumberText.ActualWidth + 4 &&
              highNumberChip.ActualWidth < 60,
            "stable highwater numbers expand their small chip enough to keep every digit readable");

        viewer.BeginManualRegionSelection();
        int beforeLoadingProposals = proposals.Count;
        viewer.ShowLoading();
        manualDelete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(!viewer.IsManualRegionSelectionActive && !button.IsEnabled && !viewer.BeginManualRegionSelection() &&
              !viewer.CompleteManualRegionSelection(start, end) && !viewer.RequestDeleteRegion(manual) &&
              proposals.Count == beforeLoadingProposals && deletions.Count == 2 && ReferenceEquals(viewer.CurrentImage, bitmap),
            "loading cancels drawing and blocks stale-region creation or deletion while retaining the previous frame");
        viewer.ShowLoadError("载入测试失败");
        Check(!viewer.BeginManualRegionSelection() && !viewer.RequestDeleteRegion(manual),
            "decode failure keeps region editing blocked until the next successful frame");
        var otherPhoto = new PhotoItem { FilePath = "another-photo.jpg" }; otherPhoto.SetBirds(new[] { manual });
        viewer.ShowPhoto(otherPhoto, bitmap);
        manualDelete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Check(deletions.Count == 2 && button.IsEnabled,
            "an old photo's context menu cannot delete even a shared object from a newly displayed photo");
        viewer.BeginManualRegionSelection(); viewer.IsEnabled = false;
        Check(!viewer.IsManualRegionSelectionActive && !viewer.BeginManualRegionSelection() && !viewer.RequestDeleteRegion(manual),
            "busy-state disabling cancels selection and blocks retained popup commands");
        viewer.IsEnabled = true;
        Layout(viewer, 600, 400);
        var buttonBounds = button.TransformToAncestor(viewer).TransformBounds(new Rect(button.RenderSize));
        var detailBounds = detail.TransformToAncestor(viewer).TransformBounds(new Rect(detail.RenderSize));
        Check(button.ActualWidth >= 60 && buttonBounds.Right <= viewer.ActualWidth && buttonBounds.Bottom <= detailBounds.Top,
            "manual selection fits in the narrow detail header without competing with footer actions");
        viewer.BeginManualRegionSelection(); viewer.ShowPhoto(null);
        Check(!viewer.IsManualRegionSelectionActive && !button.IsEnabled && !detail.IsMouseCaptured && draft.Visibility == Visibility.Collapsed,
            "clearing a session removes drawing mode, draft geometry and mouse capture");
        return count;
    }

    private static void Layout(FrameworkElement element, double width, double height)
    {
        element.Width = width;
        element.Height = height;
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static BitmapSource CreatePreviewBitmap()
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new LinearGradientBrush(Color.FromRgb(47, 72, 54), Color.FromRgb(111, 136, 100), 45),
                null, new Rect(0, 0, 1200, 800));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(32, 220, 235, 175)), null, new Point(950, 180), 150, 150);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(24, 220, 235, 175)), null, new Point(160, 600), 190, 130);
            drawing.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(76, 58, 40)), 27), new Point(160, 690), new Point(1050, 440));
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(24, 80, 91)), null,
                Geometry.Parse("M 470,465 L 370,610 L 470,565 L 540,455 Z"));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(59, 151, 164)), null, new Point(540, 385), 107, 135);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(141, 194, 172)), null, new Point(575, 403), 65, 100);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(37, 109, 130)), null, new Point(510, 382), 61, 100);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(59, 151, 164)), null, new Point(583, 272), 65, 65);
            drawing.DrawGeometry(new SolidColorBrush(Color.FromRgb(33, 44, 43)), null,
                Geometry.Parse("M 639,267 L 697,282 L 637,290 Z"));
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(20, 31, 28)), null, new Point(602, 259), 9, 9);
            drawing.DrawEllipse(Brushes.White, null, new Point(605, 256), 2.5, 2.5);
        }
        var bitmap = new RenderTargetBitmap(1200, 800, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
