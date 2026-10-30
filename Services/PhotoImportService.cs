using System.IO;
using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

public sealed class ImportResult
{
    public List<PhotoItem> Added { get; } = new();
    public List<PhotoImportUpdate> Updates { get; } = new();
    public int SkippedUnsupported { get; internal set; }
    public int SkippedDuplicate { get; internal set; }
    public int SkippedConflict { get; internal set; }
    public int PairedInputs { get; internal set; }
    public List<string> Errors { get; } = new();
}

/// <summary>A detached import plan. Apply only on the owning UI thread, after import succeeds.</summary>
public sealed class PhotoImportUpdate
{
    private readonly PhotoAsset[] _originalAssets;
    private readonly string? _originalPreferred, _originalActive;

    internal PhotoImportUpdate(string photoId, PhotoAsset[] originalAssets, string? originalPreferred,
        string? originalActive, PhotoAsset[] assets, string preferred, string active, PhotoItem? preview)
    {
        PhotoId = photoId;
        _originalAssets = originalAssets; _originalPreferred = originalPreferred; _originalActive = originalActive;
        Assets = Array.AsReadOnly(assets);
        PreferredPreviewAssetId = preferred; ActivePreviewAssetId = active; Preview = preview;
    }

    public string PhotoId { get; }
    public IReadOnlyList<PhotoAsset> Assets { get; }
    public string PreferredPreviewAssetId { get; }
    public string ActivePreviewAssetId { get; }
    public PhotoItem? Preview { get; }

    public void ApplyTo(PhotoItem photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (photo.Id != PhotoId || photo.PreferredPreviewAssetId != _originalPreferred ||
            photo.ActivePreviewAssetId != _originalActive || !SameAssets(photo.Assets, _originalAssets))
            throw new InvalidOperationException("照片成员在导入期间已变化，请重新添加，避免覆盖新状态。");
        photo.SetAssets(Assets, PreferredPreviewAssetId, ActivePreviewAssetId);
        if (Preview is not null) PhotoImportService.ApplyPreview(photo, Preview);
    }

    internal static bool SameAssets(IEnumerable<PhotoAsset> first, IEnumerable<PhotoAsset> second) =>
        first.Select(asset => (asset.Id, asset.Kind, Path: asset.CanonicalPath.ToUpperInvariant()))
            .OrderBy(asset => asset.Id, StringComparer.Ordinal)
            .SequenceEqual(second.Select(asset => (asset.Id, asset.Kind, Path: asset.CanonicalPath.ToUpperInvariant()))
                .OrderBy(asset => asset.Id, StringComparer.Ordinal));
}

public sealed class PhotoImportService
{
    /// <summary>RAW-aware import snapshots existing records before starting background work.</summary>
    public Task<ImportResult> ImportAsync(IEnumerable<string> paths, IReadOnlyList<PhotoItem> existingPhotos,
        bool includeRaw = true, CancellationToken cancellationToken = default,
        IProgress<PhotoOperationProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(existingPhotos);
        var inputs = paths.ToArray();
        if (!includeRaw)
            return ImportAsync(inputs, existingPhotos.SelectMany(photo => photo.Assets)
                .Where(asset => asset.Kind == PhotoAssetKind.Jpg).Select(asset => Path.GetFileName(asset.CanonicalPath)),
                cancellationToken, progress);
        var snapshots = existingPhotos.Select(photo => new ExistingPhoto(photo.Id, photo.Assets.ToArray(),
            photo.PreferredPreviewAssetId, photo.ActivePreviewAssetId)).ToArray();
        return Task.Run(() => ImportWithRaw(inputs, snapshots, cancellationToken, progress), cancellationToken);
    }

