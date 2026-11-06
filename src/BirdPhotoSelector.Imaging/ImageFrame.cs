namespace BirdPhotoSelector.Imaging;

/// <summary>
/// 一幅已应用 EXIF/相机方向的不可变图像。坐标原点位于显示图像左上角，单位为实际像素。
/// 实现必须能在任意线程读取，不保留输入文件句柄，也不能暴露可修改的底层缓冲。
/// UI、缓存和 AI 可以共享同一个对象；Windows 实现可直接持有冻结位图，避免复制整张大图。
/// 帧不提供独占 Dispose：缓存淘汰只移除引用，不能释放仍被 UI/AI 使用的共享像素。
/// 若后端持有原生像素，应使用 SafeHandle 等随托管引用生命周期释放的所有者。
/// </summary>
public abstract class ImageFrame
{
    /// <summary>显示方向下的像素宽度，不是原始传感器或编码文件中的宽度。</summary>
    public abstract int PixelWidth { get; }
    /// <summary>显示方向下的像素高度。</summary>
    public abstract int PixelHeight { get; }
    /// <summary>
    /// 缓存应计入的解码内存，至少按每像素四字节保守计费。
    /// 解码器的瞬时内存、UI 资源和进程本身的开销不包含在该值内。
    /// </summary>
    public abstract long RetainedBytes { get; }

    /// <summary>
    /// 将指定矩形复制为无行填充、从上至下排列的 RGB24 字节数组（R、G、B）。
    /// 每次返回独立副本；取消时应抛出 OperationCanceledException。
    /// </summary>
    public abstract byte[] CopyRgb24(PixelRect region, CancellationToken cancellationToken = default);

    /// <summary>复制整幅图像的 RGB24 像素。仅在算法真正需要时调用，避免预览阶段额外分配整图。</summary>
    public byte[] CopyRgb24(CancellationToken cancellationToken = default) =>
        CopyRgb24(new PixelRect(0, 0, PixelWidth, PixelHeight), cancellationToken);

    /// <summary>
    /// 返回缩放后的不可变图像。采样算法由后端实现；调用方必须使用返回帧的实际尺寸。
    /// 第一阶段保留 Windows/WIC 原有采样行为，避免改变分组阈值和定位模型的输入。
    /// BioCLIP 的固定双三次算法不经此入口，其独立金样必须保持字节一致。
    /// </summary>
    public abstract ImageFrame Resize(int width, int height, CancellationToken cancellationToken = default);
}

/// <summary>整数像素选区，右边界和下边界均不包含在区域内。</summary>
/// <param name="X">左边界。</param>
/// <param name="Y">上边界。</param>
/// <param name="Width">正数宽度。</param>
/// <param name="Height">正数高度。</param>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>在分配或访问像素前检查范围；使用减法避免恶意或损坏尺寸造成整数溢出。</summary>
    public void Validate(int imageWidth, int imageHeight)
    {
        if (Width <= 0 || Height <= 0 || X < 0 || Y < 0 ||
            Width > imageWidth || Height > imageHeight || X > imageWidth - Width || Y > imageHeight - Height)
            throw new ArgumentOutOfRangeException(nameof(PixelRect), "像素选区必须完整位于图像内。");
    }
}
