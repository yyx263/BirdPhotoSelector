using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using BirdPhotoSelector.Desktop.Imaging;

namespace BirdPhotoSelector.Desktop.Controls;

/// <summary>独立保留图像绘制节点与轻量覆盖节点，不增加整图副本或缓存预算。</summary>
public sealed partial class PhotoPane
{
    private static readonly IBrush BackgroundBrush = Brush.Parse("#0C1217"), EmptyBrush = Brush.Parse("#7F929F");
    private static readonly Typeface PreviewTypeface = new("Microsoft YaHei UI, PingFang SC, Noto Sans CJK SC, sans-serif");
    private readonly ImageSurface _image = new();
    private readonly OverlaySurface _overlays;
    /// <summary>画布只接收鼠标；两个只读显示层不拦截拖拽，整图位图仍来自共享帧。</summary>
    public PhotoPane()
    {
        ClipToBounds = true; _overlays = new(this);
        VisualChildren.Add(_image); VisualChildren.Add(_overlays);
        LogicalChildren.Add(_image); LogicalChildren.Add(_overlays);
    }
    /// <summary>两个只读层占据同一视口，不把整图像素尺寸反馈给窗口布局。</summary>
    protected override Size MeasureOverride(Size availableSize)
    { _image.Measure(availableSize); _overlays.Measure(availableSize); return default; }
    /// <summary>显示层与覆盖层共享完整视口，缩放只改变绘制矩形而不触发布局缩放。</summary>
    protected override Size ArrangeOverride(Size finalSize)
    { var bounds = new Rect(finalSize); _image.Arrange(bounds); _overlays.Arrange(bounds); return finalSize; }
    /// <summary>供输入回归检测静态全图是否被无关的白框移动重新绘制。</summary>
    internal int ImageRenderCount => _image.RenderCount;
    /// <summary>更换选中鸟/结果时只刷新覆盖层，保持图像节点不变。</summary>
    public void RefreshOverlays() => _overlays.InvalidateVisual();
    /// <summary>布局或视图变化时更新显示矩形；相同图像/矩形不会使图像层失效。</summary>
    private void RefreshDrawing()
    {
        var image = ImageRectangle();
        if (_frame is null || image.Width <= 0 || image.Height <= 0) _image.Update(null, default, default);
        else
        {
            // 只提交视口实际可见的原图像素，避免放大后仍提交大幅框外图像。
            var visible = image.Intersect(new Rect(Bounds.Size));
            var source = new Rect((visible.X - image.X) * _frame.Image.Size.Width / image.Width,
                (visible.Y - image.Y) * _frame.Image.Size.Height / image.Height,
                visible.Width * _frame.Image.Size.Width / image.Width, visible.Height * _frame.Image.Size.Height / image.Height);
            _image.Update(_frame, source, visible);
        }
        _overlays.InvalidateVisual(); InvalidateVisual();
    }
    /// <summary>全图固定适应尺寸，细节使用共享变换，覆盖层与图像层共用同一位置定义。</summary>
    private Rect ImageRectangle()
    {
        if (_frame is null) return default;
        if (IsDetail && _view is not null) { var r = _view.ImageRectangle; return new(r.X, r.Y, r.Width, r.Height); }
        double scale = Math.Min(Bounds.Width / _frame.PixelWidth, Bounds.Height / _frame.PixelHeight);
        double width = _frame.PixelWidth * scale, height = _frame.PixelHeight * scale;
        return new((Bounds.Width - width) / 2, (Bounds.Height - height) / 2, width, height);
    }
    /// <summary>图像节点只在图片、来源裁切或目标尺寸变化时重新记录绘制；平滑缩放使用双线性采样。</summary>
    private sealed class ImageSurface : Control
    {
        private AvaloniaImageFrame? _frame; private Rect _source, _target;
        internal int RenderCount { get; private set; }
        /// <summary>图像层不接受输入；显示采样固定为双线性，缩放过程不重解码。</summary>
        public ImageSurface() { IsHitTestVisible = false; RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.LowQuality); }
        /// <summary>引用或裁切未变化时不提交新的绘制记录。</summary>
        internal void Update(AvaloniaImageFrame? frame, Rect source, Rect target)
        {
            if (ReferenceEquals(frame, _frame) && source == _source && target == _target) return;
            _frame = frame; _source = source; _target = target; InvalidateVisual();
        }
        public override void Render(DrawingContext context)
        { base.Render(context); RenderCount++; if (_frame is not null && _target.Width > 0 && _target.Height > 0) context.DrawImage(_frame.Image, _source, _target); }
    }
    /// <summary>白框、鸟框和手势只影响此小型绘制节点，不让全图位图跟随重新提交。</summary>
    private sealed class OverlaySurface : Control
    {
        private readonly PhotoPane owner;
        /// <summary>借用所属画布，只读取其位置与结果，不拥有图像。</summary>
        public OverlaySurface(PhotoPane pane) { owner = pane; IsHitTestVisible = false; }
        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (owner._frame is null) return;
            var image = owner.ImageRectangle();
            if (!owner.IsDetail) owner.DrawOverlays(context, image);
            else if (owner._drawing)
            {
                using (context.PushClip(image.Intersect(new Rect(Bounds.Size))))
                    context.DrawRectangle(Brush.Parse("#259EDCC5"), new Pen(Brush.Parse("#D09EDCC5"), 1.5, DashStyle.Dash), new Rect(owner._regionStart, owner._regionEnd));
            }
        }
    }
}
