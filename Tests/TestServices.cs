using System.IO;
using BirdPhotoSelector;
using BirdPhotoSelector.Imaging;
using BirdPhotoSelector.Imaging.Windows;

/// <summary>Windows 回归的唯一测试组装点。故障委托只留在验证代码，生产用例只依赖正式接口。</summary>
internal static class TestServices
{
    internal static IPhotoImageDecoder Images { get; } = new BirdPhotoSelector.Imaging.Portable.PortablePhotoImageDecoder();
    internal static IExportFilePlatform Files { get; } = new SystemFileOperations();
    /// <summary>验证图像可跨线程读取，并验证实际 WPF 显示对象已冻结，不要求领域必须存储 WPF 类型。</summary>
    internal static bool IsSafeThumbnail(ImageFrame? frame) => frame is not null && WpfImageBridge.ToBitmap(frame).IsFrozen;
    internal static PhotoImportService Import(IPhotoImageDecoder? images = null) => new(images ?? Images, Files);
    internal static ProjectService Projects(IPhotoImageDecoder? images = null) => new(images ?? Images);
    internal static ExportService Export(Action<string>? recycleSource = null, Func<Stream, Stream, CancellationToken, Task>? copyOverride = null) => new(Files, recycleSource, copyOverride);
    internal static AutoSaveService AutoSave(string? recoveryDirectory = null, Func<DateTimeOffset>? clock = null, ProjectService? projects = null) => new(projects ?? Projects(), recoveryDirectory, clock);
    internal static AnalysisService Analysis(string? modelPath = null, IPhotoImageDecoder? images = null) => new(images ?? Images);
    internal static BirdRecognitionWorkflow Workflow(string? detectionModelPath = null, string? speciesAssetsDirectory = null,
        Func<ImageFrame, CancellationToken, BirdDetectionResult>? detectorOverride = null,
        Func<ImageFrame, BirdBox, bool, CancellationToken, SpeciesRecognitionResult>? recognizerOverride = null,
        IPhotoImageDecoder? images = null) => new(images ?? Images,
            detectorOverride is null ? new BirdDetectionService(detectionModelPath) : new Detector(detectorOverride),
            recognizerOverride is null ? new BirdSpeciesRecognitionService(speciesAssetsDirectory) : new Recognizer(recognizerOverride));
    internal static SinglePhotoRecognitionService Single(string? detectionModelPath = null, string? speciesAssetsDirectory = null,
        Func<ImageFrame, CancellationToken, BirdDetectionResult>? detectorOverride = null,
        Func<ImageFrame, BirdBox, bool, CancellationToken, SpeciesRecognitionResult>? recognizerOverride = null,
        Func<string, CancellationToken, ImageFrame>? loaderOverride = null, IPhotoImageDecoder? images = null) => new(
            loaderOverride is null ? images ?? Images : new Decoder(loaderOverride, images ?? Images),
            detectorOverride is null ? new BirdDetectionService(detectionModelPath) : new Detector(detectorOverride),
            recognizerOverride is null ? new BirdSpeciesRecognitionService(speciesAssetsDirectory) : new Recognizer(recognizerOverride));

    /// <summary>旧逐鸟回归场景明确创建业务批次，不再让模型服务读取照片文件。</summary>
    internal static async Task<SpeciesBatchResult> RecognizeAsync(this BirdSpeciesRecognitionService service,
        IReadOnlyList<PhotoItem> photos, bool chinaOnly = true, IProgress<SpeciesProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var batch = new SpeciesRecognitionBatchService(Images, service);
        return await batch.RecognizeAsync(photos, chinaOnly, progress, cancellationToken);
    }
    private sealed class Detector(Func<ImageFrame, CancellationToken, BirdDetectionResult> run) : IBirdDetector
    {
        public BirdDetectionResult Detect(ImageFrame image, CancellationToken cancellationToken = default) => run(image, cancellationToken);
        public void Dispose() { }
    }
    private sealed class Recognizer(Func<ImageFrame, BirdBox, bool, CancellationToken, SpeciesRecognitionResult> run) : IBirdSpeciesRecognizer
    {
        public SpeciesRecognitionResult? Prepare(CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return null; }
        public SpeciesRecognitionResult Recognize(ImageFrame image, BirdBox box, bool chinaOnly = true, CancellationToken cancellationToken = default) => run(image, box, chinaOnly, cancellationToken);
        public void Dispose() { }
    }
    private sealed class Decoder(Func<string, CancellationToken, ImageFrame> run, IPhotoImageDecoder headers) : IPhotoImageDecoder
    {
        public ImageFrame Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default) => run(path, cancellationToken);
        public PhotoImageInfo ReadInfo(string path, CancellationToken cancellationToken = default) => headers.ReadInfo(path, cancellationToken);
    }
}
