using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace BirdPhotoSelector.Controls;

/// <summary>
/// Displays an unaltered full-frame preview beside a pannable detail preview.
/// Zoom is stored relative to fitting the full image; its center uses normalized image coordinates.
/// </summary>
public partial class DualPhotoViewer : UserControl
{
    private const double MaximumPixelScale = 2.0;
    private const double BirdViewportFraction = 0.7;
    private const double MinimumRegionDisplaySize = 8;
    private const double MinimumRegionSourceSize = 4;
    private const string ManualRegionHint = "拖拽框选要识别的鸟 · Esc 取消";
    private const string DetailInteractionHint = "滚轮缩放（适应画面至 200%）· 按住左键拖拽；还原鸟类视图以 70% 占比为目标，并遵循缩放范围";
    private PhotoItem? _photo;
    private BitmapSource? _bitmap;
    private double _zoom = 1;
    private double _centerX = 0.5;
    private double _centerY = 0.5;
    private bool _needsInitialView;
    private bool _notifyInitialView;
    private bool _fitPending;
    private bool _allowUnverifiedBirdFocus;
    private bool _interactionBlocked;
    private bool _dragging;
    private Point _dragStart;
    private double _dragCenterX;
    private double _dragCenterY;
    private bool _manualRegionMode;
    private bool _drawingRegion;
    private Point _regionStart;

    public DualPhotoViewer()
    {
        InitializeComponent();
    }

    public ImageSource? CurrentImage => _bitmap;

    /// <summary>True while the next selected photo is loading; an older frame may remain visible.</summary>
    public bool IsLoading { get; private set; }

    /// <summary>Raised after a user gesture changes the persisted zoom or center.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Raised after selecting a different detected bird through this viewer.</summary>
    public event EventHandler? BirdSelectionChanged;

    public event EventHandler<ManualRegionEventArgs>? ManualRegionSelected;
    public event EventHandler<BirdRegionDeleteEventArgs>? DeleteRegionRequested;
    public bool IsManualRegionSelectionActive => _manualRegionMode;

