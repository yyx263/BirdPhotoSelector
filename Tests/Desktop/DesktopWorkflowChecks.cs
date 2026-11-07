using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BirdPhotoSelector;
using BirdPhotoSelector.Desktop.Controls;
using BirdPhotoSelector.Desktop.Dialogs;
using BirdPhotoSelector.Desktop.ViewModels;
using BirdPhotoSelector.Desktop.Views;
using BirdPhotoSelector.Imaging;
using BirdPhotoSelector.Imaging.Portable;

/// <summary>新宿主识别/编辑/导出链路检查，文件操作只针对私有生成目录，模型替身只存在于检查代码。</summary>
internal static class DesktopWorkflowChecks
{
    private static int _checks;
    private static readonly SpeciesIdentity Tit = new("大山雀", "Parus major"), Egret = new("白鹭", "Egretta garzetta");
    /// <summary>复用当前真实 Avalonia/Skia 会话，检查可观察交互和实际导出字节。</summary>
    public static async Task<int> Run(string root)
    {
        _checks = 0;
        var transform = new DetailViewTransform(4000, 2000); transform.SetViewport(800, 800); transform.Fit();
        Check(!transform.TryMapRegion(100, 20, 300, 400, out _), "manual region cannot begin in letterbox outside photo");
        Check(transform.TryMapRegion(600, 500, -100, 900, out var clipped) && clipped.X >= 0 && clipped.Y >= 0 && clipped.X + clipped.Width <= 1 && clipped.Y + clipped.Height <= 1, "reverse/outside manual drag clips to visible original pixels");
        Check(!transform.TryMapRegion(300, 300, 302, 302, out _) && !transform.TryMapRegion(double.NaN, 300, 400, 400, out _), "tiny and nonfinite manual selections never create regions");
        string dir = Path.Combine(root, "workflows"); Directory.CreateDirectory(dir);
        string first = Path.Combine(dir, "bird_1.jpg"), second = Path.Combine(dir, "bird_2.jpg");
        File.Copy(Path.Combine(root, "山雀-001.jpg"), first); File.Copy(first, second);
        var counters = new Models(); var files = new GeneratedFiles(dir);
        using var controller = new DesktopController(new(new PortablePhotoImageDecoder(), files, Path.Combine(dir, "recovery"), () => new Detector(counters), () => new Recognizer(counters)), 1);
        var window = new MainWindow(controller); window.Show();
        await controller.ImportAsync([first, second]); await controller.PreviewTask;
        var photo = controller.State.CurrentPhoto!; controller.State.SetRating(photo, PhotoRating.Keep); controller.State.SetRating(controller.State.AllPhotos.Last(), PhotoRating.Favorite);
        Click(window, "识别鸟种"); await controller.WhenIdleAsync(); await Flush();
        Check(controller.State.AllPhotos.All(p => p.Birds.Count == 2) && controller.State.Groups.Count == 2, "recognition button localizes all birds without grouping");
        Check(photo.SelectedBird!.Box.Width == .4 && photo.SelectedBird.Species?.CandidateScope == "China" && counters.ChinaOnly, "largest detected bird is selected and all recognition uses China candidates");
        Check(counters.Created == counters.Disposed, "recognition operation releases both model owners");
        Check(controller.ApplySpecies(photo, Egret) && photo.Birds.Last().ManualSpecies == Egret, "manual species has no region and sorts after numbered results");
        int infer = counters.Inferences; var manual = photo.Birds.Last(); await controller.RecognizeAsync();
        Check(photo.Birds.Contains(manual) && counters.Inferences - infer == 2, "batch identification skips whole manually edited photo");
        var region = photo.Birds.First(b => b.HasRegion); Check(controller.ApplySpecies(photo, Tit, region) && !photo.Birds.Contains(region) && photo.Birds.Count(b => b.HasRegion) == 1, "replacing automatic species removes its old region and creates manual result");
        Check(controller.UndoResults() && photo.Birds.Count(b => b.HasRegion) == 2, "replacement is undoable with original region restored");
        foreach (var bird in photo.Birds.ToArray()) controller.DeleteResult(photo, bird);
        Check(photo.Birds.Count == 0 && !photo.HasManualSpeciesEdits, "deleting last region/manual result removes batch protection");
        await controller.RecognizeAsync(); Check(photo.Birds.Count == 2 && photo.SelectedBird?.DisplayNumber == 2, "empty photo is localized again with continuous display numbers");
        var old = photo.Birds.ToArray(); counters.Fail = true; bool rebuilt = await controller.ReidentifyAsync(); counters.Fail = false;
        Check(!rebuilt && photo.Birds.SequenceEqual(old), "failed single-photo rebuild leaves original result objects intact");
        counters.Empty = true; rebuilt = await controller.ReidentifyAsync(); counters.Empty = false;
        Check(rebuilt && photo.Birds.Count == 0 && controller.State.SpeciesEdits.CanUndo, "successful zero-detection rebuild clears without confirmation and records undo");
        window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control); window.KeyReleaseQwerty(PhysicalKey.Z, RawInputModifiers.Control);
        Check(photo.Birds.Count == 2, "Control Z invokes actual result undo command");
        var detail = window.FindControl<PhotoPane>("DetailPane")!; detail.Fit(); await Flush();
        int before = photo.Birds.Count; Click(window, "手动选区");
        var start = detail.TranslatePoint(new Point(detail.Bounds.Width * .35, detail.Bounds.Height * .35), window)!.Value;
        var end = detail.TranslatePoint(new Point(detail.Bounds.Width * .65, detail.Bounds.Height * .65), window)!.Value;
        window.MouseDown(start, MouseButton.Left, RawInputModifiers.None); window.MouseMove(end, RawInputModifiers.LeftMouseButton); window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
        await controller.WhenIdleAsync(); await Flush();
        Check(photo.Birds.Count == before + 1 && photo.SelectedBird is { IsManual: true, Species.IsValid: true }, "real manual drag creates next numbered region and recognizes it");
        Check(!detail.IsManualMode && !window.FindControl<Avalonia.Controls.Primitives.ToggleButton>("ManualRegionButton")!.IsChecked.GetValueOrDefault(), "manual gesture resets tool button after submission");
        controller.UndoResults(); Check(photo.Birds.Count == before, "manual region and recognition undo together");
        counters.Block = true; var pending = controller.RecognizeRegionAsync(photo, new BirdBox { X = .3, Y = .3, Width = .2, Height = .2 }); controller.Cancel(); await pending; counters.Block = false;
        Check(photo.Birds.Count == before + 1 && photo.SelectedBird?.Species is null && controller.State.SpeciesEdits.CanUndo, "canceling manual inference retains reviewable region and undo checkpoint");
        controller.UndoResults();
        Click(window, "清空识别结果"); Check(photo.Birds.Count == 0, "clear-results button uses undoable reset"); controller.UndoResults();
        await Picker(window, controller, photo);
        await DialogDefaults(window, controller);
        await Export(window, controller, dir, files);
        string project = Path.Combine(dir, "edited.json"); await controller.SaveAsync(project); await controller.OpenAsync(project); await controller.PreviewTask;
        Check(controller.State.RecentSpecies.Items.Count > 0 && controller.State.AllPhotos.Any(p => p.Birds.Any(b => b.ManualSpecies is not null)), "project roundtrip retains manual species and recent choices");
        controller.State.Dirty = false; window.Close(); await Flush();
        if (Environment.GetEnvironmentVariable("BIRD_MODEL_PATH") is { Length: > 0 }) await RealModel(root);
        return _checks;
    }
    /// <summary>搜索窗口实际输入、近期列表和关闭取消不修改工程。</summary>
    private static async Task Picker(MainWindow window, DesktopController controller, PhotoItem photo)
    {
        Click(window, "＋ 手动添加鸟种"); var picker = await Owned<SpeciesPickerDialog>(window);
        var query = Named<TextBox>(picker, "SpeciesQuery"); query.Text = "bailu"; await Flush();
        var list = Named<ListBox>(picker, "SpeciesChoices"); Check(list.Items.OfType<SpeciesIdentity>().Any(s => s.DisplayName == "白鹭"), "species dialog searches pinyin without loading model");
        list.SelectedItem = list.Items.OfType<SpeciesIdentity>().First(s => s.DisplayName == "白鹭"); Click(picker, "添加鸟种"); await Flush();
        Check(photo.Birds.Any(b => b.ManualSpecies?.DisplayName == "白鹭") && controller.State.RecentSpecies.Items[0].DisplayName == "白鹭", "dialog confirmation adds manual species and updates recent history");
        Click(window, "＋ 手动添加鸟种"); picker = await Owned<SpeciesPickerDialog>(window); list = Named<ListBox>(picker, "SpeciesChoices");
        Check(list.Items.OfType<SpeciesIdentity>().First().DisplayName == "白鹭", "empty search shows recent choices automatically");
        query = Named<TextBox>(picker, "SpeciesQuery"); query.Text = "大山雀"; await Flush();
        Capture(picker, "species-search.png");
        var chinese = list.Items.OfType<SpeciesIdentity>().Select(s => s.Key).ToArray(); query.Text = "dashanque"; await Flush();
        Check(chinese.Length > 0 && list.Items.OfType<SpeciesIdentity>().Any(s => s.Key == chinese[0]) && query.Bounds.Height >= 46, "Chinese query has complete input line height");
        int before = photo.Birds.Count; picker.Close(); await Flush(); Check(photo.Birds.Count == before, "closing species dialog without confirmation leaves results unchanged");
        controller.ApplySpecies(photo, Tit);
    }
    /// <summary>导出复选框和长下拉框默认值、受保护冲突目标按钮与记住选项。</summary>
    private static async Task DialogDefaults(MainWindow window, DesktopController controller)
    {
        var dialog = new BirdPhotoSelector.Desktop.Dialogs.ExportDialog(controller.State.AllPhotos.ToArray()); var wait = dialog.ShowDialog<ExportOptions?>(window); await Flush();
        Check(Named<CheckBox>(dialog, "GroupBySpecies").IsChecked == false && Named<CheckBox>(dialog, "RemoveOriginal").IsChecked == false, "export classification and source removal are both off by default");
        Check(Named<ComboBox>(dialog, "ExportAction").Bounds.Width > 450, "export action dropdown provides enough width for full selected text");
        Capture(dialog, "export-options.png"); dialog.Close(); await wait;
        var conflict = new BirdPhotoSelector.Desktop.Dialogs.ExportConflictDialog(new("source.jpg", "protected.jpg", 1, 2, false, "受保护原件")); var task = conflict.ShowDialog<ExportConflictDecision?>(window); await Flush();
        Check(!Named<Button>(conflict, "ConflictOverwrite").IsEnabled && Named<CheckBox>(conflict, "RememberConflict").IsChecked == false, "protected originals cannot be overwritten and conflicts ask by default"); Capture(conflict, "export-conflict.png"); conflict.Close(); Check(await task is null, "closing conflict dialog does not grant permission");
        var remembered = new BirdPhotoSelector.Desktop.Dialogs.ExportConflictDialog(new("source.jpg", "target.jpg", 1, 2));
        var decision = remembered.ShowDialog<ExportConflictDecision?>(window); await Flush(); Named<CheckBox>(remembered, "RememberConflict").IsChecked = true; Click(remembered, "重命名");
        Check(await decision is { Action: ExportConflictAction.Rename, ApplyToRemaining: true }, "conflict remember checkbox returns only batch-scoped chosen operation");
    }
    /// <summary>真实目录、冲突窗口、分类提醒和副本字节；删除用替身验证只触及生成文件。</summary>
    private static async Task Export(MainWindow window, DesktopController controller, string dir, GeneratedFiles files)
    {
        var photo = controller.State.CurrentPhoto!;
        photo.AddManualRegion(new BirdBox { X = .1, Y = .1, Width = .1, Height = .1 });
        var review = SpeciesExportReview.Review(controller.State.AllPhotos);
        Check(review.Any(r => ReferenceEquals(r.Photo, photo) && r.Reasons.HasFlag(SpeciesExportReviewReason.UnidentifiedRegions)), "failed region triggers classification review even with other valid species");
        string grouped = Path.Combine(dir, "grouped"); Directory.CreateDirectory(grouped);
        var unmarked = controller.State.AllPhotos.Last(); controller.State.SetRating(unmarked, PhotoRating.Unmarked);
        Click(window, "导出 / 清理"); var checkOptions = await Owned<BirdPhotoSelector.Desktop.Dialogs.ExportDialog>(window);
        Named<TextBox>(checkOptions, "ExportDestination").Text = grouped; await Flush(); Click(checkOptions, "开始导出");
        var unmarkedPrompt = await Owned<PromptDialog>(window); Check(unmarkedPrompt.Title!.Contains("未标记"), "unmarked photos are checked before export even outside selected rating range");
        Click(unmarkedPrompt, "返回筛选"); await Flush(); await controller.PreviewTask;
        Check(ReferenceEquals(controller.State.CurrentPhoto, unmarked) && !Directory.EnumerateFileSystemEntries(grouped).Any(), "return from unmarked reminder locates photo and starts no file operation");
        controller.State.SetRating(unmarked, PhotoRating.Favorite); controller.State.LocatePhoto(photo); await controller.PreviewTask;
        Click(window, "导出 / 清理"); var options = await Owned<BirdPhotoSelector.Desktop.Dialogs.ExportDialog>(window); Named<TextBox>(options, "ExportDestination").Text = grouped; Named<CheckBox>(options, "GroupBySpecies").IsChecked = true; await Flush(); Click(options, "开始导出");
        var reminder = await Owned<PromptDialog>(window); Check(reminder.Title!.Contains("核对鸟种"), "classification export automatically opens review reminder"); Click(reminder, "继续导出"); await FileCompleted(controller, null); await Dismiss(window);
        Check(controller.LastFileResult is { FailedFileCount: 0 } groupedResult && groupedResult.VerifiedCopyCount >= 3 && Directory.GetDirectories(grouped).Length >= 2, "continuing review copies a multi-species photo to each species folder");
        Check(controller.State.AllPhotos.All(p => p.Assets.All(a => File.Exists(a.CanonicalPath))), "copy export retains all source originals by default");
        string flat = Path.Combine(dir, "flat"); Directory.CreateDirectory(flat);
        foreach (var p in controller.State.AllPhotos) await File.WriteAllTextAsync(Path.Combine(flat, p.FileName), "different target bytes");
        var previousResult = controller.LastFileResult;
        Click(window, "导出 / 清理"); options = await Owned<BirdPhotoSelector.Desktop.Dialogs.ExportDialog>(window); Named<TextBox>(options, "ExportDestination").Text = flat; await Flush(); Click(options, "开始导出");
        int asked = 0;
        while (asked < 2) { var conflict = await Owned<BirdPhotoSelector.Desktop.Dialogs.ExportConflictDialog>(window); asked++; Click(conflict, "重命名"); await Flush(); }
        await FileCompleted(controller, previousResult); await Dismiss(window);
        Check(asked == 2 && controller.LastFileResult?.VerifiedCopyCount == 2 && File.Exists(Path.Combine(flat, "bird_2.jpg")), "different collisions prompt for every file and rename export succeeds");
        var same = await controller.ExecuteExportAsync(controller.PrepareExport(new(ExportChoice.KeepAndFavorite, Destination: grouped, GroupBySpecies: true)), new(ExportChoice.KeepAndFavorite, Destination: grouped, GroupBySpecies: true));
        Check(same && controller.LastFileResult?.VerifiedCopyCount == 0 && controller.LastFileResult.SkippedFileCount > 0, "repeat identical-content export skips automatically without original removal");
        // Directly confirmed generated-file cleanup tests partial member application, not a user photo.
        string rejected = Path.Combine(dir, "generated-rejected.jpg"); File.Copy(controller.State.AllPhotos.First().FilePath, rejected); await controller.ImportAsync([rejected]); await controller.PreviewTask;
        var discard = controller.State.AllPhotos.Single(p => p.FilePath == rejected); controller.State.SetRating(discard, PhotoRating.Reject);
        var delete = new ExportOptions(ExportChoice.DeleteRejected); var plan = controller.PrepareExport(delete);
        Check(await controller.ExecuteExportAsync(plan, delete) && !File.Exists(rejected) && files.Removed == 1 && !controller.State.AllPhotos.Contains(discard), "successful generated-file deletion updates actual list members");
    }
    /// <summary>可用完整资源时，通过新宿主真实识别和真实 Avalonia 显示帧进行手动框识别。</summary>
    private static async Task RealModel(string root)
    {
        string dir = Path.Combine(root, "real-model"); Directory.CreateDirectory(dir); string path = Path.Combine(dir, "sample.jpg"); File.Copy(Path.Combine(root, "山雀-001.jpg"), path);
        using var controller = new DesktopController(); var window = new MainWindow(controller); window.Show(); await controller.ImportAsync([path]); await controller.PreviewTask;
        Check(await controller.RecognizeAsync() && controller.State.CurrentPhoto!.Birds.Any(b => b.Species is { IsValid: true }), "new desktop runs real YOLO and BioCLIP workflow on local JPEG");
        var photo = controller.State.CurrentPhoto!; var box = photo.SelectedBird!.Box.Copy();
        Check(await controller.RecognizeRegionAsync(photo, box) && photo.SelectedBird is { IsManual: true, Species.IsValid: true }, "real BioCLIP recognizes manual crop from Avalonia immutable display frame");
        controller.State.Dirty = false; window.Close(); await Flush();
    }
    /// <summary>等待真实异步弹窗，超时直接报错，不无限等待。</summary>
    private static async Task<T> Owned<T>(Window owner) where T : Window
    { for (int i = 0; i < 500; i++) { await Flush(); if (owner.OwnedWindows.OfType<T>().FirstOrDefault(w => w.IsVisible) is { } window) return window; await Task.Delay(10); } throw new TimeoutException("Missing dialog: " + typeof(T).Name + " · owned=" + string.Join(";", owner.OwnedWindows.Select(w => w.GetType().Name + ":" + w.Title + ":" + w.IsVisible)) + (owner is MainWindow main ? " · " + main.Controller.State.Status + " · errors=" + string.Join(";", main.Controller.LastFileResult?.Errors ?? []) : "")); }
    /// <summary>弹窗确认继续执行后，等待新批次真正完成，不能把上一次空闲状态误当成本次完成。</summary>
    private static async Task FileCompleted(DesktopController controller, FileOperationResult? previous)
    {
        for (int i = 0; i < 500; i++)
        { await Flush(); if (!controller.State.IsBusy && controller.LastFileResult is { } result && !ReferenceEquals(result, previous)) return; await Task.Delay(10); }
        throw new TimeoutException("File operation did not finish: " + controller.State.Status);
    }
    /// <summary>报告提示确认，不自动接受操作前的风险选择。</summary>
    private static async Task Dismiss(Window window) { if (window.OwnedWindows.OfType<PromptDialog>().FirstOrDefault(w => w.IsVisible) is { } message) { Click(message, "确定"); await Flush(); } }
    /// <summary>使用实际控件事件，避免测试重新实现窗口按钮逻辑。</summary>
    private static void Click(Window window, string text)
    { var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == text && b.IsVisible); if (!button.IsEnabled) throw new InvalidOperationException("Disabled button: " + text); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    /// <summary>代码构造窗口按真实视觉树查找命名控件。</summary>
    /// <summary>输出真实 Skia 绘制的对话框，便于核对中文输入行高和长选项文字。</summary>
    private static void Capture(Window window, string name)
    {
        string folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts/desktop-2.0-alpha7"); Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing rendered dialog frame.");
        frame.Save(Path.Combine(folder, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
    private static T Named<T>(Window window, string name) where T : Control => window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
    /// <summary>让绑定、显示与异步按钮继续执行。</summary>
    private static async Task Flush() { await Task.Delay(5); Dispatcher.UIThread.RunJobs(); }
    private static void Check(bool value, string text) { if (!value) throw new InvalidOperationException(text); _checks++; Console.WriteLine("  OK DESKTOP WORKFLOW " + text); }
    /// <summary>模型状态只存在于检查，生产组装没有故障委托。</summary>
    private sealed class Models { public int Created, Disposed, Inferences; public bool ChinaOnly = true, Empty, Fail, Block; }
    private sealed class Detector(Models state) : IBirdDetector
    {
        private readonly int _created = ++state.Created;
        public BirdDetectionResult Detect(ImageFrame image, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); if (state.Empty) return new(null, "未检出鸟"); var boxes = new[] { new BirdBox { X = .1, Y = .1, Width = .1, Height = .2 }, new BirdBox { X = .4, Y = .3, Width = .4, Height = .4 } }; return new(boxes[0], "已定位") { Birds = boxes }; }
        public void Dispose() { _ = _created; state.Disposed++; }
    }
    private sealed class Recognizer(Models state) : IBirdSpeciesRecognizer
    {
        private readonly int _created = ++state.Created;
        public SpeciesRecognitionResult? Prepare(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return null; }
        public SpeciesRecognitionResult Recognize(ImageFrame image, BirdBox box, bool chinaOnly = true, CancellationToken cancellationToken = default)
        { state.Inferences++; state.ChinaOnly &= chinaOnly; if (state.Block) cancellationToken.WaitHandle.WaitOne(); cancellationToken.ThrowIfCancellationRequested(); return state.Fail ? new(null, "生成的失败", Error: "generated failure") : new(new("大山雀", "Parus major", .7, Confidence: .96), "识别完成"); }
        public void Dispose() { _ = _created; state.Disposed++; }
    }
    private sealed class GeneratedFiles(string root) : IExportFilePlatform
    {
        private readonly SystemFileOperations _system = new(); public int Removed;
        public FileIdentity? TryGetIdentity(FileStream stream) => _system.TryGetIdentity(stream);
        public void RecycleOrDelete(string path) { if (!Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Outside generated fixtures"); File.Delete(path); Removed++; }
    }
}
