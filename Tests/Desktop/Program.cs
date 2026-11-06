using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.Controls.ApplicationLifetimes;
using BirdPhotoSelector.Desktop.Dialogs;
using Avalonia.VisualTree;
using BirdPhotoSelector;
using BirdPhotoSelector.Desktop;
using BirdPhotoSelector.Desktop.Controls;
using BirdPhotoSelector.Desktop.Imaging;
using BirdPhotoSelector.Desktop.ViewModels;
using BirdPhotoSelector.Desktop.Views;
using BirdPhotoSelector.Imaging;
using BirdPhotoSelector.Imaging.Portable;
using SkiaSharp;

/// <summary>实际 Avalonia 控件、Skia 帧和输入检查，不需要系统桌面，不读取或写入用户照片。</summary>
internal static class Program
{
    private static int _checks;
    /// <summary>HeadlessUnitTestSession 通过此入口初始化与真实窗口相同的主题和字体。</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    /// <summary>私有临时目录中的真实 JPEG/JSON 夹具；清理前再次校验绝对父目录和随机目录名。</summary>
    private static async Task<int> Main(string[] args)
    {
        if (args.Contains("--native-window"))
        {
            AppBuilder.Configure<NativeSmokeApp>().UsePlatformDetect().StartWithClassicDesktopLifetime([]);
            if (NativeSmokeApp.Error is { } error) { Console.Error.WriteLine("FAIL: " + error); return 1; }
            Console.WriteLine("PASS: native Avalonia window imports and previews a real JPEG, then closes."); return 0;
        }
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.Desktop"));
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Geometry();
            await using var session = HeadlessUnitTestSession.StartNew(typeof(Program));
            await session.Dispatch(async () => { await Desktop(root); _checks += await DesktopWorkflowChecks.Run(root); return 0; }, CancellationToken.None);
            Console.WriteLine($"PASS: {_checks} Avalonia desktop assertions on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != parent || !Guid.TryParseExact(Path.GetFileName(root), "N", out _)) throw new InvalidOperationException("Unsafe cleanup target.");
            Directory.Delete(root, true);
        }
    }
    /// <summary>使用独立几何预期验证倍率、鼠标锚点和边界，不复制实现公式作为金样。</summary>
    private static void Geometry()
    {
        var view = new DetailViewTransform(4000, 3000); view.SetViewport(800, 600); view.Fit();
        Check(Near(view.PixelScale, .2) && view.VisibleRegion == new ViewRectangle(0, 0, 1, 1), "fit view covers full image exactly");
        view.ZoomAt(1000, 400, 300); Check(Near(view.PixelScale, 2), "zoom cannot exceed actual 200 percent");
        view.PanBy(100000, -100000); var region = view.VisibleRegion;
        Check(region.X >= 0 && region.Y >= 0 && region.X + region.Width <= 1 && region.Y + region.Height <= 1, "pan keeps white locator inside full image");
        view.ZoomAt(.0001, 100, 100); Check(Near(view.PixelScale, .2), "zoom cannot shrink below fitting full image");
        view.Focus(.4, .4, .2, .2); Check(Near(view.PixelScale, .7) && Near(view.CenterX, .5) && Near(view.CenterY, .5), "whole bird occupies 70 percent along limiting dimension");
        view.Restore(2, .5, .5); view.ZoomAt(2, 500, 300); Check(Near(view.CenterX, .53125), "zoom preserves source point under off-center mouse");
        view.SetViewport(200, 800); region = view.VisibleRegion; Check(region.X >= 0 && region.X + region.Width <= 1 && region.Y >= 0 && region.Y + region.Height <= 1, "portrait viewport remains clamped after resize");
        var tiny = new DetailViewTransform(10, 10); tiny.SetViewport(800, 600); tiny.Fit(); Check(Near(tiny.PixelScale, 2) && tiny.CenterX == .5 && tiny.CenterY == .5, "tiny images prioritize 200 percent and stay centered");
        view.Restore(double.NaN, double.PositiveInfinity, double.NaN); Check(double.IsFinite(view.PixelScale) && double.IsFinite(view.CenterX + view.CenterY), "invalid project view values never become rendering NaN");
    }
    /// <summary>完整导入、真实界面输入、工程往返、取消和迟到预览检查。</summary>
    private static async Task Desktop(string root)
    {
        string first = Path.Combine(root, "山雀-001.jpg"), slow = Path.Combine(root, "slow.jpg"), third = Path.Combine(root, "山雀-003.jpg");
        string sample = Path.Combine(Directory.GetCurrentDirectory(), "artifacts/samples/great-tit.jpg");
        if (File.Exists(sample)) File.Copy(sample, first);
        else
        {
            using var image = new SKBitmap(1600, 1000); using var canvas = new SKCanvas(image); canvas.Clear(SKColors.DarkOliveGreen);
            using var paint = new SKPaint { Color = SKColors.Gold }; canvas.DrawCircle(800, 500, 230, paint);
            using var bitmap = SKImage.FromBitmap(image); using var data = bitmap.Encode(SKEncodedImageFormat.Jpeg, 95); using var stream = File.Create(first); data.SaveTo(stream);
        }
        File.Copy(first, slow); File.Copy(first, third);
        string ignored = Path.Combine(root, "skip.png"); await File.WriteAllTextAsync(ignored, "ignored format");
        var decoder = new ControlledDecoder(); var files = new SystemFileOperations();
        var services = new ApplicationServices(decoder, files, Path.Combine(root, "Autosaves"));
        using var controller = new DesktopController(services, 1);
        var window = new MainWindow(controller); window.Show();
        Check(!typeof(MainWindow).Assembly.GetReferencedAssemblies().Any(a => a.Name is "PresentationCore" or "PresentationFramework" or "WindowsBase" or "BirdPhotoSelector.Imaging.Windows"), "new desktop assembly has no WPF dependency");
        Check(await controller.ImportAsync([first, slow, third, ignored]) && controller.State.PhotoCount == 3, "actual JPEG import skips other formats");
        await controller.PreviewTask;
        Check(controller.State.CanRate && controller.Preview is not null, "decoded photo enables rating and gestures");
        var frame = controller.Preview!;
        var copied = frame.CopyRgb24(new BirdPhotoSelector.Imaging.PixelRect(0, 0, 1, 1));
        Check(copied.SequenceEqual(decoder.Inner.Decode(first).CopyRgb24(new BirdPhotoSelector.Imaging.PixelRect(0, 0, 1, 1))), "Avalonia display frame owns correct RGB pixels after temporary buffers are gone");
        Check(controller.CachedBytes <= controller.CacheBudget, "over-budget foreground remains visible without retention");
        var groupList = window.FindControl<ListBox>("GroupList")!; var photoList = window.FindControl<ListBox>("PhotoList")!;
        Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(photoList.SelectedItem, controller.State.CurrentPhoto), "group's displayed photo has selected thumbnail");
        window.KeyPressQwerty(PhysicalKey.Digit4, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.Digit4, RawInputModifiers.None);
        Check(controller.State.CurrentPhoto!.Rating == PhotoRating.Favorite && window.FindControl<ToggleButton>("FavoriteRating")!.IsChecked == true, "actual keyboard 4 rates favorite and latches colored button");
        window.KeyPressQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.ArrowRight, RawInputModifiers.None);
        await controller.PreviewTask; Dispatcher.UIThread.RunJobs();
        Check(controller.State.CurrentGroup == controller.State.Groups[1] && ReferenceEquals(photoList.SelectedItem, controller.State.CurrentPhoto), "direction key crosses group boundary and selects thumbnail");
        window.KeyPressQwerty(PhysicalKey.PageDown, RawInputModifiers.None); window.KeyReleaseQwerty(PhysicalKey.PageDown, RawInputModifiers.None);
        await controller.PreviewTask;
        Check(controller.State.CurrentGroup == controller.State.Groups[2], "Page Down retains independent group navigation");
        controller.State.NavigateGroup(-2); await controller.PreviewTask; var old = controller.Preview;
        decoder.Block = true; controller.State.NavigateGroup(1);
        Check(!controller.State.IsPreviewReady && ReferenceEquals(controller.Preview, old), "loading next photo keeps previous frame and blocks old-photo actions");
        controller.State.NavigateGroup(1); decoder.Release.Set(); await controller.PreviewTask;
        Check(controller.State.CurrentPhoto?.FilePath == third && ReferenceEquals(controller.PreviewPhoto, controller.State.CurrentPhoto), "late slow decode cannot replace newer selected photo");
        decoder.Block = false;
        Dispatcher.UIThread.RunJobs();
        var detail = window.FindControl<PhotoPane>("DetailPane")!; var full = window.FindControl<PhotoPane>("FullPane")!;
        var detailPoint = detail.TranslatePoint(new Point(detail.Bounds.Width / 2, detail.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(detailPoint, new Vector(0, 4), RawInputModifiers.None);
        Check(controller.State.CurrentPhoto!.ViewZoom > 1 && detail.VisibleRegion.Width < 1, "real pointer wheel zooms detail and updates white locator");
        var zoom = controller.State.CurrentPhoto.ViewZoom;
        var fullPoint = full.TranslatePoint(new Point(full.Bounds.Width / 2, full.Bounds.Height / 2), window)!.Value;
        window.MouseWheel(fullPoint, new Vector(0, -1), RawInputModifiers.None);
        Check(controller.State.CurrentPhoto.ViewZoom == zoom, "wheel outside detail never changes photo or detail scale");
        window.MouseDown(detailPoint, MouseButton.Left, RawInputModifiers.None);
        window.MouseMove(detailPoint + new Vector(-10000, -10000), RawInputModifiers.LeftMouseButton);
        window.MouseUp(detailPoint + new Vector(-10000, -10000), MouseButton.Left, RawInputModifiers.None);
        Check(detail.VisibleRegion.X >= 0 && detail.VisibleRegion.Y >= 0 && detail.VisibleRegion.X + detail.VisibleRegion.Width <= 1 && detail.VisibleRegion.Y + detail.VisibleRegion.Height <= 1,
            "captured real drag outside window cannot move locator beyond photo");
        detail.Fit();
        var extra = Enumerable.Range(0, 15).Select(i => Path.Combine(root, $"scroll-{i:00}.jpg")).ToArray();
        foreach (var path in extra) File.Copy(first, path);
        await controller.ImportAsync(extra); await controller.PreviewTask; Dispatcher.UIThread.RunJobs();
        var selectedBeforeWheel = controller.State.CurrentPhoto;
        var groupPoint = groupList.TranslatePoint(new Point(50, 240), window)!.Value;
        int firstVisible = FirstVisibleGroup(groupList);
        window.MouseWheel(groupPoint, new Vector(0, -1), RawInputModifiers.None);
        Check(FirstVisibleGroup(groupList) == firstVisible + 2, "one wheel tick moves exactly two photo-group thumbnails");
        Check(ReferenceEquals(selectedBeforeWheel, controller.State.CurrentPhoto) && groupList.GetVisualDescendants().OfType<ScrollViewer>().First().Offset.Y > 0,
            "photo-group wheel scrolls without changing selected photo");
        controller.State.CurrentRating = PhotoRating.Keep;
        await controller.GroupAsync(); await controller.PreviewTask;
        Check(controller.State.PhotoCount == 18 && controller.State.AllPhotos.Count(p => p.Rating == PhotoRating.Favorite) == 1, "background grouping preserves photos and ratings");
        Dispatcher.UIThread.RunJobs();
        var photoPoint = photoList.TranslatePoint(new Point(45, 220), window)!.Value;
        var photoScroll = photoList.GetVisualDescendants().OfType<ScrollViewer>().First();
        double scrollBefore = photoScroll.Offset.Y;
        window.MouseWheel(photoPoint, new Vector(0, -1), RawInputModifiers.None);
        Check(ReferenceEquals(selectedBeforeWheel, controller.State.CurrentPhoto) && photoScroll.Offset.Y > scrollBefore,
            "within-group wheel scrolls thumbnails without selecting another photo");
        var current = controller.State.CurrentPhoto!;
        current.SetBirds(Enumerable.Range(1, 4).Select(i => new DetectedBird { Number = i, Box = new BirdBox { X = .4, Y = .3, Width = .3, Height = .4 }, RecognitionStatus = "待识别" }));
        string project = Path.Combine(root, "挑选.json"); Check(await controller.SaveAsync(project), "desktop saves same JSON project format");
        controller.State.CurrentRating = PhotoRating.Reject; await controller.OpenAsync(project); await controller.PreviewTask;
        Check(controller.State.AllPhotos.Any(p => p.Birds.Count == 4) && !controller.State.Dirty, "project reload retains existing bird records without marking edits");
        controller.State.LocatePhoto(controller.State.AllPhotos.First(p => p.Birds.Count == 4)); await controller.PreviewTask; Dispatcher.UIThread.RunJobs();
        var birds = window.FindControl<ListBox>("BirdList")!;
        Check(birds.Items.Count == 4, "new window displays all four persisted bird results");
        string visuals = Path.Combine(Directory.GetCurrentDirectory(), "artifacts/desktop-2.0-alpha5"); Directory.CreateDirectory(visuals);
        foreach (var size in new[] { new Size(1560, 920), new Size(1150, 780) })
        {
            window.Width = size.Width; window.Height = size.Height; Dispatcher.UIThread.RunJobs();
            using var screenshot = window.CaptureRenderedFrame(); Check(screenshot is not null, "real Skia rendering at window width " + size.Width);
            screenshot!.Save(Path.Combine(visuals, size.Width > 1200 ? "main-normal.png" : "main-minimum.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            var rows = birds.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            Check(rows.Length >= 4 && rows.All(row => row.Bounds.Height > 0 && row.TranslatePoint(new Point(0, row.Bounds.Height), birds) is { } bottom && bottom.Y <= birds.Bounds.Height + .1), "four bird rows remain realized at window width " + size.Width);
        }
        await controller.ClearAsync(); Check(controller.State.PhotoCount == 0 && File.Exists(first) && File.Exists(slow) && File.Exists(third), "clear-list command preserves every original JPEG");
        window.Close(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
        var prompt = window.OwnedWindows.OfType<PromptDialog>().Single();
        Check(window.IsVisible && prompt.IsVisible, "unsaved-window close displays deferred save confirmation");
        prompt.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "返回").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(20); Dispatcher.UIThread.RunJobs();
        Check(window.IsVisible && controller.State.Dirty, "canceling close retains unsaved session");
        window.Close(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
        prompt = window.OwnedWindows.OfType<PromptDialog>().Single();
        prompt.GetVisualDescendants().OfType<Button>().Single(b => b.Content?.ToString() == "不保存，继续").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(20); Dispatcher.UIThread.RunJobs();
        Check(!window.IsVisible, "explicit discard finishes asynchronous close without closing-time dialog exception");
    }
    /// <summary>只阻塞全分辨率指定夹具，缩略图/元数据仍调用真实后端。</summary>
    private sealed class ControlledDecoder : IPhotoImageDecoder
    {
        public PortablePhotoImageDecoder Inner { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public bool Block;
        public ImageFrame Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
        { if (Block && maxWidth == 0 && Path.GetFileName(path) == "slow.jpg") Release.Wait(cancellationToken); return Inner.Decode(path, maxWidth, cancellationToken); }
        public PhotoImageInfo ReadInfo(string path, CancellationToken cancellationToken = default) => Inner.ReadInfo(path, cancellationToken);
    }
    /// <summary>通过实际容器位置确定首张完整可见缩略图，不假定滚动器的单位是像素或条目。</summary>
    private static int FirstVisibleGroup(ListBox list) => list.GetVisualDescendants().OfType<ListBoxItem>()
        .Where(item => item.DataContext is GroupCard && item.TranslatePoint(new Point(0, 0), list) is { Y: >= 0 } point && point.Y < list.Bounds.Height)
        .Select(item => ((GroupCard)item.DataContext!).Number).Min();
    /// <summary>数值允许小量浮点误差。</summary>
    private static bool Near(double first, double second) => Math.Abs(first - second) < 1e-9;
    /// <summary>记录用户可观察结果，失败立即终止。</summary>
    private static void Check(bool result, string description) { if (!result) throw new InvalidOperationException(description); _checks++; Console.WriteLine("  OK DESKTOP " + description); }
}

/// <summary>可选真实系统窗口启动检查，最小化运行，验证平台库、JPEG 显示和关闭生命周期。</summary>
public sealed class NativeSmokeApp : Application
{
    public static Exception? Error { get; private set; }
    public override void Initialize() { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; Styles.Add(new Avalonia.Themes.Fluent.FluentTheme()); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var controller = new DesktopController();
            var window = new MainWindow(controller) { WindowState = WindowState.Minimized };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try
                {
                    string sample = Path.Combine(Directory.GetCurrentDirectory(), "artifacts/samples/great-tit.jpg");
                    if (!await controller.ImportAsync([sample])) throw new InvalidOperationException("Native import canceled");
                    await controller.PreviewTask;
                    if (!controller.State.CanRate) throw new InvalidOperationException(controller.State.Status);
                }
                catch (Exception ex) { Error = ex; }
                finally { controller.State.Dirty = false; window.Close(); }
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
