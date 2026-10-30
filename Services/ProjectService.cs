using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BirdPhotoSelector;

public sealed class ProjectLoadResult
{
    public List<PhotoGroup> Groups { get; } = new();
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

public sealed class ProjectService
{
    public static bool IsProjectPath(string path) =>
        Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        MaxDepth = 32
    };

    public async Task SaveAsync(string path, IEnumerable<PhotoGroup> groups, double groupingThreshold,
        CancellationToken cancellationToken = default)
    {
        if (!IsProjectPath(path)) throw new InvalidDataException("工程文件必须使用 .json 扩展名。");
        if (!double.IsFinite(groupingThreshold) || groupingThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(groupingThreshold));
        ArgumentNullException.ThrowIfNull(groups);
        cancellationToken.ThrowIfCancellationRequested();
        // Snapshot and validate every photo on the caller's thread, before opening even a
        // temporary output. An invalid multi-asset model never replaces a valid project.
        var document = Snapshot(groups, groupingThreshold, cancellationToken);
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

    private static ProjectDocument Snapshot(IEnumerable<PhotoGroup> groups, double threshold, CancellationToken ct)
    {
        var document = new ProjectDocument
        {
            GroupingThreshold = threshold,
            GroupingThresholdScaleVersion = GroupingThreshold.ScaleVersion
        };
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
                    Birds = photo.Birds.Select(bird => (BirdRecord?)new BirdRecord
                    { Number = bird.Number, IsManual = bird.IsManual, Box = bird.Box.Copy(), Species = bird.Species, RecognitionStatus = bird.RecognitionStatus }).ToList(),
                    ViewZoom = photo.ViewZoom, ViewCenterX = photo.ViewCenterX, ViewCenterY = photo.ViewCenterY,
                    ViewReference = photo.ViewZoom is double zoom && double.IsFinite(zoom) && zoom > 0
                        ? SnapshotGeometry(PhotoGeometryReference.Capture(photo.ActivePreviewAsset!, photo.PixelWidth, photo.PixelHeight)) : null
                });
            }
            document.Groups!.Add(savedGroup);
        }
        return document;
    }

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

    public Task<ProjectLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            if (!IsProjectPath(path)) throw new InvalidDataException("仅支持 .json 工程文件。");
            string projectPath = Path.GetFullPath(path);
            await using var stream = File.OpenRead(projectPath);
            var document = await JsonSerializer.DeserializeAsync<ProjectDocument>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("工程文件为空。");
            if (document.Schema != "BirdPhotoSelector.Project" || document.Version is not (1 or 2))
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
                            ? LoadLegacy(record, baseDirectory, result, names, photoIds)
                            : LoadCurrent(record, baseDirectory, result, photoIds, assetIds, assetPaths);
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

    private static PhotoItem? LoadLegacy(PhotoRecord record, string baseDirectory, ProjectLoadResult result,
        HashSet<string> names, HashSet<string> photoIds)
    {
        if (string.IsNullOrWhiteSpace(record.FilePath)) { result.SkippedInvalid++; return null; }
        string photoPath = Path.GetFullPath(record.FilePath, baseDirectory);
        if (!File.Exists(photoPath)) { result.SkippedMissing++; return null; }
        if (!PhotoImportService.IsJpegExtension(photoPath)) { result.SkippedInvalid++; return null; }
        if (names.Contains(Path.GetFileName(photoPath))) { result.SkippedDuplicate++; return null; }
        // Version 1 means exactly one explicitly recorded JPEG, even when a RAW sits beside it.
        var photo = PhotoImportService.LoadPhoto(photoPath);
        if (!string.IsNullOrWhiteSpace(record.Id) && photoIds.Add(record.Id)) photo.Id = record.Id;
        else photoIds.Add(photo.Id);
        RestoreBirdState(photo, record);
        RestoreView(photo, record);
        names.Add(photo.FileName);
        return photo;
    }

    private static PhotoItem? LoadCurrent(PhotoRecord record, string baseDirectory, ProjectLoadResult result,
        HashSet<string> photoIds, HashSet<string> assetIds, HashSet<string> assetPaths)
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
        try { photo = active.Kind == PhotoAssetKind.Jpg ? PhotoImportService.LoadPhoto(active.FilePath) : new PhotoItem(); }
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
        if (invalidGeometry || activeChanged || sourceFileChanged || photo.Birds.Count > 0 &&
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

    private static PhotoGeometryReference? ReadGeometry(GeometryRecord? saved, string baseDirectory)
    {
        if (saved is null) return null;
        if (string.IsNullOrWhiteSpace(saved.AssetId) || string.IsNullOrWhiteSpace(saved.FilePath))
            throw new InvalidDataException("鸟框来源参照缺少成员 ID 或路径。");
        return new PhotoGeometryReference(saved.AssetId, Path.GetFullPath(saved.FilePath, baseDirectory),
            saved.DisplayWidth, saved.DisplayHeight, saved.FileLength, saved.LastWriteTimeUtcTicks);
    }

    private static bool GeometryFileChanged(PhotoGeometryReference? reference, PhotoAsset active)
    {
        if (reference is null || reference.AssetId != active.Id || !SamePath(reference.CanonicalPath, active.CanonicalPath)) return false;
        var file = new FileInfo(active.CanonicalPath);
        return reference.FileLength is { } length && file.Length != length ||
            reference.LastWriteTimeUtcTicks is { } ticks && file.LastWriteTimeUtc.Ticks != ticks;
    }

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

    private static bool SamePath(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

    private static bool TryParseNamedEnum<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        return value is not null && Enum.GetNames<T>().Any(name => string.Equals(name, value, StringComparison.OrdinalIgnoreCase)) &&
            Enum.TryParse(value, ignoreCase: true, out result);
    }

    private static void RestoreBirdState(PhotoItem photo, PhotoRecord record)
    {
        photo.Rating = Enum.IsDefined(record.Rating) ? record.Rating : PhotoRating.Unmarked;
        var validBirds = (record.Birds ?? new()).Select((bird, index) => (Bird: bird, Index: index))
            .Where(entry => entry.Bird?.Box is { IsValid: true }).ToArray();
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
                Number = entry.Bird!.Number, IsManual = entry.Bird.IsManual, Box = entry.Bird.Box!,
                Species = NormalizeSpecies(entry.Bird.Species), RecognitionStatus = entry.Bird.RecognitionStatus ?? "待识别"
            }), Math.Max(0, selected), record.NextBirdNumber);
        }
        else photo.SetBirds(record.Bird is { IsValid: true } legacy
            ? new[] { new DetectedBird { Number = record.SelectedBirdNumber ?? 0, Box = legacy } }
            : Array.Empty<DetectedBird>(), nextBirdNumber: record.NextBirdNumber);
        photo.DetectionStatus = record.DetectionStatus ?? (photo.Bird is null ? "尚未分析" : "已识别鸟类");
    }

    private static void RestoreView(PhotoItem photo, PhotoRecord record)
    {
        photo.ViewZoom = PositiveFinite(record.ViewZoom);
        photo.ViewCenterX = UnitFinite(record.ViewCenterX);
        photo.ViewCenterY = UnitFinite(record.ViewCenterY);
    }

    private static PhotoMetadata CopyMetadata(PhotoMetadata? metadata) => metadata is null ? new() : new()
    {
        CapturedAt = metadata.CapturedAt, Camera = metadata.Camera, FocalLength = metadata.FocalLength,
        Aperture = metadata.Aperture, Shutter = metadata.Shutter, Iso = metadata.Iso, ExposureBias = metadata.ExposureBias
    };
    private static double? PositiveFinite(double? value) => value is { } v && double.IsFinite(v) && v > 0 ? v : null;
    private static double? UnitFinite(double? value) => value is { } v && double.IsFinite(v) && v >= 0 && v <= 1 ? v : null;
    private static SpeciesMatch? NormalizeSpecies(SpeciesMatch? match) => match is not { IsValid: true } ? null : match with
    {
        Confidence = match.HasValidConfidence ? match.Confidence : null,
        RunnerUpSimilarity = match.RunnerUpSimilarity is { } score && double.IsFinite(score) && score is >= -1 and <= 1 ? score : null
    };

    private sealed class ProjectDocument
    {
        public ProjectDocument() { }
        [JsonRequired] public string Schema { get; set; } = "BirdPhotoSelector.Project";
        [JsonRequired] public int Version { get; set; } = 2;
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
        [JsonRequired] public double GroupingThreshold { get; set; } = 0.8;
        public int GroupingThresholdScaleVersion { get; set; } = 1;
        [JsonRequired] public List<GroupRecord?>? Groups { get; set; } = new();
    }
    private sealed class GroupRecord
    {
        public GroupRecord() { }
        public string? Id { get; set; }
        public string? Label { get; set; }
        public List<PhotoRecord?>? Photos { get; set; }
    }
    private sealed class PhotoRecord
    {
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
        public string? DetectionStatus { get; set; }
        public double? ViewZoom { get; set; }
        public double? ViewCenterX { get; set; }
        public double? ViewCenterY { get; set; }
        // Optional v2 extension: manual viewing coordinates can belong to a newer source than bird boxes.
        public GeometryRecord? ViewReference { get; set; }
    }
    private sealed class AssetRecord
    {
        public AssetRecord() { }
        public string? Id { get; set; }
        public string? FilePath { get; set; }
        public string? Kind { get; set; }
    }
    private sealed class GeometryRecord
    {
        public GeometryRecord() { }
        public string? AssetId { get; set; }
        public string? FilePath { get; set; }
        public int? DisplayWidth { get; set; }
        public int? DisplayHeight { get; set; }
        public long? FileLength { get; set; }
        public long? LastWriteTimeUtcTicks { get; set; }
    }
    private sealed class BirdRecord
    {
        public BirdRecord() { }
        public BirdBox? Box { get; set; }
        public int Number { get; set; }
        public bool IsManual { get; set; }
        public SpeciesMatch? Species { get; set; }
        public string? RecognitionStatus { get; set; }
    }
}
