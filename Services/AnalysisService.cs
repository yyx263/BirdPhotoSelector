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
    // Grouping is independent of both inference models.
    public bool MissingModel { get; internal set; }
    public List<string> Errors { get; } = new();
}

/// <summary>
/// Groups existing photos by background without detecting birds or changing their stored results.
/// The actual background similarity cutoff is supplied by the caller.
/// </summary>
public sealed class AnalysisService : IDisposable
{
    private readonly SemaphoreSlim _analysisLock = new(1, 1);
    private bool _disposed;

    // Retained for source compatibility; grouping no longer reads or loads this model.
    public AnalysisService(string? modelPath = null) { }

    public async Task<AnalyzeResult> AnalyzeAsync(IReadOnlyList<PhotoItem> photos, double similarityThreshold,
        IProgress<AnalyzeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(similarityThreshold) || similarityThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(similarityThreshold), "相似度阈值应在 0 到 1 之间。");
        var jobs = photos.Select(photo => new AnalysisJob(photo, photo.Id, photo.ActivePreviewAsset?.CanonicalPath ?? photo.FilePath,
            photo.PixelWidth, photo.PixelHeight, photo.Birds.Select(b => b.Box.Copy()).ToArray(),
            photo.DetectionStatus, photo.CanUseBirdGeometry)).ToArray();
        await _analysisLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Analyze(jobs, similarityThreshold, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _analysisLock.Release(); }
    }

    private static AnalyzeResult Analyze(AnalysisJob[] jobs, double threshold, IProgress<AnalyzeProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new AnalyzeResult();
        BackgroundDescriptor? previous = null, representative = null;
        List<PhotoItem>? currentGroup = null;
        progress?.Report(new(0, jobs.Length, "正在准备背景分组…"));
        for (int i = 0; i < jobs.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var job = jobs[i];
            string name = Path.GetFileName(job.Path);
            progress?.Report(new(i, jobs.Length, $"正在背景分组 {i + 1} / {jobs.Length} · {job.Path}"));
            try
            {
                var image = ImageLoader.Load(job.Path, maxWidth: 1280, cancellationToken: cancellationToken);
                // Previously found automatic and manual boxes can improve background comparison,
                // but grouping never reruns localization or rewrites the existing bird collection.
                var descriptor = BackgroundSimilarity.DescribeAll(image,
                    job.CanUseBirdGeometry ? job.ExistingBirds : Array.Empty<BirdBox>());
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
                var (width, height) = job.Width > 0 && job.Height > 0 ? (job.Width, job.Height) : ReadDimensions(job.Path, image, cancellationToken);
                result.Outcomes.Add(new PhotoAnalysisOutcome
                {
                    PhotoId = job.Id, Bird = job.ExistingBirds.FirstOrDefault(), Birds = job.ExistingBirds,
                    PreservedExistingBirds = true, DetectionStatus = job.DetectionStatus,
                    PixelWidth = width, PixelHeight = height
                });
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result.Groups.Add(new List<PhotoItem> { job.Photo });
                previous = representative = null;
                currentGroup = null;
                result.Outcomes.Add(new PhotoAnalysisOutcome
                {
                    PhotoId = job.Id, Bird = job.ExistingBirds.FirstOrDefault(), Birds = job.ExistingBirds,
                    PreservedExistingBirds = true, DetectionStatus = job.DetectionStatus,
                    PixelWidth = job.Width, PixelHeight = job.Height, Error = ex.Message
                });
                result.Errors.Add($"{name}：{ex.Message}");
            }
            progress?.Report(new(i + 1, jobs.Length, $"已完成背景分组 {i + 1} / {jobs.Length} · {job.Path}"));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static (int Width, int Height) ReadDimensions(string path, BitmapSource fallback, CancellationToken cancellationToken)
    {
        try
        {
            if (PhotoAsset.TryGetKind(path, out var kind) && kind == PhotoAssetKind.Raw)
            {
                var info = RawImageDecoder.ReadInfo(path, cancellationToken);
                return (info.PixelWidth, info.PixelHeight);
            }
            using var stream = File.OpenRead(path);
            var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int orientation = ImageLoader.GetOrientation(frame.Metadata as BitmapMetadata);
            return orientation is >= 5 and <= 8 ? (frame.PixelHeight, frame.PixelWidth) : (frame.PixelWidth, frame.PixelHeight);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (fallback.PixelWidth, fallback.PixelHeight); }
    }

    public void Dispose() => _disposed = true;

    private sealed record AnalysisJob(PhotoItem Photo, string Id, string Path, int Width, int Height,
        BirdBox[] ExistingBirds, string DetectionStatus, bool CanUseBirdGeometry);
}
