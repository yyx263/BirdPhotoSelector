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
public sealed partial class PhotoPane : Control
{
    private AvaloniaImageFrame? _frame;
    private PhotoItem? _photo;
    private DetailViewTransform? _view;
    private bool _dragging, _initialView;
    private Point _dragPosition, _regionStart, _regionEnd;
    private bool _manualMode, _drawing;
    private IPointer? _capturedPointer;
    /// <summary>手动选区只表示当前手势，不直接修改照片或识别记录。</summary>
    public bool IsManualMode => _manualMode;
    /// <summary>提交的手动框属于明确的已显示照片，宿主负责识别和撤销。</summary>
    public event Action<PhotoItem, BirdBox>? ManualRegionSelected;
    /// <summary>全图框右键与结果行右键共用宿主删除入口。</summary>
    public event Action<PhotoItem, DetectedBird>? DeleteRegionRequested;
    /// <summary>手动模式变化供工具按钮同步置位。</summary>
    public event Action? ManualModeChanged;
    /// <summary>全图为 false，细节图为 true；由宿主在初始化时固定。</summary>
    public bool IsDetail { get; set; }
    /// <summary>仅当前选择已加载、没有后台编辑时允许操作，旧帧不会误改新照片。</summary>
    public bool IsInteractive { get; set; }
    /// <summary>细节视图在全图中的白框；宿主把此值同步给全图画布。</summary>
    private ViewRectangle _visibleRegion = new(0, 0, 1, 1);
    public ViewRectangle VisibleRegion
    {
        get => _visibleRegion;
        set { if (_visibleRegion == value) return; _visibleRegion = value; _overlays.InvalidateVisual(); }
    }
    /// <summary>当前实际倍率的展示文本。</summary>
    public string ScaleText => _view is null ? "—" : $"{_view.PixelScale:P1}";
    /// <summary>任何视图变化；bool 为 true 时来自人工操作，应标记工程有修改。</summary>
    public event Action<bool>? ViewChanged;
    /// <summary>全图点击鸟框时请求宿主同步结果列表与细节聚焦。</summary>
    public event Action<int>? BirdSelected;
    /// <summary>接收已解码帧与其真实照片；不同照片初始化恢复视图，不自行读取文件。</summary>
    public void ShowPhoto(PhotoItem? photo, AvaloniaImageFrame? frame)
    {
        CancelManualRegion(); _dragging = false; _photo = photo; _frame = frame;
        _view = frame is null ? null : new(frame.PixelWidth, frame.PixelHeight);
        _initialView = true; InitializeView(); RefreshDrawing();
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
        CancelManualRegion(); _initialView = false; _view.SetViewport(Bounds.Width, Bounds.Height); _view.Fit(); Publish(true);
    }
    /// <summary>显式还原当前鸟的 70% 视图，允许人工核对来源变化后的旧框。</summary>
    public void FocusBird()
    {
        if (!IsInteractive || _view is null) return;
        CancelManualRegion(); _initialView = false; _view.SetViewport(Bounds.Width, Bounds.Height);
        if (_photo?.SelectedBird is { HasRegion: true } bird) _view.Focus(bird.Box.X, bird.Box.Y, bird.Box.Width, bird.Box.Height);
        else _view.Fit();
        Publish(true);
    }
    /// <summary>画布大小变动保留当前相对视图，再更新全图白框；布局变动本身不标记修改。</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        CancelManualRegion();
        if (_initialView) InitializeView();
        else if (_view is not null) { _view.SetViewport(Bounds.Width, Bounds.Height); Publish(false); }
        RefreshDrawing();
    }
    /// <summary>背景/空状态与图像、覆盖层分离；移动白框不会重新提交全图的位图绘制。</summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context); context.DrawRectangle(BackgroundBrush, null, new Rect(Bounds.Size));
        if (_frame is not null) return;
        string[] lines = IsDetail ? ["＋", "看清羽毛与眼神", "滚轮缩放 · 拖动查看细节"] : ["◉", "等待照片", "从左上角添加 JPG / RAW 照片"];
        double[] sizes = [34, IsDetail ? 13 : 15, 11];
        var texts = lines.Select((line, i) => new FormattedText(line, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, PreviewTypeface, sizes[i], EmptyBrush)).ToArray();
        double y = (Bounds.Height - texts.Sum(t => t.Height) - 16) / 2;
        foreach (var text in texts) { context.DrawText(text, new Point((Bounds.Width - text.Width) / 2, y)); y += text.Height + 8; }
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
        context.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, PreviewTypeface, size, brush), point);
    /// <summary>细节区域始终消费滚轮，不向列表传播或切换照片。</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsDetail) return;
        e.Handled = true;
        if (!IsInteractive || _view is null || _drawing) return;
        if (_initialView) InitializeView();
        var position = e.GetPosition(this); _view.ZoomAt(Math.Pow(1.18, Math.Clamp(e.Delta.Y, -8, 8)), position.X, position.Y); Publish(true);
    }
    /// <summary>切换手动框选，起点和最终区域均需通过实际图像范围校验。</summary>
    public bool BeginManualRegion()
    {
        if (!IsDetail || !IsInteractive || _frame is null) return false;
        _dragging = false; _manualMode = true; Cursor = new Cursor(StandardCursorType.Cross); ManualModeChanged?.Invoke(); return true;
    }
    /// <summary>Esc、换图、布局变化或后台操作统一取消尚未提交的框，不创建撤销记录。</summary>
    public bool CancelManualRegion()
    {
        bool active = _manualMode || _drawing; _manualMode = _drawing = _dragging = false;
        var pointer = _capturedPointer; _capturedPointer = null; pointer?.Capture(null);
        Cursor = Cursor.Default;
        if (active) { ManualModeChanged?.Invoke(); RefreshDrawing(); }
        return active;
    }
    /// <summary>细节左键为拖拽/框选；全图左键选鸟，右键为该框的删除菜单。</summary>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsInteractive || _frame is null) return;
        var point = e.GetPosition(this); var buttons = e.GetCurrentPoint(this).Properties;
        if (IsDetail)
        {
            if (!buttons.IsLeftButtonPressed || _view is null) return;
            if (_initialView) InitializeView();
            if (_manualMode)
            {
                var image = _view.ImageRectangle;
                if (point.X < Math.Max(0, image.X) || point.X > Math.Min(Bounds.Width, image.X + image.Width) || point.Y < Math.Max(0, image.Y) || point.Y > Math.Min(Bounds.Height, image.Y + image.Height)) return;
                _drawing = true; _regionStart = _regionEnd = point;
            }
            else { _dragging = true; _dragPosition = point; }
            _capturedPointer = e.Pointer; e.Pointer.Capture(this); e.Handled = true; return;
        }
        int index = HitBird(point); if (index < 0 || _photo is null) return;
        if (buttons.IsLeftButtonPressed) { BirdSelected?.Invoke(index); e.Handled = true; }
        else if (buttons.IsRightButtonPressed)
        {
            var photo = _photo; var bird = photo.Birds[index]; var item = new MenuItem { Header = "删除选区" };
            item.Click += (_, _) => { if (IsInteractive && ReferenceEquals(_photo, photo) && photo.Birds.Contains(bird)) DeleteRegionRequested?.Invoke(photo, bird); };
            var menu = new ContextMenu(); menu.Items.Add(item); menu.Open(this); e.Handled = true;
        }
    }
    /// <summary>命中实际全图位置，重叠框优先命中绘制在最上面的框。</summary>
    private int HitBird(Point point)
    {
        if (_frame is null || _photo is null) return -1;
        double scale = Math.Min(Bounds.Width / _frame.PixelWidth, Bounds.Height / _frame.PixelHeight);
        double width = _frame.PixelWidth * scale, height = _frame.PixelHeight * scale;
        double x = (point.X - (Bounds.Width - width) / 2) / width, y = (point.Y - (Bounds.Height - height) / 2) / height;
        for (int i = _photo.Birds.Count - 1; i >= 0; i--)
        { var bird = _photo.Birds[i]; var b = bird.Box; if (bird.HasRegion && x >= b.X && y >= b.Y && x <= b.X + b.Width && y <= b.Y + b.Height) return i; }
        return -1;
    }
    /// <summary>框选仅更新暂存端点；普通拖拽仍由共享几何钳制。</summary>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!IsInteractive || _view is null) return;
        var position = e.GetPosition(this);
        if (_drawing) { _regionEnd = position; _overlays.InvalidateVisual(); }
        else if (_dragging) { _view.PanBy(position.X - _dragPosition.X, position.Y - _dragPosition.Y); _dragPosition = position; Publish(true); }
    }
    /// <summary>只有有效框才提交，并在回调前解除捕获，防止异步识别过程中继续改变此框。</summary>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drawing && IsInteractive && _view is not null && _photo is { } photo)
        {
            var end = e.GetPosition(this);
            if (_view.TryMapRegion(_regionStart.X, _regionStart.Y, end.X, end.Y, out var region))
            {
                CancelManualRegion();
                ManualRegionSelected?.Invoke(photo, new BirdBox { X = region.X, Y = region.Y, Width = region.Width, Height = region.Height, Confidence = 1 }); return;
            }
        }
        _drawing = _dragging = false; _capturedPointer = null; e.Pointer.Capture(null); RefreshDrawing();
    }
    /// <summary>系统撤销捕获只取消当前手势，不提交半个选区。</summary>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    { base.OnPointerCaptureLost(e); _dragging = _drawing = false; _capturedPointer = null; RefreshDrawing(); }
    /// <summary>持久化与现有工程相同的相对视图，通知宿主更新白框及修改状态。</summary>
    private void Publish(bool user)
    {
        if (!IsDetail || _view is null) return;
        VisibleRegion = _view.VisibleRegion;
        if (_photo is not null) { _photo.ViewZoom = _view.Zoom; _photo.ViewCenterX = _view.CenterX; _photo.ViewCenterY = _view.CenterY; }
        ViewChanged?.Invoke(user); RefreshDrawing();
    }
}
