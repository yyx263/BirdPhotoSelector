using System.IO;
using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

public sealed record AnalyzeProgress(int Completed, int Total, string Message);

public sealed class PhotoAnalysisOutcome
{
    public required string PhotoId { get; init; }
    public BirdBox? Bird { get; init; }
    public IReadOnlyList<BirdBox> Birds { get; init; } = Array.Empty<BirdBox>();
    public bool PreservedExistingBirds { get; init; }
    public string DetectionStatus { get; init; } = "尚未分析";
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public string? Error { get; init; }
}

public sealed class AnalyzeResult
{
    public List<List<PhotoItem>> Groups { get; } = new();
    public List<PhotoAnalysisOutcome> Outcomes { get; } = new();
    public bool MissingModel { get; internal set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Performs CPU and file work off the UI thread and returns detached results.
/// The caller applies those results to PhotoItem and group collections on the UI thread.
/// </summary>
public sealed class AnalysisService : IDisposable
{
    private readonly BirdDetectionService _detector;
    private readonly SemaphoreSlim _analysisLock = new(1, 1);
    private bool _disposed;

    public AnalysisService(string? modelPath = null) => _detector = new BirdDetectionService(modelPath);

    public async Task<AnalyzeResult> AnalyzeAsync(IReadOnlyList<PhotoItem> photos, double similarityThreshold,
        IProgress<AnalyzeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(similarityThreshold) || similarityThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(similarityThreshold), "相似度阈值应在 0 到 1 之间。");
        // Snapshot UI-owned properties before starting the worker. No worker raises PropertyChanged.
        var jobs = photos.Select(photo => new AnalysisJob(photo, photo.Id, photo.FilePath,
            photo.PixelWidth, photo.PixelHeight, photo.Birds.Select(b => b.Box.Copy()).ToArray(),
            photo.Birds.Where(b => b.IsManual).Select(b => b.Box.Copy()).ToArray())).ToArray();
        await _analysisLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Analyze(jobs, similarityThreshold, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _analysisLock.Release(); }
    }

    private AnalyzeResult Analyze(AnalysisJob[] jobs, double threshold, IProgress<AnalyzeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new AnalyzeResult { MissingModel = _detector.IsModelMissing };
        BackgroundDescriptor? previous = null, representative = null;
        List<PhotoItem>? currentGroup = null;
        progress?.Report(new(0, jobs.Length, "正在准备分析…"));
        for (int i = 0; i < jobs.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = jobs[i];
            string name = Path.GetFileName(job.Path);
            progress?.Report(new(i, jobs.Length, $"正在识别与分析 {name}"));
            try
            {
                var image = ImageLoader.Load(job.Path, maxWidth: 1280);
                BirdDetectionResult detection;
                try { detection = _detector.Detect(image, cancellationToken); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    detection = new(null, "鸟类识别失败，可手动查看", false, ex.Message);
                }
                IReadOnlyList<BirdBox> birds = detection.Birds;
                string status = detection.Status;
                bool preserved = false;
                // A missing/broken model should not erase a valid location imported from a project.
                if (birds.Count == 0 && (detection.MissingModel || detection.Error is not null) && job.ExistingBirds.Length > 0)
                {
                    birds = job.ExistingBirds;
                    preserved = true;
                    status += "；保留已有定位";
                }
                result.MissingModel |= detection.MissingModel;
                var descriptor = BackgroundSimilarity.DescribeAll(image, preserved ? birds : birds.Concat(job.ManualBirds));
                // Compare with both the previous frame and first frame to prevent gradual scene drift.
                bool joins = previous is not null && representative is not null && currentGroup is not null &&
                    BackgroundSimilarity.Compare(previous, descriptor) >= threshold &&
                    BackgroundSimilarity.Compare(representative, descriptor) >= threshold;
                if (!joins)
                {
                    currentGroup = new List<PhotoItem>();
                    result.Groups.Add(currentGroup);
                    representative = descriptor;
                }
                currentGroup!.Add(job.Photo);
                previous = descriptor;
                var (width, height) = job.Width > 0 && job.Height > 0 ? (job.Width, job.Height) : ReadDimensions(job.Path, image);
                result.Outcomes.Add(new PhotoAnalysisOutcome
                {
                    PhotoId = job.Id, Bird = birds.FirstOrDefault(), Birds = birds, PreservedExistingBirds = preserved, DetectionStatus = status,
                    PixelWidth = width, PixelHeight = height, Error = detection.Error
                });
                if (detection.Error is not null) result.Errors.Add($"{name}：{detection.Error}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Keep unreadable/missing images visible as singleton groups; a failure breaks continuity.
                result.Groups.Add(new List<PhotoItem> { job.Photo });
                previous = representative = null;
                currentGroup = null;
                result.Outcomes.Add(new PhotoAnalysisOutcome
                {
                    PhotoId = job.Id, Bird = job.ExistingBirds.FirstOrDefault(), Birds = job.ExistingBirds,
                    PreservedExistingBirds = true, DetectionStatus = "图片读取失败",
                    PixelWidth = job.Width, PixelHeight = job.Height, Error = ex.Message
                });
                result.Errors.Add($"{name}：{ex.Message}");
            }
            progress?.Report(new(i + 1, jobs.Length, $"已分析 {i + 1} / {jobs.Length}"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static (int Width, int Height) ReadDimensions(string path, BitmapSource fallback)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int orientation = ImageLoader.GetOrientation(frame.Metadata as BitmapMetadata);
            return orientation is >= 5 and <= 8 ? (frame.PixelHeight, frame.PixelWidth) : (frame.PixelWidth, frame.PixelHeight);
        }
        catch { return (fallback.PixelWidth, fallback.PixelHeight); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _detector.Dispose();
        // Do not dispose the semaphore while a canceled worker may still be releasing it.
    }

    private sealed record AnalysisJob(PhotoItem Photo, string Id, string Path, int Width, int Height, BirdBox[] ExistingBirds, BirdBox[] ManualBirds);
}