    /// <summary>
    /// Lets a parent preview-wheel handler distinguish detail zoom from photo navigation.
    /// Header and footer controls are deliberately outside the zoom area.
    /// </summary>
    public bool IsDetailArea(DependencyObject? source)
    {
        while (source is not null)
        {
            if (ReferenceEquals(source, DetailViewport)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    /// <summary>
    /// The caller may decode a full-resolution bitmap off the UI thread and supply it here.
    /// A supplied bitmap should already have its EXIF orientation applied.
    /// </summary>
    public void ShowPhoto(PhotoItem? photo, BitmapSource? bitmap = null)
    {
        CancelManualRegionSelection();
        CloseBirdContextMenus();
        StopDragging();
        IsLoading = false;
        _interactionBlocked = false;
        BirdOverlay.IsHitTestVisible = true;
        _photo = photo;
        _bitmap = null;
        _needsInitialView = false;
        _notifyInitialView = false;
        _fitPending = false;
        _allowUnverifiedBirdFocus = false;
        EmptyTitle.Text = "等待照片";
        EmptyMessage.Text = "从左上角导入 JPG 或 RAW 照片";
        DetectionText.ToolTip = DetailInteractionHint;

        if (photo is not null)
        {
            try
            {
                _bitmap = bitmap ?? ImageLoader.Load(photo.FilePath);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or
                                       NotSupportedException or System.IO.FileFormatException or ArgumentException)
            {
                EmptyMessage.Text = "无法读取这张照片：" + ex.Message;
            }
        }

        FullImage.Source = _bitmap;
        DetailImage.Source = _bitmap;
        var hasImage = _bitmap is not null;
        EmptyState.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        DetailEmptyState.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        RestoreButton.IsEnabled = hasImage;
        FitButton.IsEnabled = hasImage;
        ManualRegionButton.IsEnabled = hasImage;
        DetailViewport.Cursor = hasImage ? Cursors.Hand : Cursors.Arrow;
        ViewportRectangle.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        ViewportOutline.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;

        if (!hasImage)
        {
            BirdOverlay.Children.Clear();
            BirdOverlay.Clip = null;
            ImageSizeText.Text = "白框显示细节预览的位置";
            DetectionText.Text = "适应画面至 200%";
            ZoomText.Text = "—";
            DetailImage.Width = 0;
            DetailImage.Height = 0;
            return;
        }

        ImageSizeText.Text = $"{_bitmap!.PixelWidth:N0} × {_bitmap.PixelHeight:N0}  ·  白框为当前视野";
        RefreshBirdOverlays();
        if (photo!.GeometryStatus != PhotoGeometryStatus.NeedsReview &&
            photo.ViewZoom is double savedZoom && double.IsFinite(savedZoom) && savedZoom > 0)
        {
            _zoom = savedZoom;
            _centerX = ValidCenter(photo.ViewCenterX);
            _centerY = ValidCenter(photo.ViewCenterY);
            UpdateView();
        }
        else
        {
            _needsInitialView = true;
            InitializeView();
        }
    }

    /// <summary>Retains the previous frame while the caller decodes the next selected photo.</summary>
    public void ShowLoading()
    {
        BlockInteractions();
        IsLoading = true;
        DetectionText.Text = "正在载入照片…";
        DetectionText.ToolTip = "正在载入下一张照片，当前画面暂时保留。";
        if (_bitmap is null)
        {
            EmptyTitle.Text = "正在载入照片";
            EmptyMessage.Text = "正在读取原始像素…";
        }
    }

    /// <summary>Retains a previous frame after a decode failure, with its interactions blocked.</summary>
    public void ShowLoadError(string message)
    {
        BlockInteractions();
        IsLoading = false;
        string detail = string.IsNullOrWhiteSpace(message) ? "无法读取这张照片。" : message;
        DetectionText.Text = "载入失败 · " + detail;
        DetectionText.ToolTip = "照片载入失败。当前若有画面则为上一张照片，已暂停操作。\n" + detail;
        if (_bitmap is null)
        {
            EmptyTitle.Text = "照片载入失败";
            EmptyMessage.Text = detail;
        }
    }

    private void BlockInteractions()
    {
        CancelManualRegionSelection();
        CloseBirdContextMenus();
        StopDragging();
        _interactionBlocked = true;
        BirdOverlay.IsHitTestVisible = false;
        FitButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
        ManualRegionButton.IsEnabled = false;
        DetailViewport.Cursor = Cursors.Arrow;
    }

    public void RestoreBirdView()
    {
        if (_interactionBlocked || _bitmap is null || _photo is null) return;
        CancelManualRegionSelection();
        StopDragging();
        _needsInitialView = true;
        _notifyInitialView = true;
        _fitPending = false;
        // The source-change notice remains visible in the window. Focusing an
        // existing box is an explicit review gesture, independent of AI geometry.
        _allowUnverifiedBirdFocus = true;
        InitializeView();
    }

    /// <summary>Selects a detected bird and restores its detail view. Loading frames cannot be edited.</summary>
    public void SelectBird(int index)
    {
        if (_interactionBlocked || _bitmap is null || _photo is null || index < 0 || index >= _photo.Birds.Count) return;
        bool changed = _photo.SelectedBirdIndex != index;
        _photo.SelectedBirdIndex = index;
        RestoreBirdView();
        RefreshBirdOverlays();
        if (changed) BirdSelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Refreshes numbered boxes and recognition text without changing the detail view.</summary>
    public void RefreshBirdOverlays()
    {
        // A retained frame still belongs to the previous photo while the parent loads a new one.
        if (_interactionBlocked) return;
        CloseBirdContextMenus();
        BirdOverlay.Children.Clear();
        UpdateDetectionLabel();
        if (_photo is null || !TryGetFullPhotoBounds(out Rect photoBounds))
        {
            BirdOverlay.Clip = null;
            return;
        }
        BirdOverlay.Clip = new RectangleGeometry(photoBounds);
        for (int index = 0; index < _photo.Birds.Count; index++)
        {
            var bird = _photo.Birds[index];
            var box = bird.Box;
            if (!IsValidBird(box.X, box.Y, box.Width, box.Height)) continue;
            var bounds = new Rect(photoBounds.Left + box.X * photoBounds.Width,
                                  photoBounds.Top + box.Y * photoBounds.Height,
                                  Math.Min(box.Width, 1 - box.X) * photoBounds.Width,
                                  Math.Min(box.Height, 1 - box.Y) * photoBounds.Height);
            bool selected = index == _photo.SelectedBirdIndex;
            var stroke = new SolidColorBrush(selected ? Color.FromArgb(135, 157, 223, 196) : Color.FromArgb(75, 228, 235, 240));
            var target = new Grid
            {
                Width = bounds.Width, Height = bounds.Height, Background = Brushes.Transparent,
                Cursor = Cursors.Hand, Tag = bird,
                ToolTip = $"第 {bird.DisplayNumber} 只鸟 · {bird.SpeciesName}\n{(bird.IsManual ? "手动选区" : "自动定位")} · 点击查看细节，右键删除选区"
            };
            var delete = new MenuItem { Header = "删除选区", Tag = new BirdRegionDeleteEventArgs(_photo, bird) };
            delete.Click += DeleteRegionMenu_Click;
            target.ContextMenu = new ContextMenu();
            target.ContextMenu.Items.Add(delete);
            AutomationProperties.SetName(target, $"选择第 {bird.DisplayNumber} 只鸟");
            target.MouseLeftButtonDown += BirdBox_MouseLeftButtonDown;
            target.Children.Add(new Rectangle
            {
                Margin = new Thickness(.75), Stroke = stroke, StrokeThickness = 1.25,
                StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false
            });
            string numberText = bird.DisplayNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
            double labelWidth = Math.Min(Math.Max(24, 10 + numberText.Length * 7), photoBounds.Width);
            double labelHeight = Math.Min(20, photoBounds.Height);
            var label = new Border
            {
                Width = labelWidth, Height = labelHeight, CornerRadius = new CornerRadius(3),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(selected ? (byte)225 : (byte)190, 20, 29, 33)),
                BorderBrush = stroke, BorderThickness = new Thickness(1),
                RenderTransform = new TranslateTransform(
                    Math.Clamp(bounds.Left + 3, photoBounds.Left, photoBounds.Right - labelWidth) - bounds.Left,
                    Math.Clamp(bounds.Top + 3, photoBounds.Top, photoBounds.Bottom - labelHeight) - bounds.Top),
                Child = new TextBlock
                {
                    Text = numberText,
                    Foreground = selected ? new SolidColorBrush(Color.FromRgb(169, 225, 205)) : Brushes.White,
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            ((TextBlock)label.Child).SetBinding(TextBlock.TextProperty,
                new Binding(nameof(DetectedBird.DisplayNumber)) { Source = bird });
            target.Children.Add(label);
            Canvas.SetLeft(target, bounds.Left);
            Canvas.SetTop(target, bounds.Top);
            BirdOverlay.Children.Add(target);
        }
    }

    private void BirdBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { Tag: DetectedBird bird } && _photo is not null)
            SelectBird(_photo.Birds.IndexOf(bird));
    }

    private void CloseBirdContextMenus()
    {
        if (BirdOverlay is null) return;
        foreach (var target in BirdOverlay.Children.OfType<FrameworkElement>())
            if (target.ContextMenu is { } menu) menu.IsOpen = false;
    }

    private void DeleteRegionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: BirdRegionDeleteEventArgs request } && ReferenceEquals(request.Photo, _photo))
            RequestDeleteRegion(request.Bird);
    }

