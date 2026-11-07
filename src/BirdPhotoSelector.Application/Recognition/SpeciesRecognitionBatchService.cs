namespace BirdPhotoSelector;

/// <summary>识别批次进度，包含当前照片或选区说明，不依赖 UI 线程。</summary>
public sealed record SpeciesProgress(int Completed, int Total, string Message);
/// <summary>按照片 ID 和选区索引返回的独立识别结果，不能直接跨线程修改工程。</summary>
public sealed record SpeciesRecognitionOutcome(string PhotoId, int BirdIndex, SpeciesMatch? Species,
    string Status, string? Error = null);
/// <summary>批量鸟种结果、错误与跳过计数，保留人工修改和来源核对规则。</summary>
public sealed class SpeciesBatchResult
{
    public List<SpeciesRecognitionOutcome> Outcomes { get; } = new();
    public List<string> Errors { get; } = new();
    public bool MissingModel { get; internal set; }
    public int SkippedPhotos { get; internal set; }
    public int ManuallyEditedSkippedPhotos { get; internal set; }
}

/// <summary>对已有选区批量识别鸟种。图像读取、人工保护、进度和跳过规则属于用例，不进入模型实现。</summary>
public sealed class SpeciesRecognitionBatchService : IDisposable
{
    private readonly IPhotoImageDecoder _images;
    private readonly IBirdSpeciesRecognizer _recognizer;
    private readonly bool _ownsRecognizer;
    private readonly SemaphoreSlim _batchGate = new(1, 1);
    private bool _disposed;

    /// <summary>注入后端与推理器；默认不拥有外部模型，复用模型时由调用方管理其生命周期。</summary>
    public SpeciesRecognitionBatchService(IPhotoImageDecoder images, IBirdSpeciesRecognizer recognizer, bool ownsRecognizer = false)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
        _ownsRecognizer = ownsRecognizer;
    }

    /// <remarks>Snapshots paths and every stored bird box on the caller's UI thread; workers return detached results.</remarks>
    /// <summary>先快照所有选区与保护标记，再在业务批次闸门内异步识别，不在模型服务读取文件。</summary>
    public async Task<SpeciesBatchResult> RecognizeAsync(IReadOnlyList<PhotoItem> photos, bool chinaOnly = true,
        IProgress<SpeciesProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var jobs = photos.Select(photo => new PhotoJob(photo.Id, photo.ActivePreviewAsset?.CanonicalPath ?? photo.FilePath,
            photo.Birds.Select((bird, index) => (bird, index)).Where(entry => entry.bird.HasRegion)
                .Select(entry => new BirdJob(entry.index, entry.bird.DisplayNumber, entry.bird.Box.Copy())).ToArray(),
            photo.GeometryStatus == PhotoGeometryStatus.NeedsReview || photo.Birds.Any(bird => bird.HasRegion) && !photo.CanUseBirdGeometry,
            photo.HasManualSpeciesEdits || photo.Birds.Any(bird => bird.ManualSpecies is not null))).ToArray();
        await _batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => RecognizeBatch(jobs, chinaOnly, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _batchGate.Release(); }
    }

    /// <summary>只处理安全的有效选区，先准备模型再逐图读取，共享一幅原图的多鸟裁切并逐项报告结果。</summary>
    private SpeciesBatchResult RecognizeBatch(PhotoJob[] jobs, bool chinaOnly, IProgress<SpeciesProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = new SpeciesBatchResult();
        var safeJobs = jobs.Where(job => !job.ManuallyEdited && !job.NeedsGeometryReview).ToArray();
        int total = safeJobs.Sum(job => job.Birds.Length), completed = 0;
        progress?.Report(new(0, total, "正在准备本机鸟种识别…"));
        result.ManuallyEditedSkippedPhotos = jobs.Count(job => job.ManuallyEdited);
        result.SkippedPhotos = result.ManuallyEditedSkippedPhotos;
        foreach (var job in jobs.Where(job => !job.ManuallyEdited && job.NeedsGeometryReview))
        {
            result.SkippedPhotos++;
            result.Errors.Add($"{job.Path}：来源已变化，鸟框位置待核对；保留原选区与鸟种结果。");
        }
        if (safeJobs.Length == 0) return result;
        if (total == 0) { result.SkippedPhotos += safeJobs.Length; return result; }
        SpeciesRecognitionResult? unavailable;
        ObjectDisposedException.ThrowIf(_disposed, this);
        unavailable = _recognizer.Prepare(cancellationToken);
        if (unavailable is not null)
        {
            result.MissingModel = unavailable.MissingModel;
            result.Errors.Add(unavailable.Error ?? unavailable.Status);
            foreach (var job in safeJobs)
                foreach (var bird in job.Birds)
                    result.Outcomes.Add(new(job.Id, bird.Index, null, unavailable.Status, unavailable.Error));
            result.SkippedPhotos += safeJobs.Count(job => job.Birds.Length == 0);
            return result;
        }
        foreach (var job in safeJobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (job.Birds.Length == 0) { result.SkippedPhotos++; continue; }
            try
            {
                // One full-resolution image at a time preserves small-bird detail without retaining
                // the whole batch in memory. Every crop uses the EXIF-oriented coordinates.
                var image = _images.Decode(job.Path, cancellationToken: cancellationToken);
                foreach (var bird in job.Birds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new(completed, total, $"正在识别 {Path.GetFileName(job.Path)} · 鸟 {bird.DisplayNumber}"));
                    var outcome = _recognizer.Recognize(image, bird.Box, chinaOnly, cancellationToken);
                    result.MissingModel |= outcome.MissingModel;
                    result.Outcomes.Add(new(job.Id, bird.Index, outcome.Match, outcome.Status, outcome.Error));
                    if (outcome.Error is not null) result.Errors.Add($"{Path.GetFileName(job.Path)} · 鸟 {bird.DisplayNumber}：{outcome.Error}");
                    completed++;
                    progress?.Report(new(completed, total, $"已识别 {completed} / {total} 只鸟"));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
            {
                result.Errors.Add($"{Path.GetFileName(job.Path)}：{ex.Message}");
                foreach (var bird in job.Birds.Where(bird => !result.Outcomes.Any(outcome => outcome.PhotoId == job.Id && outcome.BirdIndex == bird.Index)))
                {
                    result.Outcomes.Add(new(job.Id, bird.Index, null, "照片读取或识别失败", ex.Message));
                    completed++;
                }
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>停止接收新批次；仅在明确取得所有权时释放模型，不释放可能仍在退出的异步闸门。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsRecognizer) _recognizer.Dispose();
    }
    /// <summary>记录本次识别目标的集合索引、可见编号及独立框副本。</summary>
    private sealed record BirdJob(int Index, int DisplayNumber, BirdBox Box);
    /// <summary>记录一个来源的全部框及人工/坐标保护标记，后台不再读取可变记录。</summary>
    private sealed record PhotoJob(string Id, string Path, BirdJob[] Birds, bool NeedsGeometryReview, bool ManuallyEdited);
}
