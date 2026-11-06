using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector.Imaging.Native;

namespace BirdPhotoSelector;

/// <summary>保留现有 Windows 调用的元数据数据类型，尺寸已经应用相机方向。</summary>
public sealed record RawImageInfo(PhotoMetadata Metadata, int PixelWidth, int PixelHeight, int Orientation);

/// <summary>WPF 的薄 RAW 显示适配；路径、取消、元数据和真实解码由跨平台原生层统一管理。</summary>
public static class RawImageDecoder
{
    /// <summary>读取统一 UTF-8 后端并保留旧宿主的数据接口。</summary>
    public static RawImageInfo ReadInfo(string path, CancellationToken cancellationToken = default)
    { var info = NativeRawDecoder.ReadInfo(path, cancellationToken); return new(info.Metadata, info.PixelWidth, info.PixelHeight, info.Orientation); }
    /// <summary>直接把短期原生租约复制为冻结位图，避免先创建一份完整托管 RGB 再复制。</summary>
    public static BitmapSource Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
    {
        using var buffer = NativeRawDecoder.DecodeBuffer(path, maxWidth, cancellationToken);
        var image = BitmapSource.Create(buffer.Width, buffer.Height, 96, 96, PixelFormats.Rgb24, null, buffer.Pixels, buffer.Bytes, buffer.Stride);
        image.Freeze(); cancellationToken.ThrowIfCancellationRequested(); return image;
    }
}