    /// <summary>Requests removal of the exact current-photo object; the parent owns persistence.</summary>
    public bool RequestDeleteRegion(DetectedBird bird)
    {
        if (!IsEnabled || _interactionBlocked || _bitmap is null || _photo is null || !_photo.Birds.Contains(bird)) return false;
        DeleteRegionRequested?.Invoke(this, new BirdRegionDeleteEventArgs(_photo, bird));
        return true;
    }

    public bool BeginManualRegionSelection()
    {
        if (!IsEnabled || _interactionBlocked || _bitmap is null || _photo is null) return false;
        StopDragging();
        ClearManualRegionDrag();
        _manualRegionMode = true;
        ManualRegionButton.IsChecked = true;
        DetectionText.Text = ManualRegionHint;
        DetailViewport.Cursor = Cursors.Cross;
        DetailViewport.Focus();
        return true;
    }

    public bool CancelManualRegionSelection()
    {
        bool active = _manualRegionMode || _drawingRegion;
        _manualRegionMode = false;
        if (ManualRegionButton is null) return active;
        ManualRegionButton.IsChecked = false;
        ClearManualRegionDrag();
        if (!_interactionBlocked) UpdateDetectionLabel();
        return active;
    }

    /// <summary>Handles Escape for both the local viewer and a parent window's preview-key handler.</summary>
    public bool HandleSelectionKey(Key key) => key == Key.Escape && CancelManualRegionSelection();

