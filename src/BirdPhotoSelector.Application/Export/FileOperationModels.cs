namespace BirdPhotoSelector;

/// <summary>导出或移除时的原件格式范围，不改变逻辑照片的评级。</summary>
public enum PhotoFileScope { All, JpgOnly, RawOnly }
/// <summary>单个原件的实际处理状态，用于报告部分成功和取消结果。</summary>
public enum AssetFileOperationStatus { Pending, Verified, Removed, Skipped, Failed, Canceled }

/// <summary>确认前固定的原件及文件版本快照，执行阶段不能再发现新的关联文件。</summary>
public sealed record PlannedPhotoAsset(PhotoAsset Asset, long? Length, long? WriteTicks)
{
    public string Id => Asset.Id;
    public string FilePath => Asset.CanonicalPath;
    public PhotoAssetKind Kind => Asset.Kind;
}

/// <summary>一张逻辑照片的全部成员、选中成员和目标分类目录，记录只读快照。</summary>
public sealed class PhotoFilePlan
{
    /// <summary>固定单张照片的原件与目录快照，不暴露可修改计划数组。</summary>
    internal PhotoFilePlan(PhotoItem photo, PlannedPhotoAsset[] all, PlannedPhotoAsset[] selected, string[]? targetFolders = null)
    { Photo = photo; PhotoId = photo.Id; AllAssets = Array.AsReadOnly(all); Assets = Array.AsReadOnly(selected);
      TargetFolders = Array.AsReadOnly(targetFolders ?? new[] { "" }); }
    public PhotoItem Photo { get; }
    public string PhotoId { get; }
    public IReadOnlyList<PlannedPhotoAsset> AllAssets { get; }
    public IReadOnlyList<PlannedPhotoAsset> Assets { get; }
    public IReadOnlyList<string> TargetFolders { get; }
}

/// <summary>Immutable members selected before confirmation; execution never discovers companion files.</summary>
public sealed class PhotoFileOperationPlan
{
    /// <summary>固定整批照片、原件格式范围和分类模式，执行阶段不再扩展输入。</summary>
    internal PhotoFileOperationPlan(PhotoFileScope scope, PhotoFilePlan[] photos, bool groupBySpecies = false)
    { Scope = scope; Photos = Array.AsReadOnly(photos); GroupBySpecies = groupBySpecies; }
    public PhotoFileScope Scope { get; }
    public IReadOnlyList<PhotoFilePlan> Photos { get; }
    public int PhotoCount => Photos.Count;
    public int FileCount => Photos.Sum(photo => photo.Assets.Count);
    public bool GroupBySpecies { get; }
    public int CopyCount => Photos.Sum(photo => checked(photo.Assets.Count * photo.TargetFolders.Count));
}

/// <summary>一个受管理原件的实际处理结果，副本校验和来源移除分开记录。</summary>
public class AssetFileOperationResult
{
    /// <summary>创建一个计划原件对应的实际状态，默认尚未处理。</summary>
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

/// <summary>One destination copy; source removal is recorded only once in FileOperationResult.Files.</summary>
public sealed class AssetCopyOperationResult : AssetFileOperationResult
{
    /// <summary>创建某个原件在一个分类目录中的副本状态，源删除仍只计一次。</summary>
    internal AssetCopyOperationResult(string photoId, PlannedPhotoAsset asset, string folder) : base(photoId, asset)
    { PlannedAsset = asset; TargetFolder = folder; }
    public string TargetFolder { get; }
    internal PlannedPhotoAsset PlannedAsset { get; }
}

/// <summary>一次文件操作的真实汇总；只有已成功移除的成员才可从工程记录中应用删除。</summary>
public sealed class FileOperationResult
{
    public List<PhotoItem> Succeeded { get; } = new();
    // Legacy count: every managed member of this logical photo was actually removed.
    public List<PhotoItem> SourceRemoved { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Skipped { get; } = new();
    public List<AssetFileOperationResult> Files { get; } = new();
    public List<AssetCopyOperationResult> Copies { get; } = new();
    public int VerifiedCopyCount => Copies.Count(copy => copy.Verified);
    public int CopyCount => Copies.Count;
    public int CopiedFileCount => Files.Count(file => file.Verified);
    public int RemovedFileCount => Files.Count(file => file.SourceRemoved);
    public int SkippedFileCount => Files.Count(file => file.Status == AssetFileOperationStatus.Skipped);
    public int FailedFileCount => Files.Count(file => file.Status == AssetFileOperationStatus.Failed);
    public int CanceledFileCount => Files.Count(file => file.Status == AssetFileOperationStatus.Canceled);

    /// <remarks>UI-thread commit: change only successfully removed members and return empty logical photos.</remarks>
    /// <summary>只把实际移除成功的成员应用到会话，保留未处理或失败成员，并返回已无原件的逻辑照片。</summary>
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
