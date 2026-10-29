using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;

namespace BirdPhotoSelector;

public enum PhotoFileScope { All, JpgOnly, RawOnly }
public enum AssetFileOperationStatus { Pending, Verified, Removed, Skipped, Failed, Canceled }

public sealed record PlannedPhotoAsset(PhotoAsset Asset, long? Length, long? WriteTicks)
{
    public string Id => Asset.Id;
    public string FilePath => Asset.CanonicalPath;
    public PhotoAssetKind Kind => Asset.Kind;
}

public sealed class PhotoFilePlan
{
    internal PhotoFilePlan(PhotoItem photo, PlannedPhotoAsset[] all, PlannedPhotoAsset[] selected)
    { Photo = photo; PhotoId = photo.Id; AllAssets = Array.AsReadOnly(all); Assets = Array.AsReadOnly(selected); }
    public PhotoItem Photo { get; }
    public string PhotoId { get; }
    public IReadOnlyList<PlannedPhotoAsset> AllAssets { get; }
    public IReadOnlyList<PlannedPhotoAsset> Assets { get; }
}

/// <summary>Immutable members selected before confirmation; execution never discovers companion files.</summary>
public sealed class PhotoFileOperationPlan
{
    internal PhotoFileOperationPlan(PhotoFileScope scope, PhotoFilePlan[] photos)
    { Scope = scope; Photos = Array.AsReadOnly(photos); }
    public PhotoFileScope Scope { get; }
    public IReadOnlyList<PhotoFilePlan> Photos { get; }
    public int PhotoCount => Photos.Count;
    public int FileCount => Photos.Sum(photo => photo.Assets.Count);
}

public sealed class AssetFileOperationResult
{
    internal AssetFileOperationResult(string photoId, PlannedPhotoAsset asset)
    { PhotoId = photoId; AssetId = asset.Id; SourcePath = asset.FilePath; }
    public string PhotoId { get; }
    public string AssetId { get; }
    public string SourcePath { get; }
    public string? TargetPath { get; internal set; }
    public AssetFileOperationStatus Status { get; internal set; }
    public bool Copied { get; internal set; }
    public bool Verified { get; internal set; }
    public bool SourceRemoved { get; internal set; }
    public string Message { get; internal set; } = "尚未处理";
    internal byte[]? VerifiedHash { get; set; }
}

public sealed class FileOperationResult
{
    public List<PhotoItem> Succeeded { get; } = new();
    // Legacy count: every managed member of this logical photo was actually removed.
    public List<PhotoItem> SourceRemoved { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<AssetFileOperationResult> Files { get; } = new();
    public int CopiedFileCount => Files.Count(file => file.Verified);
    public int RemovedFileCount => Files.Count(file => file.SourceRemoved);
    public int SkippedFileCount => Files.Count(file => file.Status == AssetFileOperationStatus.Skipped);
    public int FailedFileCount => Files.Count(file => file.Status == AssetFileOperationStatus.Failed);
    public int CanceledFileCount => Files.Count(file => file.Status == AssetFileOperationStatus.Canceled);

    /// <summary>UI-thread commit: change only successfully removed members and return empty logical photos.</summary>
    public IReadOnlyList<PhotoItem> ApplyRemovedAssets(IEnumerable<PhotoItem> photos)
    {
        var current = photos.ToDictionary(photo => photo.Id, StringComparer.Ordinal);
        var empty = new List<PhotoItem>();
        foreach (var group in Files.Where(file => file.SourceRemoved).GroupBy(file => file.PhotoId))
        {
            if (!current.TryGetValue(group.Key, out var photo)) continue;
            foreach (var file in group)
            {
                var member = photo.Assets.FirstOrDefault(asset => asset.Id == file.AssetId);
                if (member is null) continue;
                if (!StringComparer.OrdinalIgnoreCase.Equals(member.CanonicalPath, file.SourcePath))
                    throw new InvalidOperationException("照片成员路径已变化，不能把旧处理结果应用到新的原件。");
                photo.RemoveAsset(member.Id);
            }
            if (photo.Assets.Count == 0) empty.Add(photo);
        }
        return empty;
    }
}

public sealed class ExportService
{
    private readonly Action<string> _remove;
    private readonly Func<Stream, Stream, CancellationToken, Task> _copy;
    // Overrides are solely for generated-fixture fault checks; production uses the Windows recycle bin.
    public ExportService(Action<string>? recycleSource = null,
        Func<Stream, Stream, CancellationToken, Task>? copyOverride = null)
    { _remove = recycleSource ?? Recycle; _copy = copyOverride ?? ((source, target, token) => source.CopyToAsync(target, token)); }

    public static PhotoFileOperationPlan CreatePlan(IEnumerable<PhotoItem> photos, PhotoFileScope scope = PhotoFileScope.All)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        var result = new List<PhotoFilePlan>();
        var photoIds = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
        return new(scope, result.ToArray());
    }

