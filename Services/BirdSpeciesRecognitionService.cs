using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BirdPhotoSelector;

public sealed record SpeciesProgress(int Completed, int Total, string Message);
public sealed record SpeciesRecognitionResult(SpeciesMatch? Match, string Status,
    bool MissingModel = false, string? Error = null);
public sealed record SpeciesRecognitionOutcome(string PhotoId, int BirdIndex, SpeciesMatch? Species,
    string Status, string? Error = null);
public sealed class SpeciesBatchResult
{
    public List<SpeciesRecognitionOutcome> Outcomes { get; } = new();
    public List<string> Errors { get; } = new();
    public bool MissingModel { get; internal set; }
    public int SkippedPhotos { get; internal set; }
    public int ManuallyEditedSkippedPhotos { get; internal set; }
}

/// <summary>
/// Offline BioCLIP 2 visual/text similarity over a pinned local species candidate catalog.
/// A single CPU session is loaded lazily; the model and catalog are released when the batch owner disposes.
/// Results never mutate PhotoItem or DetectedBird. Similarity remains raw cosine; confidence
/// is learned-temperature candidate competition, not a calibrated probability of correctness.
/// </summary>
public sealed class BirdSpeciesRecognitionService : IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _batchGate = new(1, 1);
    private readonly string _directory;
    private readonly Func<DenseTensor<float>, CancellationToken, float[]>? _encoderOverride;
    private InferenceSession? _session;
    private SpeciesCandidateCatalog? _catalog;
    private string? _inputName, _outputName, _loadError, _lastAssetStamp;
    private bool _disposed;

    /// <param name="encoderOverride">Optional deterministic encoder for tests or a compatible adapter. Output is a 768-dimensional embedding.</param>
    public BirdSpeciesRecognitionService(string? assetsDirectory = null,
        Func<DenseTensor<float>, CancellationToken, float[]>? encoderOverride = null)
    {
        _directory = Path.GetFullPath(assetsDirectory ?? Path.Combine(AppContext.BaseDirectory, "models", "bioclip2"));
        _encoderOverride = encoderOverride;
    }

    public string AssetsDirectory => _directory;
    public bool HasRequiredAssets => RequiredFiles.All(name => File.Exists(Path.Combine(_directory, name))) &&
        (_encoderOverride is not null || File.Exists(Path.Combine(_directory, "vision.onnx")));
    public bool SupportsGlobalCandidates { get { lock (_sync) return _catalog?.Scope == "Global"; } }
    private static readonly string[] RequiredFiles = { "metadata.json", "candidates.json", "embeddings.f32" };

    /// <summary>A blocking CPU operation; UI callers should use RecognizeAsync for the complete list.</summary>
    public SpeciesRecognitionResult Recognize(BitmapSource orientedImage, BirdBox box, bool chinaOnly = true,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orientedImage);
        ArgumentNullException.ThrowIfNull(box);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var unavailable = EnsureReady(cancellationToken);
            if (unavailable is not null) return unavailable;
            try
            {
                var input = SpeciesImagePreprocessor.CreateInput(orientedImage, box, cancellationToken);
                float[] embedding = _encoderOverride is not null
                    ? _encoderOverride(input, cancellationToken) : Encode(input, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var match = _catalog!.FindBest(embedding, chinaOnly, cancellationToken);
                return new(match, "已识别 · 候选置信度");
            }
            catch (OperationCanceledException) { throw; }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException(cancellationToken); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            { return new(null, "鸟种识别失败", false, ex.Message); }
        }
    }

    /// <summary>Snapshots paths and every stored bird box on the caller's UI thread; workers return detached results.</summary>
    public async Task<SpeciesBatchResult> RecognizeAsync(IReadOnlyList<PhotoItem> photos, bool chinaOnly = true,
        IProgress<SpeciesProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
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
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            unavailable = EnsureReady(cancellationToken);
        }
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
            lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
            if (job.Birds.Length == 0) { result.SkippedPhotos++; continue; }
            try
            {
                // One full-resolution image at a time preserves small-bird detail without retaining
                // the whole batch in memory. Every crop uses the EXIF-oriented coordinates.
                var image = ImageLoader.Load(job.Path, cancellationToken: cancellationToken);
                foreach (var bird in job.Birds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new(completed, total, $"正在识别 {Path.GetFileName(job.Path)} · 鸟 {bird.DisplayNumber}"));
                    var outcome = Recognize(image, bird.Box, chinaOnly, cancellationToken);
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

    private SpeciesRecognitionResult? EnsureReady(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HasRequiredAssets)
            return new(null, "尚未安装 BioCLIP 2 鸟种资源", true,
                "请运行 scripts/Setup-SpeciesModel.ps1，准备 models/bioclip2 下的 vision.onnx、candidates.json、embeddings.f32 和 metadata.json；如模型使用外部数据文件，也需保留同目录的数据文件。");
        if (_catalog is not null && (_session is not null || _encoderOverride is not null)) return null;
        string stamp = AssetStamp();
        if (_loadError is not null && stamp == _lastAssetStamp)
            return new(null, "BioCLIP 2 鸟种资源无法加载", false, _loadError);
        _lastAssetStamp = stamp;
        InferenceSession? candidate = null;
        try
        {
            var catalog = SpeciesCandidateCatalog.Load(_directory, cancellationToken);
            if (_encoderOverride is null)
            {
                using var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    InterOpNumThreads = 1,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
                };
                candidate = new InferenceSession(Path.Combine(_directory, "vision.onnx"), options);
                if (candidate.InputMetadata.Count != 1) throw new InvalidDataException("BioCLIP 2 图像模型应只有一个输入。");
                var input = candidate.InputMetadata.First();
                int[] dimensions = input.Value.Dimensions;
                if (input.Value.ElementType != typeof(float) || dimensions.Length != 4 ||
                    dimensions[0] > 0 && dimensions[0] != 1 || dimensions[1] != 3 || dimensions[2] != 224 || dimensions[3] != 224)
                    throw new InvalidDataException("BioCLIP 2 图像模型输入必须是 float32 [1,3,224,224]。");
                var output = candidate.OutputMetadata.FirstOrDefault(pair => pair.Value.ElementType == typeof(float) &&
                    pair.Value.Dimensions.Length is 1 or 2 && pair.Value.Dimensions[^1] == SpeciesCandidateCatalog.EmbeddingDimension);
                if (string.IsNullOrWhiteSpace(output.Key)) throw new InvalidDataException("BioCLIP 2 图像模型需要 768 维 float32 输出。");
                _inputName = input.Key; _outputName = output.Key;
                cancellationToken.ThrowIfCancellationRequested();
                _session = candidate; candidate = null;
            }
            _catalog = catalog;
            _loadError = null;
            return null;
        }
        catch (OperationCanceledException) { candidate?.Dispose(); throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            candidate?.Dispose();
            _loadError = ex.Message;
            return new(null, "BioCLIP 2 鸟种资源无法加载", false, ex.Message);
        }
    }

    private float[] Encode(DenseTensor<float> input, CancellationToken cancellationToken)
    {
        using var options = new RunOptions();
        using var registration = cancellationToken.Register(() => options.Terminate = true);
        using var output = _session!.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName!, input) }, new[] { _outputName! }, options);
        cancellationToken.ThrowIfCancellationRequested();
        return output.First().AsTensor<float>().ToArray();
    }

    private string AssetStamp() => string.Join("|", RequiredFiles.Concat(new[] { "vision.onnx" }).Select(name =>
    {
        var file = new FileInfo(Path.Combine(_directory, name));
        return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
    }));

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose(); _session = null; _catalog = null;
        }
        // A canceled async batch can still be releasing its semaphore; do not dispose it here.
    }
    private sealed record BirdJob(int Index, int DisplayNumber, BirdBox Box);
    private sealed record PhotoJob(string Id, string Path, BirdJob[] Birds, bool NeedsGeometryReview, bool ManuallyEdited);
}
