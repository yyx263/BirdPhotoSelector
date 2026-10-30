using System.IO;
using System.Windows.Media.Imaging;

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
    private readonly BirdDetectionService _detector;
    private readonly BirdSpeciesRecognitionService _recognizer;
    private readonly Func<BitmapSource, CancellationToken, BirdDetectionResult>? _detectOverride;
    private readonly Func<BitmapSource, BirdBox, bool, CancellationToken, SpeciesRecognitionResult>? _recognizeOverride;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public BirdRecognitionWorkflow(string? detectionModelPath = null, string? speciesAssetsDirectory = null,
        Func<BitmapSource, CancellationToken, BirdDetectionResult>? detectorOverride = null,
        Func<BitmapSource, BirdBox, bool, CancellationToken, SpeciesRecognitionResult>? recognizerOverride = null)
    {
        _detector = new(detectionModelPath);
        _recognizer = new(speciesAssetsDirectory);
        _detectOverride = detectorOverride;
        _recognizeOverride = recognizerOverride;
    }

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
                var image = ImageLoader.Load(job.Path, cancellationToken: cancellationToken);
                BirdDetectionResult detection;
                try
                {
                    detection = _detectOverride is not null ? _detectOverride(image, cancellationToken) :
                        _detector.Detect(image, cancellationToken);
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
                        recognition = _recognizeOverride is not null ? _recognizeOverride(image, bird.Box, chinaOnly, cancellationToken) :
                            _recognizer.Recognize(image, bird.Box, chinaOnly, cancellationToken);
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

    private static double IntersectionOverUnion(BirdBox first, BirdBox second)
    {
        double area = Math.Max(0, Math.Min(first.X + first.Width, second.X + second.Width) - Math.Max(first.X, second.X)) *
            Math.Max(0, Math.Min(first.Y + first.Height, second.Y + second.Height) - Math.Max(first.Y, second.Y));
        return area / (first.Width * first.Height + second.Width * second.Height - area);
    }

    private static DetectedBird CopyBird(DetectedBird bird) => bird.Copy();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detector.Dispose();
        _recognizer.Dispose();
    }

    private sealed record PhotoJob(string Id, string Path, DetectedBird[] Birds, int SelectedBirdNumber,
        int NextBirdNumber, string DetectionStatus, int Width, int Height, bool NeedsGeometryReview, bool ManuallyEdited);
}
