using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace BirdPhotoSelector;

/// <summary>根据不可变文件计划执行复制、同名处理和原件移除；校验成功后才能移除原件，平台操作通过接口注入。</summary>
public sealed class ExportService
{
    private readonly IExportFilePlatform _platform;
    private readonly Action<string> _remove;
    private readonly Func<Stream, Stream, CancellationToken, Task> _copy;
    // 生成文件的故障验证可替换复制/移除步骤；生产默认使用宿主注入的系统文件能力。
    /// <summary>注入系统能力，复制与移除委托可用于故障验证；生产移除始终经宿主的回收站适配。</summary>
    public ExportService(IExportFilePlatform platform, Action<string>? recycleSource = null,
        Func<Stream, Stream, CancellationToken, Task>? copyOverride = null)
    {
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _remove = recycleSource ?? platform.RecycleOrDelete;
        _copy = copyOverride ?? ((source, target, token) => source.CopyToAsync(target, token));
    }

    /// <summary>在确认前快照照片、格式范围、文件版本和鸟种目标目录，拒绝重复成员或路径。</summary>
    public static PhotoFileOperationPlan CreatePlan(IEnumerable<PhotoItem> photos, PhotoFileScope scope = PhotoFileScope.All,
        bool groupBySpecies = false)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        var result = new List<PhotoFilePlan>();
        var photoIds = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(FilePaths.Comparer);
        foreach (var photo in photos)
        {
            ArgumentNullException.ThrowIfNull(photo);
            if (!photoIds.Add(photo.Id)) throw new InvalidDataException("待处理列表中有重复的逻辑照片 ID。");
            var all = photo.Assets.Select(asset =>
            {
                try
                {
                    var file = new FileInfo(asset.CanonicalPath);
                    return new PlannedPhotoAsset(asset, file.Exists ? file.Length : null, file.Exists ? file.LastWriteTimeUtc.Ticks : null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { return new PlannedPhotoAsset(asset, null, null); }
            }).ToArray();
            var selected = all.Where(asset => scope == PhotoFileScope.All ||
                (scope == PhotoFileScope.JpgOnly ? asset.Kind == PhotoAssetKind.Jpg : asset.Kind == PhotoAssetKind.Raw)).ToArray();
            foreach (var asset in all)
                if (!ids.Add(asset.Id) || !paths.Add(asset.FilePath))
                    throw new InvalidDataException("待处理文件有重复的成员 ID 或完整路径。");
            if (selected.Length > 0) result.Add(new(photo, all, selected));
        }
        if (groupBySpecies)
        {
            var folders = SpeciesExportFolders.Create(result.Select(item => item.Photo));
            result = result.Select(item => new PhotoFilePlan(item.Photo, item.AllAssets.ToArray(),
                item.Assets.ToArray(), folders[item.PhotoId])).ToList();
        }
        return new(scope, result.ToArray(), groupBySpecies);
    }

    /// <summary>只向已存在的目标根目录导出，按不可变计划执行，冲突通过异步纯数据回调交给宿主决定。</summary>
    public Task<FileOperationResult> ExportAsync(IEnumerable<PhotoItem> photos, string existingDestination, bool removeOriginal,
        CancellationToken cancellationToken = default, IProgress<PhotoOperationProgress>? progress = null,
        Func<ExportConflict, CancellationToken, Task<ExportConflictDecision>>? conflictResolver = null,
        IReadOnlyCollection<string>? protectedSourcePaths = null) =>
        ExportAsync(CreatePlan(photos), existingDestination, removeOriginal, cancellationToken, progress, conflictResolver, protectedSourcePaths);

    /// <summary>只向已存在的目标根目录导出，按不可变计划执行，冲突通过异步纯数据回调交给宿主决定。</summary>
    public async Task<FileOperationResult> ExportAsync(PhotoFileOperationPlan plan, string existingDestination, bool removeOriginal,
        CancellationToken cancellationToken = default, IProgress<PhotoOperationProgress>? progress = null,
        Func<ExportConflict, CancellationToken, Task<ExportConflictDecision>>? conflictResolver = null,
        IReadOnlyCollection<string>? protectedSourcePaths = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string destination = Path.GetFullPath(existingDestination);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("请选择已经存在的导出目录，程序不会自动新建文件夹。");
        using var session = new ConflictSession(plan, conflictResolver, cancellationToken, protectedSourcePaths, _platform);
        return plan.GroupBySpecies ? await ExecuteGrouped(plan, destination, removeOriginal, session.Token, progress, session)
            : await Execute(plan, export: true, destination, removeOriginal, session.Token, progress, session);
    }

    /// <summary>根据确认前的原件计划移除文件，按实际完成结果报告，不假设整个批次成功。</summary>
    public Task<FileOperationResult> DeleteAsync(IEnumerable<PhotoItem> photos, CancellationToken cancellationToken = default,
        IProgress<PhotoOperationProgress>? progress = null) => DeleteAsync(CreatePlan(photos), cancellationToken, progress);
    /// <summary>根据确认前的原件计划移除文件，按实际完成结果报告，不假设整个批次成功。</summary>
    public Task<FileOperationResult> DeleteAsync(PhotoFileOperationPlan plan, CancellationToken cancellationToken = default,
        IProgress<PhotoOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Execute(plan, export: false, null, removeOriginal: true, cancellationToken, progress);
    }

    /// <summary>逐逻辑照片处理原件，取消保留已完成结果；同张所需成员均校验后才允许移除来源。</summary>
    private Task<FileOperationResult> Execute(PhotoFileOperationPlan plan, bool export, string? destination,
        bool removeOriginal, CancellationToken token, IProgress<PhotoOperationProgress>? progress,
        ConflictSession? session = null) => Task.Run(async () =>
    {
        var result = new FileOperationResult();
        foreach (var photo in plan.Photos) foreach (var member in photo.Assets) result.Files.Add(new(photo.PhotoId, member));
        int completed = 0, offset = 0;
        for (int photoIndex = 0; photoIndex < plan.Photos.Count; photoIndex++)
        {
            var photo = plan.Photos[photoIndex];
            var files = result.Files.Skip(offset).Take(photo.Assets.Count).ToArray();
            if (token.IsCancellationRequested) break;
            void Report(int index, string stage, bool finished = false) => progress?.Report(new(completed,
                plan.FileCount, offset + index + 1, files[index].SourcePath, stage, finished, photoIndex + 1, plan.PhotoCount));
            for (int index = 0; index < files.Length; index++)
            {
                if (token.IsCancellationRequested) break;
                var file = files[index]; var asset = photo.Assets[index];
                Report(index, export ? "正在复制" : "正在删除");
                if (token.IsCancellationRequested)
                { file.Status = AssetFileOperationStatus.Canceled; file.Message = export ? "已取消导出" : "已取消删除"; break; }
                if (export)
                    await CopyAndVerify(asset, file, destination!, result, () => Report(index, "正在校验"), token, session!);
                else
                {
                    try
                    {
                        if (!File.Exists(asset.FilePath))
                        { Skip(file, result, "文件已经不存在。", "已跳过不存在的文件"); continue; }
                        EnsureUnchanged(asset);
                        RemoveMember(file, result, "已删除");
                    }
                    catch (Exception ex) { Fail(file, result, ex.Message, "删除失败"); }
                }
            }
            // Copy all selected members before removing any source belonging to this logical photo.
            if (export && removeOriginal && files.All(file => file.Verified) && !token.IsCancellationRequested)
            {
                bool valid = true;
                var protectedTargets = new List<FileStream>();
                try
                {
                    for (int index = 0; index < files.Length; index++)
                    {
                        // Lock every verified backup against modification/deletion until all
                        // source removals for this logical photo have finished.
                        Report(index, "正在确认目标副本");
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            var target = new FileStream(files[index].TargetPath!, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            protectedTargets.Add(target);
                            var hash = await SHA256.HashDataAsync(target, token);
                            if (target.Length != photo.Assets[index].Length || files[index].VerifiedHash is null ||
                                !CryptographicOperations.FixedTimeEquals(hash, files[index].VerifiedHash!))
                                throw new IOException("目标副本在复制后发生变化，已保留该张全部原件。");
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { valid = false; break; }
                        catch (Exception ex)
                        {
                            valid = false; files[index].Verified = false;
                            Fail(files[index], result, ex.Message, "目标副本不可用，原件保留");
                            break;
                        }
                    }
                    if (valid)
                        for (int index = 0; index < files.Length; index++)
                        {
                            try { await VerifySource(photo.Assets[index], files[index], token); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { valid = false; break; }
                            catch (Exception ex) { valid = false; Fail(files[index], result, ex.Message, "已导出，原件变化未移除"); }
                        }
                    if (valid)
                        for (int index = 0; index < files.Length; index++)
                        {
                            if (token.IsCancellationRequested) break;
                            Report(index, "正在移除原文件");
                            if (token.IsCancellationRequested) break;
                            try
                            {
                                await VerifySource(photo.Assets[index], files[index], token);
                                token.ThrowIfCancellationRequested();
                                RemoveMember(files[index], result, "已导出并移除原文件");
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                            catch (Exception ex)
                            { Fail(files[index], result, ex.Message, "已导出，原文件未移除"); break; }
                        }
                }
                finally { foreach (var target in protectedTargets) await target.DisposeAsync(); }
            }
            if (export && removeOriginal && token.IsCancellationRequested)
                foreach (var file in files.Where(file => file.Verified && !file.SourceRemoved && file.Status == AssetFileOperationStatus.Verified))
                { file.Status = AssetFileOperationStatus.Canceled; file.Message = "已导出，取消原件移除"; }
            if (export && removeOriginal && files.Any(file => !file.Verified))
                foreach (var file in files.Where(file => file.Status == AssetFileOperationStatus.Verified))
                    file.Message = "已导出，同张另有未完成成员，原件保留";
            else if (export && removeOriginal && files.Any(file => file.Status == AssetFileOperationStatus.Failed))
                foreach (var file in files.Where(file => file.Status == AssetFileOperationStatus.Verified && !file.SourceRemoved))
                    file.Message = "已导出，同张原件移除未完成，原件保留";
            if (files.All(file => export ? file.Verified : file.SourceRemoved)) result.Succeeded.Add(photo.Photo);
            for (int index = 0; index < files.Length; index++)
            {
                var file = files[index];
                if (file.Status == AssetFileOperationStatus.Pending) continue;
                bool finished = file.Status != AssetFileOperationStatus.Canceled;
                if (finished) completed++;
                Report(index, file.Message, finished);
            }
            if (token.IsCancellationRequested && files.All(file => file.Status != AssetFileOperationStatus.Canceled))
            {
                int next = Array.FindIndex(files, file => file.Status == AssetFileOperationStatus.Pending);
                if (next >= 0) Report(next, export ? "已取消导出" : "已取消删除");
            }
            offset += files.Length;
        }
        foreach (var file in result.Files.Where(file => file.Status == AssetFileOperationStatus.Pending))
        { file.Status = AssetFileOperationStatus.Canceled; file.Message = "已取消，尚未处理"; }
        foreach (var photo in plan.Photos)
            if (photo.AllAssets.All(asset => result.Files.Any(file =>
                file.PhotoId == photo.PhotoId && file.AssetId == asset.Id && file.SourceRemoved)))
                result.SourceRemoved.Add(photo.Photo);
        if (export)
        {
            var assets = plan.Photos.SelectMany(photo => photo.Assets).ToDictionary(asset => asset.Id);
            foreach (var file in result.Files)
                result.Copies.Add(new(file.PhotoId, assets[file.AssetId], "")
                {
                    TargetPath = file.TargetPath, Status = file.Verified ? AssetFileOperationStatus.Verified : file.Status,
                    Copied = file.Copied, Verified = file.Verified, VerifiedHash = file.VerifiedHash, Message = file.Message
                });
        }
        return result;
    }, CancellationToken.None);

    /// <summary>按每个有效鸟种复制原件，整张照片的全部成员和全部目标通过校验后才移除任何来源。</summary>
    private Task<FileOperationResult> ExecuteGrouped(PhotoFileOperationPlan plan, string destination,
        bool removeOriginal, CancellationToken token, IProgress<PhotoOperationProgress>? progress,
        ConflictSession session) => Task.Run(async () =>
    {
        var result = new FileOperationResult();
        foreach (var photo in plan.Photos)
            foreach (var asset in photo.Assets)
            {
                result.Files.Add(new(photo.PhotoId, asset));
                foreach (string folder in photo.TargetFolders) result.Copies.Add(new(photo.PhotoId, asset, folder));
            }
        int completed = 0, copyOffset = 0;
        for (int photoIndex = 0; photoIndex < plan.Photos.Count; photoIndex++)
        {
            if (token.IsCancellationRequested) break;
            var photo = plan.Photos[photoIndex];
            var files = result.Files.Where(file => file.PhotoId == photo.PhotoId).ToArray();
            var copies = result.Copies.Where(copy => copy.PhotoId == photo.PhotoId).ToArray();
            void Report(int index, string stage, bool finished = false) => progress?.Report(new(completed,
                plan.CopyCount, copyOffset + index + 1, copies[index].SourcePath, stage, finished, photoIndex + 1, plan.PhotoCount));
            for (int index = 0; index < copies.Length; index++)
            {
                var copy = copies[index];
                if (token.IsCancellationRequested) break;
                Report(index, "正在复制");
                if (token.IsCancellationRequested) break;
                try
                {
                    string folder = SpeciesExportFolders.EnsureDestination(destination, copy.TargetFolder);
                    await CopyAndVerify(copy.PlannedAsset, copy, folder, result, () => Report(index, "正在校验"), token, session);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                { copy.Status = AssetFileOperationStatus.Canceled; copy.Message = "已取消导出"; }
                catch (Exception ex) { Fail(copy, result, ex.Message, "分类目录不可用"); }
            }
            foreach (var copy in copies.Where(copy => copy.Status == AssetFileOperationStatus.Pending))
            { copy.Status = AssetFileOperationStatus.Canceled; copy.Message = "已取消，尚未处理"; }
            foreach (var file in files) AggregateCopies(file, copies.Where(copy => copy.AssetId == file.AssetId).ToArray(), result);

            // All originals x all species folders form one photo-level removal barrier.
            // Keep every verified destination locked until each unique original is handled.
            if (removeOriginal && files.All(file => file.Verified) && !token.IsCancellationRequested)
            {
                var protectedTargets = new List<FileStream>();
                bool valid = true;
                try
                {
                    for (int index = 0; index < copies.Length; index++)
                    {
                        var copy = copies[index];
                        Report(index, "正在确认目标副本");
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            var target = new FileStream(copy.TargetPath!, FileMode.Open, FileAccess.Read,
                                FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
                            protectedTargets.Add(target);
                            var hash = await SHA256.HashDataAsync(target, token);
                            if (target.Length != copy.PlannedAsset.Length || copy.VerifiedHash is null ||
                                !CryptographicOperations.FixedTimeEquals(hash, copy.VerifiedHash))
                                throw new IOException("目标副本在复制后发生变化，已保留该张全部原件。");
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { valid = false; break; }
                        catch (Exception ex)
                        {
                            valid = false; copy.Verified = false;
                            Fail(copy, result, ex.Message, "目标副本不可用，原件保留");
                            AggregateCopies(files.Single(file => file.AssetId == copy.AssetId),
                                copies.Where(item => item.AssetId == copy.AssetId).ToArray(), result);
                            break;
                        }
                    }
                    if (valid)
                        foreach (var file in files)
                        {
                            try { await VerifySource(photo.Assets.Single(asset => asset.Id == file.AssetId), file, token); }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { valid = false; break; }
                            catch (Exception ex) { valid = false; Fail(file, result, ex.Message, "已导出，原件变化未移除"); }
                        }
                    if (valid)
                        foreach (var file in files)
                        {
                            if (token.IsCancellationRequested) break;
                            int index = Array.FindIndex(copies, copy => copy.AssetId == file.AssetId);
                            Report(index, "正在移除原文件");
                            if (token.IsCancellationRequested) break;
                            try
                            {
                                await VerifySource(photo.Assets.Single(asset => asset.Id == file.AssetId), file, token);
                                token.ThrowIfCancellationRequested();
                                RemoveMember(file, result, "已导出并移除原文件");
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                            catch (Exception ex) { Fail(file, result, ex.Message, "已导出，原文件未移除"); break; }
                        }
                }
                finally { foreach (var target in protectedTargets) await target.DisposeAsync(); }
            }
            if (removeOriginal && token.IsCancellationRequested)
                foreach (var file in files.Where(file => file.Verified && !file.SourceRemoved && file.Status == AssetFileOperationStatus.Verified))
                { file.Status = AssetFileOperationStatus.Canceled; file.Message = "已导出，取消原件移除"; }
            if (removeOriginal && files.Any(file => !file.SourceRemoved))
                foreach (var file in files.Where(file => file.Status == AssetFileOperationStatus.Verified))
                    file.Message = "已导出，同张另有未完成副本或移除未完成，原件保留";
            if (files.All(file => file.Verified)) result.Succeeded.Add(photo.Photo);
            for (int index = 0; index < copies.Length; index++)
            {
                var copy = copies[index];
                bool finished = copy.Status != AssetFileOperationStatus.Canceled;
                if (finished) completed++;
                var source = files.Single(file => file.AssetId == copy.AssetId);
                Report(index, source.SourceRemoved ? "已导出并移除原文件" : copy.Message, finished);
            }
            copyOffset += copies.Length;
        }
        foreach (var copy in result.Copies.Where(copy => copy.Status == AssetFileOperationStatus.Pending))
        { copy.Status = AssetFileOperationStatus.Canceled; copy.Message = "已取消，尚未处理"; }
        foreach (var file in result.Files.Where(file => file.Status == AssetFileOperationStatus.Pending))
        { file.Status = AssetFileOperationStatus.Canceled; file.Message = "已取消，尚未处理"; }
        foreach (var photo in plan.Photos)
            if (photo.AllAssets.All(asset => result.Files.Any(file =>
                file.PhotoId == photo.PhotoId && file.AssetId == asset.Id && file.SourceRemoved)))
                result.SourceRemoved.Add(photo.Photo);
        return result;
    }, CancellationToken.None);

    /// <summary>汇总同一原件多个目标副本的验证状态，任一跳过或失败都不能解锁来源移除。</summary>
    private static void AggregateCopies(AssetFileOperationResult file, AssetCopyOperationResult[] copies, FileOperationResult result)
    {
        file.TargetPath = copies.FirstOrDefault()?.TargetPath;
        file.Copied = copies.Any(copy => copy.Copied);
        file.Verified = copies.Length > 0 && copies.All(copy => copy.Verified);
        if (file.Verified)
        {
            file.VerifiedHash = copies[0].VerifiedHash;
            if (file.VerifiedHash is null || copies.Any(copy => copy.VerifiedHash is null ||
                !CryptographicOperations.FixedTimeEquals(file.VerifiedHash, copy.VerifiedHash)))
            {
                file.Verified = false;
                Fail(file, result, "各目标副本对应的原件内容不一致，原件保留。", "原件在复制期间变化");
                return;
            }
            file.Status = AssetFileOperationStatus.Verified; file.Message = "全部目标副本已导出并校验";
            return;
        }
        file.Status = copies.Any(copy => copy.Status == AssetFileOperationStatus.Failed) ? AssetFileOperationStatus.Failed :
            copies.Any(copy => copy.Status == AssetFileOperationStatus.Canceled) ? AssetFileOperationStatus.Canceled :
            copies.Any(copy => copy.Status == AssetFileOperationStatus.Skipped) ? AssetFileOperationStatus.Skipped : AssetFileOperationStatus.Pending;
        file.Message = "存在未完成目标副本，该张原件保留";
    }

    /// <summary>先比较同名内容；覆盖使用临时副本、SHA-256 和实体身份复核，保留原目标及来源安全。</summary>
    private async Task CopyAndVerify(PlannedPhotoAsset asset, AssetFileOperationResult file, string destination,
        FileOperationResult result, Action reportVerification, CancellationToken token, ConflictSession session)
    {
        string? temporary = null;
        try
        {
            token.ThrowIfCancellationRequested();
            session.InitializeProtectedSources();
            file.TargetPath = Path.Combine(destination, Path.GetFileName(asset.FilePath));
            if (FilePaths.Comparer.Equals(asset.FilePath, file.TargetPath))
            { Skip(file, result, "源文件和目标文件相同。", "已跳过相同源文件和目标"); return; }
            EnsureUnchanged(asset);
            byte[]? expectedSourceHash = null;
            TargetChoice? choice = new(file.TargetPath, null, false);

            async Task<TargetChoice?> ResolveConflict(string target)
            {
                token.ThrowIfCancellationRequested();
                EnsureUnchanged(asset);
                var existing = await TargetSnapshot.Read(target, token, _platform);
                expectedSourceHash ??= (await TargetSnapshot.Read(asset.FilePath, token, _platform)).Hash;
                if (CryptographicOperations.FixedTimeEquals(existing.Hash, expectedSourceHash))
                { Skip(file, result, "目标文件内容与原件的 SHA256 一致；跳过且保留原件。", "内容相同，已跳过"); return null; }
                bool canOverwrite = session.CanOverwrite(target, existing.Identity) &&
                    (File.GetAttributes(target) & FileAttributes.ReparsePoint) == 0;
                string? explanation = canOverwrite ? null : "该目标属于原件、链接或本次导出的已校验副本，只能重命名或取消。";
                var decision = await session.Resolve(new(asset.FilePath, target, asset.Length!.Value,
                    existing.Length, canOverwrite, explanation));
                if (decision is null)
                { Skip(file, result, "目标同名但内容不同，未提供冲突处理选择。", "已跳过目标同名文件"); return null; }
                if (decision.Action == ExportConflictAction.Cancel)
                { session.Cancel(); token.ThrowIfCancellationRequested(); }
                if (decision.Action is not (ExportConflictAction.Overwrite or ExportConflictAction.Rename))
                    throw new InvalidDataException("不支持的导出冲突选择。");
                if (decision.Action == ExportConflictAction.Overwrite && !canOverwrite)
                { Skip(file, result, "目标属于受保护的原件或副本，不能覆盖。", "已保留受保护目标"); return null; }
                EnsureUnchanged(asset);
                await existing.EnsureStillMatches(target, token, _platform);
                return decision.Action == ExportConflictAction.Rename ? new(target, null, true) : new(target, existing, false);
            }

            if (Directory.Exists(file.TargetPath))
            { Skip(file, result, "目标同名项是目录，不能作为照片覆盖。", "已跳过同名目录"); return; }
            if (File.Exists(file.TargetPath))
            {
                choice = await ResolveConflict(file.TargetPath);
                if (choice is null) return;
            }

            // The final target is never opened for writing. Interrupted copies and
            // failed verification affect only a uniquely owned same-directory file.
            temporary = Path.Combine(destination, ".BirdPhotoSelector-" + Guid.NewGuid().ToString("N") + ".tmp");
            FileIdentity? stagedIdentity;
            await using (var source = new FileStream(asset.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await _copy(source, output, token);
                await output.FlushAsync(token); output.Flush(flushToDisk: true);
                reportVerification(); token.ThrowIfCancellationRequested();
                if (source.Length != output.Length) throw new IOException("复制后的文件大小校验失败，源文件已保留。");
                source.Position = output.Position = 0;
                var sourceHash = await SHA256.HashDataAsync(source, token);
                var targetHash = await SHA256.HashDataAsync(output, token);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
                    throw new IOException("复制后的文件内容校验失败，源文件已保留。");
                if (expectedSourceHash is not null && !CryptographicOperations.FixedTimeEquals(sourceHash, expectedSourceHash))
                    throw new IOException("原件内容在冲突确认期间变化，已保留原件和原目标。请重新导出。");
                expectedSourceHash = sourceHash;
                EnsureUnchanged(asset);
                stagedIdentity = _platform.TryGetIdentity(output);
            }

            string stagedPath = temporary;
            while (choice is not null)
            {
                token.ThrowIfCancellationRequested();
                EnsureUnchanged(asset);
                string target = choice.Rename ? NextAvailableName(choice.Path, token) : choice.Path;
                file.TargetPath = target;
                if (choice.Existing is not null)
                {
                    await choice.Existing.EnsureStillMatches(target, token, _platform);
                    if (!session.CanOverwrite(target, choice.Existing.Identity) ||
                        (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("目标已变成受保护的文件，已保留原件和原目标。");
                    token.ThrowIfCancellationRequested();
                    // ReplaceFile is an atomic same-volume directory-entry swap;
                    // keep a recovery copy until the swap has actually succeeded.
                    string backup = stagedPath + ".previous";
                    try { File.Replace(stagedPath, target, backup); }
                    catch
                    {
                        if (File.Exists(backup))
                            result.Errors.Add($"{target}：原目标的恢复副本保留在 {backup}；请核对后再重试。");
                        throw;
                    }
                    temporary = null;
                    MarkPublished(file, expectedSourceHash!, session, target, stagedIdentity, overwritten: true);
                    try { File.Delete(backup); }
                    catch (Exception ex) { result.Errors.Add($"{backup}：覆盖已完成，旧目标恢复副本未能清理：{ex.Message}"); }
                    break;
                }
                try
                {
                    File.Move(stagedPath, target, overwrite: false);
                    temporary = null;
                    MarkPublished(file, expectedSourceHash!, session, target, stagedIdentity, overwritten: false);
                    break;
                }
                catch (IOException) when (File.Exists(target))
                {
                    // Another process may create a target while the temporary
                    // copy is being verified. Never turn CreateNew into overwrite.
                    if (choice.Rename) continue;
                    choice = await ResolveConflict(target);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { file.Status = AssetFileOperationStatus.Canceled; file.Message = "已取消导出"; }
        catch (Exception ex) { Fail(file, result, ex.Message, "导出失败"); }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (Exception ex) { result.Errors.Add($"{temporary}：未完成的临时文件无法清理：{ex.Message}"); }
            }
        }
    }

    /// <summary>登记本次已发布目标的路径与实体身份，禁止后续目标覆盖刚导出的文件。</summary>
    private static void MarkPublished(AssetFileOperationResult file, byte[] hash, ConflictSession session,
        string target, FileIdentity? identity, bool overwritten)
    {
        session.RegisterPublished(target, identity);
        file.Copied = file.Verified = true; file.VerifiedHash = hash;
        file.Status = AssetFileOperationStatus.Verified;
        file.Message = overwritten ? "已覆盖并校验" : "已导出并校验";
    }

    /// <summary>在末尾 _n 基础上递增后缀，跳过占用的文件或目录，不覆盖已有内容。</summary>
    private static string NextAvailableName(string original, CancellationToken token)
    {
        string directory = Path.GetDirectoryName(original)!;
        string stem = Path.GetFileNameWithoutExtension(original), extension = Path.GetExtension(original);
        var match = Regex.Match(stem, @"^(.*)_([0-9]+)$", RegexOptions.CultureInvariant);
        long number = 0;
        if (match.Success && long.TryParse(match.Groups[2].Value, out number)) stem = match.Groups[1].Value;
        else number = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            string candidate = Path.Combine(directory, stem + "_" + checked(++number) + extension);
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>一次目标决策，区分新文件、已占用目标及重命名路径。</summary>
    private sealed record TargetChoice(string Path, TargetSnapshot? Existing, bool Rename);

    /// <summary>覆盖前的目标实体身份、版本和哈希快照，供异步确认后重新核对。</summary>
    private sealed record TargetSnapshot(FileIdentity? Identity, long Length, long WriteTicks, byte[] Hash)
    {
        /// <summary>从打开句柄读取实体身份、尺寸、时间和内容哈希，用于确认后再次核对目标。</summary>
        internal static async Task<TargetSnapshot> Read(string path, CancellationToken token, IExportFilePlatform platform)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var identity = platform.TryGetIdentity(stream);
            long length = stream.Length, ticks = File.GetLastWriteTimeUtc(path).Ticks;
            return new(identity, length, ticks, await SHA256.HashDataAsync(stream, token));
        }

        /// <summary>冲突确认或复制后重读目标，任一身份、版本或哈希变化都中止覆盖。</summary>
        internal async Task EnsureStillMatches(string path, CancellationToken token, IExportFilePlatform platform)
        {
            var current = await Read(path, token, platform);
            if (current.Identity != Identity || current.Length != Length || current.WriteTicks != WriteTicks ||
                !CryptographicOperations.FixedTimeEquals(current.Hash, Hash))
                throw new IOException("目标文件在冲突确认或复制期间变化，已保留原件和当前目标。请重新导出。");
        }
    }

    private sealed class ConflictSession : IDisposable
    {
        private readonly IExportFilePlatform _platform;
        private readonly CancellationTokenSource _cancellation;
        private readonly Func<ExportConflict, CancellationToken, Task<ExportConflictDecision>>? _resolver;
        private readonly HashSet<string> _originalPaths;
        private readonly HashSet<FileIdentity> _originalIdentities = new();
        private readonly HashSet<string> _publishedPaths = new(FilePaths.Comparer);
        private readonly HashSet<FileIdentity> _publishedIdentities = new();
        private ExportConflictDecision? _remembered;
        private bool _initialized, _unidentifiedOriginal;

        /// <summary>为单次导出保存取消、冲突决定及全部受保护来源，策略不会泄露到下一批次。</summary>
        internal ConflictSession(PhotoFileOperationPlan plan,
            Func<ExportConflict, CancellationToken, Task<ExportConflictDecision>>? resolver,
            CancellationToken token, IReadOnlyCollection<string>? protectedSourcePaths, IExportFilePlatform platform)
        {
            _platform = platform;
            _resolver = resolver; _cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            _originalPaths = new(plan.Photos.SelectMany(photo => photo.AllAssets).Select(asset => asset.FilePath)
                .Concat(protectedSourcePaths ?? Array.Empty<string>()).Select(Path.GetFullPath), FilePaths.Comparer);
        }

        internal CancellationToken Token => _cancellation.Token;
        /// <summary>取消整个导出批次，已完成的真实结果仍由调用方报告。</summary>
        internal void Cancel() => _cancellation.Cancel();

        /// <summary>从平台读取工程原件实体身份，任何未知身份都会保守禁用覆盖。</summary>
        internal void InitializeProtectedSources(bool refresh = false)
        {
            if (_initialized && !refresh) return;
            foreach (string path in _originalPaths)
            {
                Token.ThrowIfCancellationRequested();
                if (!File.Exists(path)) continue;
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (_platform.TryGetIdentity(stream) is { } identity) _originalIdentities.Add(identity);
                    else _unidentifiedOriginal = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { _unidentifiedOriginal = true; }
            }
            _initialized = true;
        }

        /// <summary>在决定覆盖前重新核对所有来源，保护原件、其硬链接及本次已经导出的目标。</summary>
        internal bool CanOverwrite(string target, FileIdentity? identity)
        {
            // Re-read identity after a prompt: another application may have
            // replaced an original while preserving its path and timestamps.
            InitializeProtectedSources(refresh: true);
            return identity is { } id && !_unidentifiedOriginal &&
                !_originalPaths.Contains(Path.GetFullPath(target)) && !_originalIdentities.Contains(id) &&
                !_publishedPaths.Contains(Path.GetFullPath(target)) && !_publishedIdentities.Contains(id);
        }

        /// <summary>记录本次输出，后续不能将其当作普通旧目标覆盖。</summary>
        internal void RegisterPublished(string target, FileIdentity? identity)
        { _publishedPaths.Add(Path.GetFullPath(target)); if (identity is { } id) _publishedIdentities.Add(id); }

        /// <summary>复用本批次已记住的合法选择，否则调用宿主异步确认；取消必须向上传播。</summary>
        internal async Task<ExportConflictDecision?> Resolve(ExportConflict conflict)
        {
            if (_remembered is not null && (_remembered.Action != ExportConflictAction.Overwrite || conflict.CanOverwrite))
                return _remembered;
            if (_resolver is null) return null;
            ExportConflictDecision decision;
            try { decision = await _resolver(conflict, Token).WaitAsync(Token); }
            catch (OperationCanceledException) { Cancel(); throw; }
            Token.ThrowIfCancellationRequested();
            if (decision.ApplyToRemaining && decision.Action is ExportConflictAction.Overwrite or ExportConflictAction.Rename)
                _remembered = decision;
            return decision;
        }

        /// <summary>释放当前批次独有的取消源，不修改其他导出会话。</summary>
        public void Dispose() => _cancellation.Dispose();
    }

    /// <summary>仅调用注入的移除能力；处理 shell 已移走文件后仍报错的情况，按实际存在性记录结果。</summary>
    private void RemoveMember(AssetFileOperationResult file, FileOperationResult result, string message)
    {
        try { _remove(file.SourcePath); }
        catch (Exception ex)
        {
            if (File.Exists(file.SourcePath)) throw;
            // Some shell operations can report an error after the file has already moved.
            result.Errors.Add($"{file.SourcePath}：原文件已移除，但回收站操作报告：{ex.Message}");
        }
        if (File.Exists(file.SourcePath)) throw new IOException("原文件仍存在，未记为已移除。");
        file.SourceRemoved = true; file.Status = AssetFileOperationStatus.Removed; file.Message = message;
    }
    /// <summary>记录保守跳过原因，不把已存在或未校验目标算作成功副本。</summary>
    private static void Skip(AssetFileOperationResult file, FileOperationResult result, string detail, string stage)
    { file.Status = AssetFileOperationStatus.Skipped; file.Message = stage; result.Skipped.Add($"{file.SourcePath}：{detail}"); }
    /// <summary>记录逐成员失败并保留可恢复的原件状态。</summary>
    private static void Fail(AssetFileOperationResult file, FileOperationResult result, string detail, string stage)
    { file.Status = AssetFileOperationStatus.Failed; file.Message = stage; result.Errors.Add($"{file.SourcePath}：{stage}：{detail}"); }
    /// <summary>比较确认快照与当前原件长度和修改时间，来源变化时拒绝移除。</summary>
    private static void EnsureUnchanged(PlannedPhotoAsset asset)
    {
        var file = new FileInfo(asset.FilePath);
        if (!file.Exists) throw new FileNotFoundException("原文件不存在，未移除任何记录。", asset.FilePath);
        if (asset.Length is null || asset.WriteTicks is null || file.Length != asset.Length || file.LastWriteTimeUtc.Ticks != asset.WriteTicks)
            throw new IOException("原文件在确认后发生变化，已保留原件，请重新选择处理。");
    }
    /// <summary>重新计算来源哈希与已验证副本比较，校验前后都检查来源版本。</summary>
    private static async Task VerifySource(PlannedPhotoAsset asset, AssetFileOperationResult result, CancellationToken token)
    {
        EnsureUnchanged(asset);
        await using var stream = new FileStream(asset.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            131072, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, token);
        if (result.VerifiedHash is null || !CryptographicOperations.FixedTimeEquals(hash, result.VerifiedHash))
            throw new IOException("原文件内容已改变，已保留原件及已校验副本。");
        EnsureUnchanged(asset);
    }
}
