using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BirdPhotoSelector;
using BirdPhotoSelector.Desktop.Dialogs;
using BirdPhotoSelector.Desktop.ViewModels;
using BirdPhotoSelector.Desktop.Views;
using BirdPhotoSelector.Imaging;
using BirdPhotoSelector.Imaging.Portable;

/// <summary>导出全部和删 JPG 留 RAW 的实际文件/窗口回归；仅删除 GUID 测试根中的自建副本。</summary>
internal static class ExportAllAndRawChecks
{
    private static int _checks;
    /// <summary>四级评级、真实下拉选择、复制后换源，以及可选真实 LibRaw 解码。</summary>
    public static async Task<int> Run(string root)
    {
        _checks = 0;
        string dir = Path.Combine(root, "export-all-raw"); Directory.CreateDirectory(dir);
        var images = new FormatImages(); var files = new GeneratedFiles(dir);
        using var controller = new DesktopController(new(images, files, Path.Combine(dir, "recovery")));
        var window = new MainWindow(controller); window.Show();
        var inputs = new List<string>();
        foreach (var rating in Enum.GetValues<PhotoRating>())
        {
            string stem = Path.Combine(dir, rating.ToString());
            await File.WriteAllTextAsync(stem + ".jpg", "generated JPG " + rating);
            await File.WriteAllTextAsync(stem + ".CR2", "generated RAW " + rating);
            inputs.Add(stem + ".jpg");
        }
        await controller.ImportAsync(inputs); await controller.PreviewTask;
        var photos = controller.State.AllPhotos.ToArray();
        for (int i = 0; i < photos.Length; i++) controller.State.SetRating(photos[i], (PhotoRating)i);
        var species = new SpeciesIdentity("大山雀", "Parus major");
        foreach (var photo in photos)
        {
            controller.State.LocatePhoto(photo); await controller.PreviewTask;
            if (!controller.ApplySpecies(photo, species)) throw new InvalidOperationException("Generated species edit was not applied.");
        }
        controller.State.LocatePhoto(photos[0]); await controller.PreviewTask;
        string output = Path.Combine(dir, "all"); Directory.CreateDirectory(output);
        var dialog = new ExportDialog(photos); var response = dialog.ShowDialog<ExportOptions?>(window);
        await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        var action = dialog.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "ExportAction");
        action.SelectedItem = action.Items.Cast<string>().Single(value => value == "导出全部");
        dialog.GetVisualDescendants().OfType<TextBox>().Single(c => c.Name == "ExportDestination").Text = output;
        await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        var confirm = dialog.GetVisualDescendants().OfType<Button>().Single(c => c.Name == "ConfirmExport");
        if (!confirm.IsEnabled) throw new InvalidOperationException("Export-all action should be enabled for an existing directory.");
        confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var options = await response.WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new InvalidOperationException("Missing export options.");
        Check(options is { Choice: ExportChoice.All, RemoveOriginal: false, GroupBySpecies: false, Scope: PhotoFileScope.All }, "export-all dialog returns all-rating copy mode with safe defaults");
        var plan = controller.PrepareExport(options);
        Check(plan.PhotoCount == 4 && plan.FileCount == 8, "all export includes all four ratings and both paired originals");
        Check(await controller.ExecuteExportAsync(plan, options) && controller.LastFileResult is { VerifiedCopyCount: 8, RemovedFileCount: 0 }, "all export verifies eight copies and preserves original files");
        Check(photos.SelectMany(p => p.Assets).All(a => File.Exists(a.CanonicalPath) && File.ReadAllBytes(a.CanonicalPath).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(output, Path.GetFileName(a.CanonicalPath))))), "exported JPG and RAW bytes match every source");
        var onlyRaw = controller.PrepareExport(new(ExportChoice.All, PhotoFileScope.RawOnly));
        Check(onlyRaw.PhotoCount == 4 && onlyRaw.FileCount == 4 && onlyRaw.Photos.All(p => p.Assets.All(a => a.Kind == PhotoAssetKind.Raw)), "all export continues honoring RAW-only file scope");
        var grouped = controller.PrepareExport(new(ExportChoice.All, GroupBySpecies: true));
        Check(grouped.PhotoCount == 4 && grouped.Photos.All(p => p.TargetFolders.Count == 1 && p.TargetFolders[0].Contains("大山雀")), "all export continues honoring species folders");
        var thumbnails = photos.Select(p => p.Thumbnail).ToArray();
        string jpgOutput = Path.Combine(dir, "jpg-only"); Directory.CreateDirectory(jpgOutput);
        var jpg = new ExportOptions(ExportChoice.All, PhotoFileScope.JpgOnly, jpgOutput, RemoveOriginal: true);
        Check(await controller.ExecuteExportAsync(controller.PrepareExport(jpg), jpg) && controller.LastFileResult is { RemovedFileCount: 4, VerifiedCopyCount: 4 }, "JPG-only move applies all successful removals while RAW files survive");
        await controller.PreviewTask;
        Check(photos.All(p => p.Assets.Count == 1 && p.ActivePreviewAsset?.Kind == PhotoAssetKind.Raw && File.Exists(p.FilePath)) && controller.State.PhotoCount == 4, "removing JPG keeps all four RAW logical photos in the list");
        Check(photos.Select((p, i) => p.Thumbnail is not null && !ReferenceEquals(p.Thumbnail, thumbnails[i]) && p.Thumbnail.CopyRgb24()[0] == 200 && p.Metadata.Camera == "RAW Camera").All(value => value), "RAW thumbnails and metadata regenerate for selected and unselected photos");
        Check(photos.Select((p, i) => p.Rating == (PhotoRating)i && p.Birds.Single().ManualSpecies == species).All(value => value), "source changes preserve all ratings and manual species");
        controller.State.Dirty = false; window.Close();
        await PartialCancellation(root);
        await RealRaw(root);
        return _checks;
    }
    /// <summary>导出取消不能丢掉已执行的文件结果；恢复缩略图不能继续使用已取消的导出令牌。</summary>
    private static async Task PartialCancellation(string root)
    {
        string dir = Path.Combine(root, "cancel-raw"); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "cancel.jpg"); await File.WriteAllTextAsync(path, "jpg"); await File.WriteAllTextAsync(Path.ChangeExtension(path, ".CR2"), "raw");
        var files = new GeneratedFiles(dir);
        using var controller = new DesktopController(new(new FormatImages(), files, Path.Combine(dir, "recovery")));
        await controller.ImportAsync([path]); await controller.PreviewTask;
        var photo = controller.State.CurrentPhoto!; controller.State.SetRating(photo, PhotoRating.Reject);
        files.AfterRemoval = controller.Cancel;
        var options = new ExportOptions(ExportChoice.DeleteRejected, PhotoFileScope.JpgOnly);
        await controller.ExecuteExportAsync(controller.PrepareExport(options), options); await controller.PreviewTask;
        Check(controller.LastFileResult?.RemovedFileCount == 1 && photo.ActivePreviewAsset?.Kind == PhotoAssetKind.Raw && photo.Thumbnail?.CopyRgb24()[0] == 200, "cancel after successful JPG removal still restores the remaining RAW thumbnail");
    }
    /// <summary>使用本地 RAW 夹具的私有复制品，确认恢复内容来自 LibRaw，且从不修改原样本。</summary>
    private static async Task RealRaw(string root)
    {
        string? samples = Environment.GetEnvironmentVariable("BIRD_RAW_SAMPLES");
        string? original = samples is null ? null : Path.Combine(samples, "Canon_EOS_450D_RAW.CR2");
        if (original is null || !File.Exists(original)) { Console.WriteLine("SKIP: RAW thumbnail desktop fixture unavailable."); return; }
        byte[] hash = SHA256.HashData(File.ReadAllBytes(original));
        string dir = Path.Combine(root, "real-raw-thumbnail"); Directory.CreateDirectory(dir);
        string jpg = Path.Combine(dir, "paired.jpg"), raw = Path.ChangeExtension(jpg, ".CR2");
        File.Copy(Path.Combine(root, "山雀-001.jpg"), jpg); File.Copy(original, raw);
        using var controller = new DesktopController(new(new PortablePhotoImageDecoder(), new GeneratedFiles(dir), Path.Combine(dir, "recovery")));
        await controller.ImportAsync([jpg]); await controller.PreviewTask;
        var photo = controller.State.CurrentPhoto!; controller.State.SetRating(photo, PhotoRating.Reject);
        var options = new ExportOptions(ExportChoice.DeleteRejected, PhotoFileScope.JpgOnly);
        await controller.ExecuteExportAsync(controller.PrepareExport(options), options); await controller.PreviewTask;
        Check(photo.ActivePreviewAsset?.Kind == PhotoAssetKind.Raw && photo.Thumbnail is { PixelWidth: 160 } && photo.Thumbnail.CopyRgb24().Distinct().Count() > 16 && photo.Metadata.Camera.Contains("450D"), "actual RAW decoder restores a sensor-derived thumbnail after JPG deletion");
        Check(hash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(original))) && hash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(raw))), "RAW recovery leaves fixture and remaining original bytes unchanged");
    }
    /// <summary>测试图像用两种不同像素和相机标签识别来源，避免误复用 JPG 缩略图也通过检查。</summary>
    private sealed class FormatImages : IPhotoImageDecoder
    {
        public ImageFrame Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); byte red = PhotoAsset.TryGetKind(path, out var kind) && kind == PhotoAssetKind.Raw ? (byte)200 : (byte)10; return new Rgb24ImageFrame(8, 4, Enumerable.Repeat(red, 8 * 4 * 3).ToArray()); }
        public PhotoImageInfo ReadInfo(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new(new() { Camera = PhotoAsset.TryGetKind(path, out var kind) && kind == PhotoAssetKind.Raw ? "RAW Camera" : "JPG Camera" }, 8, 4, 1); }
    }
    /// <summary>只对本次生成目录中的副本执行删除，身份读取仍复用生产平台。</summary>
    private sealed class GeneratedFiles(string root) : IExportFilePlatform
    {
        private readonly SystemFileOperations _system = new();
        public Action? AfterRemoval;
        public FileIdentity? TryGetIdentity(FileStream stream) => _system.TryGetIdentity(stream);
        public void RecycleOrDelete(string path)
        {
            string relative = Path.GetRelativePath(root, Path.GetFullPath(path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) throw new InvalidOperationException("Outside generated fixtures.");
            File.Delete(path); AfterRemoval?.Invoke();
        }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); _checks++; Console.WriteLine("  OK EXPORT/RAW " + message); }
}
