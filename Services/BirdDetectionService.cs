using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BirdPhotoSelector;

/// <summary>
/// Offline CPU inference for the COCO YOLOv10 end-to-end ONNX model.
/// Input: RGB NCHW, float 0..1, letterboxed with value 114. Output: [1,N,6] xyxy/score/class.
/// Boxes are normalized against the EXIF-oriented displayed image, not the letterboxed input.
/// </summary>
public sealed class BirdDetectionService : IDisposable
{
    private const int BirdClass = 14;
    private readonly string _modelPath;
    private readonly object _sync = new();
    private InferenceSession? _session;
    private string? _inputName;
    private int _inputWidth = 640, _inputHeight = 640;
    private bool _disposed;
    private string? _loadError;
    private DateTime _lastAttemptWriteTime;

    public BirdDetectionService(string? modelPath = null) => _modelPath = modelPath ??
        Path.Combine(AppContext.BaseDirectory, "models", "yolov10n.onnx");

    public string ModelPath => _modelPath;
    public bool IsModelMissing => !File.Exists(_modelPath);
    public double ConfidenceThreshold { get; set; } = 0.25;

    public BirdDetectionResult Detect(BitmapSource image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(_modelPath))
                return new(null, "未安装鸟类识别模型", true);
            if (!EnsureSession())
                return new(null, "鸟类识别模型无法加载", false, _loadError);

            var (input, contentWidth, contentHeight, padX, padY) = CreateInput(image, cancellationToken);
            using var runOptions = new RunOptions();
            using var registration = cancellationToken.Register(() => runOptions.Terminate = true);
            try
            {
                using var outputs = _session!.Run(
                    new[] { NamedOnnxValue.CreateFromTensor(_inputName!, input) },
                    _session.OutputNames, runOptions);
                cancellationToken.ThrowIfCancellationRequested();
                var birds = new List<BirdBox>();
                bool recognizedOutput = false;
                foreach (var output in outputs)
                {
                    Tensor<float> data;
                    try { data = output.AsTensor<float>(); }
                    catch (InvalidCastException) { continue; }
                    var dimensions = data.Dimensions.ToArray();
                    bool batched = dimensions.Length == 3 && dimensions[0] == 1 && dimensions[2] == 6;
                    bool unbatched = dimensions.Length == 2 && dimensions[1] == 6;
                    if (!batched && !unbatched) continue;
                    recognizedOutput = true;
                    int count = batched ? dimensions[1] : dimensions[0];
                    float Value(int row, int column) => batched ? data[0, row, column] : data[row, column];
                    for (int row = 0; row < count; row++)
                    {
                        double confidence = Value(row, 4), category = Value(row, 5);
                        if (!double.IsFinite(confidence) || confidence < ConfidenceThreshold || confidence > 1 ||
                            !double.IsFinite(category) || Math.Abs(category - BirdClass) > 0.1) continue;
                        double x1 = Value(row, 0), y1 = Value(row, 1), x2 = Value(row, 2), y2 = Value(row, 3);
                        if (!double.IsFinite(x1 + y1 + x2 + y2)) continue;
                        x1 = Math.Clamp((x1 - padX) / contentWidth, 0, 1);
                        x2 = Math.Clamp((x2 - padX) / contentWidth, 0, 1);
                        y1 = Math.Clamp((y1 - padY) / contentHeight, 0, 1);
                        y2 = Math.Clamp((y2 - padY) / contentHeight, 0, 1);
                        if (x2 <= x1 || y2 <= y1) continue;
                        birds.Add(new BirdBox { X = x1, Y = y1, Width = x2 - x1, Height = y2 - y1, Confidence = confidence });
                    }
                }
                if (!recognizedOutput)
                    return new(null, "模型输出格式不受支持", false, "需要 YOLOv10 end-to-end ONNX 模型，输出应为 [1,N,6]。");
                // Keep every distinct detected bird; numbering and the initial detail view start
                // with the largest box, independently of the detector's row/score order.
                var distinct = new List<BirdBox>();
                foreach (var bird in birds.OrderByDescending(b => b.Confidence))
                    if (!distinct.Any(existing => IntersectionOverUnion(existing, bird) > .7)) distinct.Add(bird);
                var ordered = distinct.OrderByDescending(b => b.Width * b.Height).ThenByDescending(b => b.Confidence)
                    .ThenBy(b => b.X).ThenBy(b => b.Y).ToArray();
                return ordered.Length == 0 ? new(null, "未检测到鸟，可手动查看") :
                    new(ordered[0], $"已定位 {ordered.Length} 只鸟 · 当前最大主体") { Birds = ordered };
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private static double IntersectionOverUnion(BirdBox a, BirdBox b)
    {
        double intersection = Math.Max(0, Math.Min(a.X + a.Width, b.X + b.Width) - Math.Max(a.X, b.X)) *
            Math.Max(0, Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y));
        return intersection / (a.Width * a.Height + b.Width * b.Height - intersection);
    }

