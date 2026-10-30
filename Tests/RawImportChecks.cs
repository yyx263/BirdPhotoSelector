using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

/// <summary>Pairs opaque RAW originals only when an actual JPEG supplies the preview.</summary>
internal static class RawImportChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            count++;
            Console.WriteLine("  OK RAW IMPORT " + message);
        }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.RawImportChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new PhotoImportService();
            string a = Directory.CreateDirectory(Path.Combine(root, "A")).FullName;
            string b = Directory.CreateDirectory(Path.Combine(root, "B")).FullName;
            string jpg = Path.Combine(a, "SCENE.JPG"), raw = Path.Combine(a, "scene.NEF");
            string otherJpg = Path.Combine(b, "SCENE.JPG"), otherRaw = Path.Combine(b, "scene.ARW");
            WriteJpeg(jpg); WriteJpeg(otherJpg);
            await File.WriteAllTextAsync(raw, "Opaque RAW fixture, never a successful RAW-only decode.");
            await File.WriteAllTextAsync(otherRaw, "Opaque Sony fixture, not an embedded JPEG.");
            var hashes = new[] { jpg, raw, otherJpg, otherRaw }.ToDictionary(path => path,
                path => SHA256.HashData(File.ReadAllBytes(path)));
            var forwardProgress = new List<PhotoOperationProgress>();
            var forward = await service.ImportAsync(new[] { jpg, raw }, Array.Empty<PhotoItem>(),
                progress: new InlineProgress(forwardProgress.Add));
            var paired = forward.Added.Single();
            Check(paired.Assets.Count == 2 && paired.FormatLabel == "JPG + RAW" &&
                  paired.ActivePreviewAsset?.Kind == PhotoAssetKind.Jpg && paired.Thumbnail is { IsFrozen: true },
                "JPG and RAW become one logical photo with real JPG pixels and a frozen thumbnail");
            Check(forward.Errors.Count == 0 && forward.SkippedDuplicate == 0 && forward.PairedInputs == 1 &&
                  forwardProgress.Where(item => item.FileCompleted).Select(item => item.Completed).SequenceEqual(new[] { 1, 2 }) &&
                  forwardProgress.All(item => item.Total == 2 && Path.IsPathFullyQualified(item.FilePath)),
                "paired input progress completes every selected file without presenting the companion as a duplicate photo");
            var reverse = await service.ImportAsync(new[] { raw, jpg }, Array.Empty<PhotoItem>());
            Check(reverse.Added.Count == 1 && reverse.Added[0].ActivePreviewAsset?.CanonicalPath == jpg &&
                  reverse.Added[0].Assets.Select(asset => asset.CanonicalPath).Order().SequenceEqual(paired.Assets.Select(asset => asset.CanonicalPath).Order()),
                "import order does not affect member pairing or the JPG preview preference");
            var autoCompanion = await service.ImportAsync(new[] { jpg }, Array.Empty<PhotoItem>());
            Check(autoCompanion.Added.Single().Assets.Any(asset => asset.CanonicalPath == raw),
                "selecting a JPG discovers its one same-directory same-stem RAW companion");
            var autoJpg = await service.ImportAsync(new[] { raw }, Array.Empty<PhotoItem>());
            Check(autoJpg.Added.Single().ActivePreviewAsset?.CanonicalPath == jpg && autoJpg.Errors.Count == 0,
                "selecting an opaque RAW beside JPG uses the JPG without pretending to decode RAW pixels");
            var separateDirectories = await service.ImportAsync(new[] { jpg, otherJpg }, Array.Empty<PhotoItem>());
            Check(separateDirectories.Added.Count == 2 && separateDirectories.Added.All(photo => photo.Assets.Count == 2) &&
                  separateDirectories.Added.Select(photo => photo.Id).Distinct().Count() == 2,
                "same-named photos in different directories retain distinct logical identities and companions");
            var legacy = await service.ImportAsync(new[] { jpg, otherJpg, raw }, Array.Empty<PhotoItem>(), includeRaw: false);
            Check(legacy.Added.Count == 1 && legacy.Added[0].Assets.Count == 1 && legacy.SkippedDuplicate == 1 &&
                  legacy.SkippedUnsupported == 1 && legacy.Added[0].ActivePreviewAsset?.Kind == PhotoAssetKind.Jpg,
                "turning RAW off preserves JPG-only global filename deduplication and does not associate a companion");
            var legacyExisting = await service.ImportAsync(new[] { jpg }, new[] { paired }, includeRaw: false);
            Check(legacyExisting.Added.Count == 0 && legacyExisting.Updates.Count == 0 && legacyExisting.SkippedDuplicate == 1,
                "RAW-off duplicate checks include managed JPG members even in a paired logical record");
            var repeated = await service.ImportAsync(new[] { raw, jpg, jpg }, new[] { paired });
            Check(repeated.Added.Count == 0 && repeated.Updates.Count == 0 && paired.Assets.Count == 2,
                "repeated imports preserve the existing pair rather than duplicating its rating record");

            var single = new PhotoItem { FilePath = jpg, PixelWidth = 64, PixelHeight = 48,
                Rating = PhotoRating.Favorite, ViewZoom = 2.4 };
            single.SetBirds(new[] { Bird() }, nextBirdNumber: 25);
            string originalId = single.Id, jpgId = single.Assets.Single().Id;
            var oldBird = single.SelectedBird;
            var attached = await service.ImportAsync(new[] { raw }, new[] { single });
            Check(attached.Added.Count == 0 && attached.Updates.Count == 1 && single.Assets.Count == 1,
                "adding a companion to an existing JPG produces a detached update without touching UI-owned state");
            attached.Updates.Single().ApplyTo(single);
            Check(single.Id == originalId && single.Assets.Count == 2 && single.ActivePreviewAssetId == jpgId &&
                  single.Rating == PhotoRating.Favorite && single.NextBirdNumber == 25 && ReferenceEquals(single.SelectedBird, oldBird) &&
                  single.CanUseBirdGeometry && single.ViewZoom == 2.4,
                "applying a companion update keeps the current JPG, photo identity, rating, birds, numbering and view");

            var rawOnly = new PhotoItem { FilePath = raw, PixelWidth = 6000, PixelHeight = 4000,
                Rating = PhotoRating.Keep, ViewZoom = 2.7 };
            rawOnly.SetBirds(new[] { Bird() }, nextBirdNumber: 37);
            string rawOnlyId = rawOnly.Id, oldRawMember = rawOnly.Assets.Single().Id;
            var rawBird = rawOnly.SelectedBird;
            var switched = await service.ImportAsync(new[] { jpg }, new[] { rawOnly });
            Check(switched.Added.Count == 0 && switched.Updates.Count == 1 && rawOnly.ActivePreviewAsset?.Kind == PhotoAssetKind.Raw,
                "a later JPG prepares its preview without mutating the existing RAW photo during background work");
            switched.Updates.Single().ApplyTo(rawOnly);
            Check(rawOnly.Id == rawOnlyId && rawOnly.Assets.Count == 2 && rawOnly.ActivePreviewAsset?.Kind == PhotoAssetKind.Jpg &&
                  rawOnly.Thumbnail is not null && rawOnly.PixelWidth == 64 && rawOnly.PixelHeight == 48 &&
                  rawOnly.GeometryStatus == PhotoGeometryStatus.NeedsReview && !rawOnly.CanUseBirdGeometry &&
                  rawOnly.GeometryReference?.AssetId == oldRawMember && ReferenceEquals(rawOnly.SelectedBird, rawBird) &&
                  rawOnly.Rating == PhotoRating.Keep && rawOnly.NextBirdNumber == 37 && rawOnly.ViewZoom is null,
                "RAW-to-JPG import preserves species, stable identity and numbering while withholding the old frame and view");
            var stale = await service.ImportAsync(new[] { raw }, new[] { new PhotoItem { Id = "stale", FilePath = jpg } });
            var changed = new PhotoItem { Id = "stale", FilePath = jpg };
            bool rejectedStale = false;
            try { stale.Updates.Single().ApplyTo(changed); }
            catch (InvalidOperationException) { rejectedStale = true; }
            Check(rejectedStale && changed.Assets.Count == 1,
                "a detached plan cannot overwrite a record whose member identity changed while import was running");

            string conflicts = Directory.CreateDirectory(Path.Combine(root, "conflicts")).FullName;
            string conflictJpg = Path.Combine(conflicts, "capture.jpg");
            WriteJpeg(conflictJpg);
            await File.WriteAllTextAsync(Path.Combine(conflicts, "capture.NEF"), "opaque one");
            await File.WriteAllTextAsync(Path.Combine(conflicts, "capture.ARW"), "opaque two");
            var rawConflict = await service.ImportAsync(new[] { conflictJpg, Path.Combine(conflicts, "capture.NEF") }, Array.Empty<PhotoItem>());
            Check(rawConflict.Added.Count == 0 && rawConflict.Updates.Count == 0 && rawConflict.SkippedConflict == 1 &&
                  rawConflict.Errors.Single().Contains("capture.NEF") && rawConflict.Errors.Single().Contains("capture.ARW"),
                "multiple RAW candidates report a conflict once and never choose one arbitrarily");
            var offConflict = await service.ImportAsync(new[] { conflictJpg }, Array.Empty<PhotoItem>(), includeRaw: false);
            Check(offConflict.Added.Single().Assets.Count == 1 && offConflict.Errors.Count == 0,
                "turning RAW off can import JPG without inspecting conflicting RAW companions");
            string duplicateJpg = Path.Combine(conflicts, "double.jpg");
            WriteJpeg(duplicateJpg); WriteJpeg(Path.Combine(conflicts, "double.jpeg"));
            var jpgConflict = await service.ImportAsync(new[] { duplicateJpg }, Array.Empty<PhotoItem>());
            Check(jpgConflict.Added.Count == 0 && jpgConflict.SkippedConflict == 1,
                "JPG and JPEG candidates for one stem are also explicit conflicts");
            var separateOwners = await service.ImportAsync(new[] { raw },
                new[] { new PhotoItem { FilePath = jpg }, new PhotoItem { FilePath = raw } });
            Check(separateOwners.SkippedConflict == 1 && separateOwners.Added.Count == 0 && separateOwners.Updates.Count == 0,
                "import never merges two existing logical photos with potentially different ratings");
            string alone = Path.Combine(root, "opaque-only.CR3");
            await File.WriteAllTextAsync(alone, "This fixture is not a decodable RAW image.");
            var invalidRaw = await service.ImportAsync(new[] { alone }, Array.Empty<PhotoItem>());
            Check(invalidRaw.Added.Count == 0 && invalidRaw.Errors.Count == 1,
                "RAW-only import must really decode its image and rejects opaque bytes instead of claiming preview support");

            using var cancellation = new CancellationTokenSource();
            bool canceled = false;
            var cancelPhoto = new PhotoItem { FilePath = jpg, Rating = PhotoRating.Favorite };
            try
            {
                await service.ImportAsync(new[] { raw }, new[] { cancelPhoto }, cancellationToken: cancellation.Token,
                    progress: new InlineProgress(_ => cancellation.Cancel()));
            }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && cancelPhoto.Assets.Count == 1 && cancelPhoto.Rating == PhotoRating.Favorite,
                "canceling at import progress leaves existing members and rating unchanged");
            Check(hashes.All(entry => File.Exists(entry.Key) && SHA256.HashData(File.ReadAllBytes(entry.Key)).SequenceEqual(entry.Value)),
                "pair discovery, previews, updates, conflicts and cancellation preserve all original JPG and RAW bytes");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
        return count;
    }

    private static DetectedBird Bird() => new() { Number = 7, IsManual = true,
        Box = new() { X = .1, Y = .2, Width = .3, Height = .4, Confidence = .9 },
        Species = new("大山雀", "Parus major", .42) };
    private sealed class InlineProgress(Action<PhotoOperationProgress> report) : IProgress<PhotoOperationProgress>
    { public void Report(PhotoOperationProgress value) => report(value); }
    private static void WriteJpeg(string path)
    {
        const int width = 64, height = 48;
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat((byte)100, width * height * 3).ToArray(), width * 3);
        image.Freeze();
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
