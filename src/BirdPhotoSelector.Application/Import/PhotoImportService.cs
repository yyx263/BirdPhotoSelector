using System.IO;

namespace BirdPhotoSelector;

/// <summary>后台导入的独立结果，含新增照片、更新提案、跳过计数和逐项错误，不直接改写已有记录。</summary>
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

    /// <summary>固定原记录和导入提案；应用时必须再次核对原件，不能覆盖导入期间的新状态。</summary>
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

    /// <summary>在 UI/会话线程应用导入提案前核对原件和来源快照，拒绝覆盖并发新状态。</summary>
    public void ApplyTo(PhotoItem photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (photo.Id != PhotoId || photo.PreferredPreviewAssetId != _originalPreferred ||
            photo.ActivePreviewAssetId != _originalActive || !SameAssets(photo.Assets, _originalAssets))
            throw new InvalidOperationException("照片成员在导入期间已变化，请重新添加，避免覆盖新状态。");
        photo.SetAssets(Assets, PreferredPreviewAssetId, ActivePreviewAssetId);
        if (Preview is not null) PhotoImportService.ApplyPreview(photo, Preview);
    }

    /// <summary>比较成员稳定身份、格式及路径，确认导入期间原记录没有变化。</summary>
    internal static bool SameAssets(IEnumerable<PhotoAsset> first, IEnumerable<PhotoAsset> second) =>
        first.Select(asset => (asset.Id, asset.Kind, Path: asset.CanonicalPath.ToUpperInvariant()))
            .OrderBy(asset => asset.Id, StringComparer.Ordinal)
            .SequenceEqual(second.Select(asset => (asset.Id, asset.Kind, Path: asset.CanonicalPath.ToUpperInvariant()))
                .OrderBy(asset => asset.Id, StringComparer.Ordinal));
}

/// <summary>按输入顺序导入并关联 JPG/RAW，后台只生成新增或更新提案，不直接改写正在显示的照片对象。</summary>
public sealed class PhotoImportService
{
    private readonly IPhotoImageDecoder _images;
    /// <summary>由宿主提供图像后端，导入流程只负责原件关联和业务记录。</summary>
    public PhotoImportService(IPhotoImageDecoder images) => _images = images ?? throw new ArgumentNullException(nameof(images));

    /// <remarks>RAW-aware import snapshots existing records before starting background work.</remarks>
    /// <summary>快照输入与已有记录后在后台导入；按既定格式/重复规则返回独立提案，取消继续传播。</summary>
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

    /// <summary>快照输入与已有记录后在后台导入；按既定格式/重复规则返回独立提案，取消继续传播。</summary>
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
                    var item = LoadPhoto(path, cancellationToken, _images);
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

    /// <summary>按目录索引关联同名原件，明确报告配对冲突，首选来源失败时尝试另一成员并保留错误。</summary>
    private ImportResult ImportWithRaw(string[] inputs, ExistingPhoto[] existingPhotos,
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
                    try { preview = LoadSourcePhoto(active.CanonicalPath, cancellationToken, _images); }
                    catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
                    {
                        var fallback = assets.FirstOrDefault(asset => asset.Id != active.Id && File.Exists(asset.CanonicalPath));
                        if (fallback is null) throw;
                        result.Errors.Add($"{active.CanonicalPath}：无法读取首选预览，保留原件并尝试另一成员：{ex.Message}");
                        active = fallback;
                        progress?.Report(new(index, inputs.Length, index + 1, active.CanonicalPath, "正在解码备用原件缩略图"));
                        preview = LoadSourcePhoto(active.CanonicalPath, cancellationToken, _images);
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

    /// <summary>把受支持扩展名转为原件格式，未知格式作为无效数据报告。</summary>
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

    /// <summary>只包含导入所需的原件与来源快照，后台不访问可变业务对象。</summary>
    private sealed record ExistingPhoto(string Id, PhotoAsset[] Assets, string? PreferredId, string? ActiveId);

    /// <summary>读取一个原件的显示尺寸、拍摄参数与小缩略图，不把 UI 位图写入领域对象。</summary>
    internal static PhotoItem LoadSourcePhoto(string path, CancellationToken cancellationToken,
        IPhotoImageDecoder images)
    {
        var decoder = images ?? throw new ArgumentNullException(nameof(images));
        var info = decoder.ReadInfo(path, cancellationToken);
        var thumbnail = decoder.Decode(path, 160, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new PhotoItem { FilePath = Path.GetFullPath(path), Metadata = info.Metadata,
            PixelWidth = info.PixelWidth, PixelHeight = info.PixelHeight, Thumbnail = thumbnail };
    }

    /// <summary>只应用当前来源的尺寸、拍摄参数和缩略图，不改变鸟种和原件列表。</summary>
    internal static void ApplyPreview(PhotoItem photo, PhotoItem preview)
    {
        photo.Metadata = preview.Metadata;
        photo.PixelWidth = preview.PixelWidth; photo.PixelHeight = preview.PixelHeight;
        photo.Thumbnail = preview.Thumbnail;
    }

    /// <summary>使用图像层统一的 JPG 扩展名规则。</summary>
    public static bool IsJpegExtension(string path) => PhotoImageFormats.IsJpeg(path);

    /// <summary>兼容现有工程读取的 JPG 入口；格式内容验证和方向处理交由图像后端。</summary>
    internal static PhotoItem LoadPhoto(string path, CancellationToken cancellationToken,
        IPhotoImageDecoder images) => LoadSourcePhoto(path, cancellationToken, images);
}
