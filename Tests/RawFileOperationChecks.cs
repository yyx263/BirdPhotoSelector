using System.IO;
using System.Security.Cryptography;
using BirdPhotoSelector;

internal static class RawFileOperationChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string text)
        {
            if (!condition) throw new InvalidOperationException(text);
            count++; Console.WriteLine("  OK RAW FILES " + text);
        }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.RawFileOperationChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            PhotoItem Pair(string name)
            {
                string folder = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                string jpg = Path.Combine(folder, "scene.JPG"), raw = Path.Combine(folder, "scene.NEF");
                File.WriteAllText(jpg, "Generated JPG-kind original for file operations only.");
                File.WriteAllText(raw, "Generated opaque RAW original; no decoder or user photos involved.");
                var photo = new PhotoItem { Id = name, Rating = PhotoRating.Favorite };
                photo.SetAssets(new[] { new PhotoAsset(jpg, PhotoAssetKind.Jpg), new PhotoAsset(raw, PhotoAssetKind.Raw) });
                photo.PixelWidth = 6000; photo.PixelHeight = 4000;
                photo.SetBirds(new[] { new DetectedBird { Number = 7, IsManual = true,
                    Box = new() { X = .1, Y = .2, Width = .2, Height = .3, Confidence = .9 },
                    Species = new("大山雀", "Parus major", .42) } }, nextBirdNumber: 31);
                return photo;
            }
            string Destination(string name) => Directory.CreateDirectory(Path.Combine(root, "out-" + name)).FullName;
            string PathOf(PhotoItem photo, PhotoAssetKind kind) => photo.Assets.Single(asset => asset.Kind == kind).CanonicalPath;
            bool BothExist(PhotoItem photo) => photo.Assets.All(asset => File.Exists(asset.CanonicalPath));
            var service = new ExportService(recycleSource: File.Delete); // Generated random fixtures only.

            var scopes = Pair("scopes");
            Check(ExportService.CreatePlan(new[] { scopes }).FileCount == 2 &&
                  ExportService.CreatePlan(new[] { scopes }, PhotoFileScope.JpgOnly).FileCount == 1 &&
                  ExportService.CreatePlan(new[] { scopes }, PhotoFileScope.RawOnly).FileCount == 1,
                "all, JPG-only and RAW-only scopes count actual managed members");
            var jpgOnly = new PhotoItem { FilePath = PathOf(scopes, PhotoAssetKind.Jpg) };
            Check(ExportService.CreatePlan(new[] { jpgOnly }, PhotoFileScope.RawOnly) is { FileCount: 0, PhotoCount: 0 },
                "a scope with no matching member does not count a nonexistent file or logical photo");

            var copied = Pair("copy-all"); string allOut = Destination("all");
            var copy = await service.ExportAsync(ExportService.CreatePlan(new[] { copied }), allOut, false);
            Check(copy.CopiedFileCount == 2 && copy.Succeeded.Count == 1 && copy.RemovedFileCount == 0 && BothExist(copied) &&
                  copy.Files.All(file => file.Verified && File.ReadAllBytes(file.SourcePath).SequenceEqual(File.ReadAllBytes(file.TargetPath!))),
                "copying a pair verifies both complete originals byte-for-byte without removing either source");
            foreach (var scope in new[] { PhotoFileScope.JpgOnly, PhotoFileScope.RawOnly })
            {
                string destination = Destination(scope.ToString());
                var result = await service.ExportAsync(ExportService.CreatePlan(new[] { copied }, scope), destination, false);
                Check(result.CopiedFileCount == 1 && Directory.EnumerateFiles(destination).Count() == 1 && BothExist(copied),
                    scope + " copies exactly its selected member and preserves both originals");
            }
            var same = await service.ExportAsync(ExportService.CreatePlan(new[] { copied }),
                Path.GetDirectoryName(copied.FilePath)!, true);
            Check(same.SkippedFileCount == 2 && same.RemovedFileCount == 0 && BothExist(copied),
                "identical source/destination never removes either member");

            var collisionPhoto = Pair("collision"); string collisionOut = Destination("collision");
            string sentinel = Path.Combine(collisionOut, "scene.NEF"); File.WriteAllText(sentinel, "Existing sentinel");
            var collision = await service.ExportAsync(ExportService.CreatePlan(new[] { collisionPhoto }), collisionOut, true);
            Check(collision.CopiedFileCount == 1 && collision.SkippedFileCount == 1 && collision.RemovedFileCount == 0 &&
                  collision.Succeeded.Count == 0 && BothExist(collisionPhoto) && File.ReadAllText(sentinel) == "Existing sentinel",
                "one RAW collision preserves every source in the pair and never overwrites the target");
            Check(File.Exists(Path.Combine(collisionOut, "scene.JPG")) && collision.Files.Single(file => file.Verified).Message.Contains("原件保留"),
                "a verified partial copy remains explicit while associated originals are retained");

            var faultPhoto = Pair("copy-fault"); string faultOut = Destination("copy-fault"); int copies = 0;
            var faultService = new ExportService(File.Delete, async (source, output, token) =>
            {
                if (++copies == 2) { await output.WriteAsync(new byte[] { 1, 2, 3 }, token); throw new IOException("Injected partial copy failure."); }
                await source.CopyToAsync(output, token);
            });
            var fault = await faultService.ExportAsync(ExportService.CreatePlan(new[] { faultPhoto }), faultOut, true);
            Check(fault.CopiedFileCount == 1 && fault.FailedFileCount == 1 && fault.RemovedFileCount == 0 &&
                  BothExist(faultPhoto) && File.Exists(Path.Combine(faultOut, "scene.JPG")) && !File.Exists(Path.Combine(faultOut, "scene.NEF")),
                "a partial second copy is cleaned while its verified sibling and both originals are preserved");

            var hashPhoto = Pair("bad-hash"); string hashOut = Destination("bad-hash");
            var hashService = new ExportService(File.Delete, async (source, output, token) =>
            {
                var bytes = new byte[checked((int)source.Length)]; await source.ReadExactlyAsync(bytes, token);
                bytes[0] ^= 1; await output.WriteAsync(bytes, token);
            });
            var badHash = await hashService.ExportAsync(ExportService.CreatePlan(new[] { hashPhoto }), hashOut, true);
            Check(badHash.FailedFileCount == 2 && badHash.CopiedFileCount == 0 && badHash.RemovedFileCount == 0 &&
                  BothExist(hashPhoto) && !Directory.EnumerateFiles(hashOut).Any(),
                "equal-length but corrupted copies fail SHA-256 checks and are cleaned without deleting originals");

            var cancelPhoto = Pair("cancel-verify"); string cancelOut = Destination("cancel-verify");
            using (var cancellation = new CancellationTokenSource())
            {
                var progress = new List<PhotoOperationProgress>();
                var canceled = await service.ExportAsync(ExportService.CreatePlan(new[] { cancelPhoto }), cancelOut, true,
                    cancellation.Token, new InlineProgress(item => { progress.Add(item); if (item.Stage == "正在校验") cancellation.Cancel(); }));
                Check(canceled.RemovedFileCount == 0 && canceled.CopiedFileCount == 0 && BothExist(cancelPhoto) &&
                      !Directory.EnumerateFiles(cancelOut).Any() &&
                      progress.Last() is { Completed: 0, FileCompleted: false, Stage: "已取消导出" },
                    "canceling during verification removes the unfinished target and keeps all originals");
            }
            var secondCancelPhoto = Pair("cancel-second"); string secondCancelOut = Destination("cancel-second");
            using (var cancellation = new CancellationTokenSource())
            {
                var canceled = await service.ExportAsync(ExportService.CreatePlan(new[] { secondCancelPhoto }), secondCancelOut, true,
                    cancellation.Token, new InlineProgress(item =>
                    { if (item.Stage == "正在复制" && item.FileName.EndsWith(".NEF", StringComparison.OrdinalIgnoreCase)) cancellation.Cancel(); }));
                Check(canceled.CopiedFileCount == 1 && canceled.RemovedFileCount == 0 && BothExist(secondCancelPhoto) &&
                      Directory.EnumerateFiles(secondCancelOut).Count() == 1,
                    "canceling after one verified copy retains that copy and every original in the pair");
            }

            var jpgRemoval = Pair("remove-jpg"); var originalBird = jpgRemoval.SelectedBird;
            string remainingRaw = PathOf(jpgRemoval, PhotoAssetKind.Raw);
            var jpgResult = await service.DeleteAsync(ExportService.CreatePlan(new[] { jpgRemoval }, PhotoFileScope.JpgOnly));
            Check(jpgResult.RemovedFileCount == 1 && jpgResult.SourceRemoved.Count == 0 && jpgRemoval.Assets.Count == 2 &&
                  File.Exists(remainingRaw), "JPG-only deletion returns detached member results and does not remove the surviving logical photo");
            var emptyAfterJpg = jpgResult.ApplyRemovedAssets(new[] { jpgRemoval });
            Check(emptyAfterJpg.Count == 0 && jpgRemoval.Assets.Count == 1 && jpgRemoval.ActivePreviewAsset?.Kind == PhotoAssetKind.Raw &&
                  jpgRemoval.GeometryStatus == PhotoGeometryStatus.NeedsReview && jpgRemoval.Thumbnail is null &&
                  ReferenceEquals(jpgRemoval.SelectedBird, originalBird) && jpgRemoval.Rating == PhotoRating.Favorite &&
                  jpgRemoval.NextBirdNumber == 31 && jpgRemoval.Bird is null,
                "applying JPG removal switches to RAW lazily and keeps species, rating and numbering with old geometry under review");

            var rawRemoval = Pair("remove-raw"); var reference = rawRemoval.GeometryReference;
            var rawResult = await service.ExportAsync(ExportService.CreatePlan(new[] { rawRemoval }, PhotoFileScope.RawOnly), Destination("remove-raw"), true);
            Check(rawResult.CopiedFileCount == 1 && rawResult.RemovedFileCount == 1 && rawResult.SourceRemoved.Count == 0 &&
                  rawResult.ApplyRemovedAssets(new[] { rawRemoval }).Count == 0 && rawRemoval.Assets.Count == 1 &&
                  rawRemoval.ActivePreviewAsset?.Kind == PhotoAssetKind.Jpg && rawRemoval.CanUseBirdGeometry &&
                  ReferenceEquals(rawRemoval.GeometryReference, reference),
                "RAW-only export/removal leaves the JPG and its confirmed geometry intact");

            var allRemoval = Pair("remove-all");
            var allRemoved = await service.ExportAsync(ExportService.CreatePlan(new[] { allRemoval }), Destination("remove-all"), true);
            Check(allRemoved.CopiedFileCount == 2 && allRemoved.RemovedFileCount == 2 && allRemoved.SourceRemoved.Count == 1 &&
                  allRemoval.Assets.Count == 2 && allRemoved.ApplyRemovedAssets(new[] { allRemoval }).Single() == allRemoval &&
                  allRemoval.Assets.Count == 0, "only a logical photo with every member successfully removed is returned for removal from the session");

            var partial = Pair("partial-delete"); string partialRaw = PathOf(partial, PhotoAssetKind.Raw);
            var partialService = new ExportService(path => { if (path == partialRaw) throw new IOException("Injected recycling failure."); File.Delete(path); });
            var partialResult = await partialService.DeleteAsync(ExportService.CreatePlan(new[] { partial }));
            partialResult.ApplyRemovedAssets(new[] { partial });
            Check(partialResult.RemovedFileCount == 1 && partialResult.FailedFileCount == 1 && partialResult.SourceRemoved.Count == 0 &&
                  partial.Assets.Count == 1 && File.Exists(partialRaw) && partial.ActivePreviewAsset?.Kind == PhotoAssetKind.Raw,
                "partial recycle failure updates only the actually removed member and preserves the surviving RAW record");

            var changed = Pair("changed-source"); string changedJpg = PathOf(changed, PhotoAssetKind.Jpg);
            var changedPlan = ExportService.CreatePlan(new[] { changed });
            File.AppendAllText(changedJpg, "External change after confirmation.");
            var changedResult = await service.DeleteAsync(changedPlan);
            Check(changedResult.FailedFileCount == 1 && File.Exists(changedJpg) && changedResult.RemovedFileCount == 1,
                "direct deletion refuses a member changed since confirmation while accurately reporting its sibling outcome");

            var lateChange = Pair("late-change"); string lateJpg = PathOf(lateChange, PhotoAssetKind.Jpg);
            string lateOut = Destination("late-change"); bool altered = false;
            var lateResult = await service.ExportAsync(ExportService.CreatePlan(new[] { lateChange }), lateOut, true,
                progress: new InlineProgress(item =>
                { if (!altered && item.Stage == "正在移除原文件") { File.AppendAllText(lateJpg, "Changed after verified copy."); altered = true; } }));
            Check(altered && BothExist(lateChange) && lateResult.Files.Single(file => file.SourcePath == lateJpg) is { Verified: true, SourceRemoved: false } &&
                  lateResult.FailedFileCount == 1,
                "source mutation after copying is rechecked before removal and never deletes changed contents");

            foreach (bool missingTarget in new[] { true, false })
            {
                var targetPhoto = Pair(missingTarget ? "target-removed" : "target-changed");
                string targetOut = Destination(missingTarget ? "target-removed" : "target-changed");
                bool tampered = false;
                var targetResult = await service.ExportAsync(ExportService.CreatePlan(new[] { targetPhoto }), targetOut, true,
                    progress: new InlineProgress(item =>
                    {
                        if (tampered || item.Stage != "正在确认目标副本") return;
                        tampered = true;
                        string target = Path.Combine(targetOut, "scene.JPG");
                        if (missingTarget) File.Delete(target); else File.AppendAllText(target, "External target mutation.");
                    }));
                Check(tampered && targetResult.RemovedFileCount == 0 && targetResult.FailedFileCount == 1 && BothExist(targetPhoto),
                    (missingTarget ? "a disappeared backup" : "a modified backup") + " aborts all source removal for its logical photo");
            }

            var lockedTargets = Pair("protected-backups"); string protectedOut = Destination("protected-backups");
            var expectedBytes = lockedTargets.Assets.ToDictionary(asset => Path.GetFileName(asset.CanonicalPath),
                asset => File.ReadAllBytes(asset.CanonicalPath));
            int blocked = 0; bool attempted = false;
            var lockedResult = await service.ExportAsync(ExportService.CreatePlan(new[] { lockedTargets }), protectedOut, true,
                progress: new InlineProgress(item =>
                {
                    if (attempted || item.Stage != "正在移除原文件") return;
                    attempted = true;
                    foreach (string name in expectedBytes.Keys)
                    {
                        string target = Path.Combine(protectedOut, name);
                        try { File.Delete(target); } catch (IOException) { blocked++; }
                        try { File.WriteAllText(target, "Blocked mutation"); } catch (IOException) { blocked++; }
                    }
                }));
            Check(blocked == 4 && lockedResult.RemovedFileCount == 2 && expectedBytes.All(entry =>
                      File.ReadAllBytes(Path.Combine(protectedOut, entry.Key)).SequenceEqual(entry.Value)),
                "all verified backups stay read-locked against writes and deletion through both source removals");

            var missing = Pair("missing"); string missingRaw = PathOf(missing, PhotoAssetKind.Raw); File.Delete(missingRaw);
            var missingResult = await service.DeleteAsync(ExportService.CreatePlan(new[] { missing }, PhotoFileScope.RawOnly));
            Check(missingResult.SkippedFileCount == 1 && missingResult.RemovedFileCount == 0 &&
                  missingResult.ApplyRemovedAssets(new[] { missing }).Count == 0 && missing.Assets.Count == 2,
                "an externally missing original is reported without pretending it was deleted or dropping its audit record");

            var fixedPhoto = Pair("fixed-plan"); var fixedPlan = ExportService.CreatePlan(new[] { fixedPhoto }, PhotoFileScope.JpgOnly);
            string untracked = Path.ChangeExtension(PathOf(fixedPhoto, PhotoAssetKind.Jpg), ".CR3"); File.WriteAllText(untracked, "Unmanaged companion.");
            var fixedResult = await service.DeleteAsync(fixedPlan);
            Check(fixedResult.Files.Count == 1 && fixedResult.RemovedFileCount == 1 && File.Exists(untracked) &&
                  File.Exists(PathOf(fixedPhoto, PhotoAssetKind.Raw)), "execution uses the confirmed member list and never discovers new companion originals");

            var progressPhoto = Pair("progress"); var events = new List<PhotoOperationProgress>();
            var progressResult = await service.ExportAsync(ExportService.CreatePlan(new[] { progressPhoto }), Destination("progress"), false,
                progress: new InlineProgress(events.Add));
            Check(progressResult.CopiedFileCount == 2 && events.All(item => item.PhotoIndex == 1 && item.PhotoTotal == 1 &&
                      item.Total == 2 && item.Message.Contains("照片 1 / 1") && item.Message.Contains("文件")) &&
                  events.Where(item => item.FileCompleted).Select(item => item.Completed).SequenceEqual(new[] { 1, 2 }),
                "progress distinguishes logical photo and actual file counts through copy and verification");

            bool noDirectory = false;
            try { await service.ExportAsync(ExportService.CreatePlan(new[] { progressPhoto }), Path.Combine(root, "absent-target"), false); }
            catch (DirectoryNotFoundException) { noDirectory = true; }
            Check(noDirectory && !Directory.Exists(Path.Combine(root, "absent-target")), "export still refuses to create a missing destination directory");

            var postError = Pair("post-shell-error");
            var postErrorResult = await new ExportService(path => { File.Delete(path); throw new IOException("Shell reported after moving."); })
                .DeleteAsync(ExportService.CreatePlan(new[] { postError }, PhotoFileScope.JpgOnly));
            Check(postErrorResult.RemovedFileCount == 1 && postErrorResult.Errors.Count == 1,
                "a shell diagnostic after an actual removal keeps membership accounting aligned with the filesystem");
            return count;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe file-operation fixture cleanup.");
            Directory.Delete(resolved, true);
        }
    }
    private sealed class InlineProgress(Action<PhotoOperationProgress> action) : IProgress<PhotoOperationProgress>
    { public void Report(PhotoOperationProgress value) => action(value); }
}
