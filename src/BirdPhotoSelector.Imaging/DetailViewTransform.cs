namespace BirdPhotoSelector.Imaging;

/// <summary>与 UI 无关的矩形；预览布局使用像素，VisibleRegion 使用 0..1 图像坐标。</summary>
/// <param name="X">左边界。</param><param name="Y">上边界。</param>
/// <param name="Width">宽度。</param><param name="Height">高度。</param>
public readonly record struct ViewRectangle(double X, double Y, double Width, double Height);

/// <summary>
/// 双预览的确定性几何。集中管理适应画面到 200% 的倍率、鼠标锚点、平移边界和全图白框。
/// 不依赖 WPF/Avalonia，不持有像素或照片；宿主在 UI 线程串行操作，保存 Zoom 与归一化中心。
/// </summary>
public sealed class DetailViewTransform
{
    /// <summary>实际像素最大倍率 200%，极小图片也不突破此上限。</summary>
    public const double MaximumScale = 2;
    /// <summary>图像完整显示宽度。</summary>
    public int ImageWidth { get; }
    /// <summary>图像完整显示高度。</summary>
    public int ImageHeight { get; }
    /// <summary>视口宽度，单位为 UI 的逻辑像素。</summary>
    public double ViewportWidth { get; private set; } = 1;
    /// <summary>视口高度，单位同上。</summary>
    public double ViewportHeight { get; private set; } = 1;
    /// <summary>全图适应视口所需的实际倍率。</summary>
    public double FitScale => Math.Min(ViewportWidth / ImageWidth, ViewportHeight / ImageHeight);
    /// <summary>当前实际倍率；适应尺寸超过 200% 时优先遵守最大限制。</summary>
    public double PixelScale => Math.Clamp(FitScale * Zoom, Math.Min(FitScale, MaximumScale), MaximumScale);
    /// <summary>相对适应画面的倍率，与工程现有 ViewZoom 含义一致。</summary>
    public double Zoom { get; private set; } = 1;
    /// <summary>归一化图像中心 X。</summary>
    public double CenterX { get; private set; } = .5;
    /// <summary>归一化图像中心 Y。</summary>
    public double CenterY { get; private set; } = .5;
    /// <summary>细节图在视口中的位置；比视口小的方向始终居中。</summary>
    public ViewRectangle ImageRectangle => new(ViewportWidth / 2 - CenterX * ImageWidth * PixelScale,
        ViewportHeight / 2 - CenterY * ImageHeight * PixelScale, ImageWidth * PixelScale, ImageHeight * PixelScale);
    /// <summary>当前可见部分在全图中的归一化白框，始终完全位于 0..1 范围。</summary>
    public ViewRectangle VisibleRegion
    {
        get
        {
            double w = Math.Min(1, ViewportWidth / (ImageWidth * PixelScale));
            double h = Math.Min(1, ViewportHeight / (ImageHeight * PixelScale));
            return new(Math.Clamp(CenterX - w / 2, 0, 1 - w), Math.Clamp(CenterY - h / 2, 0, 1 - h), w, h);
        }
    }
    /// <summary>创建几何状态，拒绝无效尺寸，调用方随后应设置实际视口并恢复工程视图。</summary>
    public DetailViewTransform(int imageWidth, int imageHeight)
    {
        if (imageWidth <= 0 || imageHeight <= 0) throw new ArgumentOutOfRangeException(nameof(imageWidth));
        ImageWidth = imageWidth; ImageHeight = imageHeight;
    }
    /// <summary>窗口尺寸变化保留相对倍率和中心，再按新视口钳制；不会自动保存或标记工程。</summary>
    public void SetViewport(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0) return;
        ViewportWidth = width; ViewportHeight = height; Clamp();
    }
    /// <summary>恢复已保存的相对倍率和中心；非法值安全回到全图中心，不传播 NaN。</summary>
    public void Restore(double? zoom, double? centerX, double? centerY)
    {
        Zoom = zoom is > 0 && double.IsFinite(zoom.Value) ? zoom.Value : 1;
        CenterX = centerX is { } x && double.IsFinite(x) ? x : .5;
        CenterY = centerY is { } y && double.IsFinite(y) ? y : .5;
        Clamp();
    }
    /// <summary>回到可用范围内的全图适应倍率，并居中。</summary>
    public void Fit() { Zoom = 1; CenterX = CenterY = .5; Clamp(); }
    /// <summary>居中查看归一化鸟框，默认使整只鸟占框内 70%，仍遵守倍率和平移边界。</summary>
    public void Focus(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x + y + width + height) || x < 0 || y < 0 || x >= 1 || y >= 1 || width <= 0 || height <= 0) { Fit(); return; }
        width = Math.Min(width, 1 - x); height = Math.Min(height, 1 - y);
        Zoom = Math.Min(ViewportWidth * .7 / (width * ImageWidth), ViewportHeight * .7 / (height * ImageHeight)) / FitScale;
        CenterX = x + width / 2; CenterY = y + height / 2; Clamp();
    }
    /// <summary>围绕视口内鼠标位置缩放；除遇到边界钳制外，鼠标下的原图位置保持不变。</summary>
    public void ZoomAt(double factor, double x, double y)
    {
        if (!double.IsFinite(factor) || factor <= 0 || !double.IsFinite(x + y)) return;
        double sourceX = CenterX * ImageWidth + (x - ViewportWidth / 2) / PixelScale;
        double sourceY = CenterY * ImageHeight + (y - ViewportHeight / 2) / PixelScale;
        Zoom *= factor;
        Zoom = PixelScale / FitScale;
        CenterX = (sourceX - (x - ViewportWidth / 2) / PixelScale) / ImageWidth;
        CenterY = (sourceY - (y - ViewportHeight / 2) / PixelScale) / ImageHeight;
        Clamp();
    }
    /// <summary>按视口拖拽距离移动图像，不能把可见图像拖出边界，较小方向保持居中。</summary>
    public void PanBy(double dx, double dy)
    {
        if (!double.IsFinite(dx + dy)) return;
        CenterX -= dx / (ImageWidth * PixelScale); CenterY -= dy / (ImageHeight * PixelScale); Clamp();
    }
    /// <summary>将倍率与归一化中心限制到有效范围，保证白框始终留在照片内部。</summary>
    private void Clamp()
    {
        Zoom = PixelScale / FitScale;
        double halfX = Math.Min(.5, ViewportWidth / (2 * ImageWidth * PixelScale));
        double halfY = Math.Min(.5, ViewportHeight / (2 * ImageHeight * PixelScale));
        CenterX = Math.Clamp(CenterX, halfX, 1 - halfX); CenterY = Math.Clamp(CenterY, halfY, 1 - halfY);
    }
}
