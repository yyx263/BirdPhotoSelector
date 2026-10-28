using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BirdPhotoSelector;

public sealed class ProjectLoadResult
{
    public List<PhotoGroup> Groups { get; } = new();
    public double GroupingThreshold { get; internal set; } = BirdPhotoSelector.GroupingThreshold.Default;
    public int SkippedMissing { get; internal set; }
    public int SkippedInvalid { get; internal set; }
    public int SkippedDuplicate { get; internal set; }
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
        var document = new ProjectDocument
        {
            GroupingThreshold = groupingThreshold,
            GroupingThresholdScaleVersion = GroupingThreshold.ScaleVersion,
            Groups = groups.Select(group => (GroupRecord?)new GroupRecord
            {
                Id = group.Id, Label = group.Label,
                Photos = group.Photos.Select(photo => (PhotoRecord?)new PhotoRecord
                {
                    Id = photo.Id, FilePath = photo.FilePath, Rating = photo.Rating,
                    Bird = photo.Bird?.Copy(), DetectionStatus = photo.DetectionStatus,
                    SelectedBirdIndex = photo.SelectedBirdIndex,
                    SelectedBirdNumber = photo.SelectedBird?.Number, NextBirdNumber = photo.NextBirdNumber,
                    Birds = photo.Birds.Select(bird => (BirdRecord?)new BirdRecord
                    { Number = bird.Number, IsManual = bird.IsManual, Box = bird.Box.Copy(), Species = bird.Species, RecognitionStatus = bird.RecognitionStatus }).ToList(),
                    ViewZoom = photo.ViewZoom, ViewCenterX = photo.ViewCenterX, ViewCenterY = photo.ViewCenterY
                }).ToList()
            }).ToList()
        };
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

    public Task<ProjectLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            if (!IsProjectPath(path)) throw new InvalidDataException("仅支持 .json 工程文件。");
            string projectPath = Path.GetFullPath(path);
            await using var stream = File.OpenRead(projectPath);
            var document = await JsonSerializer.DeserializeAsync<ProjectDocument>(stream, JsonOptions, cancellationToken)
                ?? throw new InvalidDataException("工程文件为空。");
            if (document.Schema != "BirdPhotoSelector.Project" || document.Version != 1)
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
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var groupRecord in document.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (groupRecord?.Photos is null) { result.SkippedInvalid++; continue; }
                var group = new PhotoGroup(groupRecord.Label ?? "");
                if (!string.IsNullOrWhiteSpace(groupRecord.Id)) group.Id = groupRecord.Id;
                foreach (var record in groupRecord.Photos)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (record is null || string.IsNullOrWhiteSpace(record.FilePath))
                    { result.SkippedInvalid++; continue; }
                    try
                    {
                        string photoPath = Path.GetFullPath(record.FilePath, Path.GetDirectoryName(projectPath)!);
                        if (!File.Exists(photoPath)) { result.SkippedMissing++; continue; }
                        if (!PhotoImportService.IsJpegExtension(photoPath)) { result.SkippedInvalid++; continue; }
                        if (names.Contains(Path.GetFileName(photoPath))) { result.SkippedDuplicate++; continue; }
                        var photo = PhotoImportService.LoadPhoto(photoPath);
                        if (!string.IsNullOrWhiteSpace(record.Id) && ids.Add(record.Id)) photo.Id = record.Id;
                        else ids.Add(photo.Id);
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
                                Species = NormalizeSpecies(entry.Bird.Species),
                                RecognitionStatus = entry.Bird.RecognitionStatus ?? "待识别"
                            }), Math.Max(0, selected), record.NextBirdNumber);
                        }
                        else photo.SetBirds(record.Bird is { IsValid: true } legacy
                            ? new[] { new DetectedBird { Number = record.SelectedBirdNumber ?? 0, Box = legacy } }
                            : Array.Empty<DetectedBird>(), nextBirdNumber: record.NextBirdNumber);
                        photo.DetectionStatus = record.DetectionStatus ?? (photo.Bird is null ? "尚未分析" : "已识别鸟类");
                        photo.ViewZoom = PositiveFinite(record.ViewZoom);
                        photo.ViewCenterX = UnitFinite(record.ViewCenterX);
                        photo.ViewCenterY = UnitFinite(record.ViewCenterY);
                        names.Add(photo.FileName);
                        group.Photos.Add(photo);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result.SkippedInvalid++;
                        result.Errors.Add($"{Path.GetFileName(record.FilePath)}：{ex.Message}");
                    }
                }
                if (group.Count > 0) result.Groups.Add(group);
            }
            return result;
        }, cancellationToken);

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
        [JsonRequired] public int Version { get; set; } = 1;
        public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.UtcNow;
        [JsonRequired] public double GroupingThreshold { get; set; } = 0.8;
        // Missing in 1.5 and earlier projects: those values are direct background scores.
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
        public string? FilePath { get; set; }
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
