using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BirdPhotoSelector;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            // The real Application constructor queues its startup callback. These tests own the
            // dispatcher and may pump it for Close regressions, so prevent its default GUI launch.
            var startupOperations = new List<DispatcherOperation>();
            var dispatcher = Dispatcher.CurrentDispatcher;
            void CaptureStartup(object? sender, DispatcherHookEventArgs e) => startupOperations.Add(e.Operation);
            dispatcher.Hooks.OperationPosted += CaptureStartup;
            App app;
            try { app = new App(); }
            finally { dispatcher.Hooks.OperationPosted -= CaptureStartup; }
            foreach (var operation in startupOperations) operation.Abort();
            app.InitializeComponent();
            app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
            if (args.Contains("--file-operations-only", StringComparer.Ordinal))
            {
                _assertions += BatchProgressChecks.RunAsync().GetAwaiter().GetResult();
                _assertions += AssetTransitionGuardChecks.RunAsync().GetAwaiter().GetResult();
                _assertions += RawFileOperationChecks.RunAsync().GetAwaiter().GetResult();
                _assertions += ExportScopeUiChecks.Run();
                Console.WriteLine($"PASS: {_assertions} targeted file-operation assertions.");
                return 0;
            }
            _assertions += AutoSaveChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += PhotoPreviewCacheChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += SpeciesRecognitionChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += GroupingThresholdChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += RecognitionWorkflowChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += BatchProgressChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += PhotoAssetChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += PhotoProjectV2Checks.RunAsync().GetAwaiter().GetResult();
            _assertions += AssetTransitionGuardChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += RawFileOperationChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += SinglePhotoRecognitionChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += RawImportChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += RawPreviewChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += RawNativeChecks.RunAsync().GetAwaiter().GetResult();
            _assertions += RawWindowChecks.Run();
            _assertions += ExportScopeUiChecks.Run();
            _assertions += SinglePhotoWindowChecks.Run();
            _assertions += ViewerUiChecks.Run();
            _assertions += MainWindowUiChecks.Run();
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {_assertions} UI and service assertions.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: " + ex);
            return 1;
        }
    }

    private static async Task RunAsync()
    {
        _assertions += await MultiBirdChecks.RunAsync();
        // This suite only operates below its own random temporary directory.
        string testRoot = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SmokeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            string a = Directory.CreateDirectory(Path.Combine(testRoot, "A")).FullName;
            string b = Directory.CreateDirectory(Path.Combine(testRoot, "B")).FullName;
            string destination = Directory.CreateDirectory(Path.Combine(testRoot, "Export")).FullName;
            string first = Path.Combine(a, "BIRD.JPG");
            string duplicate = Path.Combine(b, "bird.jpg");
            string second = Path.Combine(b, "other.jpeg");
            string unsupported = Path.Combine(a, "unsupported.png");
            string invalid = Path.Combine(a, "invalid.jpg");
            WriteJpeg(first, 64, 40, 20);
            WriteJpeg(duplicate, 64, 40, 100);
            WriteJpeg(second, 72, 48, 160);
            File.Copy(first, unsupported);
            await File.WriteAllTextAsync(invalid, "This is not a JPEG.");

            var importer = new PhotoImportService();
            var imported = await importer.ImportAsync(new[] { first, duplicate, second, unsupported, invalid }, Array.Empty<string>());
            Check(imported.Added.Count == 2, "only valid unique JPEG photos import");
            Check(imported.SkippedDuplicate == 1, "filename deduplication ignores directory and case");
            Check(imported.SkippedUnsupported == 1, "unsupported extension skips automatically");
            Check(imported.Errors.Count == 1, "JPEG extension with invalid magic is rejected");
            Check(imported.Added[0].FilePath == first && imported.Added[1].FilePath == second, "selection order is retained");
            Check(imported.Added[0].Thumbnail?.IsFrozen == true, "thumbnail is safe for cross-thread UI use");
            Check(imported.Added[0].PixelWidth == 64 && imported.Added[0].PixelHeight == 40, "original photo dimensions retained");
            var existing = await importer.ImportAsync(new[] { duplicate }, new[] { "BIRD.JPG" });
            Check(existing.Added.Count == 0 && existing.SkippedDuplicate == 1, "dedupe includes previously imported filenames");

            TestExifOrientation(a);

            PhotoItem p1 = imported.Added[0], p2 = imported.Added[1];
            p1.Rating = PhotoRating.Favorite;
            p1.Bird = new BirdBox { X = 0.2, Y = 0.1, Width = 0.4, Height = 0.5, Confidence = 0.95 };
            p1.DetectionStatus = "已识别鸟类";
            p1.ViewZoom = 3.25;
            p1.ViewCenterX = 0.4;
            p1.ViewCenterY = 0.35;
            p2.Rating = PhotoRating.Keep;
            TestBackgroundSimilarity();
            await TestAnalysisAsync(testRoot, p1, p2);
            var group = new PhotoGroup("测试分组");
            group.Photos.Add(p1); group.Photos.Add(p2);
            var projects = new ProjectService();
            string projectPath = Path.Combine(testRoot, "roundtrip.json");
            await projects.SaveAsync(projectPath, new[] { group }, 0.73);
            var loaded = await projects.LoadAsync(projectPath);
            Check(loaded.Groups.Count == 1 && loaded.Groups[0].Count == 2, "project retains group membership");
            Check(loaded.Groups[0].Id == group.Id && loaded.Groups[0].Label == "测试分组", "project retains group identity and label");
            Check(loaded.GroupingThreshold == 0.73, "project retains threshold");
            PhotoItem restored = loaded.Groups[0].Photos[0];
            Check(restored.Id == p1.Id && restored.FilePath == p1.FilePath, "project retains photo identity and path");
            Check(restored.Rating == PhotoRating.Favorite && loaded.Groups[0].Photos[1].Rating == PhotoRating.Keep,
                "project retains ratings");
            Check(restored.Bird is { X: 0.2, Y: 0.1, Width: 0.4, Height: 0.5, Confidence: 0.95 },
                "project retains normalized detection box");
            Check(restored.ViewZoom == 3.25 && restored.ViewCenterX == 0.4 && restored.ViewCenterY == 0.35,
                "project retains manual viewport");
            Check(restored.Thumbnail is not null && restored.Metadata is not null, "project reloads thumbnail and metadata");
            string unsupportedProjectPath = Path.Combine(testRoot, "unsupported.bphoto");
            File.Copy(projectPath, unsupportedProjectPath);
            await ExpectThrowsAsync<InvalidDataException>(() => projects.LoadAsync(unsupportedProjectPath),
                "non-JSON project extension is rejected even when its contents are valid JSON");
            await ExpectThrowsAsync<InvalidDataException>(() => projects.SaveAsync(unsupportedProjectPath, new[] { group }, 0.73),
                "saving a project also requires the JSON file extension");
            // Atomic replacement path must also remain readable.
            await projects.SaveAsync(projectPath, new[] { group }, 0.81);
            Check((await projects.LoadAsync(projectPath)).GroupingThreshold == 0.81, "existing project is atomically replaceable");
            Check(!Directory.EnumerateFiles(testRoot, "*.tmp").Any(), "project save leaves no temporary files");

            string malformed = Path.Combine(testRoot, "malformed.json");
            await File.WriteAllTextAsync(malformed, "{}");
            await ExpectThrowsAsync<JsonException>(() => projects.LoadAsync(malformed), "project missing schema is rejected");
            await File.WriteAllTextAsync(malformed, "{\"Schema\":\"BirdPhotoSelector.Project\",\"Version\":999,\"GroupingThreshold\":0.8,\"Groups\":[]}");
            await ExpectThrowsAsync<InvalidDataException>(() => projects.LoadAsync(malformed), "future project version is rejected");

            var exporter = new ExportService();
            string collision = Path.Combine(destination, p1.FileName);
            byte[] sentinel = { 8, 6, 7, 5, 3, 0, 9 };
            await File.WriteAllBytesAsync(collision, sentinel);
            var exported = await exporter.ExportAsync(new[] { p1, p2 }, destination, removeOriginal: false);
            Check(exported.Succeeded.Count == 1 && exported.Succeeded[0].Id == p2.Id, "export reports actual copied photo");
            Check(exported.Skipped.Count == 1 && exported.Errors.Count == 0, "destination name collision is reported");
            Check((await File.ReadAllBytesAsync(collision)).SequenceEqual(sentinel), "existing destination is never overwritten");
            Check((await File.ReadAllBytesAsync(Path.Combine(destination, p2.FileName))).SequenceEqual(await File.ReadAllBytesAsync(second)),
                "exported photo bytes match source");
            Check(File.Exists(first) && File.Exists(second) && exported.SourceRemoved.Count == 0,
                "export without removal leaves both sources intact");

            string missingDirectory = Path.Combine(testRoot, "DoNotCreate");
            await ExpectThrowsAsync<DirectoryNotFoundException>(
                () => exporter.ExportAsync(new[] { p1 }, missingDirectory, removeOriginal: false),
                "missing destination is rejected");
            Check(!Directory.Exists(missingDirectory), "export does not create a destination folder");
            var sameFolder = await exporter.ExportAsync(new[] { p1 }, a, removeOriginal: true);
            Check(sameFolder.Succeeded.Count == 0 && sameFolder.Skipped.Count == 1 && File.Exists(first),
                "same source and destination never remove the source");

            string canceledDirectory = Directory.CreateDirectory(Path.Combine(testRoot, "Canceled")).FullName;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var canceled = await exporter.ExportAsync(new[] { p1, p2 }, canceledDirectory, removeOriginal: true,
                cancellation.Token);
            Check(canceled.Succeeded.Count == 0 && canceled.SourceRemoved.Count == 0, "pre-canceled export performs no operation");
            Check(File.Exists(first) && File.Exists(second) && !Directory.EnumerateFiles(canceledDirectory).Any(),
                "canceled export leaves sources and destination unchanged");

            await TestDestructiveOperationsAsync(testRoot);

            // Delete only the fixture we generated; loading should skip it, retaining surviving group members.
            File.Delete(second);
            var missing = await projects.LoadAsync(projectPath);
            Check(missing.SkippedMissing == 1 && missing.Groups.Count == 1 && missing.Groups[0].Count == 1,
                "project load skips missing files and retains valid members");

            await TestModelIntegrationAsync(testRoot);
        }
        finally
        {
            string resolved = Path.GetFullPath(testRoot);
            string allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SmokeTests")) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, recursive: true);
        }
    }

    private static void TestExifOrientation(string directory)
    {
        string path = Path.Combine(directory, "oriented.jpg");
        try
        {
            var metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)6);
            metadata.SetQuery("/app1/ifd/{ushort=272}", "Smoke Test Camera");
            metadata.SetQuery("/app1/ifd/exif/{ushort=36867}", "2025:01:02 03:04:05");
            metadata.SetQuery("/app1/ifd/exif/{ushort=37386}", 600UL | (1UL << 32));
            metadata.SetQuery("/app1/ifd/exif/{ushort=33437}", 28UL | (10UL << 32));
            metadata.SetQuery("/app1/ifd/exif/{ushort=33434}", 1UL | (640UL << 32));
            metadata.SetQuery("/app1/ifd/exif/{ushort=34855}", (ushort)800);
            metadata.SetQuery("/app1/ifd/exif/{ushort=37380}", unchecked((long)(0xffffffffUL | (3UL << 32))));
            WriteJpeg(path, 64, 40, 80, metadata);
        }
        catch (Exception ex) when (ex is NotSupportedException or System.Runtime.InteropServices.COMException)
        {
            Console.WriteLine("SKIP: platform JPEG encoder cannot write EXIF fixture: " + ex.Message);
            return;
        }
        var decoded = ImageLoader.Load(path);
        Check(decoded.PixelWidth == 40 && decoded.PixelHeight == 64, "EXIF rotation is applied to image pixels");
        var imported = new PhotoImportService().ImportAsync(new[] { path }, Array.Empty<string>()).GetAwaiter().GetResult();
        Check(imported.Added.Count == 1 && imported.Added[0].PixelWidth == 40 && imported.Added[0].PixelHeight == 64,
            "EXIF-oriented dimensions agree between import and preview");
        var metadataRead = new MetadataService().Read(path);
        Check(metadataRead.Camera == "Smoke Test Camera", "EXIF camera model is read");
        Check(metadataRead.CapturedAt == "2025-01-02 03:04:05", "EXIF capture date is normalized");
        Check(metadataRead.FocalLength == "600 mm" && metadataRead.Aperture == "f/2.8" &&
              metadataRead.Shutter == "1/640 s" && metadataRead.Iso == "800" && metadataRead.ExposureBias == "-0.33 EV",
            "EXIF focal length, aperture, shutter, ISO and signed compensation are formatted");
    }

    private static void TestBackgroundSimilarity()
    {
        var normal = BackgroundSimilarity.Describe(SolidBitmap(80));
        var identical = BackgroundSimilarity.Describe(SolidBitmap(80));
        var brighter = BackgroundSimilarity.Describe(SolidBitmap(85));
        var veryDifferent = BackgroundSimilarity.Describe(SolidBitmap(255));
        Check(Math.Abs(BackgroundSimilarity.Compare(normal, identical) - 1) < 0.000001,
            "identical backgrounds score one");
        double exposureSimilarity = BackgroundSimilarity.Compare(normal, brighter);
        Check(exposureSimilarity > 0.85,
            $"small exposure changes retain a high similarity ({exposureSimilarity:0.000})");
        Check(BackgroundSimilarity.Compare(normal, veryDifferent) < 0.5,
            "very different backgrounds separate");
    }

    private static async Task TestAnalysisAsync(string directory, PhotoItem first, PhotoItem second)
    {
        string noModel = Path.Combine(directory, "intentionally-missing-model.onnx");
        using var analyzer = new AnalysisService(noModel);
        BirdBox? prior = first.Bird;
        var missingModel = await analyzer.AnalyzeAsync(new[] { first, second }, 0.82);
        Check(!missingModel.MissingModel && missingModel.Outcomes.Count == 2,
            "background grouping never requires or loads a detection model");
        Check(missingModel.Outcomes[0].Bird is { X: 0.2, Y: 0.1, Width: 0.4, Height: 0.5 },
            "missing model preserves an existing valid bird location");
        Check(ReferenceEquals(first.Bird, prior) && first.DetectionStatus == "已识别鸟类",
            "analysis returns detached results without mutating UI-owned photos");

        var driftPhotos = new List<PhotoItem>();
        foreach (byte shade in new byte[] { 40, 80, 120 })
        {
            string path = Path.Combine(directory, $"drift-{shade}.jpg");
            var encoder = new JpegBitmapEncoder { QualityLevel = 100 };
            encoder.Frames.Add(BitmapFrame.Create(SolidBitmap(shade)));
            using (var stream = File.Create(path)) encoder.Save(stream);
            driftPhotos.Add(new PhotoItem { FilePath = path, PixelWidth = 160, PixelHeight = 120 });
        }
        var descriptors = driftPhotos.Select(p => BackgroundSimilarity.Describe(ImageLoader.Load(p.FilePath))).ToArray();
        double adjacent = Math.Min(BackgroundSimilarity.Compare(descriptors[0], descriptors[1]),
            BackgroundSimilarity.Compare(descriptors[1], descriptors[2]));
        double anchor = BackgroundSimilarity.Compare(descriptors[0], descriptors[2]);
        Check(adjacent > anchor + 0.02, "drift fixture has similar neighbors and a different anchor");
        double threshold = (adjacent + anchor) / 2;
        var drift = await analyzer.AnalyzeAsync(driftPhotos, threshold);
        Check(drift.Groups.Count == 2 && drift.Groups[0].Count == 2 && drift.Groups[1].Count == 1,
            "continuous scene drift splits against the group anchor");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await ExpectThrowsAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(driftPhotos, threshold, cancellationToken: cancel.Token),
            "analysis honors cancellation before processing");

        string invalidModel = Path.Combine(directory, "invalid-model.onnx");
        await File.WriteAllBytesAsync(invalidModel, new byte[] { 0, 1, 2, 3 });
        using var invalidDetector = new BirdDetectionService(invalidModel);
        var invalidResult = invalidDetector.Detect(SolidBitmap(80));
        Check(invalidResult.Bird is null && !invalidResult.MissingModel && !string.IsNullOrWhiteSpace(invalidResult.Error),
            "invalid model produces a recoverable diagnostic instead of an unhandled error");
    }

    private static async Task TestModelIntegrationAsync(string testRoot)
    {
        string? modelPath = Environment.GetEnvironmentVariable("BIRD_MODEL_PATH");
        string? samplePath = Environment.GetEnvironmentVariable("BIRD_SAMPLE_PATH");
        if (string.IsNullOrWhiteSpace(modelPath) || string.IsNullOrWhiteSpace(samplePath))
        {
            Console.WriteLine("SKIP: real-model integration requires BIRD_MODEL_PATH and BIRD_SAMPLE_PATH.");
            return;
        }
        modelPath = Path.GetFullPath(modelPath);
        samplePath = Path.GetFullPath(samplePath);
        Check(File.Exists(modelPath) && File.Exists(samplePath), "real integration model and sample exist");
        var image = ImageLoader.Load(samplePath, maxWidth: 1280);
        using var detector = new BirdDetectionService(modelPath);
        var detected = detector.Detect(image);
        Check(!detected.MissingModel && detected.Error is null, "real model loads and runs with native ONNX Runtime");
        Check(detected.Bird is { IsValid: true, Confidence: >= 0.25 }, "real model locates a valid bird in the sample photo");

        string firstPath = Path.Combine(testRoot, "integration-scene-a.jpg");
        string secondPath = Path.Combine(testRoot, "integration-scene-b.jpg");
        File.Copy(samplePath, firstPath);
        File.Copy(samplePath, secondPath);
        var imported = await new PhotoImportService().ImportAsync(new[] { firstPath, secondPath }, Array.Empty<string>());
        Check(imported.Added.Count == 2 && imported.Errors.Count == 0, "real JPEG fixture copies import successfully");
        // This integration exercises the real YOLO model and the production orchestration.
        // Species is deliberately deterministic here; native BioCLIP validation has its own checks.
        var fixtureSpecies = new SpeciesMatch("集成测试候选", "Testus integration", .61, Confidence: .88);
        using var workflow = new BirdRecognitionWorkflow(modelPath,
            recognizerOverride: (_, _, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return new(fixtureSpecies, "集成测试候选 · 确定性编码器");
            });
        var recognized = await workflow.RecognizeAsync(imported.Added);
        Check(!recognized.MissingModel && recognized.Errors.Count == 0 && recognized.Outcomes.Count == 2 &&
              recognized.Outcomes.All(outcome => outcome.Birds.Any(bird => bird.Box is { IsValid: true, Confidence: >= 0.25 } &&
                  bird.Species == fixtureSpecies)),
            "the recognition command runs real localization before identifying both previously ungrouped photos");
        Check(imported.Added.All(photo => photo.Birds.Count == 0),
            "real inference returns detached results before explicit UI application");
        var importedById = imported.Added.ToDictionary(photo => photo.Id);
        foreach (var outcome in recognized.Outcomes) outcome.ApplyTo(importedById[outcome.PhotoId]);
        foreach (var photo in imported.Added)
        {
            photo.ViewZoom = 2.1;
            photo.ViewCenterX = .43;
            photo.ViewCenterY = .57;
        }
        var stored = imported.Added.Select(photo => new
        {
            Photo = photo, Birds = photo.Birds.ToArray(), Selected = photo.SelectedBird,
            photo.DetectionStatus, photo.NextBirdNumber
        }).ToArray();
        using var analyzer = new AnalysisService(modelPath);
        var analyzed = await analyzer.AnalyzeAsync(imported.Added, 0.95);
        Check(!analyzed.MissingModel && analyzed.Errors.Count == 0 && analyzed.Outcomes.Count == 2,
            "background-only grouping succeeds for both localized real-model photos");
        Check(analyzed.Groups.Count == 1 && analyzed.Groups[0].Count == 2,
            "the stricter background cutoff groups identical same-scene photos together");
        Check(analyzed.Outcomes.All(outcome => outcome.PreservedExistingBirds &&
                  outcome.Birds.All(bird => bird is { IsValid: true, Confidence: >= 0.25 })) &&
              stored.All(entry => entry.Photo.Birds.SequenceEqual(entry.Birds) &&
                  ReferenceEquals(entry.Photo.SelectedBird, entry.Selected) &&
                  entry.Photo.Birds.All(bird => bird.Species == fixtureSpecies) &&
                  entry.Photo.DetectionStatus == entry.DetectionStatus && entry.Photo.NextBirdNumber == entry.NextBirdNumber &&
                  entry.Photo.ViewZoom == 2.1 && entry.Photo.ViewCenterX == .43 && entry.Photo.ViewCenterY == .57),
            "regrouping preserves real detected objects, species, selection, stable numbering and the saved detail view");

        string outputDirectory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
        Directory.CreateDirectory(outputDirectory);
        string sampleDirectory = Directory.CreateDirectory(Path.Combine(outputDirectory, "samples")).FullName;
        string demoFirst = Path.Combine(sampleDirectory, "native-test-a.jpg");
        string demoSecond = Path.Combine(sampleDirectory, "native-test-b.jpg");
        File.Copy(samplePath, demoFirst, overwrite: true);
        File.Copy(samplePath, demoSecond, overwrite: true);
        var demoPhotos = await new PhotoImportService().ImportAsync(new[] { demoFirst, demoSecond }, Array.Empty<string>());
        if (demoPhotos.Added.Count != 2) throw new InvalidOperationException("Native demo fixtures failed to import.");
        var demoGroup = new PhotoGroup("同场景鸟类识别测试");
        for (int index = 0; index < demoPhotos.Added.Count; index++)
        {
            var photo = demoPhotos.Added[index];
            var source = imported.Added[index];
            photo.SetBirds(source.Birds.Select(bird => new DetectedBird
            {
                Number = bird.Number, IsManual = bird.IsManual, Box = bird.Box.Copy(),
                Species = bird.Species, RecognitionStatus = bird.RecognitionStatus
            }), source.SelectedBirdIndex, source.NextBirdNumber);
            photo.DetectionStatus = source.DetectionStatus;
            photo.Rating = index == 0 ? PhotoRating.Favorite : PhotoRating.Keep;
            demoGroup.Photos.Add(photo);
        }
        string demoProject = Path.Combine(sampleDirectory, "native-test.json");
        var projects = new ProjectService();
        await projects.SaveAsync(demoProject, new[] { demoGroup }, 0.50);
        var reloadedDemo = await projects.LoadAsync(demoProject);
        Check(reloadedDemo.Groups.Count == 1 && reloadedDemo.Groups[0].Count == 2 &&
              reloadedDemo.Groups[0].Photos.All(photo => photo.Bird is { IsValid: true } &&
                  photo.Birds.All(bird => bird.Species == fixtureSpecies)),
            "real-model demo project reloads persistent photo copies, bird locations and stored species evidence");
        var report = new
        {
            Model = modelPath,
            Sample = samplePath,
            detected.Status,
            Bird = detected.Bird,
            RecognitionFixture = "real YOLO localization with deterministic species results; not native BioCLIP accuracy",
            recognized.MatchedBirds,
            analyzed.MissingModel,
            GroupCount = analyzed.Groups.Count,
            PhotoCount = analyzed.Outcomes.Count,
            PixelWidth = imported.Added[0].PixelWidth,
            PixelHeight = imported.Added[0].PixelHeight,
            DemoProject = demoProject,
            Outcomes = analyzed.Outcomes.Select(outcome => new { outcome.Bird, outcome.DetectionStatus }),
            Assertions = 10,
            CompletedUtc = DateTimeOffset.UtcNow
        };
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "integration-result.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task TestDestructiveOperationsAsync(string testRoot)
    {
        // Exercise the real Windows shell only on two JPEGs created specifically for this test.
        // Never enumerate, empty, or otherwise alter the user's recycle bin.
        string exportSource = Path.Combine(testRoot, $"generated-recycle-export-{Guid.NewGuid():N}.jpg");
        string deleteSource = Path.Combine(testRoot, $"generated-recycle-delete-{Guid.NewGuid():N}.jpg");
        string destination = Directory.CreateDirectory(Path.Combine(testRoot, "VerifiedRemovalExport")).FullName;
        WriteJpeg(exportSource, 48, 32, 30);
        WriteJpeg(deleteSource, 48, 32, 45);
        byte[] expected = await File.ReadAllBytesAsync(exportSource);
        var exporter = new ExportService();
        var photo = new PhotoItem { FilePath = exportSource, Rating = PhotoRating.Keep };
        Console.WriteLine("  RUN recycle export of generated temporary JPEG only");
        var exported = await exporter.ExportAsync(new[] { photo }, destination, removeOriginal: true);
        Check(exported.Succeeded.Count == 1 && exported.SourceRemoved.Count == 1 && exported.Errors.Count == 0,
            "verified export removes its generated temporary source through the Windows shell");
        Check(!File.Exists(exportSource) &&
              (await File.ReadAllBytesAsync(Path.Combine(destination, photo.FileName))).SequenceEqual(expected),
            "export removal leaves an exact destination copy and no source file");
        Console.WriteLine("  RUN recycle delete of generated temporary JPEG only");
        var deleted = await exporter.DeleteAsync(new[] { new PhotoItem { FilePath = deleteSource, Rating = PhotoRating.Reject } });
        Check(deleted.Succeeded.Count == 1 && deleted.SourceRemoved.Count == 1 && deleted.Errors.Count == 0 && !File.Exists(deleteSource),
            "delete operation removes its generated temporary fixture through the Windows shell");
    }

    private static BitmapSource SolidBitmap(byte shade)
    {
        const int width = 160, height = 120;
        var pixels = Enumerable.Repeat(shade, width * height * 3).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        bitmap.Freeze();
        return bitmap;
    }

    private static void WriteJpeg(string path, int width, int height, byte seed, BitmapMetadata? metadata = null)
    {
        byte[] pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 3;
            pixels[offset] = unchecked((byte)(seed + x));
            pixels[offset + 1] = unchecked((byte)(seed + y));
            pixels[offset + 2] = unchecked((byte)(seed + x + y));
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Check(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
        _assertions++;
        Console.WriteLine("  OK " + description);
    }

    private static async Task ExpectThrowsAsync<T>(Func<Task> action, string description) where T : Exception
    {
        try { await action(); }
        catch (T) { Check(true, description); return; }
        throw new InvalidOperationException(description + " (expected " + typeof(T).Name + ")");
    }
}