    /// <summary>Commits detail-viewport coordinates through the current pan/zoom; no photo model is mutated.</summary>
    public bool CompleteManualRegionSelection(Point start, Point end)
    {
        if (!IsEnabled || _interactionBlocked || !_manualRegionMode || _bitmap is null || _photo is null) return false;
        var photo = _photo;
        if (!TryMapManualRegion(start, end, out var box))
        {
            ClearManualRegionDrag();
            return false;
        }
        CancelManualRegionSelection();
        ManualRegionSelected?.Invoke(this, new ManualRegionEventArgs(photo, box));
        return true;
    }

    private bool TryGetVisibleDetailImageBounds(out Rect bounds)
    {
        bounds = Rect.Empty;
        if (_bitmap is null || !(DetailImage.Width > 0) || !(DetailImage.Height > 0)) return false;
        bounds = Rect.Intersect(new Rect(Canvas.GetLeft(DetailImage), Canvas.GetTop(DetailImage), DetailImage.Width, DetailImage.Height),
                                new Rect(0, 0, DetailViewport.ActualWidth, DetailViewport.ActualHeight));
        return !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
    }

    private bool TryMapManualRegion(Point start, Point end, out BirdBox box)
    {
        box = new BirdBox();
        if (_bitmap is null || !double.IsFinite(start.X) || !double.IsFinite(start.Y) ||
            !double.IsFinite(end.X) || !double.IsFinite(end.Y) ||
            !TryGetVisibleDetailImageBounds(out var visible) || !visible.Contains(start)) return false;
        Rect region = Rect.Intersect(new Rect(start, end), visible);
        if (region.IsEmpty || region.Width < MinimumRegionDisplaySize || region.Height < MinimumRegionDisplaySize ||
            region.Width * _bitmap.PixelWidth / DetailImage.Width < MinimumRegionSourceSize ||
            region.Height * _bitmap.PixelHeight / DetailImage.Height < MinimumRegionSourceSize) return false;
        double left = Math.Clamp((region.Left - Canvas.GetLeft(DetailImage)) / DetailImage.Width, 0, 1);
        double top = Math.Clamp((region.Top - Canvas.GetTop(DetailImage)) / DetailImage.Height, 0, 1);
        double right = Math.Clamp((region.Right - Canvas.GetLeft(DetailImage)) / DetailImage.Width, 0, 1);
        double bottom = Math.Clamp((region.Bottom - Canvas.GetTop(DetailImage)) / DetailImage.Height, 0, 1);
        box = new BirdBox { X = left, Y = top, Width = right - left, Height = bottom - top, Confidence = 0 };
        return box.IsValid;
    }

    private void UpdateManualRegionDrag(Point end)
    {
        if (!TryGetVisibleDetailImageBounds(out var visible)) return;
        var region = Rect.Intersect(new Rect(_regionStart, end), visible);
        ManualRegionOverlay.Clip = new RectangleGeometry(visible);
        ManualRegionRectangle.Visibility = region.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        if (region.IsEmpty) return;
        ManualRegionRectangle.Width = region.Width;
        ManualRegionRectangle.Height = region.Height;
        Canvas.SetLeft(ManualRegionRectangle, region.Left);
        Canvas.SetTop(ManualRegionRectangle, region.Top);
    }

    private void ClearManualRegionDrag()
    {
        _drawingRegion = false;
        ManualRegionRectangle.Visibility = Visibility.Collapsed;
        ManualRegionRectangle.Width = ManualRegionRectangle.Height = 0;
        ManualRegionOverlay.Clip = null;
        if (!_dragging && DetailViewport.IsMouseCaptured) DetailViewport.ReleaseMouseCapture();
        DetailViewport.Cursor = _interactionBlocked || _bitmap is null ? Cursors.Arrow : _manualRegionMode ? Cursors.Cross : Cursors.Hand;
    }

    /// <summary>Centers the full photo at the largest fitting scale, capped at 200% of original pixels.</summary>
    public void FitToFrame()
    {
        if (_interactionBlocked || _bitmap is null || _photo is null) return;
        CancelManualRegionSelection();
        StopDragging();
        _needsInitialView = true;
        _notifyInitialView = true;
        _fitPending = true;
        InitializeView();
    }

