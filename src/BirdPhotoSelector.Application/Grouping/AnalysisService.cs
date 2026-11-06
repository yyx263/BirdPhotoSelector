using System.IO;

namespace BirdPhotoSelector;

/// <summary>背景分组的进度数据，由宿主决定怎样展示。</summary>
public sealed record AnalyzeProgress(int Completed, int Total, string Message);

/// <summary>一张照片的独立分组/来源读取结果，不重跑定位或覆盖已有鸟种。</summary>
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

/// <summary>保持导入顺序的分组提案及逐项失败信息，由调用方应用。</summary>
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
    private readonly IPhotoImageDecoder _images;
    private bool _disposed;

    /// <summary>只注入图像后端；背景分组不创建、读取或加载任何定位模型。</summary>
    public AnalysisService(IPhotoImageDecoder images) =>
        _images = images ?? throw new ArgumentNullException(nameof(images));

    /// <summary>在调用线程快照分组所需数据，再用注入的图像后端异步计算，不改写已有识别。</summary>
    public async Task<AnalyzeResult> AnalyzeAsync(IReadOnlyList<PhotoItem> photos, double similarityThreshold,
        IProgress<AnalyzeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!double.IsFinite(similarityThreshold) || similarityThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(similarityThreshold), "相似度阈值应在 0 到 1 之间。");
        var jobs = photos.Select(photo => new AnalysisJob(photo, photo.Id, photo.ActivePreviewAsset?.CanonicalPath ?? photo.FilePath,
            photo.PixelWidth, photo.PixelHeight, photo.Birds.Where(bird => bird.HasRegion).Select(b => b.Box.Copy()).ToArray(),
            photo.DetectionStatus, photo.CanUseBirdGeometry)).ToArray();
        await _analysisLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Analyze(jobs, similarityThreshold, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        finally { _analysisLock.Release(); }
    }

    /// <summary>顺序比较当前与上一张及组代表背景，防止渐变场景无限串组，保留所有旧结果。</summary>
    private AnalyzeResult Analyze(AnalysisJob[] jobs, double threshold, IProgress<AnalyzeProgress>? progress,
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
                var image = _images.Decode(job.Path, maxWidth: 1280, cancellationToken: cancellationToken);
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

    /// <summary>优先读取完整显示尺寸，头信息失败时只使用已解码帧的尺寸，不猜测旧来源。</summary>
    private (int Width, int Height) ReadDimensions(string path, ImageFrame fallback, CancellationToken cancellationToken)
    {
        try
        {
            var info = _images.ReadInfo(path, cancellationToken);
            return (info.PixelWidth, info.PixelHeight);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (fallback.PixelWidth, fallback.PixelHeight); }
    }

    /// <summary>停止接收新分组调用，不释放仍可能被异步操作使用的闸门。</summary>
    public void Dispose() => _disposed = true;

    /// <summary>分组所需的原件路径、显示尺寸和已有框快照，不包含控件或 Windows 位图。</summary>
    private sealed record AnalysisJob(PhotoItem Photo, string Id, string Path, int Width, int Height,
        BirdBox[] ExistingBirds, string DetectionStatus, bool CanUseBirdGeometry);
}
