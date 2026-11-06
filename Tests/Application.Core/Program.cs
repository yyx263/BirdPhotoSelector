using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using BirdPhotoSelector;
using BirdPhotoSelector.Imaging;

/// <summary>无 WPF、无模型文件的业务与依赖边界验证，可在三种桌面系统上直接运行。</summary>
internal static class Program
{
    private static int _checks;

    /// <summary>每次只在独立 GUID 临时目录生成数据，失败时以非零退出码报告。</summary>
    private static async Task<int> Main()
    {
        string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.Application"));
        string root = Path.Combine(tempRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Boundaries();
            await SessionAndProjects(root);
            await Recognition(root);
            await FileOperations(root);
            Console.WriteLine($"PASS: {_checks} portable application assertions on {RuntimeInformation.OSDescription}.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex); return 1; }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != tempRoot || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
                throw new InvalidOperationException("Refusing cleanup outside generated application check directory.");
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>检验实际编译产物的引用和本地入口，而不只依赖项目文件中的目标框架声明。</summary>
    private static void Boundaries()
    {
        var domain = typeof(PhotoItem).Assembly; var recognition = typeof(BirdDetectionService).Assembly;
        var application = typeof(ApplicationServices).Assembly;
        foreach (var assembly in new[] { domain, recognition, application })
        {
            var refs = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToArray();
            Check(!refs.Any(n => n is "PresentationCore" or "PresentationFramework" or "WindowsBase" or "BirdPhotoSelector.Imaging.Windows" or "BirdPhotoSelector"),
                assembly.GetName().Name + " has no dependency on WPF or the Windows host");
            Check(!assembly.GetTypes().SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                .Any(m => m.GetCustomAttribute<DllImportAttribute>() is not null), assembly.GetName().Name + " contains no direct OS native entry point");
        }
        Check(!domain.GetReferencedAssemblies().Any(a => a.Name is "BirdPhotoSelector.Application" or "BirdPhotoSelector.Recognition"), "domain cannot depend back on workflows or models");
        Check(!recognition.GetReferencedAssemblies().Any(a => a.Name == "BirdPhotoSelector.Application"), "models cannot depend back on application orchestration");
        Check(typeof(BirdSpeciesRecognitionService).GetMethod("RecognizeAsync") is null,
            "the image model no longer owns photo-file batches");
    }

    /// <summary>验证选择、评级、人工编辑与持久化规则，所有图像均由纯托管后端提供。</summary>
    private static async Task SessionAndProjects(string root)
    {
        string dir = Path.Combine(root, "session"); Directory.CreateDirectory(dir);
        string aPath = await Write(dir, "a.jpg", "generated-a"), rawPath = await Write(dir, "a.NEF", "opaque-raw"), bPath = await Write(dir, "b.jpg", "generated-b");
        var images = new Images(); var files = new Files(root);
        var services = new ApplicationServices(images, files, Path.Combine(dir, "recovery"));
        var imported = await services.Import.ImportAsync(new[] { aPath, bPath }, Array.Empty<PhotoItem>());
        var a = imported.Added[0]; var b = imported.Added[1];
        Check(imported.Errors.Count == 0 && a.Assets.Count == 2 && a.ActivePreviewAsset?.Kind == PhotoAssetKind.Jpg,
            "portable import retains same-shot JPG/RAW pairing and JPG preference");
        Check(images.Widths.SequenceEqual(new[] { 160, 160 }) && a.Thumbnail is Rgb24ImageFrame,
            "imports request small thumbnails through one injected non-Windows decoder");
        var first = new PhotoGroup("first"); first.Photos.Add(a); var second = new PhotoGroup("second"); second.Photos.Add(b);
        var session = new PhotoSelectionSession(); bool consistent = true; int changes = 0;
        session.PhotoChanged += photo => { consistent &= photo is null || session.CurrentGroup?.Photos.Contains(photo) == true; changes++; };
        session.SetGroups(new[] { first, second }); session.IsPreviewReady = true;
        session.NavigatePhoto(1);
        Check(session.CurrentPhoto == b && session.CurrentGroup?.Model == second && consistent && changes == 2,
            "cross-group photo navigation publishes a consistent group/photo selection without transient null");
        session.NavigatePhoto(-1); Check(session.CurrentPhoto == a, "reverse navigation crosses back to the previous group");
        session.IsBusy = true; session.NavigateGroup(1); session.SetRating(a, PhotoRating.Favorite);
        Check(session.CurrentPhoto == a && a.Rating == PhotoRating.Unmarked, "busy session blocks navigation and rating changes");
        session.IsBusy = false; session.IsPreviewReady = false;
        Check(!session.ApplyManualSpecies(a, new("大山雀", "Parus major")), "manual edits require a loaded current preview");
        session.IsPreviewReady = true; session.SetRating(a, PhotoRating.Favorite);
        Check(a.Rating == PhotoRating.Favorite && session.RatingSummary.Contains("精选 1"), "session owns rating state and aggregate counts");
        Check(session.ApplyManualSpecies(a, new("Z bird", "Testus z")) && session.ApplyManualSpecies(a, new("A bird", "Testus a")),
            "manual species actions are available without a Windows window");
        Check(a.Birds.Select(x => x.SpeciesName).SequenceEqual(new[] { "A bird", "Z bird" }) && a.HasManualSpeciesEdits,
            "manual no-box species remain sorted and protect nonempty results from ordinary batch inference");
        Check(session.RecentSpecies.Items.Count == 2 && session.SpeciesEdits.CanUndo, "manual actions maintain shared recent choices and undo history");
        Check(session.ClearRecognitionResults(a) && a.Birds.Count == 0 && !a.HasManualSpeciesEdits && a.GeometryStatus == PhotoGeometryStatus.Unspecified,
            "clearing every result resets geometry and makes the photo eligible for another batch");
        Check(session.UndoSpeciesEdit() == a && a.Birds.Count == 2 && a.HasManualSpeciesEdits && session.RecentSpecies.Items.Count == 2,
            "undo restores results and manual protection without rolling back recent choices");
        Check(!session.DeleteBirdResult(b, a.Birds[0]), "editing another photo cannot bypass the current-photo guard");
        string projectPath = Path.Combine(dir, "project.json");
        await services.Projects.SaveAsync(projectPath, new[] { first, second }, .43, recentSpecies: session.RecentSpecies.Items);
        string json = await File.ReadAllTextAsync(projectPath);
        Check(json.Contains("\"Version\": 3") && !json.Contains("Thumbnail") && !json.Contains("Bitmap"),
            "v3 project persists state without pixels, UI objects or cache details");
        int before = images.Decodes;
        var reopened = await services.Projects.LoadAsync(projectPath);
        Check(reopened.Groups.Count == 2 && reopened.RecentSpecies.Count == 2 && reopened.GroupingThreshold == .43 && images.Decodes == before + 2,
            "project recovery uses the injected image backend and retains groups, threshold and recent choices");
        Check(reopened.Groups[0].Photos[0].HasManualSpeciesEdits && reopened.Groups[0].Photos[0].Birds.Count == 2,
            "project recovery preserves manual records and the protection flag");
        var saved = await services.AutoSave.SaveAsync(new[] { first, second }, .43, null, 7, recentSpecies: session.RecentSpecies.Items);
        Check(saved.IsRecovery && File.Exists(saved.Path) && services.AutoSave.LastSavedRevision == 7,
            "autosave uses the same portable project service and writes a recoverable session");
        var autoLoaded = await services.Projects.LoadAsync(saved.Path);
        Check(autoLoaded.RecentSpecies.Count == 2 && autoLoaded.Groups.Count == 2, "autosave retains the same manual and grouping state");
        session.SetGroups(new[] { second });
        Check(!session.SpeciesEdits.CanUndo && session.RecentSpecies.Items.Count == 2 && session.CurrentPhoto == b,
            "removing photos invalidates old undo history inside the portable session while keeping recent choices");
        using var cancel = new CancellationTokenSource();
        images.BeforeDecode = () => cancel.Cancel();
        bool canceled = false;
        try { await services.Projects.LoadAsync(projectPath, cancel.Token); } catch (OperationCanceledException) { canceled = true; }
        images.BeforeDecode = null;
        Check(canceled, "project cancellation reaches the injected thumbnail decoder");
        Check((await File.ReadAllTextAsync(aPath)) == "generated-a" && (await File.ReadAllTextAsync(rawPath)) == "opaque-raw",
            "session edits, project writes and autosave never change original photo bytes");
    }

    /// <summary>验证模型实例所有权、延迟创建、人工保护、失败/取消及单张提案的来源检查。</summary>
    private static async Task Recognition(string root)
    {
        string dir = Path.Combine(root, "recognition"); Directory.CreateDirectory(dir);
        string path = await Write(dir, "bird.jpg", "stable-generated-source");
        var images = new Images(); int detectorCreates = 0, recognizerCreates = 0;
        var detectors = new List<Detector>(); var recognizers = new List<Recognizer>();
        var services = new ApplicationServices(images, new Files(root), Path.Combine(dir, "recovery"),
            detectors: () => { detectorCreates++; var d = new Detector(); detectors.Add(d); return d; },
            recognizers: () => { recognizerCreates++; var r = new Recognizer(); recognizers.Add(r); return r; });
        Check(detectorCreates == 0 && recognizerCreates == 0, "host composition does not eagerly construct native models");
        var photo = new PhotoItem { FilePath = path };
        using (var operation = services.CreateRecognition())
        {
            var batch = await operation.RecognizeAsync(new[] { photo });
            Check(batch.MatchedBirds == 1 && photo.Birds.Count == 0, "batch recognition returns a detached proposal before any UI state changes");
            batch.Outcomes.Single().ApplyTo(photo);
            Check(photo.Birds.Count == 1 && photo.CanUseBirdGeometry && photo.SelectedBird?.Species?.ScientificName == "Parus major",
                "explicit result application confirms the active image coordinate source");
        }
        Check(detectors[0].Disposals == 1 && recognizers[0].Disposals == 1, "batch workflow releases its owned engines once");
        photo.HasManualSpeciesEdits = true; int decodes = images.Decodes;
        using (var operation = services.CreateRecognition())
        {
            var batch = await operation.RecognizeAsync(new[] { photo });
            Check(batch.ManuallyEditedSkippedPhotos == 1 && images.Decodes == decodes && detectors[1].Calls == 0,
                "ordinary recognition skips a manually edited photo before image decoding or inference");
        }
        var history = new SpeciesEditHistory(); history.Clear(photo);
        using (var operation = services.CreateRecognition())
        {
            var batch = await operation.RecognizeAsync(new[] { photo });
            Check(batch.MatchedBirds == 1 && batch.ManuallyEditedSkippedPhotos == 0,
                "a photo with all results deleted is localized again by the next batch");
        }
        photo.HasManualSpeciesEdits = false;
        using (var operation = services.CreateSinglePhotoRecognition())
        {
            var proposal = await operation.RebuildAsync(photo);
            Check(proposal.Success && proposal.Birds.Count == 1, "single-photo rebuilding uses the same injected engine contracts");
            await File.AppendAllTextAsync(path, "changed-after-compute");
            bool rejected = false;
            try { proposal.ApplyTo(photo); } catch (IOException) { rejected = true; }
            Check(rejected, "source changes after recognition prevent applying a stale complete proposal");
        }
        var orphan = new Detector();
        var broken = new ApplicationServices(images, new Files(root), detectors: () => orphan,
            recognizers: () => throw new InvalidOperationException("generated factory failure"));
        bool failed = false; try { using var operation = broken.CreateRecognition(); } catch (InvalidOperationException) { failed = true; }
        Check(failed && orphan.Disposals == 1, "failure constructing the second engine releases the first engine");
        photo.SetBirds(new[] { new DetectedBird { Box = Box(), Number = 1 } });
        var shared = new Recognizer();
        using (var batch = new SpeciesRecognitionBatchService(images, shared))
        {
            var result = await batch.RecognizeAsync(new[] { photo });
            Check(result.Outcomes.Count == 1 && shared.Preparations == 1 && shared.Calls == 1,
                "existing-region batch prepares the model once and delegates one crop per bird");
        }
        Check(shared.Disposals == 0, "a batch borrowing an existing model never disposes the caller's model"); shared.Dispose();
        var owned = new Recognizer(); using (var batch = new SpeciesRecognitionBatchService(images, owned, ownsRecognizer: true)) { }
        Check(owned.Disposals == 1, "an owning batch explicitly releases its model");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using (var operation = services.CreateRecognition())
        {
            int reads = images.Decodes; bool stopped = false;
            try { await operation.RecognizeAsync(new[] { photo }, cancellationToken: canceled.Token); } catch (OperationCanceledException) { stopped = true; }
            Check(stopped && images.Decodes == reads, "pre-canceled workflow never reads an image");
        }
    }

    /// <summary>只在生成文件上检验复制校验、冲突、未知文件身份的保守策略及删除平台调用。</summary>
    private static async Task FileOperations(string root)
    {
        string dir = Path.Combine(root, "files"), output = Path.Combine(dir, "output"); Directory.CreateDirectory(output);
        var platform = new Files(root); var service = new ExportService(platform);
        string source = await Write(dir, "bird.jpg", "generated-original"); var photo = new PhotoItem { FilePath = source };
        var copied = await service.ExportAsync(new[] { photo }, output, false);
        Check(copied.VerifiedCopyCount == 1 && File.Exists(source) && (await File.ReadAllBytesAsync(source)).SequenceEqual(await File.ReadAllBytesAsync(Path.Combine(output, "bird.jpg"))),
            "portable export still verifies an exact copy and preserves the source by default");
        var identical = await service.ExportAsync(new[] { photo }, output, true);
        Check(identical.SkippedFileCount == 1 && identical.RemovedFileCount == 0 && File.Exists(source) && platform.Removed.Count == 0,
            "identical destinations skip automatically and never authorize source removal");
        await File.WriteAllTextAsync(Path.Combine(output, "bird.jpg"), "different-destination");
        var renamed = await service.ExportAsync(new[] { photo }, output, false, conflictResolver: (conflict, _) =>
        { Check(!conflict.CanOverwrite, "unknown physical identity conservatively disables overwrite"); return Task.FromResult(new ExportConflictDecision(ExportConflictAction.Rename)); });
        Check(renamed.VerifiedCopyCount == 1 && File.Exists(Path.Combine(output, "bird_1.jpg")) &&
            await File.ReadAllTextAsync(Path.Combine(output, "bird.jpg")) == "different-destination", "rename preserves the occupied target and uses the expected suffix");
        string clean = Path.Combine(dir, "clean"); Directory.CreateDirectory(clean);
        var moved = await service.ExportAsync(new[] { photo }, clean, true);
        Check(moved.RemovedFileCount == 1 && !File.Exists(source) && platform.Removed.Count == 1 && File.Exists(Path.Combine(clean, "bird.jpg")),
            "original removal goes through the injected platform only after all required copies verify");
        Check(platform.IdentityReads > 0, "export obtains file identities from the platform interface rather than native calls in business code");
        string reject = await Write(dir, "reject.jpg", "generated-reject");
        var deleted = await service.DeleteAsync(new[] { new PhotoItem { FilePath = reject } });
        Check(deleted.RemovedFileCount == 1 && platform.Removed.Count == 2 && !File.Exists(reject), "delete-rejected uses the same explicit platform removal contract");
    }

    /// <summary>固定合法鸟框，不依赖真实模型与图片内容。</summary>
    private static BirdBox Box() => new() { X = .2, Y = .2, Width = .4, Height = .5, Confidence = .9 };
    /// <summary>生成测试原件字节。</summary>
    private static async Task<string> Write(string dir, string name, string content)
    { string path = Path.Combine(dir, name); await File.WriteAllTextAsync(path, content); return path; }
    /// <summary>将断言失败转为清晰的进程失败。</summary>
    private static void Check(bool passed, string message)
    { if (!passed) throw new InvalidOperationException(message); _checks++; Console.WriteLine("  OK APPLICATION " + message); }

    /// <summary>记录读取行为的纯托管图像后端，可在解码期间注入取消。</summary>
    private sealed class Images : IPhotoImageDecoder
    {
        private readonly ImageFrame _image = new Rgb24ImageFrame(8, 4, new byte[8 * 4 * 3]);
        public ConcurrentQueue<int> Widths { get; } = new();
        public int Decodes; public Action? BeforeDecode;
        public ImageFrame Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
        { BeforeDecode?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref Decodes); Widths.Enqueue(maxWidth); return _image; }
        public PhotoImageInfo ReadInfo(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new(new() { Camera = "Managed backend" }, 8, 4, 1); }
    }
    /// <summary>仅允许删除此测试根下的文件，不伪造未知实体身份。</summary>
    private sealed class Files(string root) : IExportFilePlatform
    {
        public List<string> Removed { get; } = new(); public int IdentityReads;
        public FileIdentity? TryGetIdentity(FileStream stream) { IdentityReads++; return null; }
        public void RecycleOrDelete(string path)
        {
            string relative = Path.GetRelativePath(root, Path.GetFullPath(path));
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar))
                throw new InvalidOperationException("Attempted to delete outside generated fixtures.");
            File.Delete(path); Removed.Add(path);
        }
    }
    /// <summary>明确记录推理和释放次数的定位模型替身。</summary>
    private sealed class Detector : IBirdDetector
    {
        public int Calls, Disposals;
        public BirdDetectionResult Detect(ImageFrame image, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return new(Box(), "located"); }
        public void Dispose() => Disposals++;
    }
    /// <summary>明确区分准备、推理和释放，不加载原生模型。</summary>
    private sealed class Recognizer : IBirdSpeciesRecognizer
    {
        public int Calls, Disposals, Preparations;
        public SpeciesRecognitionResult? Prepare(CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Preparations++; return null; }
        public SpeciesRecognitionResult Recognize(ImageFrame image, BirdBox box, bool chinaOnly = true, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return new(new("大山雀", "Parus major", .7, Confidence: .9), "recognized"); }
        public void Dispose() => Disposals++;
    }
}