    private static double ValidCenter(double? center) =>
        center is double value && double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0.5;

    private double FitScale => _bitmap is null || DetailViewport.ActualWidth <= 0 || DetailViewport.ActualHeight <= 0
        ? 0
        : Math.Min(DetailViewport.ActualWidth / _bitmap.PixelWidth, DetailViewport.ActualHeight / _bitmap.PixelHeight);

    // For tiny originals a natural fit could exceed 200%. Keep the full photo centered at 200% instead.
    private double MinimumPixelScale => Math.Min(FitScale, MaximumPixelScale);

    private double ClampPixelScale(double scale) => Math.Clamp(scale, MinimumPixelScale, MaximumPixelScale);

    private void InitializeView()
    {
        if (_bitmap is null || _photo is null || FitScale <= 0) return;
        _needsInitialView = false;
        _zoom = MinimumPixelScale / FitScale;
        _centerX = _centerY = 0.5;
        var bird = _photo.CanUseBirdGeometry || _allowUnverifiedBirdFocus ? _photo.SelectedBird?.Box : null;
        if (!_fitPending && bird is not null && IsValidBird(bird.X, bird.Y, bird.Width, bird.Height))
        {
            var birdWidth = Math.Min(bird.Width, 1 - bird.X) * _bitmap.PixelWidth;
            var birdHeight = Math.Min(bird.Height, 1 - bird.Y) * _bitmap.PixelHeight;
            var scale = Math.Min(DetailViewport.ActualWidth * BirdViewportFraction / birdWidth,
                                 DetailViewport.ActualHeight * BirdViewportFraction / birdHeight);
            _zoom = ClampPixelScale(scale) / FitScale;
            _centerX = bird.X + bird.Width / 2;
            _centerY = bird.Y + bird.Height / 2;
        }
        UpdateView(_notifyInitialView);
        _notifyInitialView = false;
        _fitPending = false;
    }

    private static bool IsValidBird(double x, double y, double width, double height) =>
        double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(width) && double.IsFinite(height) &&
        x >= 0 && x < 1 && y >= 0 && y < 1 && width > 0 && height > 0;

    private void UpdateDetectionLabel()
    {
        var bird = _photo?.SelectedBird?.Box;
        if (bird is null || !IsValidBird(bird.X, bird.Y, bird.Width, bird.Height))
        {
            DetectionText.Text = "未定位鸟类 · 可手动缩放";
            return;
        }
        var selected = _photo!.SelectedBird;
        DetectionText.Text = _manualRegionMode ? ManualRegionHint : selected is not null && (_photo.Birds.Count > 1 || selected.Species is not null || selected.IsManual)
            ? selected.IsManual ? $"手动选区{selected.DisplayNumber} · {selected.SpeciesName}" : $"第{selected.DisplayNumber}只鸟 · {selected.SpeciesName}"
            : $"鸟类已定位 · {bird.Confidence:P0}";
    }

    private bool TryGetFullPhotoBounds(out Rect bounds)
    {
        bounds = Rect.Empty;
        if (_bitmap is null || FullViewport.ActualWidth <= 0 || FullViewport.ActualHeight <= 0) return false;
        double scale = Math.Min(FullViewport.ActualWidth / _bitmap.PixelWidth, FullViewport.ActualHeight / _bitmap.PixelHeight);
        double width = _bitmap.PixelWidth * scale, height = _bitmap.PixelHeight * scale;
        bounds = new Rect((FullViewport.ActualWidth - width) / 2, (FullViewport.ActualHeight - height) / 2, width, height);
        return true;
    }