    public Task<FileOperationResult> ExportAsync(IEnumerable<PhotoItem> photos, string existingDestination, bool removeOriginal,
        CancellationToken cancellationToken = default, IProgress<PhotoOperationProgress>? progress = null) =>
        ExportAsync(CreatePlan(photos), existingDestination, removeOriginal, cancellationToken, progress);

    public Task<FileOperationResult> ExportAsync(PhotoFileOperationPlan plan, string existingDestination, bool removeOriginal,
        CancellationToken cancellationToken = default, IProgress<PhotoOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string destination = Path.GetFullPath(existingDestination);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("请选择已经存在的导出目录，程序不会自动新建文件夹。");
        return Execute(plan, export: true, destination, removeOriginal, cancellationToken, progress);
    }

    public Task<FileOperationResult> DeleteAsync(IEnumerable<PhotoItem> photos, CancellationToken cancellationToken = default,
        IProgress<PhotoOperationProgress>? progress = null) => DeleteAsync(CreatePlan(photos), cancellationToken, progress);
    public Task<FileOperationResult> DeleteAsync(PhotoFileOperationPlan plan, CancellationToken cancellationToken = default,
        IProgress<PhotoOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Execute(plan, export: false, null, removeOriginal: true, cancellationToken, progress);
    }

    private Task<FileOperationResult> Execute(PhotoFileOperationPlan plan, bool export, string? destination,
        bool removeOriginal, CancellationToken token, IProgress<PhotoOperationProgress>? progress) => Task.Run(async () =>
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
                    await CopyAndVerify(asset, file, destination!, result, () => Report(index, "正在校验"), token);
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
        return result;
    }, CancellationToken.None);

    private async Task CopyAndVerify(PlannedPhotoAsset asset, AssetFileOperationResult file, string destination,
        FileOperationResult result, Action reportVerification, CancellationToken token)
    {
        bool ownsTarget = false;
        try
        {
            file.TargetPath = Path.Combine(destination, Path.GetFileName(asset.FilePath));
            if (StringComparer.OrdinalIgnoreCase.Equals(asset.FilePath, file.TargetPath))
            { Skip(file, result, "源文件和目标文件相同。", "已跳过相同源文件和目标"); return; }
            if (File.Exists(file.TargetPath))
            { Skip(file, result, "目标目录已存在同名文件。", "已跳过目标同名文件"); return; }
            EnsureUnchanged(asset);
            await using (var source = new FileStream(asset.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(file.TargetPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ownsTarget = true;
                await _copy(source, output, token);
                await output.FlushAsync(token); output.Flush(flushToDisk: true);
                file.Copied = true;
                reportVerification();
                token.ThrowIfCancellationRequested();
                if (source.Length != output.Length) throw new IOException("复制后的文件大小校验失败，源文件已保留。");
                source.Position = output.Position = 0;
                var sourceHash = await SHA256.HashDataAsync(source, token);
                var targetHash = await SHA256.HashDataAsync(output, token);
                if (!CryptographicOperations.FixedTimeEquals(sourceHash, targetHash))
                    throw new IOException("复制后的文件内容校验失败，源文件已保留。");
                EnsureUnchanged(asset);
                file.VerifiedHash = sourceHash; file.Verified = true;
                file.Status = AssetFileOperationStatus.Verified; file.Message = "已导出并校验";
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { file.Status = AssetFileOperationStatus.Canceled; file.Message = "已取消导出"; }
        catch (Exception ex) { Fail(file, result, ex.Message, "导出失败"); }
        finally
        {
            if (ownsTarget && !file.Verified && file.TargetPath is not null)
            {
                try { File.Delete(file.TargetPath); file.Copied = false; }
                catch (Exception ex) { result.Errors.Add($"{file.TargetPath}：未完成的目标文件无法清理：{ex.Message}"); }
            }
        }
    }

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
    private static void Skip(AssetFileOperationResult file, FileOperationResult result, string detail, string stage)
    { file.Status = AssetFileOperationStatus.Skipped; file.Message = stage; result.Skipped.Add($"{file.SourcePath}：{detail}"); }
    private static void Fail(AssetFileOperationResult file, FileOperationResult result, string detail, string stage)
    { file.Status = AssetFileOperationStatus.Failed; file.Message = stage; result.Errors.Add($"{file.SourcePath}：{stage}：{detail}"); }
    private static void EnsureUnchanged(PlannedPhotoAsset asset)
    {
        var file = new FileInfo(asset.FilePath);
        if (!file.Exists) throw new FileNotFoundException("原文件不存在，未移除任何记录。", asset.FilePath);
        if (asset.Length is null || asset.WriteTicks is null || file.Length != asset.Length || file.LastWriteTimeUtc.Ticks != asset.WriteTicks)
            throw new IOException("原文件在确认后发生变化，已保留原件，请重新选择处理。");
    }
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
    private static void Recycle(string path) => FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
        RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
}
