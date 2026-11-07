namespace BirdPhotoSelector.Imaging;

/// <summary>
/// 完全托管的 RGB24 图像实现，供不依赖 UI 的算法、跨平台适配器和验证程序使用。
/// 公共构造时复制输入像素，读取时也只返回副本；可信内部适配器可移交私有数组所有权。
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
    internal Rgb24ImageFrame(int width, int height, byte[] pixels)
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
    public override ImageFrame Resize(int width, int height, CancellationToken cancellationToken = default) =>
        ImageResampling.Resize(this, width, height, cancellationToken);

    /// <summary>仅供可信适配器复制读取；不得修改或保留为可写共享数据，公共入口仍始终返回独立副本。</summary>
    internal byte[] ReadOnlyPixels => _pixels;
}