    public Task<ImportResult> ImportAsync(IEnumerable<string> paths, IEnumerable<string> existingFileNames,
        CancellationToken cancellationToken = default, IProgress<PhotoOperationProgress>? progress = null)
    {
        var inputs = paths.ToArray();
        var names = new HashSet<string>(existingFileNames, StringComparer.OrdinalIgnoreCase);
        return Task.Run(() =>
        {
            var result = new ImportResult();
            for (int index = 0; index < inputs.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = inputs[index];
                progress?.Report(new(index, inputs.Length, index + 1, path, "正在导入"));
                cancellationToken.ThrowIfCancellationRequested();
                string stage = "已导入";
                try
                {
                    if (!IsJpegExtension(path))
                    { result.SkippedUnsupported++; stage = "已跳过不支持的格式"; continue; }
                    if (names.Contains(Path.GetFileName(path)))
                    { result.SkippedDuplicate++; stage = "已跳过同名照片"; continue; }
                    var item = LoadPhoto(path);
                    result.Added.Add(item);
                    names.Add(item.FileName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { result.Errors.Add($"{Path.GetFileName(path)}：{ex.Message}"); stage = "导入失败"; }
                finally
                { progress?.Report(new(index + 1, inputs.Length, index + 1, path, stage, FileCompleted: true)); }
            }
            return result;
        }, cancellationToken);
    }

    private static ImportResult ImportWithRaw(string[] inputs, ExistingPhoto[] existingPhotos,
        CancellationToken cancellationToken, IProgress<PhotoOperationProgress>? progress)
    {
        var result = new ImportResult();
        var directories = new Dictionary<string, Dictionary<string, string[]>>(StringComparer.OrdinalIgnoreCase);
        var seenInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var handledPairs = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var existingPairs = existingPhotos.SelectMany(photo => photo.Assets.Select(asset => (asset.PairKey, Photo: photo)))
            .GroupBy(entry => entry.PairKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Photo).Distinct().ToArray(),
                StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < inputs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = inputs[index];
            string stage = "已导入";
            bool completed = true;
            progress?.Report(new(index, inputs.Length, index + 1, path, "正在检查关联文件"));
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                path = Path.GetFullPath(path);
                if (!PhotoAsset.TryGetKind(path, out var kind))
                { result.SkippedUnsupported++; stage = "已跳过不支持的格式"; continue; }
                if (!seenInputs.Add(path))
                { result.SkippedDuplicate++; stage = "已跳过重复输入"; continue; }
                if (!File.Exists(path)) throw new FileNotFoundException("照片文件不存在。", path);
                var input = new PhotoAsset(path, kind);
                if (handledPairs.TryGetValue(input.PairKey, out bool succeeded))
                {
                    if (succeeded) { result.PairedInputs++; stage = "已归并到同张照片"; }
                    else stage = "已跳过此前报告的配对错误";
                    continue;
                }
                handledPairs[input.PairKey] = false;
                var owners = existingPairs.GetValueOrDefault(input.PairKey) ?? Array.Empty<ExistingPhoto>();
                if (owners.Length > 1)
                {
                    result.SkippedConflict++;
                    result.Errors.Add($"{path}：同目录同名原件已属于多张照片，无法自动合并不同评级记录。请先处理现有记录。");
                    stage = "已跳过配对冲突";
                    continue;
                }
                var owner = owners.FirstOrDefault();
                string directory = Path.GetDirectoryName(path)!;
                if (!directories.TryGetValue(directory, out var candidatesByStem))
                {
                    candidatesByStem = ReadDirectory(directory, cancellationToken);
                    directories[directory] = candidatesByStem;
                }
                string stem = Path.GetFileNameWithoutExtension(path);
                var candidatePaths = (candidatesByStem.GetValueOrDefault(stem) ?? Array.Empty<string>())
                    .Concat(owner?.Assets.Select(asset => asset.CanonicalPath) ?? Enumerable.Empty<string>())
                    .Append(path)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(candidate => candidate, StringComparer.OrdinalIgnoreCase).ToArray();
                var assets = candidatePaths.Select(candidate => owner?.Assets.FirstOrDefault(asset =>
                        StringComparer.OrdinalIgnoreCase.Equals(asset.CanonicalPath, candidate)) ??
                    new PhotoAsset(candidate, Kind(candidate))).ToArray();
                if (assets.GroupBy(asset => asset.Kind).Any(group => group.Count() > 1))
                {
                    result.SkippedConflict++;
                    result.Errors.Add($"{path}：存在多个同类候选，未自动配对：{string.Join("、", assets.Select(asset => Path.GetFileName(asset.CanonicalPath)))}。请先整理同目录同名的冲突文件后重试。");
                    stage = "已跳过配对冲突";
                    continue;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var preferred = assets.FirstOrDefault(asset => asset.Kind == PhotoAssetKind.Jpg) ?? assets[0];
                var active = File.Exists(preferred.CanonicalPath) ? preferred :
                    assets.First(asset => File.Exists(asset.CanonicalPath));
                PhotoItem? preview = null;
                if (owner is null || owner.ActiveId != active.Id)
                {
                    progress?.Report(new(index, inputs.Length, index + 1, active.CanonicalPath,
                        active.Kind == PhotoAssetKind.Raw ? "正在解码 RAW 缩略图" : "正在读取 JPG 缩略图"));
                    try { preview = LoadSourcePhoto(active.CanonicalPath, cancellationToken); }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        var fallback = assets.FirstOrDefault(asset => asset.Id != active.Id && File.Exists(asset.CanonicalPath));
                        if (fallback is null) throw;
                        result.Errors.Add($"{active.CanonicalPath}：无法读取首选预览，保留原件并尝试另一成员：{ex.Message}");
                        active = fallback;
                        progress?.Report(new(index, inputs.Length, index + 1, active.CanonicalPath, "正在解码备用原件缩略图"));
                        preview = LoadSourcePhoto(active.CanonicalPath, cancellationToken);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (owner is null)
                {
                    var photo = new PhotoItem();
                    photo.SetAssets(assets, preferred.Id, active.Id);
                    ApplyPreview(photo, preview!);
                    result.Added.Add(photo);
                    stage = "已导入 " + photo.FormatLabel;
                }
                else if (!PhotoImportUpdate.SameAssets(assets, owner.Assets) ||
                    preferred.Id != owner.PreferredId || active.Id != owner.ActiveId)
                {
                    result.Updates.Add(new(owner.Id, owner.Assets, owner.PreferredId, owner.ActiveId,
                        assets, preferred.Id, active.Id, preview));
                    stage = "已准备关联原件";
                }
                else { result.SkippedDuplicate++; stage = "已跳过重复照片"; }
                handledPairs[input.PairKey] = true;
            }
            catch (OperationCanceledException)
            { completed = false; stage = "已取消导入"; throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { result.Errors.Add($"{path}：{ex.Message}"); stage = "导入失败"; }
            finally
            { progress?.Report(new(index + (completed ? 1 : 0), inputs.Length, index + 1, path, stage, FileCompleted: completed)); }
        }
        return result;
    }

    private static PhotoAssetKind Kind(string path) => PhotoAsset.TryGetKind(path, out var kind) ? kind :
        throw new InvalidDataException("不支持的照片成员类型。");

    private static Dictionary<string, string[]> ReadDirectory(string directory, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PhotoAsset.TryGetKind(path, out _)) continue;
            string stem = Path.GetFileNameWithoutExtension(path);
            if (!candidates.TryGetValue(stem, out var paths)) candidates[stem] = paths = new();
            paths.Add(path);
        }
        return candidates.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private sealed record ExistingPhoto(string Id, PhotoAsset[] Assets, string? PreferredId, string? ActiveId);

    internal static PhotoItem LoadSourcePhoto(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsJpegExtension(path)) return LoadPhoto(path, cancellationToken);
        var info = RawImageDecoder.ReadInfo(path, cancellationToken);
        var thumbnail = ImageLoader.Load(path, 160, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new PhotoItem { FilePath = Path.GetFullPath(path), Metadata = info.Metadata,
            PixelWidth = info.PixelWidth, PixelHeight = info.PixelHeight, Thumbnail = thumbnail };
    }

    internal static void ApplyPreview(PhotoItem photo, PhotoItem preview)
    {
        photo.Metadata = preview.Metadata;
        photo.PixelWidth = preview.PixelWidth; photo.PixelHeight = preview.PixelHeight;
        photo.Thumbnail = preview.Thumbnail;
    }

    public static bool IsJpegExtension(string path) =>
        Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    internal static PhotoItem LoadPhoto(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8)
            throw new InvalidDataException("文件内容不是有效的 JPEG 图片。");
        stream.Position = 0;
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var metadata = frame.Metadata as BitmapMetadata;
        int orientation = ImageLoader.GetOrientation(metadata);
        var item = new PhotoItem
        {
            FilePath = Path.GetFullPath(path),
            PixelWidth = orientation is >= 5 and <= 8 ? frame.PixelHeight : frame.PixelWidth,
            PixelHeight = orientation is >= 5 and <= 8 ? frame.PixelWidth : frame.PixelHeight,
            Metadata = MetadataService.Read(metadata),
            Thumbnail = ImageLoader.Load(path, 160, cancellationToken)
        };
        return item;
    }
}
