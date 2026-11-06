using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BirdPhotoSelector;

public sealed class ProjectLoadResult
{
    public List<PhotoGroup> Groups { get; } = new();
    public IReadOnlyList<SpeciesIdentity> RecentSpecies { get; internal set; } = Array.Empty<SpeciesIdentity>();
    public double GroupingThreshold { get; internal set; } = BirdPhotoSelector.GroupingThreshold.Default;
    // These counters count logical photos. Member-level failures are reported separately.
    public int SkippedMissing { get; internal set; }
    public int SkippedInvalid { get; internal set; }
    public int SkippedDuplicate { get; internal set; }
    public int MissingAssetCount { get; internal set; }
    public int InvalidAssetCount { get; internal set; }
    public int DuplicateAssetCount { get; internal set; }
    public List<string> Errors { get; } = new();
}

/// <summary>工程 v1/v2/v3 的持久化用例。写入先快照校验再原子替换，读取通过注入的图像后端恢复 JPG 缩略图。</summary>
public sealed class ProjectService
{
    private readonly IPhotoImageDecoder _images;
    /// <summary>工程恢复与导入共用宿主注入的图像后端，领域和工程代码不选择 Windows 实现。</summary>
    public ProjectService(IPhotoImageDecoder images) => _images = images ?? throw new ArgumentNullException(nameof(images));

    /// <summary>只接收 JSON 工程，旧自定义扩展名不再兼容。</summary>
    public static bool IsProjectPath(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };

