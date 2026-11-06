using System.IO;

namespace BirdPhotoSelector;

/// <summary>A detached per-photo result, applied explicitly by the UI after the batch completes.</summary>
public sealed class PhotoRecognitionOutcome
{
    public required string PhotoId { get; init; }
    public IReadOnlyList<DetectedBird> Birds { get; init; } = Array.Empty<DetectedBird>();
    public int SelectedBirdNumber { get; init; }
    public int NextBirdNumber { get; init; } = 1;
    public string DetectionStatus { get; init; } = "尚未分析";
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public int MatchedBirds { get; init; }

    /// <summary>仅在会话线程显式应用指定照片的独立结果，保留可复用旧对象及人工选择，不在后台直接写集合。</summary>
    public void ApplyTo(PhotoItem photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (photo.Id != PhotoId) throw new ArgumentException("识别结果与照片不匹配。", nameof(photo));
        if (photo.Birds.Count > 0 && (photo.HasManualSpeciesEdits || photo.Birds.Any(bird => bird.ManualSpecies is not null))) throw new InvalidOperationException("照片已有人工编辑，未应用批量识别结果。");
        bool hadLocatedBirds = photo.Birds.Any(bird => bird.HasRegion);
        var original = photo.Birds.ToDictionary(bird => bird.Number);
        var applied = Birds.Select(bird =>
        {
            // Keep manual regions and unchanged automatic objects so current UI selections
            // and bindings remain attached to the same objects.
            if (original.TryGetValue(bird.Number, out var previous) && previous.IsManual == bird.IsManual && previous.ManualSpecies == bird.ManualSpecies &&
                SameBox(previous.Box, bird.Box))
            {
                previous.Species = bird.Species;
                previous.RecognitionStatus = bird.RecognitionStatus;
                return previous;
            }
            return bird;
        }).ToArray();
        int selected = Array.FindIndex(applied, bird => bird.Number == SelectedBirdNumber);
        photo.SetBirds(applied, Math.Max(0, selected), NextBirdNumber);
        photo.DetectionStatus = DetectionStatus;
        if (PixelWidth > 0 && PixelHeight > 0)
        {
            photo.PixelWidth = PixelWidth;
            photo.PixelHeight = PixelHeight;
        }
        // A photo may already have a fit-to-window view saved from browsing before its
        // first localization. Let ShowPhoto initialize the bird detail view next time.
        // Existing automatic/manual regions keep the user's saved zoom and pan.
        if (!hadLocatedBirds && photo.Birds.Any(bird => bird.HasRegion))
        {
            if (photo.ActivePreviewAsset is { } active)
                photo.SetGeometryReference(PhotoGeometryReference.Capture(active, photo.PixelWidth, photo.PixelHeight), PhotoGeometryStatus.Current);
            photo.ViewZoom = photo.ViewCenterX = photo.ViewCenterY = null;
        }
    }

    /// <summary>逐坐标与置信度比较框，确定异步结果是否可复用旧记录对象。</summary>
    private static bool SameBox(BirdBox first, BirdBox second) =>
        first.X == second.X && first.Y == second.Y && first.Width == second.Width &&
        first.Height == second.Height && first.Confidence == second.Confidence;
}

public sealed class BirdRecognitionBatchResult
{
    public List<PhotoRecognitionOutcome> Outcomes { get; } = new();
    public List<string> Errors { get; } = new();
    public bool MissingDetectionModel { get; internal set; }
    public bool MissingSpeciesModel { get; internal set; }
    public bool MissingModel => MissingDetectionModel || MissingSpeciesModel;
    public int SkippedPhotos { get; internal set; }
    public int ManuallyEditedSkippedPhotos { get; internal set; }
    public int MatchedBirds => Outcomes.Sum(outcome => outcome.MatchedBirds);
}

/// <summary>
/// The recognition command localizes birds and then identifies every automatic/manual region.
/// It snapshots UI-owned state and returns detached results; cancellation applies no partial edits.
/// </summary>
public sealed class BirdRecognitionWorkflow : IDisposable
{
    private readonly IBirdDetector _detector;
    private readonly IBirdSpeciesRecognizer _recognizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IPhotoImageDecoder _images;
    private bool _disposed;

