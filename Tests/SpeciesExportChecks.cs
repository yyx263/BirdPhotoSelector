using System.IO;
using BirdPhotoSelector;

internal static class SpeciesExportChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); count++; Console.WriteLine("  OK SPECIES EXPORT " + message); }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SpeciesExportChecks");
        string root = Directory.CreateDirectory(Path.Combine(allowed, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            BirdBox Box() => new() { X = .1, Y = .2, Width = .3, Height = .4, Confidence = .9 };
            DetectedBird Auto(string chinese, string latin, double? confidence = .9) => new()
            { Box = Box(), Species = new(chinese, latin, .6, Confidence: confidence) };
            PhotoItem Pair(string name, bool species = true, string stem = "scene")
            {
                string folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                string jpg = Path.Combine(folder, stem + ".JPG"), raw = Path.Combine(folder, stem + ".NEF");
                File.WriteAllText(jpg, name + " generated JPG-kind original"); File.WriteAllText(raw, name + " generated RAW-kind original");
                var photo = new PhotoItem { Rating = PhotoRating.Favorite };
                photo.SetAssets(new[] { new PhotoAsset(jpg, PhotoAssetKind.Jpg), new PhotoAsset(raw, PhotoAssetKind.Raw) });
                if (species) photo.SetBirds(new[] { Auto("大山雀", "Parus major"), Auto("麻雀", "Passer montanus") });
                return photo;
            }
            string Output(string name) => Directory.CreateDirectory(Path.Combine(root, "out-" + name)).FullName;
            bool Exist(PhotoItem photo) => photo.Assets.All(asset => File.Exists(asset.CanonicalPath));
            var service = new ExportService(File.Delete); // Generated random fixtures only.
            var photo = Pair("all");
            photo.Birds.Add(Auto("同一物种别名", " parus MAJOR "));
            var plan = ExportService.CreatePlan(new[] { photo }, groupBySpecies: true);
            Check(plan.GroupBySpecies && plan.FileCount == 2 && plan.CopyCount == 4 &&
                  plan.Photos.Single().TargetFolders.Count == 2,
                "scientific identity deduplicates repeated birds and keeps two unique originals versus four destination copies");
            string destination = Output("all");
            var result = await service.ExportAsync(plan, destination, false);
            Check(result.Files.Count == 2 && result.Copies.Count == 4 && result.CopiedFileCount == 2 &&
                  result.VerifiedCopyCount == 4 && result.RemovedFileCount == 0 && Exist(photo) &&
                  result.Copies.All(copy => copy.Verified && File.ReadAllBytes(copy.SourcePath).SequenceEqual(File.ReadAllBytes(copy.TargetPath!))),
                "each species receives complete selected originals with independent verified copies and unique source accounting");
            Check(result.Copies.Select(copy => copy.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4 &&
                  Directory.GetDirectories(destination).Select(Path.GetFileName).Order().SequenceEqual(new[] { "大山雀", "麻雀" }.Order()),
                "Chinese species folders are distinct and duplicate Latin identities do not create redundant folders");
            var fallbackName = Pair("chinese-label", false);
            fallbackName.Birds.Add(Auto("", "Parus major"));
            fallbackName.Birds.Add(Auto("大山雀", "PARUS MAJOR"));
            Check(ExportService.CreatePlan(new[] { fallbackName }, groupBySpecies: true).Photos.Single()
                      .TargetFolders.SequenceEqual(new[] { "大山雀" }),
                "a later Chinese name replaces Latin fallback without producing a second folder for the same identity");
            var rawPlan = ExportService.CreatePlan(new[] { photo }, PhotoFileScope.RawOnly, true);
            var rawResult = await service.ExportAsync(rawPlan, Output("raw"), false);
            Check(rawPlan.FileCount == 1 && rawPlan.CopyCount == 2 && rawResult.Files.Count == 1 &&
                  rawResult.Copies.Count == 2 && rawResult.Copies.All(copy => copy.SourcePath.EndsWith(".NEF")),
                "RAW-only scope still copies that complete member once per distinct species");

            var flat = Pair("flat");
            string flatOut = Output("flat");
            var flatResult = await service.ExportAsync(ExportService.CreatePlan(new[] { flat }), flatOut, false);
            Check(flatResult.VerifiedCopyCount == 2 && flatResult.CopiedFileCount == 2 &&
                  Directory.GetDirectories(flatOut).Length == 0 && Directory.GetFiles(flatOut).Length == 2,
                "unchecked classification preserves the existing flat directory behavior and counts");
            var unknown = Pair("unknown", false);
            var unknownResult = await service.ExportAsync(ExportService.CreatePlan(new[] { unknown }, groupBySpecies: true), Output("unknown"), false);
            Check(unknownResult.VerifiedCopyCount == 2 && unknownResult.Copies.All(copy => copy.TargetFolder == "未分类"),
                "a photo without an effective species goes only into the unclassified folder");

            var names = new[] { ("同名鸟", "Species one"), ("同名鸟", "Species two"),
                ("A/B", "Species three"), ("A:B", "Species four"), ("CON", "Species five"), ("未分类", "Species six") };
            var unusual = names.Select((name, index) =>
            {
                var item = Pair("names-" + index, false);
                item.Birds.Add(new DetectedBird { ManualSpecies = new(name.Item1, name.Item2) });
                return item;
            }).ToArray();
            var unusualPlan = ExportService.CreatePlan(unusual, groupBySpecies: true);
            var folders = unusualPlan.Photos.SelectMany(item => item.TargetFolders).ToArray();
            Check(folders.Distinct(StringComparer.OrdinalIgnoreCase).Count() == names.Length &&
                  folders.All(folder => folder != "未分类" && folder != "CON" && folder.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
                      folder != "." && folder != ".." && !Path.IsPathRooted(folder)) &&
                  folders.Count(folder => folder.Contains("Species one") || folder.Contains("Species two")) == 2,
                "homonyms, sanitized collisions, Windows device names and the unclassified reserved label remain safe and unambiguous");
            var unusualResult = await service.ExportAsync(unusualPlan, Output("names"), false);
            Check(unusualResult.VerifiedCopyCount == names.Length * 2 && unusualResult.Errors.Count == 0,
                "sanitized manual-only labels export successfully without requiring AI geometry or confidence");

            var mutable = Pair("snapshot"); var frozen = ExportService.CreatePlan(new[] { mutable }, groupBySpecies: true);
            var frozenNames = frozen.Photos.Single().TargetFolders.ToArray();
            mutable.Birds.Clear(); mutable.Birds.Add(new DetectedBird { ManualSpecies = new("另一种", "Other species") });
            var frozenResult = await service.ExportAsync(frozen, Output("snapshot"), false);
            Check(frozenResult.Copies.Select(copy => copy.TargetFolder).Distinct().Order().SequenceEqual(frozenNames.Order()),
                "a confirmed plan snapshots species targets instead of reading later UI label changes on the worker");

            var conflict = Pair("conflict"); var healthy = Pair("healthy", true, "other");
            var conflictPlan = ExportService.CreatePlan(new[] { conflict, healthy }, groupBySpecies: true);
            string conflictOut = Output("conflict");
            string conflictFolder = conflictPlan.Photos[0].TargetFolders.Last();
            Directory.CreateDirectory(Path.Combine(conflictOut, conflictFolder));
            string sentinel = Path.Combine(conflictOut, conflictFolder, "scene.NEF"); File.WriteAllText(sentinel, "sentinel");
            var conflictResult = await service.ExportAsync(conflictPlan, conflictOut, true);
            Check(Exist(conflict) && !Exist(healthy) && conflictResult.Files.Count == 4 && conflictResult.RemovedFileCount == 2 &&
                  conflictResult.VerifiedCopyCount == 7 && File.ReadAllText(sentinel) == "sentinel" &&
                  conflictResult.Files.Single(file => file.SourcePath.EndsWith("conflict" + Path.DirectorySeparatorChar + "scene.NEF")).Verified == false,
                "one existing destination preserves every original in that photo, does not overwrite it and allows other photos to finish");

            var failed = Pair("failed"); int copies = 0;
            var failing = new ExportService(File.Delete, async (source, output, token) =>
            {
                if (++copies == 4) { await output.WriteAsync(new byte[] { 1 }, token); throw new IOException("Injected copy failure."); }
                await source.CopyToAsync(output, token);
            });
            string failedOut = Output("failed");
            var failedResult = await failing.ExportAsync(ExportService.CreatePlan(new[] { failed }, groupBySpecies: true), failedOut, true);
            Check(Exist(failed) && failedResult.RemovedFileCount == 0 && failedResult.VerifiedCopyCount == 3 &&
                  Directory.GetFiles(failedOut, "*", SearchOption.AllDirectories).Length == 3,
                "one late copy failure cleans only its unfinished target and keeps all originals for the photo");

            var canceled = Pair("canceled");
            using (var cancellation = new CancellationTokenSource())
            {
                int attempted = 0;
                var cancelService = new ExportService(File.Delete, async (source, output, token) =>
                { if (++attempted == 2) cancellation.Cancel(); await source.CopyToAsync(output, token); });
                var cancelResult = await cancelService.ExportAsync(ExportService.CreatePlan(new[] { canceled }, groupBySpecies: true),
                    Output("canceled"), true, cancellation.Token);
                Check(Exist(canceled) && cancelResult.RemovedFileCount == 0 && cancelResult.VerifiedCopyCount == 1 &&
                      cancelResult.Files.All(file => !file.SourceRemoved),
                    "canceling a destination copy retains earlier verified copies but never starts source removal");
            }
            var tampered = Pair("tampered"); string tamperedOut = Output("tampered");
            var tamperedPlan = ExportService.CreatePlan(new[] { tampered }, groupBySpecies: true); bool altered = false;
            var tamperedResult = await service.ExportAsync(tamperedPlan, tamperedOut, true, progress: new InlineProgress(item =>
            {
                if (!altered && item.Stage == "正在确认目标副本")
                { altered = true; File.Delete(Path.Combine(tamperedOut, tamperedPlan.Photos[0].TargetFolders.Last(), "scene.NEF")); }
            }));
            Check(altered && Exist(tampered) && tamperedResult.RemovedFileCount == 0 && tamperedResult.VerifiedCopyCount == 3,
                "a missing copy at the final all-target barrier prevents removal of every original");

            var protectedPhoto = Pair("protected"); var protectedPlan = ExportService.CreatePlan(new[] { protectedPhoto }, groupBySpecies: true);
            string protectedOut = Output("protected"); int blocked = 0, removals = 0;
            var protectedService = new ExportService(path =>
            {
                if (removals++ == 0)
                    foreach (string target in Directory.GetFiles(protectedOut, "*", SearchOption.AllDirectories))
                    {
                        try { File.Delete(target); } catch (IOException) { blocked++; }
                        try { File.WriteAllText(target, "forbidden"); } catch (IOException) { blocked++; }
                    }
                File.Delete(path);
            });
            var protectedResult = await protectedService.ExportAsync(protectedPlan, protectedOut, true);
            Check(blocked == 8 && removals == 2 && protectedResult.RemovedFileCount == 2 &&
                  protectedResult.Files.Count == 2 && protectedResult.Copies.Count == 4 &&
                  protectedResult.ApplyRemovedAssets(new[] { protectedPhoto }).Single() == protectedPhoto && protectedPhoto.Assets.Count == 0,
                "all four copies remain locked while each original is recycled exactly once and committed once");

            var partial = Pair("partial"); string partialRaw = partial.Assets.Single(asset => asset.Kind == PhotoAssetKind.Raw).CanonicalPath;
            var partialService = new ExportService(path =>
            { if (path == partialRaw) throw new IOException("Injected recycle failure."); File.Delete(path); });
            var partialResult = await partialService.ExportAsync(ExportService.CreatePlan(new[] { partial }, groupBySpecies: true), Output("partial"), true);
            partialResult.ApplyRemovedAssets(new[] { partial });
            Check(partialResult.VerifiedCopyCount == 4 && partialResult.RemovedFileCount == 1 && partial.Assets.Count == 1 &&
                  File.Exists(partialRaw) && partial.Rating == PhotoRating.Favorite,
                "partial source removal writes back only the actual removed member and preserves the remaining RAW and rating");

            var noSpecies = Pair("review-none", false);
            noSpecies.Birds.Add(new DetectedBird { Box = Box() });
            var low = Pair("review-low", false); low.Birds.Add(Auto("大山雀", "Parus major", .79));
            low.SetGeometryReference(low.GeometryReference, PhotoGeometryStatus.NeedsReview);
            var manualOnly = Pair("review-manual", false);
            manualOnly.Birds.Add(new DetectedBird { ManualSpecies = new("手动鸟种", "Manual species"), Species = new("旧AI", "Old species", .4, Confidence: .01) });
            var threshold = Pair("review-threshold", false); threshold.Birds.Add(Auto("大山雀", "Parus major", .8));
            var review = SpeciesExportReview.Review(new[] { noSpecies, low, manualOnly, threshold });
            Check(review.Count == 2 && review.Single(item => item.Photo == noSpecies).Reasons ==
                      (SpeciesExportReviewReason.NoSpecies | SpeciesExportReviewReason.UnidentifiedRegions) &&
                  review.Single(item => item.Photo == low).Reasons == SpeciesExportReviewReason.LowConfidence,
                "review combines missing-species reasons, flags automatic confidence below .8 and ignores manual or geometry-review status");
            var jpgOnly = new PhotoItem { FilePath = noSpecies.Assets.Single(asset => asset.Kind == PhotoAssetKind.Jpg).CanonicalPath };
            Check(SpeciesExportReview.Review(new[] { jpgOnly }, PhotoFileScope.RawOnly).Count == 0,
                "classification review ignores photos with no member in the selected file scope");
            bool missingRoot = false; string absent = Path.Combine(root, "never-create-root");
            try { await service.ExportAsync(ExportService.CreatePlan(new[] { photo }, groupBySpecies: true), absent, false); }
            catch (DirectoryNotFoundException) { missingRoot = true; }
            Check(missingRoot && !Directory.Exists(absent), "classification creates species children only after the user selects an existing root");
            return count;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe species export fixture cleanup.");
            Directory.Delete(resolved, true);
        }
    }
    private sealed class InlineProgress(Action<PhotoOperationProgress> action) : IProgress<PhotoOperationProgress>
    { public void Report(PhotoOperationProgress value) => action(value); }
}