    /// <summary>先在调用线程快照和验证，异步写临时文件、刷新后原子替换；取消或失败保留旧工程。</summary>
    public async Task SaveAsync(string path, IEnumerable<PhotoGroup> groups, double groupingThreshold,
        CancellationToken cancellationToken = default, IReadOnlyList<SpeciesIdentity>? recentSpecies = null)
    {
        if (!IsProjectPath(path)) throw new InvalidDataException("工程文件必须使用 .json 扩展名。");
        if (!double.IsFinite(groupingThreshold) || groupingThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(groupingThreshold));
        ArgumentNullException.ThrowIfNull(groups);
        cancellationToken.ThrowIfCancellationRequested();
        // Snapshot and validate every photo on the caller's thread, before opening even a
        // temporary output. An invalid multi-asset model never replaces a valid project.
        var document = Snapshot(groups, groupingThreshold, cancellationToken, recentSpecies);
        string destination = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("工程文件保存目录不存在。");
        string tempPath = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, document, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(destination)) File.Replace(tempPath, destination, null);
            else File.Move(tempPath, destination);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>验证组、成员、身份及来源参照后形成独立序列化数据，不把缩略图或缓存写入 JSON。</summary>
    private static ProjectDocument Snapshot(IEnumerable<PhotoGroup> groups, double threshold, CancellationToken ct,
        IReadOnlyList<SpeciesIdentity>? recentSpecies)
    {
        var document = new ProjectDocument
        {
            GroupingThreshold = threshold,
            GroupingThresholdScaleVersion = GroupingThreshold.ScaleVersion
        };
        var recent = new RecentSpeciesHistory();
        recent.Replace(recentSpecies ?? Array.Empty<SpeciesIdentity>());
        document.RecentSpecies = recent.Items.ToList();
        var photoIds = new HashSet<string>(StringComparer.Ordinal);
        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            ct.ThrowIfCancellationRequested();
            if (group is null) throw new InvalidDataException("工程包含无效的照片组。");
            var savedGroup = new GroupRecord { Id = group.Id, Label = group.Label, Photos = new() };
            foreach (var photo in group.Photos)
            {
                ct.ThrowIfCancellationRequested();
                if (photo is null || string.IsNullOrWhiteSpace(photo.Id) || !photoIds.Add(photo.Id))
                    throw new InvalidDataException("工程包含空的或重复的逻辑照片 ID。");
                var assets = photo.Assets.ToArray();
                if (assets.Length == 0) throw new InvalidDataException("逻辑照片没有受管理的文件成员。");
                var kinds = new HashSet<PhotoAssetKind>();
                foreach (var asset in assets)
                {
                    if (asset is null || string.IsNullOrWhiteSpace(asset.Id) ||
                        !PhotoAsset.TryGetKind(asset.CanonicalPath, out var kind) || kind != asset.Kind)
                        throw new InvalidDataException("照片成员的类型、扩展名或 ID 无效。");
                    if (!assetIds.Add(asset.Id) || !assetPaths.Add(asset.CanonicalPath))
                        throw new InvalidDataException("工程包含重复的照片成员 ID 或文件路径。");
                    if (!kinds.Add(asset.Kind) || asset != assets[0] && !asset.CanPairWith(assets[0]))
                        throw new InvalidDataException("同一逻辑照片的成员必须同目录、同主体，且每种类型只能有一个。");
                }
                if (!assets.Any(asset => asset.Id == photo.PreferredPreviewAssetId) ||
                    !assets.Any(asset => asset.Id == photo.ActivePreviewAssetId))
                    throw new InvalidDataException("照片的首选或当前预览来源不属于已管理成员。");
                if (!Enum.IsDefined(photo.GeometryStatus))
                    throw new InvalidDataException("照片的鸟框几何状态无效。");
                var geometry = SnapshotGeometry(photo.GeometryReference);
                if (geometry is not null)
                {
                    // Historical coordinates can refer to an asset's old path after a
                    // source change. Only Current geometry claims the active file's frame.
                    var referenced = assets.FirstOrDefault(asset => asset.Id == geometry.AssetId);
                    if (photo.GeometryStatus == PhotoGeometryStatus.Current && referenced is not null &&
                        !SamePath(referenced.CanonicalPath, geometry.FilePath!))
                        throw new InvalidDataException("鸟框参照的成员 ID 与文件路径不一致。");
                }
                if (photo.PixelWidth < 0 || photo.PixelHeight < 0 || (photo.PixelWidth == 0) != (photo.PixelHeight == 0))
                    throw new InvalidDataException("照片的已知像素尺寸无效。");
                savedGroup.Photos!.Add(new PhotoRecord
                {
                    Id = photo.Id, FilePath = photo.ActivePreviewAsset!.CanonicalPath, Rating = photo.Rating,
                    Assets = assets.Select(asset => (AssetRecord?)new AssetRecord
                    { Id = asset.Id, FilePath = asset.CanonicalPath, Kind = asset.Kind.ToString() }).ToList(),
                    PreferredPreviewAssetId = photo.PreferredPreviewAssetId,
                    ActivePreviewAssetId = photo.ActivePreviewAssetId,
                    Metadata = CopyMetadata(photo.Metadata), PixelWidth = photo.PixelWidth, PixelHeight = photo.PixelHeight,
                    GeometryReference = geometry, GeometryStatus = photo.GeometryStatus.ToString(),
                    Bird = photo.Bird?.Copy(), DetectionStatus = photo.DetectionStatus,
                    SelectedBirdIndex = photo.SelectedBirdIndex,
                    SelectedBirdNumber = photo.SelectedBird?.Number, NextBirdNumber = photo.NextBirdNumber,
                    HasManualSpeciesEdits = photo.HasManualSpeciesEdits,
                    Birds = photo.Birds.Select(bird => (BirdRecord?)new BirdRecord
                    {
                        Number = bird.Number, IsManual = bird.IsManual,
                        Box = bird.HasRegion ? bird.Box.Copy() : null,
                        ManualSpecies = bird.ManualSpecies,
                        Species = bird.ManualSpecies is null ? bird.Species : null,
                        RecognitionStatus = bird.RecognitionStatus
                    }).ToList(),
                    ViewZoom = photo.ViewZoom, ViewCenterX = photo.ViewCenterX, ViewCenterY = photo.ViewCenterY,
                    ViewReference = photo.ViewZoom is double zoom && double.IsFinite(zoom) && zoom > 0
                        ? SnapshotGeometry(PhotoGeometryReference.Capture(photo.ActivePreviewAsset!, photo.PixelWidth, photo.PixelHeight)) : null
                });
            }
            document.Groups!.Add(savedGroup);
        }
        return document;
    }

    /// <summary>复制有效几何参照并拒绝不合理坐标元数据，防止损坏工程覆盖正常工程。</summary>
    private static GeometryRecord? SnapshotGeometry(PhotoGeometryReference? reference)
    {
        if (reference is null) return null;
        try
        {
            // Run the same structural validation as deserialization, including historical references.
            _ = new PhotoGeometryReference(reference.AssetId, reference.CanonicalPath, reference.DisplayWidth,
                reference.DisplayHeight, reference.FileLength, reference.LastWriteTimeUtcTicks);
            return new GeometryRecord
            {
                AssetId = reference.AssetId, FilePath = reference.CanonicalPath,
                DisplayWidth = reference.DisplayWidth, DisplayHeight = reference.DisplayHeight,
                FileLength = reference.FileLength, LastWriteTimeUtcTicks = reference.LastWriteTimeUtcTicks
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NotSupportedException)
        { throw new InvalidDataException("照片的鸟框来源参照无效。", ex); }
    }

    /// <summary>后台读取工程，兼容 v1/v2/v3，逐项跳过缺失或无效成员并报告，保留人工保护与最近选择。</summary>
    public Task<ProjectLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            if (!IsProjectPath(path)) throw new InvalidDataException("仅支持 .json 工程文件。");
            string projectPath = Path.GetFullPath(path);
            await using var stream = File.OpenRead(projectPath);
            var document = await JsonSerializer.DeserializeAsync<ProjectDocument>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("工程文件为空。");
            if (document.Schema != "BirdPhotoSelector.Project" || document.Version is not (1 or 2 or 3))
                throw new InvalidDataException("工程格式或版本不受支持。");
            if (document.Groups is null || !double.IsFinite(document.GroupingThreshold) ||
                document.GroupingThreshold is < 0 or > 1)
                throw new InvalidDataException("工程文件缺少有效的分组信息。");
            if (document.GroupingThresholdScaleVersion is not (1 or GroupingThreshold.ScaleVersion))
                throw new InvalidDataException("工程中的分组阈值刻度版本不受支持。");
            var result = new ProjectLoadResult
            {
                GroupingThreshold = document.GroupingThresholdScaleVersion == 1
                    ? GroupingThreshold.FromActual(document.GroupingThreshold)
                    : document.GroupingThreshold
            };
            var recent = new RecentSpeciesHistory();
            recent.Replace((document.RecentSpecies ?? new()).Where(species => species is { IsValid: true }));
            result.RecentSpecies = recent.Items.ToArray();
            string baseDirectory = Path.GetDirectoryName(projectPath)!;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var photoIds = new HashSet<string>(StringComparer.Ordinal);
            var assetIds = new HashSet<string>(StringComparer.Ordinal);
            var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var groupRecord in document.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (groupRecord?.Photos is null) { result.SkippedInvalid++; continue; }
                var group = new PhotoGroup(groupRecord.Label ?? "");
                if (!string.IsNullOrWhiteSpace(groupRecord.Id)) group.Id = groupRecord.Id;
                foreach (var record in groupRecord.Photos)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (record is null) { result.SkippedInvalid++; continue; }
                    try
                    {
                        var photo = document.Version == 1
                            ? LoadLegacy(record, baseDirectory, result, names, photoIds, cancellationToken)
                            : LoadCurrent(record, baseDirectory, result, photoIds, assetIds, assetPaths, cancellationToken);
                        if (photo is not null) group.Photos.Add(photo);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        result.SkippedInvalid++;
                        result.Errors.Add($"{record.FilePath ?? record.Id ?? "未知照片"}：{ex.Message}");
                    }
                }
                if (group.Count > 0) result.Groups.Add(group);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken);

    /// <summary>恢复 v1 的单 JPG 记录，不额外发现 RAW，缩略图由注入后端读取。</summary>
    private PhotoItem? LoadLegacy(PhotoRecord record, string baseDirectory, ProjectLoadResult result,
        HashSet<string> names, HashSet<string> photoIds, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.FilePath)) { result.SkippedInvalid++; return null; }
        string photoPath = Path.GetFullPath(record.FilePath, baseDirectory);
        if (!File.Exists(photoPath)) { result.SkippedMissing++; return null; }
        if (!PhotoImportService.IsJpegExtension(photoPath)) { result.SkippedInvalid++; return null; }
        if (names.Contains(Path.GetFileName(photoPath))) { result.SkippedDuplicate++; return null; }
        // Version 1 means exactly one explicitly recorded JPEG, even when a RAW sits beside it.
        var photo = PhotoImportService.LoadPhoto(photoPath, cancellationToken, _images);
        if (!string.IsNullOrWhiteSpace(record.Id) && photoIds.Add(record.Id)) photo.Id = record.Id;
        else photoIds.Add(photo.Id);
        RestoreBirdState(photo, record);
        RestoreView(photo, record);
        names.Add(photo.FileName);
        return photo;
    }

    /// <summary>恢复明确登记的多原件记录，选择存在的来源，换源或文件变更后将旧几何标为待核对。</summary>
    private PhotoItem? LoadCurrent(PhotoRecord record, string baseDirectory, ProjectLoadResult result,
        HashSet<string> photoIds, HashSet<string> assetIds, HashSet<string> assetPaths, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(record.Id))
            throw new InvalidDataException("逻辑照片 ID 缺失。");
        if (photoIds.Contains(record.Id))
        {
            result.SkippedDuplicate++;
            result.Errors.Add($"{record.Id}：重复的逻辑照片 ID，已跳过后续记录。");
            return null;
        }
        if (record.Assets is null || record.Assets.Count == 0)
            throw new InvalidDataException("v2 照片没有明确记录的文件成员。");
        var assets = new List<PhotoAsset>();
        var localIds = new HashSet<string>(StringComparer.Ordinal);
        var localPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localKinds = new HashSet<PhotoAssetKind>();
        int duplicatesBefore = result.DuplicateAssetCount;
        int invalidBefore = result.InvalidAssetCount;
        foreach (var saved in record.Assets)
        {
            try
            {
                if (saved is null || string.IsNullOrWhiteSpace(saved.Id) || string.IsNullOrWhiteSpace(saved.FilePath) ||
                    !TryParseNamedEnum(saved.Kind, out PhotoAssetKind kind))
                    throw new InvalidDataException("照片成员缺少有效 ID、路径或类型。");
                string fullPath = Path.GetFullPath(saved.FilePath, baseDirectory);
                if (!PhotoAsset.TryGetKind(fullPath, out var actualKind) || actualKind != kind)
                    throw new InvalidDataException("照片成员的类型与扩展名不一致或不受支持。");
                if (localIds.Contains(saved.Id) || assetIds.Contains(saved.Id) ||
                    localPaths.Contains(fullPath) || assetPaths.Contains(fullPath))
                {
                    result.DuplicateAssetCount++;
                    result.Errors.Add($"{saved.FilePath}：重复的成员 ID 或文件路径，已忽略此成员。");
                    continue;
                }
                var asset = new PhotoAsset(fullPath, kind, saved.Id);
                if (localKinds.Contains(kind) || assets.Count > 0 && !asset.CanPairWith(assets[0]))
                    throw new InvalidDataException("成员必须同目录、同文件名主体，且每种类型只能有一个。");
                assets.Add(asset);
                localIds.Add(asset.Id); localPaths.Add(fullPath); localKinds.Add(kind);
                // Preserve a legal missing member for audit and its old geometry reference.
                // External disappearance is not an explicit RemoveAsset operation.
                if (!File.Exists(fullPath)) result.MissingAssetCount++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                result.InvalidAssetCount++;
                result.Errors.Add($"{saved?.FilePath ?? record.Id}：无效成员，已忽略：{ex.Message}");
            }
        }
        if (assets.Count == 0)
        {
            if (result.InvalidAssetCount == invalidBefore && result.DuplicateAssetCount > duplicatesBefore)
                result.SkippedDuplicate++;
            else result.SkippedInvalid++;
            return null;
        }
        var available = assets.Where(asset => File.Exists(asset.FilePath)).ToArray();
        if (available.Length == 0) { result.SkippedMissing++; return null; }
        var preferred = assets.FirstOrDefault(asset => asset.Id == record.PreferredPreviewAssetId)
            ?? assets.FirstOrDefault(asset => asset.Kind == PhotoAssetKind.Jpg) ?? assets[0];
        var active = available.FirstOrDefault(asset => asset.Id == record.ActivePreviewAssetId)
            ?? available.FirstOrDefault(asset => asset.Id == preferred.Id)
            ?? available.FirstOrDefault(asset => asset.Kind == PhotoAssetKind.Jpg) ?? available[0];
        if (!string.IsNullOrWhiteSpace(record.PreferredPreviewAssetId) &&
            !assets.Any(asset => asset.Id == record.PreferredPreviewAssetId))
            result.Errors.Add($"{record.Id}：首选预览来源无效，已使用合法成员。");
        if (!string.IsNullOrWhiteSpace(record.ActivePreviewAssetId) &&
            !assets.Any(asset => asset.Id == record.ActivePreviewAssetId))
            result.Errors.Add($"{record.Id}：当前预览来源无效，已使用可用成员。");

        PhotoItem photo;
        try { photo = active.Kind == PhotoAssetKind.Jpg ? PhotoImportService.LoadPhoto(active.FilePath, cancellationToken, _images) : new PhotoItem(); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // A validly named but unreadable JPEG must not discard a surviving RAW or its species.
            var fallback = available.FirstOrDefault(asset => asset.Id != active.Id);
            result.InvalidAssetCount++;
            result.Errors.Add($"{active.FilePath}：预览来源无法读取：{ex.Message}");
            if (fallback is null) throw;
            active = fallback;
            photo = new PhotoItem();
        }
        var thumbnail = photo.Thumbnail;
        int width = photo.PixelWidth, height = photo.PixelHeight;
        var metadata = active.Kind == PhotoAssetKind.Jpg ? photo.Metadata : CopyMetadata(record.Metadata);
        photo.SetAssets(assets, preferred.Id, active.Id);
        photo.Id = record.Id;
        bool activeChanged = record.ActivePreviewAssetId != active.Id;
        photo.Metadata = metadata;
        if (active.Kind == PhotoAssetKind.Jpg)
        {
            // SetAssets can invalidate temporary JPEG asset IDs created by LoadPhoto.
            photo.Thumbnail = thumbnail; photo.PixelWidth = width; photo.PixelHeight = height;
        }
        else if (!activeChanged && record.PixelWidth > 0 && record.PixelHeight > 0)
        {
            // RAW-only placeholders may restore already known metadata, but never invoke
            // JpegBitmapDecoder or borrow dimensions from a now-missing JPEG.
            photo.PixelWidth = record.PixelWidth; photo.PixelHeight = record.PixelHeight;
        }
        RestoreBirdState(photo, record);
        PhotoGeometryReference? reference = null;
        bool invalidGeometry = false;
        try { reference = ReadGeometry(record.GeometryReference, baseDirectory); }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            invalidGeometry = true;
            result.Errors.Add($"{record.Id}：鸟框来源参照无效，已保留识别结果并标记待核对：{ex.Message}");
        }
        if (!TryParseNamedEnum(record.GeometryStatus ?? "Unspecified", out PhotoGeometryStatus status))
        {
            invalidGeometry = true;
            status = PhotoGeometryStatus.NeedsReview;
            result.Errors.Add($"{record.Id}：无法识别鸟框几何状态，已保留识别结果并标记待核对。");
        }
        bool sourceFileChanged = GeometryFileChanged(reference, active);
        bool referenceMatchesActive = reference is not null && reference.AssetId == active.Id &&
            SamePath(reference.CanonicalPath, active.CanonicalPath);
        if (invalidGeometry || activeChanged || sourceFileChanged || photo.Birds.Any(bird => bird.HasRegion) &&
            (status != PhotoGeometryStatus.Current || !referenceMatchesActive))
            status = PhotoGeometryStatus.NeedsReview;
        photo.SetGeometryReference(reference, status);
        if (sourceFileChanged && active.Kind == PhotoAssetKind.Raw)
            photo.PixelWidth = photo.PixelHeight = 0;
        if (!activeChanged && CanRestoreView(record, photo, active, baseDirectory, result)) RestoreView(photo, record);
        photoIds.Add(photo.Id);
        foreach (var asset in assets) { assetIds.Add(asset.Id); assetPaths.Add(asset.CanonicalPath); }
        return photo;
    }

    /// <summary>基于工程目录解析参照路径，验证尺寸及文件版本后创建领域参照。</summary>
    private static PhotoGeometryReference? ReadGeometry(GeometryRecord? saved, string baseDirectory)
    {
        if (saved is null) return null;
        if (string.IsNullOrWhiteSpace(saved.AssetId) || string.IsNullOrWhiteSpace(saved.FilePath))
            throw new InvalidDataException("鸟框来源参照缺少成员 ID 或路径。");
        return new PhotoGeometryReference(saved.AssetId, Path.GetFullPath(saved.FilePath, baseDirectory),
            saved.DisplayWidth, saved.DisplayHeight, saved.FileLength, saved.LastWriteTimeUtcTicks);
    }

    /// <summary>比较原件长度和修改时间，识别内容可能变化的原坐标来源。</summary>
    private static bool GeometryFileChanged(PhotoGeometryReference? reference, PhotoAsset active)
    {
        if (reference is null || reference.AssetId != active.Id || !SamePath(reference.CanonicalPath, active.CanonicalPath)) return false;
        var file = new FileInfo(active.CanonicalPath);
        return reference.FileLength is { } length && file.Length != length ||
            reference.LastWriteTimeUtcTicks is { } ticks && file.LastWriteTimeUtc.Ticks != ticks;
    }

    /// <summary>核对保存视图与当前来源是否一致，待核对旧工程不能无条件复用缩放。</summary>
    private static bool CanRestoreView(PhotoRecord record, PhotoItem photo, PhotoAsset active,
        string baseDirectory, ProjectLoadResult result)
    {
        // Old documents do not distinguish the viewport's source from historical bird geometry.
        if (record.ViewReference is null) return photo.GeometryStatus != PhotoGeometryStatus.NeedsReview;
        try
        {
            var reference = ReadGeometry(record.ViewReference, baseDirectory)!;
            return reference.AssetId == active.Id && SamePath(reference.CanonicalPath, active.CanonicalPath) &&
                reference.FileLength is not null && reference.LastWriteTimeUtcTicks is not null &&
                !GeometryFileChanged(reference, active);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            result.Errors.Add($"{record.Id}：视图来源参照无效，已恢复全图：{ex.Message}");
            return false;
        }
    }

    /// <summary>沿用现有 Windows 路径比较，后续平台阶段与路径身份策略一起迁移。</summary>
    private static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    /// <summary>只接受已定义的枚举名称，拒绝未知字符串或数字冒充有效状态。</summary>
    private static bool TryParseNamedEnum<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        return value is not null && Enum.GetNames<T>().Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) &&
            Enum.TryParse(value, ignoreCase: true, out result);
    }

    /// <summary>兼容旧单鸟和新版多鸟记录，恢复人工身份、选择及序号高水位。</summary>
    private static void RestoreBirdState(PhotoItem photo, PhotoRecord record)
    {
        photo.Rating = Enum.IsDefined(record.Rating) ? record.Rating : PhotoRating.Unmarked;
        var validBirds = (record.Birds ?? new()).Select((bird, index) => (Bird: bird, Index: index))
            .Where(entry => entry.Bird is { } bird && (bird.ManualSpecies is not null
                ? bird.ManualSpecies.IsValid : bird.Box is { IsValid: true })).ToArray();
        if (validBirds.Length > 0)
        {
            int selected = Array.FindIndex(validBirds, entry => entry.Index == record.SelectedBirdIndex);
            if (record.SelectedBirdNumber is > 0)
            {
                int byNumber = Array.FindIndex(validBirds, entry => entry.Bird!.Number == record.SelectedBirdNumber);
                if (byNumber >= 0 && validBirds.Count(entry => entry.Bird!.Number == record.SelectedBirdNumber) == 1) selected = byNumber;
            }
            photo.SetBirds(validBirds.Select(entry => new DetectedBird
            {
                Number = entry.Bird!.Number, IsManual = entry.Bird.IsManual,
                Box = entry.Bird.ManualSpecies is null ? entry.Bird.Box! : new BirdBox(),
                ManualSpecies = entry.Bird.ManualSpecies,
                Species = entry.Bird.ManualSpecies is null ? NormalizeSpecies(entry.Bird.Species) : null,
                RecognitionStatus = entry.Bird.RecognitionStatus ?? "待识别"
            }), Math.Max(0, selected), record.NextBirdNumber);
        }
        else photo.SetBirds(record.Bird is { IsValid: true } legacy
            ? new[] { new DetectedBird { Number = record.SelectedBirdNumber ?? 0, Box = legacy } }
            : Array.Empty<DetectedBird>(), nextBirdNumber: record.NextBirdNumber);
        // Older projects did not store the edit barrier, but a saved hand-drawn
        // region is explicit evidence that this photo has already been edited.
        photo.HasManualSpeciesEdits = record.HasManualSpeciesEdits ||
            photo.Birds.Any(bird => bird.ManualSpecies is not null || bird.IsManual);
        photo.DetectionStatus = record.DetectionStatus ?? (photo.Bird is null ? "尚未分析" : "已识别鸟类");
    }

    /// <summary>只恢复有限、合理的缩放与中心坐标，避免异常数值进入预览。</summary>
    private static void RestoreView(PhotoItem photo, PhotoRecord record)
    {
        photo.ViewZoom = PositiveFinite(record.ViewZoom);
        photo.ViewCenterX = UnitFinite(record.ViewCenterX);
        photo.ViewCenterY = UnitFinite(record.ViewCenterY);
    }

    /// <summary>复制拍摄参数文本，避免序列化快照共享可变元数据对象。</summary>
    private static PhotoMetadata CopyMetadata(PhotoMetadata? metadata) => metadata is null ? new() : new()
    {
        CapturedAt = metadata.CapturedAt, Camera = metadata.Camera, FocalLength = metadata.FocalLength,
        Aperture = metadata.Aperture, Shutter = metadata.Shutter, Iso = metadata.Iso, ExposureBias = metadata.ExposureBias
    };
    /// <summary>筛选有效的正数缩放参数。</summary>
    private static double? PositiveFinite(double? value) => value is { } v && double.IsFinite(v) && v > 0 ? v : null;
    /// <summary>筛选位于零到一之间的有限归一化坐标。</summary>
    private static double? UnitFinite(double? value) => value is { } v && double.IsFinite(v) && v >= 0 && v <= 1 ? v : null;
    /// <summary>验证并规范旧识别结果的相似度和可选置信度，不把未知分数升级为可信结果。</summary>
    private static SpeciesMatch? NormalizeSpecies(SpeciesMatch? match) => match is not { IsValid: true } ? null : match with
    {
        Confidence = match.HasValidConfidence ? match.Confidence : null,
        RunnerUpSimilarity = match.RunnerUpSimilarity is { } score && double.IsFinite(score) && score is >= -1 and <= 1 ? score : null
    };

    /// <summary>工程文件根 DTO；v3 保存明确原件和人工记录，不保存领域事件或图像对象。</summary>
    private sealed class ProjectDocument
    {
        /// <summary>JSON 序列化专用空构造，缺省字段保留既有工程兼容约定。</summary>
        public ProjectDocument() { }
        [JsonRequired] public string Schema { get; set; } = "BirdPhotoSelector.Project";
        [JsonRequired] public int Version { get; set; } = 3;
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
        [JsonRequired] public double GroupingThreshold { get; set; } = 0.8;
        public int GroupingThresholdScaleVersion { get; set; } = 1;
        [JsonRequired] public List<GroupRecord?>? Groups { get; set; } = new();
        public List<SpeciesIdentity>? RecentSpecies { get; set; } = new();
    }
    /// <summary>保存组身份和有序照片列表，不承担运行时的属性通知。</summary>
    private sealed class GroupRecord
    {
        /// <summary>JSON 序列化专用空构造，组数据与领域观察集合分开保存。</summary>
        public GroupRecord() { }
        public string? Id { get; set; }
        public string? Label { get; set; }
        public List<PhotoRecord?>? Photos { get; set; }
    }
    /// <summary>兼容旧单来源与新版多原件工程的照片 DTO，不直接作为可编辑领域对象使用。</summary>
    private sealed class PhotoRecord
    {
        /// <summary>JSON 序列化专用空构造，同时兼容旧单 JPG 与新版多原件字段。</summary>
        public PhotoRecord() { }
        public string? Id { get; set; }
        // Used only by the v1 reader. V2 is authoritative through Assets.
        public string? FilePath { get; set; }
        public List<AssetRecord?>? Assets { get; set; }
        public string? PreferredPreviewAssetId { get; set; }
        public string? ActivePreviewAssetId { get; set; }
        public PhotoMetadata? Metadata { get; set; }
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public GeometryRecord? GeometryReference { get; set; }
        public string? GeometryStatus { get; set; }
        public PhotoRating Rating { get; set; }
        public BirdBox? Bird { get; set; }
        public List<BirdRecord?>? Birds { get; set; }
        public int SelectedBirdIndex { get; set; }
        public int? SelectedBirdNumber { get; set; }
        public int NextBirdNumber { get; set; } = 1;
        public bool HasManualSpeciesEdits { get; set; }
        public string? DetectionStatus { get; set; }
        public double? ViewZoom { get; set; }
        public double? ViewCenterX { get; set; }
        public double? ViewCenterY { get; set; }
        // Optional v2 extension: manual viewing coordinates can belong to a newer source than bird boxes.
        public GeometryRecord? ViewReference { get; set; }
    }
    /// <summary>保存成员 ID、类型和路径，导入工程后重新验证。</summary>
    private sealed class AssetRecord
    {
        /// <summary>JSON 序列化专用空构造，记录稳定成员身份和文件路径。</summary>
        public AssetRecord() { }
        public string? Id { get; set; }
        public string? FilePath { get; set; }
        public string? Kind { get; set; }
    }
    /// <summary>保存鸟框原坐标来源，来源缺失时仍保留参照以提示待核对。</summary>
    private sealed class GeometryRecord
    {
        /// <summary>JSON 序列化专用空构造，保留旧鸟框所属来源和已知文件版本。</summary>
        public GeometryRecord() { }
        public string? AssetId { get; set; }
        public string? FilePath { get; set; }
        public int? DisplayWidth { get; set; }
        public int? DisplayHeight { get; set; }
        public long? FileLength { get; set; }
        public long? LastWriteTimeUtcTicks { get; set; }
    }
    /// <summary>保存稳定编号、选区、人工身份和自动识别证据。</summary>
    private sealed class BirdRecord
    {
        /// <summary>JSON 序列化专用空构造，保存框、人工状态及可选自动识别证据。</summary>
        public BirdRecord() { }
        public BirdBox? Box { get; set; }
        public int Number { get; set; }
        public bool IsManual { get; set; }
        public SpeciesIdentity? ManualSpecies { get; set; }
        public SpeciesMatch? Species { get; set; }
        public string? RecognitionStatus { get; set; }
    }
}
