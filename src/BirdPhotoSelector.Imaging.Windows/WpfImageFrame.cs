using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector.Imaging;

namespace BirdPhotoSelector.Imaging.Windows;

/// <summary>
/// Windows/WPF 图像帧适配器。只接收已冻结位图，直接共享原始像素存储，预览不会额外复制整图。
/// 此类型及 BitmapSource 只能出现在 Windows 适配层或 WPF 显示边界。
/// </summary>
public sealed class WpfImageFrame : ImageFrame
{
    /// <summary>冻结的 Windows 位图，仅供 WPF 控件显示或平台验证使用。</summary>
    public BitmapSource Bitmap { get; }
    /// <inheritdoc/>
    public override int PixelWidth => Bitmap.PixelWidth;
    /// <inheritdoc/>
    public override int PixelHeight => Bitmap.PixelHeight;
    /// <inheritdoc/>
    public override long RetainedBytes => checked(((long)PixelWidth * Math.Max(32, Bitmap.Format.BitsPerPixel) + 31) / 32 * 4 * PixelHeight);

    /// <summary>持有已冻结位图；未冻结的外部图像应先通过 WpfImageBridge 包装。</summary>
    internal WpfImageFrame(BitmapSource bitmap) => Bitmap = bitmap.IsFrozen ? bitmap :
        throw new ArgumentException("共享图像必须先冻结。", nameof(bitmap));

    /// <summary>只转换和复制所需矩形，避免逐鸟裁切时重复分配整张照片。</summary>
    public override byte[] CopyRgb24(PixelRect region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        region.Validate(PixelWidth, PixelHeight);
        int stride = checked(region.Width * 3);
        var pixels = new byte[checked(stride * region.Height)];
        var rgb = new FormatConvertedBitmap(Bitmap, PixelFormats.Rgb24, null, 0);
        rgb.CopyPixels(new Int32Rect(region.X, region.Y, region.Width, region.Height), pixels, stride, 0);
        cancellationToken.ThrowIfCancellationRequested();
        return pixels;
    }

    /// <summary>使用与跨平台后端相同的固定采样规则，避免框定位和背景特征随显示框架改变。</summary>
    public override ImageFrame Resize(int width, int height, CancellationToken cancellationToken = default) =>
        ImageResampling.Resize(this, width, height, cancellationToken);

}

/// <summary>
/// WPF 显示边界的双向桥接。弱键表不会使已移出缓存的图像继续存活。
/// 同一冻结位图重复包装会返回同一图像帧；Windows 图像显示时直接取回原对象。
/// </summary>
public static class WpfImageBridge
{
    private static readonly ConditionalWeakTable<BitmapSource, WpfImageFrame> Frames = new();
    private static readonly ConditionalWeakTable<ImageFrame, BitmapSource> Rendered = new();

    /// <summary>包装位图并确保不可变；未冻结输入会先克隆，避免冻结调用者仍在使用的对象。</summary>
    public static WpfImageFrame FromBitmap(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        if (!bitmap.IsFrozen) { bitmap = bitmap.Clone(); bitmap.Freeze(); }
        return Frames.GetValue(bitmap, static source => new WpfImageFrame(source));
    }

    /// <summary>
    /// 转换为 WPF 可显示对象。Windows 帧零复制；托管帧仅在显示边界创建位图并按帧弱缓存。
    /// 后者额外的 UI 位图开销属于显示层，不包含在核心缓存的像素预算内。
    /// </summary>
    public static BitmapSource ToBitmap(ImageFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame is WpfImageFrame windows) return windows.Bitmap;
        return Rendered.GetValue(frame, static source =>
        {
            var bitmap = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Rgb24,
                null, source is Rgb24ImageFrame managed ? managed.ReadOnlyPixels : source.CopyRgb24(), checked(source.PixelWidth * 3));
            bitmap.Freeze();
            return bitmap;
        });
    }
}
