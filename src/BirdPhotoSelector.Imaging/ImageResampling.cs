namespace BirdPhotoSelector.Imaging;

/// <summary>统一各图像后端的 RGB 采样规则；定位与背景分组不再随 WIC/Skia 改变像素中心或舍入方式。</summary>
public static class ImageResampling
{
    /// <summary>固定算法标识。BioCLIP 固定的抗混叠双三次裁切不经过此入口。</summary>
    public const string Policy = "BilinearPixelCenter-v1";

    /// <summary>像素中心对齐的双线性缩放，边缘钳制，八位通道使用 .NET 中点到偶数舍入；输出尺寸精确匹配请求。</summary>
    public static ImageFrame Resize(ImageFrame image, int width, int height, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (width == image.PixelWidth && height == image.PixelHeight) return image;
        byte[] source = image is Rgb24ImageFrame managed ? managed.ReadOnlyPixels : image.CopyRgb24(cancellationToken);
        var result = new byte[checked(width * height * 3)];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sy = Math.Clamp((y + .5) * image.PixelHeight / height - .5, 0, image.PixelHeight - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, image.PixelHeight - 1); double fy = sy - y0;
            for (int x = 0; x < width; x++)
            {
                double sx = Math.Clamp((x + .5) * image.PixelWidth / width - .5, 0, image.PixelWidth - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, image.PixelWidth - 1); double fx = sx - x0;
                for (int c = 0; c < 3; c++)
                {
                    double top = source[(y0 * image.PixelWidth + x0) * 3 + c] * (1 - fx) + source[(y0 * image.PixelWidth + x1) * 3 + c] * fx;
                    double bottom = source[(y1 * image.PixelWidth + x0) * 3 + c] * (1 - fx) + source[(y1 * image.PixelWidth + x1) * 3 + c] * fx;
                    result[(y * width + x) * 3 + c] = (byte)Math.Clamp(Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                }
            }
        }
        return new Rgb24ImageFrame(width, height, result);
    }

    /// <summary>统一 EXIF 八种方向与镜像，返回显示方向像素；非法或缺失标记按普通方向处理。</summary>
    public static ImageFrame Orient(ImageFrame source, int orientation, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (orientation is <= 1 or > 8) return source;
        int sw = source.PixelWidth, sh = source.PixelHeight;
        int width = orientation >= 5 ? sh : sw, height = orientation >= 5 ? sw : sh;
        byte[] pixels = source is Rgb24ImageFrame managed ? managed.ReadOnlyPixels : source.CopyRgb24(cancellationToken);
        var result = new byte[checked(width * height * 3)];
        for (int y = 0; y < sh; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < sw; x++)
            {
                (int dx, int dy) = orientation switch
                {
                    2 => (sw - 1 - x, y), 3 => (sw - 1 - x, sh - 1 - y), 4 => (x, sh - 1 - y),
                    5 => (y, x), 6 => (sh - 1 - y, x), 7 => (sh - 1 - y, sw - 1 - x), 8 => (y, sw - 1 - x), _ => (x, y)
                };
                pixels.AsSpan((y * sw + x) * 3, 3).CopyTo(result.AsSpan((dy * width + dx) * 3, 3));
            }
        }
        return new Rgb24ImageFrame(width, height, result);
    }
}