    /// <summary>接管注入的定位/鸟种推理器；使用同一图像后端并返回独立结果，调用方显式应用后才修改工程。</summary>
    public BirdRecognitionWorkflow(IPhotoImageDecoder images, IBirdDetector detector, IBirdSpeciesRecognizer recognizer)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
    }

    /// <summary>在调用线程快照原件和已有结果，后台定位/逐鸟识别，取消不应用任何部分修改。</summary>
    public async Task<BirdRecognitionBatchResult> RecognizeAsync(IReadOnlyList<PhotoItem> photos, bool chinaOnly = true,
        IProgress<SpeciesProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var jobs = photos.Select(photo => new PhotoJob(photo.Id, photo.ActivePreviewAsset?.CanonicalPath ?? photo.FilePath,
            photo.Birds.Select(CopyBird).ToArray(), photo.SelectedBird?.Number ?? 0,
            photo.NextBirdNumber, photo.DetectionStatus, photo.PixelWidth, photo.PixelHeight,
            photo.Birds.Any(bird => bird.HasRegion) && (photo.GeometryStatus == PhotoGeometryStatus.NeedsReview || !photo.CanUseBirdGeometry),
            photo.Birds.Count > 0 && (photo.HasManualSpeciesEdits || photo.Birds.Any(bird => bird.ManualSpecies is not null)))).ToArray();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Recognize(jobs, chinaOnly, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>按快照处理全部照片，跳过非空人工修改和待核对坐标，失败保留已有证据。</summary>
    private BirdRecognitionBatchResult Recognize(PhotoJob[] jobs, bool chinaOnly, IProgress<SpeciesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new BirdRecognitionBatchResult();
        for (int index = 0; index < jobs.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            var job = jobs[index];
            string label = $"{index + 1} / {jobs.Length} · {Path.GetFileName(job.Path)} · {job.Path}";
            if (job.ManuallyEdited)
            {
                result.SkippedPhotos++; result.ManuallyEditedSkippedPhotos++;
                progress?.Report(new(index + 1, jobs.Length, $"已跳过人工编辑照片 {label}"));
                continue;
            }
            if (job.NeedsGeometryReview)
            {
                result.SkippedPhotos++;
                result.Errors.Add($"{job.Path}：来源已变化，鸟框位置待核对；保留原选区与鸟种结果。");
                progress?.Report(new(index + 1, jobs.Length, $"已跳过待核对鸟框 {label}"));
                continue;
            }
            progress?.Report(new(index, jobs.Length, $"正在定位鸟类 {label}"));
            try
            {
                var image = _images.Decode(job.Path, cancellationToken: cancellationToken);
                BirdDetectionResult detection;
                try
                {
                    detection = _detector.Detect(image, cancellationToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
                {
                    detection = new(null, "鸟类定位失败", false, ex.Message);
                }
                result.MissingDetectionModel |= detection.MissingModel;
                if (detection.Error is not null) result.Errors.Add($"{job.Path}：{detection.Error}");
                bool preserve = detection.MissingModel || detection.Error is not null ||
                    detection.Birds.Count == 0 && job.Birds.Any(bird => bird.HasRegion && !bird.IsManual);
                int nextNumber = job.NextBirdNumber;
                var birds = preserve ? job.Birds.Select(CopyBird).ToArray() :
                    MergeDetectedBirds(job.Birds, detection.Birds, ref nextNumber);
                string detectionStatus = detection.Status + (preserve && job.Birds.Length > 0 ? "；保留已有定位" : "");
                int matched = 0;
                bool recognitionFailed = false;
                for (int birdIndex = 0; birdIndex < birds.Length; birdIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var bird = birds[birdIndex];
                    if (!bird.HasRegion) continue;
                    progress?.Report(new(index, jobs.Length,
                        $"正在识别鸟种 {label} · 选区 {birdIndex + 1} / {birds.Length}（鸟 {birdIndex + 1}）"));
                    SpeciesRecognitionResult recognition;
                    try
                    {
                        recognition = _recognizer.Recognize(image, bird.Box, chinaOnly, cancellationToken);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
                    {
                        recognition = new(null, "鸟种识别失败", false, ex.Message);
                    }
                    result.MissingSpeciesModel |= recognition.MissingModel;
                    if (recognition.Match is { IsValid: true } match) { bird.Species = match; matched++; }
                    else recognitionFailed = true;
                    bird.RecognitionStatus = recognition.Status +
                        (recognition.Match is not { IsValid: true } && bird.Species is not null ? "；保留上次结果" : "");
                    if (recognition.Error is not null)
                        result.Errors.Add($"{job.Path} · 鸟 {birdIndex + 1}：{recognition.Error}");
                }
                // A failed species stage must not discard old evidence when a changed detector
                // output could not be matched back to a previous automatic region.
                if (recognitionFailed)
                    birds = birds.Concat(job.Birds.Where(previous =>
                        !birds.Any(current => current.Number == previous.Number)).Select(CopyBird)).ToArray();
                if (birds.Length == 0) result.SkippedPhotos++;
                result.Outcomes.Add(new PhotoRecognitionOutcome
                {
                    PhotoId = job.Id, Birds = birds, SelectedBirdNumber = job.SelectedBirdNumber,
                    NextBirdNumber = nextNumber, DetectionStatus = detectionStatus,
                    PixelWidth = image.PixelWidth, PixelHeight = image.PixelHeight, MatchedBirds = matched
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
            {
                result.Errors.Add($"{job.Path}：{ex.Message}");
                result.Outcomes.Add(new PhotoRecognitionOutcome
                {
                    PhotoId = job.Id, Birds = job.Birds, SelectedBirdNumber = job.SelectedBirdNumber,
                    NextBirdNumber = job.NextBirdNumber, DetectionStatus = job.DetectionStatus,
                    PixelWidth = job.Width, PixelHeight = job.Height
                });
                if (job.Birds.Length == 0) result.SkippedPhotos++;
            }
            progress?.Report(new(index + 1, jobs.Length, $"已处理 {label}"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>用框重叠匹配旧自动结果，复用稳定编号，保留人工框及无框人工鸟种。</summary>
    private static DetectedBird[] MergeDetectedBirds(DetectedBird[] previous, IReadOnlyList<BirdBox> detected,
        ref int nextNumber)
    {
        var unused = previous.Where(bird => bird.HasRegion && !bird.IsManual).ToList();
        var merged = new List<DetectedBird>();
        foreach (var box in detected.Where(box => box.IsValid))
        {
            var match = unused.Select(bird => (Bird: bird, Overlap: IntersectionOverUnion(bird.Box, box)))
                .Where(candidate => candidate.Overlap >= .5).OrderByDescending(candidate => candidate.Overlap)
                .Select(candidate => candidate.Bird).FirstOrDefault();
            int number;
            if (match is not null) { unused.Remove(match); number = match.Number; }
            else
            {
                if (nextNumber >= int.MaxValue) throw new InvalidOperationException("选区序号已达到上限。");
                number = nextNumber++;
            }
            merged.Add(new DetectedBird
            {
                Number = number, Box = box.Copy(), Species = match?.Species,
                RecognitionStatus = match?.RecognitionStatus ?? "待识别"
            });
        }
        merged.AddRange(previous.Where(bird => bird.IsManual || bird.ManualSpecies is not null).Select(CopyBird));
        return merged.ToArray();
    }

    /// <summary>计算新旧框交并比，仅用于匹配，不能把附近不同鸟直接合并为同一身份。</summary>
    private static double IntersectionOverUnion(BirdBox first, BirdBox second)
    {
        double area = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X)) *
            Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        return area / (first.Width * first.Height + second.Width * second.Height - area);
    }

    /// <summary>深拷贝结果和鸟框，后台不会直接修改 UI 绑定对象。</summary>
    private static DetectedBird CopyBird(DetectedBird bird) => bird.Copy();

    /// <summary>拒绝后续调用并释放此用例接管的两个推理器，异步闸门等待既有操作自行退出。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detector.Dispose();
        _recognizer.Dispose();
    }

    /// <summary>一次批量识别所需的只读来源、旧结果和保护标记快照。</summary>
    private sealed record PhotoJob(string Id, string Path, DetectedBird[] Birds, int SelectedBirdNumber,
        int NextBirdNumber, string DetectionStatus, int Width, int Height, bool NeedsGeometryReview, bool ManuallyEdited);
}
