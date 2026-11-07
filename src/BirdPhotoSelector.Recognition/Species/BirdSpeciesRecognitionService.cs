using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BirdPhotoSelector;

/// <summary>单只鸟的模型结果；相似度和置信度沿用原有语义，不直接修改领域对象。</summary>
public sealed record SpeciesRecognitionResult(SpeciesMatch? Match, string Status,
    bool MissingModel = false, string? Error = null);

/// <summary>
/// 基于固定本地候选表的离线 BioCLIP 2 图像/文本相似度模型。CPU 会话延迟加载，实例由拥有方释放。
/// 模型只接受已经解码的图像，不读取照片文件、不管理照片批次，也不修改领域对象。
/// 相似度保留原始余弦值，置信度来自学习温度下的候选竞争，不代表校准后的准确率。
/// </summary>
public sealed class BirdSpeciesRecognitionService : IBirdSpeciesRecognizer
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly Func<DenseTensor<float>, CancellationToken, float[]>? _encoderOverride;
    private InferenceSession? _session;
    private SpeciesCandidateCatalog? _catalog;
    private string? _inputName, _outputName, _loadError, _lastAssetStamp;
    private bool _disposed;

    /// <param name="encoderOverride">Optional deterministic encoder for tests or a compatible adapter. Output is a 768-dimensional embedding.</param>
    /// <summary>创建延迟加载的鸟种识别服务；模型和候选表由本服务管理；输入必须已经解码，不在模型层读取照片文件。</summary>
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

    /// <summary>阻塞式逐鸟 CPU 推理；完整照片列表应交给业务层批次用例，在后台线程调用此方法。</summary>
    public SpeciesRecognitionResult Recognize(ImageFrame orientedImage, BirdBox box, bool chinaOnly = true,
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

    /// <summary>延迟准备模型与候选表；成功返回 null，不可用时返回带原因的结果，取消继续向上传播。</summary>
    public SpeciesRecognitionResult? Prepare(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return EnsureReady(cancellationToken);
        }
    }

    /// <summary>校验固定候选和模型元数据，延迟创建 CPU 会话；失败保留原因，资源版本变化后允许重试。</summary>
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

    /// <summary>使用可取消 ONNX 调用输出图像嵌入，所有原生结果在离开方法时释放。</summary>
    private float[] Encode(DenseTensor<float> input, CancellationToken cancellationToken)
    {
        using var options = new RunOptions();
        using var registration = cancellationToken.Register(() => options.Terminate = true);
        using var output = _session!.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName!, input) }, new[] { _outputName! }, options);
        cancellationToken.ThrowIfCancellationRequested();
        return output.First().AsTensor<float>().ToArray();
    }

    /// <summary>用模型与候选文件的长度/时间形成失败重试版本标识，不在每次推理重复加载。</summary>
    private string AssetStamp() => string.Join("|", RequiredFiles.Concat(new[] { "vision.onnx" }).Select(name =>
    {
        var file = new FileInfo(Path.Combine(_directory, name));
        return file.Exists ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks}" : "missing";
    }));

    /// <summary>在推理锁下释放模型和候选引用，文件批次生命周期由业务层独立管理。</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose(); _session = null; _catalog = null;
        }
    }
}
