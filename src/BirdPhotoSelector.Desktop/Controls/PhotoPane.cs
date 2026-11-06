using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using BirdPhotoSelector.Desktop.Imaging;

namespace BirdPhotoSelector.Desktop.Controls;

/// <summary>
/// 一块照片画布。全图始终适应画面，细节图使用共享几何模块；两者共享同一显示帧。
/// 不执行解码或业务编辑，加载时可以显示旧帧但禁止点击、缩放和拖拽。
/// </summary>
public sealed class PhotoPane : Control
{
    private AvaloniaImageFrame? _frame;
    private PhotoItem? _photo;
    private DetailViewTransform? _view;
    private bool _dragging, _initialView;
    private Point _dragPosition;
    /// <summary>全图为 false，细节图为 true；由宿主在初始化时固定。</summary>
    public bool IsDetail { get; set; }
    /// <summary>仅当前选择已加载、没有后台编辑时允许操作，旧帧不会误改新照片。</summary>
    public bool IsInteractive { get; set; }
    /// <summary>细节视图在全图中的白框；宿主把此值同步给全图画布。</summary>
    public ViewRectangle VisibleRegion { get; set; } = new(0, 0, 1, 1);
    /// <summary>当前实际倍率的展示文本。</summary>
    public string ScaleText => _view is null ? "—" : $"{_view.PixelScale:P1}";
    /// <summary>任何视图变化；bool 为 true 时来自人工操作，应标记工程有修改。</summary>
    public event Action<bool>? ViewChanged;
    /// <summary>全图点击鸟框时请求宿主同步结果列表与细节聚焦。</summary>
    public event Action<int>? BirdSelected;
    /// <summary>接收已解码帧与其真实照片；不同照片初始化恢复视图，不自行读取文件。</summary>
    public void ShowPhoto(PhotoItem? photo, AvaloniaImageFrame? frame)
    {
        _dragging = false; _photo = photo; _frame = frame;
        _view = frame is null ? null : new(frame.PixelWidth, frame.PixelHeight);
        _initialView = true; InitializeView(); InvalidateVisual();
    }
    /// <summary>按工程视图或默认最大鸟初始化；来源待核对时不自动采用旧框。</summary>
    private void InitializeView()
    {
        if (_view is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        _view.SetViewport(Bounds.Width, Bounds.Height);
        if (_photo?.ViewZoom is { } zoom) _view.Restore(zoom, _photo.ViewCenterX, _photo.ViewCenterY);
        else if (_photo is { CanUseBirdGeometry: true, SelectedBird: { HasRegion: true } bird }) _view.Focus(bird.Box.X, bird.Box.Y, bird.Box.Width, bird.Box.Height);
        else _view.Fit();
        _initialView = false; Publish(false);
    }
    /// <summary>适应画面是明确人工操作，保存视图但不改变鸟框。</summary>
    public void Fit()
    {
        if (!IsInteractive || _view is null) return;
        _initialView = false; _view.SetViewport(Bounds.Width, Bounds.Height); _view.Fit(); Publish(true);
    }
    /// <summary>显式还原当前鸟的 70% 视图，允许人工核对来源变化后的旧框。</summary>
    public void FocusBird()
    {
        if (!IsInteractive || _view is null) return;
        _initialView = false; _view.SetViewport(Bounds.Width, Bounds.Height);
        if (_photo?.SelectedBird is { HasRegion: true } bird) _view.Focus(bird.Box.X, bird.Box.Y, bird.Box.Width, bird.Box.Height);
        else _view.Fit();
        Publish(true);
    }
    /// <summary>画布大小变动保留当前相对视图，再更新全图白框；布局变动本身不标记修改。</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_initialView) InitializeView();
        else if (_view is not null) { _view.SetViewport(Bounds.Width, Bounds.Height); Publish(false); }
        InvalidateVisual();
    }
    /// <summary>直接绘制共享图像和覆盖层，不创建第二份整图或每次缩放重采样。</summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brush.Parse("#0C1217"), null, new Rect(Bounds.Size));
        if (_frame is null)
        {
            DrawText(context, "添加照片，开始本次挑选", new Point(24, Math.Max(24, Bounds.Height / 2 - 10)), Brush.Parse("#7F949F"), 14);
            return;
        }
        // Render 只读取状态；初始化与通知在 ShowPhoto/布局回调完成，禁止绘制中再次使视觉树失效。
        if (_initialView) return;
        Rect target;
        if (IsDetail && _view is not null)
        { var r = _view.ImageRectangle; target = new(r.X, r.Y, r.Width, r.Height); }
        else
        {
            double scale = Math.Min(Bounds.Width / _frame.PixelWidth, Bounds.Height / _frame.PixelHeight);
            var size = new Size(_frame.PixelWidth * scale, _frame.PixelHeight * scale);
            target = new((Bounds.Width - size.Width) / 2, (Bounds.Height - size.Height) / 2, size.Width, size.Height);
        }
        using (context.PushClip(new Rect(Bounds.Size)))
        {
            context.DrawImage(_frame.Image, new Rect(_frame.Image.Size), target);
            if (!IsDetail) DrawOverlays(context, target);
        }
    }
    /// <summary>鸟框高透明虚线与编号、白框均裁切到真实照片边界，描边不越界。</summary>
    private void DrawOverlays(DrawingContext context, Rect image)
    {
        using (context.PushClip(image))
        {
            if (_photo is not null)
                foreach (var bird in _photo.Birds.Where(b => b.HasRegion))
                {
                    var box = bird.Box;
                    var rectangle = new Rect(image.X + box.X * image.Width, image.Y + box.Y * image.Height,
                        Math.Min(box.Width, 1 - box.X) * image.Width, Math.Min(box.Height, 1 - box.Y) * image.Height);
                    var brush = Brush.Parse(ReferenceEquals(bird, _photo.SelectedBird) ? "#8A9EDCC5" : "#60EDF4F8");
                    context.DrawRectangle(null, new Pen(brush, 1, DashStyle.Dash), rectangle);
                    DrawText(context, bird.DisplayNumberText, new Point(rectangle.X + 3, rectangle.Y + 2), brush, 11);
                }
            var region = VisibleRegion;
            var white = new Rect(image.X + region.X * image.Width, image.Y + region.Y * image.Height,
                region.Width * image.Width, region.Height * image.Height);
            var inset = new Thickness(Math.Min(.75, white.Width / 2), Math.Min(.75, white.Height / 2));
            context.DrawRectangle(null, new Pen(Brush.Parse("#99000000"), 3), white.Deflate(inset));
            context.DrawRectangle(null, new Pen(Brushes.White, 1.5), white.Deflate(inset));
        }
    }
    /// <summary>按当前平台字体绘制少量覆盖文字，完整中文名称由结果列表显示。</summary>
    private static void DrawText(DrawingContext context, string text, Point point, IBrush brush, double size) =>
        context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush), point);
    /// <summary>细节区域始终消费滚轮，不向列表传播或切换照片。</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsDetail) return;
        e.Handled = true;
        if (!IsInteractive || _view is null) return;
        if (_initialView) InitializeView();
        var position = e.GetPosition(this); _view.ZoomAt(Math.Pow(1.18, Math.Clamp(e.Delta.Y, -8, 8)), position.X, position.Y); Publish(true);
    }
    /// <summary>细节左键捕获拖拽；全图左键只选择实际鸟框，不能缩放全图。</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsInteractive || _frame is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (IsDetail) { _dragging = true; _dragPosition = e.GetPosition(this); e.Pointer.Capture(this); e.Handled = true; return; }
        double scale = Math.Min(Bounds.Width / _frame.PixelWidth, Bounds.Height / _frame.PixelHeight);
        var point = e.GetPosition(this); double width = _frame.PixelWidth * scale, height = _frame.PixelHeight * scale;
        double x = (point.X - (Bounds.Width - width) / 2) / width, y = (point.Y - (Bounds.Height - height) / 2) / height;
        if (_photo is not null)
            for (int i = _photo.Birds.Count - 1; i >= 0; i--)
            { var bird = _photo.Birds[i]; var b = bird.Box; if (bird.HasRegion && x >= b.X && y >= b.Y && x <= b.X + b.Width && y <= b.Y + b.Height) { BirdSelected?.Invoke(i); e.Handled = true; break; } }
    }
    /// <summary>每次拖拽使用相对增量，边界由共享几何统一钳制。</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsInteractive || !_dragging || _view is null) return;
        var position = e.GetPosition(this); _view.PanBy(position.X - _dragPosition.X, position.Y - _dragPosition.Y); _dragPosition = position; Publish(true);
    }
    /// <summary>释放捕获后终止拖拽，不遗留跨照片的移动状态。</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); _dragging = false; e.Pointer.Capture(null); }
    /// <summary>系统切换窗口或弹出菜单时也必须终止拖拽。</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) { base.OnPointerCaptureLost(e); _dragging = false; }
    /// <summary>持久化与现有工程相同的相对视图，通知宿主更新白框及修改状态。</summary>
    private void Publish(bool user)
    {
        if (!IsDetail || _view is null) return;
        VisibleRegion = _view.VisibleRegion;
        if (_photo is not null) { _photo.ViewZoom = _view.Zoom; _photo.ViewCenterX = _view.CenterX; _photo.ViewCenterY = _view.CenterY; }
        ViewChanged?.Invoke(user); InvalidateVisual();
    }
}