    private bool EnsureSession()
    {
        if (_session is not null) return true;
        DateTime writeTime = File.GetLastWriteTimeUtc(_modelPath);
        if (_loadError is not null && writeTime == _lastAttemptWriteTime) return false;
        _lastAttemptWriteTime = writeTime;
        InferenceSession? candidate = null;
        try
        {
            using var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                InterOpNumThreads = 1,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 8)
            };
            candidate = new InferenceSession(_modelPath, options);
            if (candidate.InputMetadata.Count != 1) throw new InvalidDataException("识别模型应只有一个图像输入。");
            var input = candidate.InputMetadata.First();
            int[] dimensions = input.Value.Dimensions;
            if (input.Value.ElementType != typeof(float) || dimensions.Length != 4 ||
                (dimensions[0] > 0 && dimensions[0] != 1) || (dimensions[1] > 0 && dimensions[1] != 3))
                throw new InvalidDataException("识别模型需要 float RGB NCHW 输入。");
            _inputHeight = dimensions[2] > 0 ? dimensions[2] : 640;
            _inputWidth = dimensions[3] > 0 ? dimensions[3] : 640;
            if (_inputWidth is < 32 or > 2048 || _inputHeight is < 32 or > 2048)
                throw new InvalidDataException("识别模型输入尺寸不受支持。");
            _inputName = input.Key;
            _session = candidate;
            _loadError = null;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            candidate?.Dispose();
            _loadError = ex.Message;
            return false;
        }
    }

    private (DenseTensor<float> Input, int Width, int Height, int PadX, int PadY) CreateInput(
        BitmapSource image, CancellationToken cancellationToken)
    {
        double ratio = Math.Min((double)_inputWidth / image.PixelWidth, (double)_inputHeight / image.PixelHeight);
        int contentWidth = Math.Clamp((int)Math.Round(image.PixelWidth * ratio), 1, _inputWidth);
        int contentHeight = Math.Clamp((int)Math.Round(image.PixelHeight * ratio), 1, _inputHeight);
        int padX = (_inputWidth - contentWidth) / 2, padY = (_inputHeight - contentHeight) / 2;
        var transformed = new TransformedBitmap(image, new ScaleTransform(
            (double)contentWidth / image.PixelWidth, (double)contentHeight / image.PixelHeight));
        var rgb = new FormatConvertedBitmap(transformed, PixelFormats.Bgr24, null, 0);
        // Use the actual WIC result size to account for floating point rounding in transforms.
        contentWidth = rgb.PixelWidth;
        contentHeight = rgb.PixelHeight;
        padX = (_inputWidth - contentWidth) / 2;
        padY = (_inputHeight - contentHeight) / 2;
        int stride = contentWidth * 3;
        var pixels = new byte[stride * contentHeight];
        rgb.CopyPixels(pixels, stride, 0);
        var buffer = new float[3 * _inputWidth * _inputHeight];
        Array.Fill(buffer, 114f / 255);
        int plane = _inputWidth * _inputHeight;
        for (int y = 0; y < contentHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < contentWidth; x++)
            {
                int pixel = y * stride + x * 3, target = (y + padY) * _inputWidth + x + padX;
                buffer[target] = pixels[pixel + 2] / 255f;
                buffer[plane + target] = pixels[pixel + 1] / 255f;
                buffer[plane * 2 + target] = pixels[pixel] / 255f;
            }
        }
        return (new DenseTensor<float>(buffer, new[] { 1, 3, _inputHeight, _inputWidth }),
            contentWidth, contentHeight, padX, padY);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _session?.Dispose();
            _session = null;
        }
    }
}

public sealed record BirdDetectionResult(BirdBox? Bird, string Status, bool MissingModel = false, string? Error = null)
{
    public IReadOnlyList<BirdBox> Birds { get; init; } = Bird is null ? Array.Empty<BirdBox>() : new[] { Bird };
}
