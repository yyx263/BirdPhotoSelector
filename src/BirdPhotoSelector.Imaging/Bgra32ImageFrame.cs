namespace BirdPhotoSelector.Imaging;

/// <summary>不依赖界面的不透明 BGRA32 帧；直接承接解码器显示布局，算法仍只读取 RGB24 副本。</summary>
public sealed class Bgra32ImageFrame : ImageFrame
{
    private readonly byte[] _pixels;
    /// <inheritdoc/>
    public override int PixelWidth { get; }
    /// <inheritdoc/>
    public override int PixelHeight { get; }
    /// <inheritdoc/>
    public override long RetainedBytes => (long)PixelWidth * PixelHeight * 4;
    /// <summary>公共入口复制像素，不能通过调用者数组修改已经缓存的照片。</summary>
    public Bgra32ImageFrame(int width, int height, ReadOnlySpan<byte> pixels) : this(width, height, pixels.ToArray()) { }
    /// <summary>可信解码适配器转交新分配的私有数组，不重复复制整图。</summary>
    internal Bgra32ImageFrame(int width, int height, byte[] pixels)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (pixels.Length != checked(width * height * 4)) throw new ArgumentException("BGRA32 数据长度与尺寸不符。", nameof(pixels));
        PixelWidth = width; PixelHeight = height; _pixels = pixels;
    }
    /// <summary>只供可信显示/定向适配器复制读取，不向公共调用方暴露可写共享数据。</summary>
    internal byte[] ReadOnlyPixels => _pixels;
    /// <summary>按请求区域投影 RGB，忽略不透明布局的第四字节；不为小鸟框复制整幅照片。</summary>
    public override byte[] CopyRgb24(PixelRect region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); region.Validate(PixelWidth, PixelHeight);
        var result = new byte[checked(region.Width * region.Height * 3)];
        for (int y = 0; y < region.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int source = ((region.Y + y) * PixelWidth + region.X) * 4, target = y * region.Width * 3;
            for (int x = 0; x < region.Width; x++, source += 4, target += 3)
            { result[target] = _pixels[source + 2]; result[target + 1] = _pixels[source + 1]; result[target + 2] = _pixels[source]; }
        }
        return result;
    }
    /// <summary>缩放继续采用统一像素中心双线性策略，不能改变模型/背景分析的输入规则。</summary>
    public override ImageFrame Resize(int width, int height, CancellationToken cancellationToken = default) => ImageResampling.Resize(this, width, height, cancellationToken);
}
