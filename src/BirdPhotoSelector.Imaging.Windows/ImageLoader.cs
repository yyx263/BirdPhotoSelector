using BirdPhotoSelector.Imaging;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

/// <summary>Loads oriented JPG or true RAW pixels without retaining a file lock.</summary>
public static class ImageLoader
{
    /// <summary>Windows 位图兼容入口。OnLoad 保证解码结束后关闭输入流；RAW 始终走真实像素解码。</summary>
    public static BitmapSource Load(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (PhotoImageFormats.IsRaw(path))
            return RawImageDecoder.Decode(path, maxWidth, cancellationToken);
        using var stream = File.OpenRead(path);
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        int orientation = GetOrientation(decoder.Frames[0].Metadata as BitmapMetadata);
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
        if (maxWidth > 0 && decoder.Frames[0].PixelWidth > maxWidth) bitmap.DecodePixelWidth = maxWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        BitmapSource result = ApplyOrientation(bitmap, orientation);
        result.Freeze();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>读取 EXIF 274 方向标记；缺失或不可读取时按普通方向处理。</summary>
    public static int GetOrientation(BitmapMetadata? metadata)
    {
        try { return Convert.ToInt32(metadata?.GetQuery("/app1/ifd/{ushort=274}") ?? 1); }
        catch { return 1; }
    }

    /// <summary>覆盖 EXIF 的全部八种旋转/镜像组合，统一预览、缩略图与 AI 的坐标。</summary>
    private static BitmapSource ApplyOrientation(BitmapSource bitmap, int orientation)
    {
        Transform? transform = orientation switch
        {
            2 => new MatrixTransform(-1, 0, 0, 1, 0, 0),
            3 => new RotateTransform(180),
            4 => new MatrixTransform(1, 0, 0, -1, 0, 0),
            5 => new MatrixTransform(0, 1, 1, 0, 0, 0),
            6 => new RotateTransform(90),
            7 => new MatrixTransform(0, -1, -1, 0, 0, 0),
            8 => new RotateTransform(270),
            _ => null
        };
        return transform is null ? bitmap : new TransformedBitmap(bitmap, transform);
    }
}
