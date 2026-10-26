using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

/// <summary>Loads a JPEG without retaining a file lock and honors all EXIF orientations.</summary>
public static class ImageLoader
{
    public static BitmapSource Load(string path, int maxWidth = 0)
    {
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
        return result;
    }

    internal static int GetOrientation(BitmapMetadata? metadata)
    {
        try { return Convert.ToInt32(metadata?.GetQuery("/app1/ifd/{ushort=274}") ?? 1); }
        catch { return 1; }
    }

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
