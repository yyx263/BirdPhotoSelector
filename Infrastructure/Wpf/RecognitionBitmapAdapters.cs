using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BirdPhotoSelector;

/// <summary>WPF 宿主兼容入口。只在显示边界包装位图，所有实际算法接收 ImageFrame。</summary>
public static class WpfRecognitionExtensions
{
    /// <summary>将现有 Windows 位图作为不可变图像交给鸟类定位器。</summary>
    public static BirdDetectionResult Detect(this BirdDetectionService service, BitmapSource image,
        CancellationToken cancellationToken = default) => service.Detect(WpfImageBridge.FromBitmap(image), cancellationToken);
    /// <summary>将手动绘框时使用的 Windows 预览交给鸟种识别器，不复制整幅像素。</summary>
    public static SpeciesRecognitionResult Recognize(this BirdSpeciesRecognitionService service, BitmapSource image,
        BirdBox box, bool chinaOnly = true, CancellationToken cancellationToken = default) =>
        service.Recognize(WpfImageBridge.FromBitmap(image), box, chinaOnly, cancellationToken);
}

/// <summary>仅供当前 WPF 宿主使用的预处理兼容入口。</summary>
public static partial class SpeciesImagePreprocessor
{
    /// <summary>包装冻结位图后调用与 UI 无关的 RGB 预处理。</summary>
    public static DenseTensor<float> CreateInput(BitmapSource image, BirdBox box, CancellationToken cancellationToken = default) =>
        CreateInput(WpfImageBridge.FromBitmap(image), box, cancellationToken);
}

/// <summary>仅供当前 WPF 宿主使用的背景比较兼容入口。</summary>
public static partial class BackgroundSimilarity
{
    /// <summary>包装位图并排除一个鸟框。</summary>
    public static BackgroundDescriptor Describe(BitmapSource source, BirdBox? bird = null) => Describe(WpfImageBridge.FromBitmap(source), bird);
    /// <summary>包装位图并排除全部鸟框。</summary>
    public static BackgroundDescriptor DescribeAll(BitmapSource source, IEnumerable<BirdBox> birds) => DescribeAll(WpfImageBridge.FromBitmap(source), birds);
}
