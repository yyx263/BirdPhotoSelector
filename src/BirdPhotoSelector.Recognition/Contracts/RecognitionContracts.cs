namespace BirdPhotoSelector;

/// <summary>定位模型契约，输入为已经定向的不可变图像；实现不得修改工程或原文件。</summary>
public interface IBirdDetector : IDisposable
{
    /// <summary>返回全部有效归一化鸟框，取消抛出异常；成功零检出与模型失败必须明确区分。</summary>
    BirdDetectionResult Detect(ImageFrame image, CancellationToken cancellationToken = default);
}

/// <summary>逐鸟识别契约。调用方管理模型的范围和释放，接口不读取照片文件或依赖任何桌面 UI。</summary>
public interface IBirdSpeciesRecognizer : IDisposable
{
    /// <summary>准备模型；null 表示就绪，非 null 表示缺失或失败原因；取消不得转成普通失败。</summary>
    SpeciesRecognitionResult? Prepare(CancellationToken cancellationToken = default);
    /// <summary>在指定鸟框上推理，默认使用中国候选，返回独立结果，不修改传入框或工程。</summary>
    SpeciesRecognitionResult Recognize(ImageFrame orientedImage, BirdBox box, bool chinaOnly = true,
        CancellationToken cancellationToken = default);
}
