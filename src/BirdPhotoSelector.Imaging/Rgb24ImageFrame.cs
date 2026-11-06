namespace BirdPhotoSelector.Imaging;

/// <summary>
/// 完全托管的 RGB24 图像实现，供不依赖 UI 的算法、跨平台适配器和验证程序使用。
/// 构造时复制输入像素，读取时也只返回副本，因此缓存可安全地跨线程共享。
/// </summary>
public sealed class Rgb24ImageFrame : ImageFrame
{
    private readonly byte[] _pixels;
    /// <inheritdoc/>
    public override int PixelWidth { get; }
    /// <inheritdoc/>
    public override int PixelHeight { get; }
    /// <inheritdoc/>
    public override long RetainedBytes => (long)PixelWidth * PixelHeight * 4;

    /// <summary>从紧密排列的 RGB24 数据创建图像，长度必须恰好为 width × height × 3。</summary>
    public Rgb24ImageFrame(int width, int height, ReadOnlySpan<byte> pixels)
    {
        ValidateSize(width, height, pixels.Length);
        PixelWidth = width; PixelHeight = height; _pixels = pixels.ToArray();
    }

    /// <summary>内部创建路径接管刚分配的私有数组，避免缩放后再复制一次；外部不能访问数组。</summary>
    private Rgb24ImageFrame(int width, int height, byte[] pixels)
    {
        ValidateSize(width, height, pixels.Length);
        PixelWidth = width; PixelHeight = height; _pixels = pixels;
    }

    /// <summary>在像素复制或分配前验证尺寸、乘法溢出及紧密排列的数据长度。</summary>
    private static void ValidateSize(int width, int height, int length)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (length != checked(width * height * 3)) throw new ArgumentException("RGB24 数据长度与尺寸不符。", nameof(length));
    }

    /// <inheritdoc/>
    public override byte[] CopyRgb24(PixelRect region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        region.Validate(PixelWidth, PixelHeight);
        int stride = checked(region.Width * 3);
        var result = new byte[checked(stride * region.Height)];
        for (int y = 0; y < region.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pixels.AsSpan(((y + region.Y) * PixelWidth + region.X) * 3, stride).CopyTo(result.AsSpan(y * stride));
        }
        return result;
    }

    /// <summary>
    /// 使用像素中心对齐的双线性采样，边界钳制到最近有效像素。
    /// 这是托管实现的明确采样策略，不能用于替换 BioCLIP 的固定双三次预处理。
    /// </summary>
    public override ImageFrame Resize(int width, int height, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (width == PixelWidth && height == PixelHeight) return this;
        var result = new byte[checked(width * height * 3)];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double sy = Math.Clamp((y + .5) * PixelHeight / height - .5, 0, PixelHeight - 1);
            int y0 = (int)sy, y1 = Math.Min(y0 + 1, PixelHeight - 1);
            double fy = sy - y0;
            for (int x = 0; x < width; x++)
            {
                double sx = Math.Clamp((x + .5) * PixelWidth / width - .5, 0, PixelWidth - 1);
                int x0 = (int)sx, x1 = Math.Min(x0 + 1, PixelWidth - 1);
                double fx = sx - x0;
                for (int c = 0; c < 3; c++)
                {
                    double top = _pixels[(y0 * PixelWidth + x0) * 3 + c] * (1 - fx) + _pixels[(y0 * PixelWidth + x1) * 3 + c] * fx;
                    double bottom = _pixels[(y1 * PixelWidth + x0) * 3 + c] * (1 - fx) + _pixels[(y1 * PixelWidth + x1) * 3 + c] * fx;
                    result[(y * width + x) * 3 + c] = (byte)Math.Clamp(Math.Round(top * (1 - fy) + bottom * fy), 0, 255);
                }
            }
        }
        return new Rgb24ImageFrame(width, height, result);
    }
}