    private void UpdateView(bool notifyChange = false)
    {
        if (_bitmap is null || _photo is null || FitScale <= 0) return;
        var scale = ClampPixelScale(FitScale * _zoom);
        _zoom = scale / FitScale;
        var imageWidth = _bitmap.PixelWidth * scale;
        var imageHeight = _bitmap.PixelHeight * scale;
        // A magnified image must cover the viewport on each larger axis. Smaller axes stay
        // centered, so dragging or restoring an edge bird cannot expose additional empty space.
        _centerX = ClampCenter(_centerX, DetailViewport.ActualWidth, imageWidth);
        _centerY = ClampCenter(_centerY, DetailViewport.ActualHeight, imageHeight);
        DetailImage.Width = imageWidth;
        DetailImage.Height = imageHeight;
        Canvas.SetLeft(DetailImage, DetailViewport.ActualWidth / 2 - _centerX * imageWidth);
        Canvas.SetTop(DetailImage, DetailViewport.ActualHeight / 2 - _centerY * imageHeight);
        ZoomText.Text = $"{scale * 100:0.#}%";
        UpdateViewportRectangle(imageWidth, imageHeight);
        var changed = _photo.ViewZoom != _zoom || _photo.ViewCenterX != _centerX || _photo.ViewCenterY != _centerY;
        _photo.ViewZoom = _zoom;
        _photo.ViewCenterX = _centerX;
        _photo.ViewCenterY = _centerY;
        if (changed && notifyChange) ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private static double ClampCenter(double center, double viewportLength, double imageLength)
    {
        // Fitting scale multiplication can leave a sub-pixel floating-point excess.
        if (imageLength <= viewportLength || imageLength - viewportLength < 1e-9) return 0.5;
        double halfVisible = viewportLength / imageLength / 2;
        return Math.Clamp(center, halfVisible, 1 - halfVisible);
    }

    private void UpdateViewportRectangle(double detailWidth, double detailHeight)
    {
        if (_bitmap is null || FullViewport.ActualWidth <= 0 || FullViewport.ActualHeight <= 0) return;
        var fullScale = Math.Min(FullViewport.ActualWidth / _bitmap.PixelWidth, FullViewport.ActualHeight / _bitmap.PixelHeight);
        var fullWidth = _bitmap.PixelWidth * fullScale;
        var fullHeight = _bitmap.PixelHeight * fullScale;
        var photoLeft = (FullViewport.ActualWidth - fullWidth) / 2;
        var photoTop = (FullViewport.ActualHeight - fullHeight) / 2;
        // Clip both the white locator and its contrast outline to the actual fitted photo.
        FullOverlay.Clip = new RectangleGeometry(new Rect(photoLeft, photoTop, fullWidth, fullHeight));
        var halfVisibleWidth = DetailViewport.ActualWidth / detailWidth / 2;
        var halfVisibleHeight = DetailViewport.ActualHeight / detailHeight / 2;
        var normalizedLeft = Math.Max(0, _centerX - halfVisibleWidth);
        var normalizedTop = Math.Max(0, _centerY - halfVisibleHeight);
        var normalizedRight = Math.Min(1, _centerX + halfVisibleWidth);
        var normalizedBottom = Math.Min(1, _centerY + halfVisibleHeight);
        var left = photoLeft + normalizedLeft * fullWidth;
        var top = photoTop + normalizedTop * fullHeight;
        var width = (normalizedRight - normalizedLeft) * fullWidth;
        var height = (normalizedBottom - normalizedTop) * fullHeight;
        foreach (var rectangle in new[] { ViewportOutline, ViewportRectangle })
        {
            // WPF strokes straddle the shape edge. Inset the geometry so the outside of the
            // stroke marks the visible image boundary without painting over the photo margin.
            var insetX = Math.Min(rectangle.StrokeThickness / 2, width / 2);
            var insetY = Math.Min(rectangle.StrokeThickness / 2, height / 2);
            rectangle.Width = Math.Max(0, width - insetX * 2);
            rectangle.Height = Math.Max(0, height - insetY * 2);
            Canvas.SetLeft(rectangle, left + insetX);
            Canvas.SetTop(rectangle, top + insetY);
        }
    }

    private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // SizeChanged may be raised while InitializeComponent is still constructing the visual tree.
        if (DetailViewport is null || FullViewport is null || _bitmap is null || _interactionBlocked) return;
        ClearManualRegionDrag();
        if (_needsInitialView) InitializeView();
        else UpdateView();
        RefreshBirdOverlays();
    }

