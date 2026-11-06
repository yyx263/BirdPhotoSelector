using System.IO;
using System.Windows.Media.Imaging;
using BirdPhotoSelector.Imaging;

namespace BirdPhotoSelector.Imaging.Windows;

/// <summary>
/// 现有 Windows 行为的图像后端：JPG 使用 WIC，RAW 使用固定版本 LibRaw 桥接。
/// 所有入口关闭输入文件后才返回；类本身无会话状态，可以在导入、预览、AI 间共享。
/// RAW 的串行门和取消回调由 RawImageDecoder 统一管理。
/// </summary>
public sealed class WindowsPhotoImageDecoder : IPhotoImageDecoder
{
    /// <summary>按格式分派现有解码器，并将冻结位图包装为与 UI 无关的图像帧。</summary>
    public ImageFrame Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
    {
        ValidateFormat(path, cancellationToken);
        if (maxWidth < 0) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        return WpfImageBridge.FromBitmap(ImageLoader.Load(path, maxWidth, cancellationToken));
    }

    /// <summary>只读取头信息/RAW 元数据；显示尺寸统一应用相机方向，鸟框始终使用该坐标系。</summary>
    public PhotoImageInfo ReadInfo(string path, CancellationToken cancellationToken = default)
    {
        ValidateFormat(path, cancellationToken);
        if (PhotoImageFormats.IsRaw(path))
        {
            var raw = RawImageDecoder.ReadInfo(path, cancellationToken);
            return new(raw.Metadata, raw.PixelWidth, raw.PixelHeight, raw.Orientation);
        }
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8)
            throw new InvalidDataException("文件内容不是有效的 JPEG 图片。");
        stream.Position = 0;
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var metadata = frame.Metadata as BitmapMetadata;
        int orientation = ImageLoader.GetOrientation(metadata);
        var result = new PhotoImageInfo(MetadataService.Read(metadata),
            orientation is >= 5 and <= 8 ? frame.PixelHeight : frame.PixelWidth,
            orientation is >= 5 and <= 8 ? frame.PixelWidth : frame.PixelHeight, orientation);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>拒绝空路径或未支持的格式；不把扩展名检查当成图像内容验证。</summary>
    private static void ValidateFormat(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!PhotoImageFormats.IsJpeg(path) && !PhotoImageFormats.IsRaw(path))
            throw new NotSupportedException("只支持 JPG/JPEG 和已登记的 RAW 格式。");
    }
}
