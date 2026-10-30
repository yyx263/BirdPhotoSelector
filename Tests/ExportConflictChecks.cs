using System.IO;
using System.Runtime.InteropServices;
using BirdPhotoSelector;

internal static class ExportConflictChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); count++; Console.WriteLine("  OK EXPORT CONFLICT " + message); }
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.ExportConflictChecks", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string Folder(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
            PhotoItem Photo(string label, string filename = "bird.JPG", string content = "new generated original")
            {
                string path = Path.Combine(Folder("source-" + label), filename);
                File.WriteAllText(path, content);
                return new PhotoItem { FilePath = path, Rating = PhotoRating.Favorite };
            }
            string Put(string directory, string filename, string content = "old generated target")
            { string path = Path.Combine(directory, filename); File.WriteAllText(path, content); return path; }
            bool Clean(string directory) => !Directory.EnumerateFiles(directory, ".BirdPhotoSelector-*", SearchOption.AllDirectories).Any();
            Task<ExportConflictDecision> Overwrite(ExportConflict _, CancellationToken __) => Task.FromResult(new ExportConflictDecision(ExportConflictAction.Overwrite));
            Task<ExportConflictDecision> Rename(ExportConflict _, CancellationToken __) => Task.FromResult(new ExportConflictDecision(ExportConflictAction.Rename));
            var service = new ExportService(File.Delete); // Random generated fixtures only; no user files/recycle dialogs.

            var identical = Photo("identical"); string identicalOut = Folder("identical-out");
            File.Copy(identical.FilePath, Path.Combine(identicalOut, identical.FileName));
            int identicalPrompts = 0;
            var same = await service.ExportAsync(new[] { identical }, identicalOut, true, conflictResolver: (conflict, token) =>
            { identicalPrompts++; return Overwrite(conflict, token); });
            Check(identicalPrompts == 0 && same.SkippedFileCount == 1 && same.VerifiedCopyCount == 0 &&
                  same.RemovedFileCount == 0 && File.Exists(identical.FilePath) && same.Files.Single().Message.Contains("内容相同"),
                "SHA-identical targets skip without prompting, copying, or removing the original");

            var noResolver = Photo("no-resolver", content: "AAAA"); string noResolverOut = Folder("no-resolver-out");
            string noResolverTarget = Put(noResolverOut, noResolver.FileName, "BBBB");
            var conservative = await service.ExportAsync(new[] { noResolver }, noResolverOut, true);
            Check(conservative.SkippedFileCount == 1 && conservative.Errors.Count == 0 &&
                  File.ReadAllText(noResolverTarget) == "BBBB" && File.ReadAllText(noResolver.FilePath) == "AAAA",
                "equal lengths with different SHA hashes remain untouched when no resolver is supplied");

            var replaced = Photo("overwrite"); string replaceOut = Folder("overwrite-out");
            string replaceTarget = Put(replaceOut, replaced.FileName); ExportConflict? observed = null;
            var overwritten = await service.ExportAsync(new[] { replaced }, replaceOut, false, conflictResolver: (conflict, token) =>
            { observed = conflict; return Overwrite(conflict, token); });
            Check(observed is { CanOverwrite: true } && observed.SourcePath == replaced.FilePath && observed.TargetPath == replaceTarget &&
                  overwritten.VerifiedCopyCount == 1 && overwritten.CopiedFileCount == 1 &&
                  File.ReadAllText(replaceTarget) == File.ReadAllText(replaced.FilePath) && Clean(replaceOut),
                "an explicit overwrite publishes a verified copy and removes its temporary and recovery files");
            var moved = Photo("overwrite-remove"); string movedText = File.ReadAllText(moved.FilePath);
            string moveOut = Folder("overwrite-remove-out"); string moveTarget = Put(moveOut, moved.FileName);
            var movedResult = await service.ExportAsync(new[] { moved }, moveOut, true, conflictResolver: Overwrite);
            Check(movedResult.RemovedFileCount == 1 && !File.Exists(moved.FilePath) && File.ReadAllText(moveTarget) == movedText,
                "source removal is allowed only after overwrite verification and the existing final backup barrier");

            var renamed = Photo("rename"); string renameOut = Folder("rename-out");
            string oldCaseTarget = Put(renameOut, "BIRD.jpg", "keep case-insensitive target");
            Put(renameOut, "bird_1.JPG"); Put(renameOut, "BIRD_2.jpg");
            var renamedResult = await service.ExportAsync(new[] { renamed }, renameOut, false, conflictResolver: Rename);
            Check(renamedResult.VerifiedCopyCount == 1 && Path.GetFileName(renamedResult.Files.Single().TargetPath) == "bird_3.JPG" &&
                  File.ReadAllText(oldCaseTarget) == "keep case-insensitive target" && File.Exists(renamed.FilePath),
                "rename respects Windows case-insensitive collisions and finds the next free stem suffix");
            var suffixed = Photo("already-suffixed", "bird_1.JPG"); string suffixOut = Folder("suffix-out");
            Put(suffixOut, "bird_1.JPG"); Put(suffixOut, "bird_2.JPG");
            var suffixResult = await service.ExportAsync(new[] { suffixed }, suffixOut, false, conflictResolver: Rename);
            Check(Path.GetFileName(suffixResult.Files.Single().TargetPath) == "bird_3.JPG" && !File.Exists(Path.Combine(suffixOut, "bird_1_1.JPG")),
                "a name already ending in _1 continues at _2 instead of appending _1_1");

            var first = Photo("ask-first", "one.JPG"); var second = Photo("ask-second", "two.JPG");
            string askOut = Folder("ask-out"); Put(askOut, first.FileName); string askSecond = Put(askOut, second.FileName);
            int asked = 0;
            var askResult = await service.ExportAsync(new[] { first, second }, askOut, false, conflictResolver: (_, _) =>
                Task.FromResult(new ExportConflictDecision(++asked == 1 ? ExportConflictAction.Overwrite : ExportConflictAction.Rename)));
            Check(asked == 2 && askResult.VerifiedCopyCount == 2 && File.ReadAllText(askSecond) == "old generated target" &&
                  File.Exists(Path.Combine(askOut, "two_1.JPG")),
                "without remember each different-content conflict gets its own independent decision");
            string rememberOut = Folder("remember-out"); Put(rememberOut, first.FileName); Put(rememberOut, second.FileName);
            int rememberedPrompts = 0;
            var rememberResult = await service.ExportAsync(new[] { first, second }, rememberOut, false, conflictResolver: (_, _) =>
            { rememberedPrompts++; return Task.FromResult(new ExportConflictDecision(ExportConflictAction.Overwrite, true)); });
            Check(rememberedPrompts == 1 && rememberResult.VerifiedCopyCount == 2, "remember applies the chosen action within this export session");
            string laterOut = Folder("later-out"); Put(laterOut, first.FileName);
            await service.ExportAsync(new[] { first }, laterOut, false, conflictResolver: (conflict, token) =>
            { rememberedPrompts++; return Rename(conflict, token); });
            Check(rememberedPrompts == 2 && File.Exists(Path.Combine(laterOut, "one_1.JPG")), "remember never leaks into the next ExportAsync call");

            int samePathPrompts = 0;
            var samePath = await service.ExportAsync(new[] { first }, Path.GetDirectoryName(first.FilePath)!, true, conflictResolver: (conflict, token) =>
            { samePathPrompts++; return Overwrite(conflict, token); });
            Check(samePathPrompts == 0 && samePath.SkippedFileCount == 1 && File.Exists(first.FilePath), "same-path export always skips without overwriting or deleting the source");

            var protectedSource = Photo("protected-source", "scene.JPG", "copy me"); string originalOut = Folder("original-out");
            string protectedTarget = Put(originalOut, "scene.JPG", "another original");
            var otherOriginal = new PhotoItem { FilePath = protectedTarget };
            bool blockedOriginal = false;
            var originalPlanResult = await service.ExportAsync(ExportService.CreatePlan(new[] { protectedSource, otherOriginal }), originalOut, false,
                conflictResolver: (conflict, token) => { blockedOriginal = !conflict.CanOverwrite; return Rename(conflict, token); });
            Check(blockedOriginal && File.ReadAllText(protectedTarget) == "another original" &&
                  originalPlanResult.VerifiedCopyCount == 1 && originalPlanResult.SkippedFileCount == 1,
                "a target matching another planned original permits rename but cannot be overwritten");

            var outside = Photo("outside-plan"); string outsideOut = Folder("outside-out");
            string outsideTarget = Put(outsideOut, outside.FileName, "unselected project original");
            bool blockedOutside = false;
            var outsideResult = await service.ExportAsync(new[] { outside }, outsideOut, false,
                conflictResolver: (conflict, token) => { blockedOutside = !conflict.CanOverwrite; return Rename(conflict, token); },
                protectedSourcePaths: new[] { outsideTarget });
            Check(blockedOutside && outsideResult.VerifiedCopyCount == 1 && File.ReadAllText(outsideTarget) == "unselected project original",
                "caller-supplied original paths protect files outside the selected export plan");

            var hardSource = Photo("hard-source"); string hardOut = Folder("hard-out");
            string otherPath = Put(Folder("hard-protected"), "original.NEF", "hard-linked protected original");
            string hardTarget = Path.Combine(hardOut, hardSource.FileName);
            Check(CreateHardLink(hardTarget, otherPath, IntPtr.Zero), "generated NTFS hard-link fixture is created without touching user data");
            bool blockedLink = false;
            var hardResult = await service.ExportAsync(new[] { hardSource }, hardOut, false,
                conflictResolver: (conflict, token) => { blockedLink = !conflict.CanOverwrite; return Rename(conflict, token); },
                protectedSourcePaths: new[] { otherPath });
            Check(blockedLink && hardResult.VerifiedCopyCount == 1 && File.ReadAllText(otherPath) == "hard-linked protected original" &&
                  File.ReadAllText(hardTarget) == "hard-linked protected original", "file identity protects a hard-link alias of another project original");
            string sameLinkOut = Folder("same-link-out"); string sameLink = Path.Combine(sameLinkOut, hardSource.FileName);
            Check(CreateHardLink(sameLink, hardSource.FilePath, IntPtr.Zero), "a hard-link alias of the current source is created");
            var sameLinkResult = await service.ExportAsync(new[] { hardSource }, sameLinkOut, true, conflictResolver: (_, _) => throw new InvalidOperationException("Identical alias must not prompt"));
            Check(sameLinkResult.SkippedFileCount == 1 && sameLinkResult.RemovedFileCount == 0 && File.Exists(hardSource.FilePath),
                "identical source hard links skip and never trigger original removal");

            var targetChange = Photo("target-change", content: "CCCC"); string targetChangeOut = Folder("target-change-out");
            string changedTarget = Put(targetChangeOut, targetChange.FileName, "AAAA"); DateTime originalTargetTime = File.GetLastWriteTimeUtc(changedTarget);
            var targetChangedResult = await service.ExportAsync(new[] { targetChange }, targetChangeOut, true, conflictResolver: (_, _) =>
            {
                File.WriteAllText(changedTarget, "BBBB"); File.SetLastWriteTimeUtc(changedTarget, originalTargetTime);
                return Task.FromResult(new ExportConflictDecision(ExportConflictAction.Overwrite));
            });
            Check(targetChangedResult.FailedFileCount == 1 && targetChangedResult.VerifiedCopyCount == 0 &&
                  File.ReadAllText(changedTarget) == "BBBB" && File.ReadAllText(targetChange.FilePath) == "CCCC" && Clean(targetChangeOut),
                "target content changed during the prompt is detected even with restored length and timestamp");

            var sourceChange = Photo("source-change", content: "AAAA"); string sourceChangeOut = Folder("source-change-out");
            string sourceChangeTarget = Put(sourceChangeOut, sourceChange.FileName, "BBBB"); DateTime sourceTime = File.GetLastWriteTimeUtc(sourceChange.FilePath);
            var sourceChangedResult = await service.ExportAsync(new[] { sourceChange }, sourceChangeOut, true, conflictResolver: (_, _) =>
            {
                File.WriteAllText(sourceChange.FilePath, "CCCC"); File.SetLastWriteTimeUtc(sourceChange.FilePath, sourceTime);
                return Task.FromResult(new ExportConflictDecision(ExportConflictAction.Overwrite));
            });
            Check(sourceChangedResult.FailedFileCount == 1 && sourceChangedResult.VerifiedCopyCount == 0 && sourceChangedResult.RemovedFileCount == 0 &&
                  File.ReadAllText(sourceChangeTarget) == "BBBB" && File.ReadAllText(sourceChange.FilePath) == "CCCC" && Clean(sourceChangeOut),
                "same-size source edits during the prompt fail verification without replacing the previous target");

            var identityChange = Photo("identity-change"); string identityOut = Folder("identity-out");
            string identityTarget = Put(identityOut, identityChange.FileName, "AAAA");
            string substitute = Put(identityOut, "replacement.tmp", "AAAA"); DateTime targetTime = File.GetLastWriteTimeUtc(identityTarget);
            var identityResult = await service.ExportAsync(new[] { identityChange }, identityOut, true, conflictResolver: (_, _) =>
            {
                File.Move(substitute, identityTarget, true); File.SetLastWriteTimeUtc(identityTarget, targetTime);
                return Task.FromResult(new ExportConflictDecision(ExportConflictAction.Overwrite));
            });
            Check(identityResult.FailedFileCount == 1 && identityResult.RemovedFileCount == 0 &&
                  File.ReadAllText(identityTarget) == "AAAA" && File.Exists(identityChange.FilePath),
                "replacing the target identity during a prompt is detected even when its bytes and timestamp match");

            foreach (bool corrupt in new[] { false, true })
            {
                var faultPhoto = Photo("copy-fault-" + corrupt); string faultOut = Folder("copy-fault-out-" + corrupt);
                string faultTarget = Put(faultOut, faultPhoto.FileName);
                var faulty = new ExportService(File.Delete, async (source, output, token) =>
                {
                    if (!corrupt) { await output.WriteAsync(new byte[] { 1 }, token); throw new IOException("Injected interrupted copy"); }
                    var bytes = new byte[checked((int)source.Length)]; await source.ReadExactlyAsync(bytes, token);
                    bytes[0] ^= 1; await output.WriteAsync(bytes, token);
                });
                var faultResult = await faulty.ExportAsync(new[] { faultPhoto }, faultOut, true, conflictResolver: Overwrite);
                Check(faultResult.FailedFileCount == 1 && faultResult.VerifiedCopyCount == 0 && File.Exists(faultPhoto.FilePath) &&
                      File.ReadAllText(faultTarget) == "old generated target" && Clean(faultOut),
                    corrupt ? "a corrupt temporary copy cannot replace the old target" : "a failed temporary copy cannot replace the old target");
            }

            var locked = Photo("locked"); string lockedOut = Folder("locked-out"); string lockedTarget = Put(lockedOut, locked.FileName);
            using (var targetLock = new FileStream(lockedTarget, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var lockedResult = await service.ExportAsync(new[] { locked }, lockedOut, true, conflictResolver: Overwrite);
                Check(lockedResult.FailedFileCount == 1 && lockedResult.VerifiedCopyCount == 0 && File.Exists(locked.FilePath) && Clean(lockedOut),
                    "a target locked against replacement produces a safe failure and cleans only the temporary copy");
            }
            Check(File.ReadAllText(lockedTarget) == "old generated target", "atomic replacement failure leaves the existing destination bytes intact");

            var canceled = Photo("cancel-copy"); string canceledOut = Folder("cancel-copy-out"); string canceledTarget = Put(canceledOut, canceled.FileName);
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelService = new ExportService(File.Delete, async (_, output, token) =>
                { await output.WriteAsync(new byte[] { 1 }, token); cancellation.Cancel(); token.ThrowIfCancellationRequested(); });
                var canceledResult = await cancelService.ExportAsync(new[] { canceled }, canceledOut, true, cancellation.Token, conflictResolver: Overwrite);
                Check(canceledResult.CanceledFileCount == 1 && File.Exists(canceled.FilePath) && File.ReadAllText(canceledTarget) == "old generated target" && Clean(canceledOut),
                    "cancellation while copying keeps the old target and all originals");
            }
            var cancelFirst = Photo("cancel-first", "first.JPG"); var cancelSecond = Photo("cancel-second", "second.JPG");
            string cancelSessionOut = Folder("cancel-session-out"); Put(cancelSessionOut, cancelFirst.FileName); Put(cancelSessionOut, cancelSecond.FileName);
            int cancelPrompts = 0;
            var cancelSessionResult = await service.ExportAsync(new[] { cancelFirst, cancelSecond }, cancelSessionOut, true, conflictResolver: (_, _) =>
            { cancelPrompts++; return Task.FromResult(new ExportConflictDecision(ExportConflictAction.Cancel)); });
            Check(cancelPrompts == 1 && cancelSessionResult.CanceledFileCount == 2 && cancelSessionResult.RemovedFileCount == 0 &&
                  File.Exists(cancelFirst.FilePath) && File.Exists(cancelSecond.FilePath), "canceling a conflict ends the entire export rather than continuing to the next original");

            var late = Photo("late"); string lateOut = Folder("late-out"); string lateTarget = Path.Combine(lateOut, late.FileName); int latePrompts = 0;
            var lateService = new ExportService(File.Delete, async (source, output, token) =>
            { File.WriteAllText(lateTarget, "appeared during copy"); await source.CopyToAsync(output, token); });
            var lateResult = await lateService.ExportAsync(new[] { late }, lateOut, false, conflictResolver: (conflict, token) =>
            { latePrompts++; return Rename(conflict, token); });
            Check(latePrompts == 1 && lateResult.VerifiedCopyCount == 1 && File.ReadAllText(lateTarget) == "appeared during copy" &&
                  Path.GetFileName(lateResult.Files.Single().TargetPath) == "bird_1.JPG" && Clean(lateOut),
                "a target appearing after preflight is resolved safely using the already-verified temporary copy");

            var pair = Photo("pair"); string pairRaw = Path.ChangeExtension(pair.FilePath, ".NEF"); File.WriteAllText(pairRaw, "opaque generated RAW");
            pair.AddAsset(new PhotoAsset(pairRaw, PhotoAssetKind.Raw));
            string pairOut = Folder("pair-out"); File.Copy(pair.FilePath, Path.Combine(pairOut, pair.FileName));
            var pairResult = await service.ExportAsync(new[] { pair }, pairOut, true);
            Check(pairResult.SkippedFileCount == 1 && pairResult.VerifiedCopyCount == 1 && pairResult.RemovedFileCount == 0 &&
                  File.Exists(pair.FilePath) && File.Exists(pairRaw), "one identical skipped JPG keeps both JPG and RAW originals behind the photo-level barrier");

            pair.SetBirds(new[] { new DetectedBird { ManualSpecies = new("大山雀", "Parus major") },
                new DetectedBird { ManualSpecies = new("麻雀", "Passer montanus") } });
            var groupedPlan = ExportService.CreatePlan(new[] { pair }, groupBySpecies: true); string groupedOut = Folder("grouped-out");
            foreach (string folder in groupedPlan.Photos.Single().TargetFolders)
                foreach (var member in pair.Assets) Put(Directory.CreateDirectory(Path.Combine(groupedOut, folder)).FullName, Path.GetFileName(member.CanonicalPath));
            int groupedPrompts = 0;
            var groupedResult = await service.ExportAsync(groupedPlan, groupedOut, false, conflictResolver: (_, _) =>
            { groupedPrompts++; return Task.FromResult(new ExportConflictDecision(ExportConflictAction.Rename, true)); });
            Check(groupedPrompts == 1 && groupedResult.VerifiedCopyCount == 4 && groupedResult.CopiedFileCount == 2 &&
                  groupedResult.Copies.All(copy => copy.TargetPath is not null &&
                      Path.GetFileNameWithoutExtension(copy.TargetPath).EndsWith("_1", StringComparison.Ordinal)) && Clean(groupedOut),
                "remembered renaming handles every species destination for both JPG and RAW while counting originals once");

            string partialOut = Folder("grouped-partial-out");
            string identicalFolder = Directory.CreateDirectory(Path.Combine(partialOut, groupedPlan.Photos.Single().TargetFolders[0])).FullName;
            File.Copy(pair.FilePath, Path.Combine(identicalFolder, pair.FileName));
            var partialResult = await service.ExportAsync(groupedPlan, partialOut, true);
            Check(partialResult.VerifiedCopyCount == 3 && partialResult.SkippedFileCount == 1 && partialResult.RemovedFileCount == 0 &&
                  pair.Assets.All(asset => File.Exists(asset.CanonicalPath)), "one identical species copy prevents removal of every original despite three successful destination copies");

            var earlier = Photo("published-earlier", "same.JPG", "earlier original");
            var later = Photo("published-later", "same.JPG", "later original"); string publishedOut = Folder("published-out"); bool protectedPublished = false;
            var publishedResult = await service.ExportAsync(new[] { earlier, later }, publishedOut, true, conflictResolver: (conflict, token) =>
            { protectedPublished = !conflict.CanOverwrite; return Rename(conflict, token); });
            Check(protectedPublished && publishedResult.RemovedFileCount == 2 &&
                  File.ReadAllText(Path.Combine(publishedOut, "same.JPG")) == "earlier original" &&
                  File.ReadAllText(Path.Combine(publishedOut, "same_1.JPG")) == "later original",
                "later conflicts cannot overwrite this session's only verified backup of an already-removed original");

            var rememberA = Photo("remember-protection-a", "a.JPG"); var rememberB = Photo("remember-protection-b", "b.JPG");
            string rememberProtectionOut = Folder("remember-protection-out"); Put(rememberProtectionOut, rememberA.FileName);
            string rememberProtected = Put(rememberProtectionOut, rememberB.FileName, "protected outside original"); int protectedPrompts = 0;
            var protectedRememberResult = await service.ExportAsync(new[] { rememberA, rememberB }, rememberProtectionOut, false, conflictResolver: (conflict, token) =>
            {
                protectedPrompts++;
                return Task.FromResult(new ExportConflictDecision(conflict.CanOverwrite ? ExportConflictAction.Overwrite : ExportConflictAction.Rename, true));
            }, protectedSourcePaths: new[] { rememberProtected });
            Check(protectedPrompts == 2 && protectedRememberResult.VerifiedCopyCount == 2 && File.ReadAllText(rememberProtected) == "protected outside original",
                "remembered overwrite cannot bypass source protection and asks again for a safe rename");
            return count;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.ExportConflictChecks")) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, recursive: true);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
