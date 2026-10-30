using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

internal static class BatchProgressChecks
{
    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            assertions++;
            Console.WriteLine("  OK " + description);
        }

        string root = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.BatchProgress", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "first.jpg");
            string second = Path.Combine(root, "second.jpg");
            string invalid = Path.Combine(root, "invalid.jpg");
            string unsupported = Path.Combine(root, "unsupported.png");
            WriteJpeg(first);
            WriteJpeg(second);
            await File.WriteAllTextAsync(invalid, "invalid JPEG fixture");
            var importProgress = new List<PhotoOperationProgress>();
            var imported = await new PhotoImportService().ImportAsync(
                new[] { first, first, unsupported, invalid, second }, Array.Empty<string>(),
                progress: new InlineProgress(importProgress.Add));
            Check(imported.Added.Count == 2 && imported.SkippedDuplicate == 1 &&
                  imported.SkippedUnsupported == 1 && imported.Errors.Count == 1,
                "import progress preserves successful, duplicate, unsupported and failed input results");
            Check(importProgress.Count == 10 && importProgress.Where(p => p.FileCompleted)
                    .Select(p => p.Completed).SequenceEqual(Enumerable.Range(1, 5)),
                "import reports before and after every input including skipped and failed photos");
            Check(importProgress.All(p => p.Total == 5 && Path.IsPathFullyQualified(p.FilePath) &&
                      p.Message.Contains(p.FileName) && p.Message.Contains($"{p.CurrentIndex} / 5")) &&
                  importProgress.Last() is { Completed: 5, CurrentIndex: 5, FileCompleted: true },
                "import progress identifies the current full path, filename and input index");

            var exporter = new ExportService();
            string destination = Directory.CreateDirectory(Path.Combine(root, "export")).FullName;
            await File.WriteAllTextAsync(Path.Combine(destination, "first.jpg"), "collision sentinel");
            var missing = new PhotoItem { FilePath = Path.Combine(root, "missing.jpg") };
            var exportProgress = new List<PhotoOperationProgress>();
            var exported = await exporter.ExportAsync(new[] { imported.Added[0], missing, imported.Added[1] },
                destination, removeOriginal: false, progress: new InlineProgress(exportProgress.Add));
            Check(exported.Succeeded.Count == 1 && exported.Errors.Count == 1 && exported.Skipped.Count == 1,
                "export progress preserves collision, missing source and successful copy behavior");
            Check(exportProgress.Where(p => p.FileCompleted).Select(p => p.Completed).SequenceEqual(new[] { 1, 2, 3 }) &&
                  exportProgress.All(p => p.Total == 3 && p.Message.Contains(p.FileName)),
                "export advances progress for skipped and failed photos as well as copied photos");
            Check(exportProgress.Any(p => p.FilePath == second && p.Stage == "正在复制" && p.Completed == 2) &&
                  exportProgress.Any(p => p.FilePath == second && p.Stage == "正在校验" && p.Completed == 2) &&
                  exportProgress.Last() is { Completed: 3, FileCompleted: true } &&
                  (await File.ReadAllBytesAsync(Path.Combine(destination, "second.jpg"))).SequenceEqual(await File.ReadAllBytesAsync(second)),
                "export exposes copy and verification stages for the current file before reporting it complete");

            string canceledDestination = Directory.CreateDirectory(Path.Combine(root, "canceled-export")).FullName;
            using var cancelExport = new CancellationTokenSource();
            var canceledExportProgress = new List<PhotoOperationProgress>();
            var canceledExport = await exporter.ExportAsync(imported.Added, canceledDestination, removeOriginal: true,
                cancelExport.Token, new InlineProgress(p =>
                {
                    canceledExportProgress.Add(p);
                    if (p.Stage == "正在校验") cancelExport.Cancel();
                }));
            Check(canceledExport.Succeeded.Count == 0 && canceledExport.SourceRemoved.Count == 0 &&
                  File.Exists(first) && File.Exists(second) && !Directory.EnumerateFiles(canceledDestination).Any(),
                "cancellation during export verification preserves sources and removes the unfinished copy");
            Check(canceledExportProgress.Last() is { Completed: 0, FileCompleted: false, Stage: "已取消导出" } &&
                  canceledExportProgress.All(p => p.CurrentIndex == 1),
                "a canceled export neither claims the current file complete nor starts the next photo");

            var deleteProgress = new List<PhotoOperationProgress>();
            var deleted = await exporter.DeleteAsync(new[] { missing, new PhotoItem { FilePath = Path.Combine(root, "another-missing.jpg") } },
                progress: new InlineProgress(deleteProgress.Add));
            Check(deleted.Skipped.Count == 2 && deleteProgress.Count == 4 &&
                  deleteProgress.Where(p => p.FileCompleted).Select(p => p.Completed).SequenceEqual(new[] { 1, 2 }),
                "delete reports the current photo and completion even when the source no longer exists");

            using var cancelDelete = new CancellationTokenSource();
            var canceledDeleteProgress = new List<PhotoOperationProgress>();
            var canceledDelete = await exporter.DeleteAsync(imported.Added, cancelDelete.Token, new InlineProgress(p =>
            {
                canceledDeleteProgress.Add(p);
                if (p.Stage == "正在删除") cancelDelete.Cancel();
            }));
            Check(canceledDelete.SourceRemoved.Count == 0 && File.Exists(first) && File.Exists(second) &&
                  canceledDeleteProgress.Last() is { Completed: 0, FileCompleted: false, Stage: "已取消删除" },
                "canceling at the current-file delete progress leaves both generated sources untouched");

            using var cancelImport = new CancellationTokenSource();
            bool importCanceled = false;
            try
            {
                await new PhotoImportService().ImportAsync(new[] { first, second }, Array.Empty<string>(), cancelImport.Token,
                    new InlineProgress(_ => cancelImport.Cancel()));
            }
            catch (OperationCanceledException) { importCanceled = true; }
            Check(importCanceled && File.Exists(first) && File.Exists(second),
                "import progress retains the existing task cancellation contract without changing source photos");
            return assertions;
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class InlineProgress(Action<PhotoOperationProgress> report) : IProgress<PhotoOperationProgress>
    { public void Report(PhotoOperationProgress value) => report(value); }

    private static void WriteJpeg(string path)
    {
        const int width = 16, height = 12;
        var pixels = Enumerable.Repeat((byte)80, width * height * 3).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
