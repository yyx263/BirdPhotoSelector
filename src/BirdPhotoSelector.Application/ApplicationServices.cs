namespace BirdPhotoSelector;

/// <summary>
/// 一个宿主会话的用例组装。图像和文件平台是显式依赖；UI 不再分散构造默认服务。
/// 模型按操作延迟创建，每次识别用例接管其模型实例并在结束时释放。
/// </summary>
public sealed class ApplicationServices
{
    private readonly Func<IBirdDetector> _detectors;
    private readonly Func<IBirdSpeciesRecognizer> _recognizers;
    /// <summary>本宿主选择的图像后端，导入、工程恢复、预览与识别必须保持一致。</summary>
    public IPhotoImageDecoder Images { get; }
    /// <summary>照片导入与原件关联用例。</summary>
    public PhotoImportService Import { get; }
    /// <summary>工程持久化与缩略图恢复用例。</summary>
    public ProjectService Projects { get; }
    /// <summary>文件导出与移除用例，系统文件能力已由宿主注入。</summary>
    public ExportService Export { get; }
    /// <summary>使用同一工程服务的自动保存用例。</summary>
    public AutoSaveService AutoSave { get; }

    /// <summary>
    /// 创建明确的依赖图。模型工厂可替换不同推理实现，但每次必须返回独立、可释放的实例。
    /// 未提供工厂时仍使用现有 YOLO/BioCLIP CPU 实现，不在构造时加载模型。
    /// </summary>
    public ApplicationServices(IPhotoImageDecoder images, IExportFilePlatform files, string? recoveryDirectory = null,
        Func<IBirdDetector>? detectors = null, Func<IBirdSpeciesRecognizer>? recognizers = null)
    {
        Images = images ?? throw new ArgumentNullException(nameof(images));
        ArgumentNullException.ThrowIfNull(files);
        _detectors = detectors ?? (() => new BirdDetectionService());
        _recognizers = recognizers ?? (() => new BirdSpeciesRecognitionService());
        Import = new(images); Projects = new(images); Export = new(files);
        AutoSave = new(Projects, recoveryDirectory);
    }

    /// <summary>创建只读取背景的分组用例，不创建或加载识别模型。</summary>
    public AnalysisService CreateAnalysis() => new(Images);
    /// <summary>创建单个鸟框的模型服务，调用方负责在操作结束时释放。</summary>
    public IBirdSpeciesRecognizer CreateSpeciesRecognizer() => _recognizers() ?? throw new InvalidOperationException("鸟种模型工厂未返回实例。");
    /// <summary>创建拥有独立模型的批量鸟种用例，负责其模型生命周期。</summary>
    public SpeciesRecognitionBatchService CreateSpeciesBatch() => new(Images, CreateSpeciesRecognizer(), ownsRecognizer: true);
    /// <summary>创建定位后识别全部照片的用例；第二个模型创建失败时释放第一个，避免泄漏。</summary>
    public BirdRecognitionWorkflow CreateRecognition()
    {
        var detector = _detectors() ?? throw new InvalidOperationException("定位模型工厂未返回实例。");
        try { return new(Images, detector, CreateSpeciesRecognizer()); }
        catch { detector.Dispose(); throw; }
    }
    /// <summary>创建可撤销的单张重建用例，输出仍是经过来源校验的独立提案。</summary>
    public SinglePhotoRecognitionService CreateSinglePhotoRecognition()
    {
        var detector = _detectors() ?? throw new InvalidOperationException("定位模型工厂未返回实例。");
        try { return new(Images, detector, CreateSpeciesRecognizer()); }
        catch { detector.Dispose(); throw; }
    }
}