    private void DetailViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Always consume wheel events within the detail area, even when no photo is loaded.
        e.Handled = true;
        if (_interactionBlocked || _bitmap is null || FitScale <= 0) return;
        ClearManualRegionDrag();
        StopDragging();
        var position = e.GetPosition(DetailViewport);
        var oldScale = FitScale * _zoom;
        var sourceX = _centerX * _bitmap.PixelWidth + (position.X - DetailViewport.ActualWidth / 2) / oldScale;
        var sourceY = _centerY * _bitmap.PixelHeight + (position.Y - DetailViewport.ActualHeight / 2) / oldScale;
        var newScale = ClampPixelScale(oldScale * Math.Pow(1.18, e.Delta / 120.0));
        if (Math.Abs(newScale - oldScale) <= Math.Max(1, oldScale) * 1e-12) return;
        _zoom = newScale / FitScale;
        _centerX = (sourceX - (position.X - DetailViewport.ActualWidth / 2) / newScale) / _bitmap.PixelWidth;
        _centerY = (sourceY - (position.Y - DetailViewport.ActualHeight / 2) / newScale) / _bitmap.PixelHeight;
        UpdateView(notifyChange: true);
    }

    private void DetailViewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_interactionBlocked) { e.Handled = true; return; }
        if (_bitmap is null) return;
        if (_manualRegionMode)
        {
            e.Handled = true;
            var start = e.GetPosition(DetailViewport);
            if (!TryGetVisibleDetailImageBounds(out var visible) || !visible.Contains(start)) return;
            _regionStart = start;
            _drawingRegion = DetailViewport.CaptureMouse();
            if (_drawingRegion) UpdateManualRegionDrag(start);
            return;
        }
        _dragStart = e.GetPosition(DetailViewport);
        _dragCenterX = _centerX;
        _dragCenterY = _centerY;
        _dragging = DetailViewport.CaptureMouse();
        DetailViewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void DetailViewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_interactionBlocked || _bitmap is null || FitScale <= 0) return;
        if (_drawingRegion)
        {
            e.Handled = true;
            if (e.LeftButton == MouseButtonState.Pressed) UpdateManualRegionDrag(e.GetPosition(DetailViewport));
            else ClearManualRegionDrag();
            return;
        }
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            StopDragging();
            return;
        }
        var position = e.GetPosition(DetailViewport);
        var scale = FitScale * _zoom;
        _centerX = _dragCenterX - (position.X - _dragStart.X) / (_bitmap.PixelWidth * scale);
        _centerY = _dragCenterY - (position.Y - _dragStart.Y) / (_bitmap.PixelHeight * scale);
        UpdateView(notifyChange: true);
        e.Handled = true;
    }

    private void DetailViewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawingRegion)
        {
            e.Handled = true;
            CompleteManualRegionSelection(_regionStart, e.GetPosition(DetailViewport));
            return;
        }
        if (!_dragging) return;
        StopDragging();
        e.Handled = true;
    }

    private void DetailViewport_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _dragging = false;
        if (_drawingRegion) ClearManualRegionDrag();
        DetailViewport.Cursor = _interactionBlocked || _bitmap is null ? Cursors.Arrow : _manualRegionMode ? Cursors.Cross : Cursors.Hand;
    }

    private void StopDragging()
    {
        _dragging = false;
        if (DetailViewport.IsMouseCaptured) DetailViewport.ReleaseMouseCapture();
        DetailViewport.Cursor = _interactionBlocked || _bitmap is null ? Cursors.Arrow : _manualRegionMode ? Cursors.Cross : Cursors.Hand;
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e) => RestoreBirdView();

    private void FitButton_Click(object sender, RoutedEventArgs e) => FitToFrame();

    private void ManualRegionButton_Click(object sender, RoutedEventArgs e)
    {
        if (ManualRegionButton.IsChecked == true) BeginManualRegionSelection();
        else CancelManualRegionSelection();
    }

    private void Viewer_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleSelectionKey(e.Key)) e.Handled = true;
    }

    private void Viewer_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DetailViewport is null || ManualRegionRectangle is null) return;
        if (e.NewValue is false)
        {
            CancelManualRegionSelection();
            CloseBirdContextMenus();
            StopDragging();
        }
    }
}

public sealed class ManualRegionEventArgs(PhotoItem photo, BirdBox box) : EventArgs
{
    public PhotoItem Photo { get; } = photo;
    public BirdBox Box { get; } = box;
}

public sealed class BirdRegionDeleteEventArgs(PhotoItem photo, DetectedBird bird) : EventArgs
{
    public PhotoItem Photo { get; } = photo;
    public DetectedBird Bird { get; } = bird;
}
