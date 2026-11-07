using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BirdPhotoSelector.Desktop.Views;
using BirdPhotoSelector.Desktop.Controls;
using BirdPhotoSelector.Desktop.Imaging;
using BirdPhotoSelector.Imaging;
using System.Diagnostics;
using System.Text.Json;
using BirdPhotoSelector;

/// <summary>固定规模的通知基准；衡量实际更新范围，避免只凭主观感觉判断交互性能。</summary>
internal static class InteractionChecks
{
    /// <summary>五千照片组中评级两百次，统计无关组刷新和标题通知；只创建内存模型。</summary>
    internal static void Benchmark(string root, bool validate)
    {
        var state = new PhotoSelectionSession();
        var groups = Enumerable.Range(0, 5000).Select(i => { var group = new PhotoGroup(); group.Photos.Add(new PhotoItem { FilePath = Path.Combine(root, $"{i}.jpg") }); return group; }).ToArray();
        state.SetGroups(groups); state.IsPreviewReady = true;
        int groupUpdates = 0, titles = 0;
        foreach (var group in state.Groups) group.PropertyChanged += (_, _) => groupUpdates++;
        state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(state.WindowTitle)) titles++; };
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 200; i++) { state.CurrentRating = i % 2 == 0 ? PhotoRating.Keep : PhotoRating.Favorite; _ = state.RatingSummary; }
        timer.Stop(); int ratingTitles = titles;
        state.Dirty = false; titles = 0;
        for (int i = 0; i < 1000; i++) state.Dirty = true;
        var result = new { Groups = 5000, Ratings = 200, RatingMilliseconds = timer.Elapsed.TotalMilliseconds, GroupUpdates = groupUpdates, RatingTitleUpdates = ratingTitles, DragTitleUpdates = titles };
        Console.WriteLine("INTERACTION: " + JsonSerializer.Serialize(result));
        string folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, validate ? "interaction-after.json" : "interaction-before.json"), JsonSerializer.Serialize(result));
        if (validate && (groupUpdates != 200 || ratingTitles != 1 || titles != 1 || state.UnmarkedCount != 4999)) throw new InvalidOperationException("Interaction notifications spread beyond the changed photo/group.");
    }
    /// <summary>真实布局检查，空状态截图和滑块拖动，避免样式看似正常却失去实际交互。</summary>
    internal static async Task<int> EmptyWindow(MainWindow window)
    {
        int checks = 0;
        void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); checks++; Console.WriteLine("  OK STYLE " + message); }
        await Flush(); Capture(window, "empty-state.png");
        var brand = window.FindControl<Border>("BrandMark")!;
        Check(brand.Bounds.Size == new Size(38, 38), "brand mark is exactly square");
        var text = brand.GetVisualDescendants().OfType<TextBlock>().Single(); var center = text.TranslatePoint(new Point(text.Bounds.Width / 2, text.Bounds.Height / 2), brand)!.Value;
        Check(Math.Abs(center.X - 19) < 1 && Math.Abs(center.Y - 19) < 1, "brand text is centered in both axes");
        foreach (string label in new[] { "拆分整组", "从列表中移除", "将当前图独立" })
        {
            var button = window.GetVisualDescendants().OfType<Button>().First(b => b.Content?.ToString() == label);
            Check(button.HorizontalContentAlignment == Avalonia.Layout.HorizontalAlignment.Center && button.VerticalContentAlignment == Avalonia.Layout.VerticalAlignment.Center, label + " content is centered");
        }
        var slider = window.FindControl<Slider>("GroupingSlider")!;
        var track = slider.GetVisualDescendants().OfType<Track>().Single();
        var thumb = track.GetVisualDescendants().OfType<Thumb>().Single();
        Check(track.GetVisualDescendants().OfType<RepeatButton>().All(b => b.Bounds.Width > 20), "both slider rail segments have visible width");
        var point = thumb.TranslatePoint(new Point(8, 8), window)!.Value;
        double previous = slider.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseMove(point + new Vector(30, 0), RawInputModifiers.LeftMouseButton); window.MouseUp(point + new Vector(30, 0), MouseButton.Left); await Flush();
        Check(slider.Value > previous && slider.Value <= 1, "compact slider really changes threshold when dragged");
        var relative = thumb.TranslatePoint(new Point(8, 8), slider)!.Value;
        Check(Math.Abs(relative.Y - slider.Bounds.Height / 2) < 1 && thumb.Bounds.Height <= slider.Bounds.Height, "slider thumb is centered and unclipped");
        slider.Value = previous; window.Controller.State.Dirty = false;
        return checks;
    }
    /// <summary>核对选中绿底、评级颜色继承及悬停/键盘焦点状态，参数标签和值保持不同颜色。</summary>
    internal static async Task<int> ReadyWindow(MainWindow window)
    {
        int checks = 0;
        void Check(bool pass, string message) { if (!pass) throw new InvalidOperationException(message); checks++; Console.WriteLine("  OK STYLE " + message); }
        await Flush();
        var item = window.FindControl<ListBox>("PhotoList")!.GetVisualDescendants().OfType<ListBoxItem>().First(i => i.IsSelected);
        Check(item.Background?.ToString() == "#ff2a3d3c", "selected thumbnail retains the full green card background");
        foreach (var (name, rating, color, ink) in new[] { ("UnmarkedRating",PhotoRating.Unmarked,"#ffaab7c3","#ff17232b"), ("RejectRating",PhotoRating.Reject,"#ffe08787","#ff35151a"), ("KeepRating",PhotoRating.Keep,"#ff9ddfc4","#ff102f25"), ("FavoriteRating",PhotoRating.Favorite,"#fff4cb71","#ff3d2b0a") })
        {
            var button = window.FindControl<ToggleButton>(name)!;
            var click = button.TranslatePoint(new Point(button.Bounds.Width / 2, 22), window)!.Value;
            window.MouseDown(click, MouseButton.Left); window.MouseUp(click, MouseButton.Left); await Flush();
            Check(window.Controller.State.CurrentRating == rating && button.IsChecked == true && button.Bounds.Height == 44, name + " is mutually latched at the correct height");
            Check(button.Background?.ToString() == color && button.GetVisualDescendants().OfType<TextBlock>().All(t => t.Foreground?.ToString() == ink), name + " preserves its own highlight and readable text");
            var position = button.TranslatePoint(new Point(button.Bounds.Width / 2, 22), window)!.Value; window.MouseMove(position); await Flush();
            Check(button.Background?.ToString() == color, name + " hover preserves the selected color");
            window.FindControl<Slider>("GroupingSlider")!.Focus(NavigationMethod.Pointer);
            button.Focus(NavigationMethod.Tab); await Flush();
            Check(button.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_RatingBorder").BorderBrush?.ToString() == "#fff0fff8", name + " has a visible keyboard focus border");
        }
        Check(window.FindControl<ToggleButton>("UnmarkedRating")!.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "●" && t.FontSize == 11), "unmarked rating is a solid dot");
        var metadata = window.FindControl<BalancedMetadataPanel>("MetadataPanel")!;
        Check(metadata.Children.OfType<TextBlock>().All(t => t.FontSize == 11 && t.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(r => r.Foreground?.ToString()).Distinct().Count() == 2), "metadata is compact with separate label and value colors");
        window.MouseMove(new Point(400, 120)); await Flush(); Capture(window, "rating-focus.png");
        return checks;
    }
    /// <summary>覆盖 SIMD 四像素分组及所有短尾长度，保证 RAW 显示通道没有越界、换色或丢像素。</summary>
    internal static int DisplayPixels()
    {
        for (int width=1;width<=21;width++)
        {
            var rgb=Enumerable.Range(0,width*2*3).Select(i=>(byte)(i*37+11)).ToArray();
            var source=new Rgb24ImageFrame(width,2,rgb); var display=AvaloniaImageFrame.Create(source);
            if (!display.CopyRgb24().SequenceEqual(rgb)) throw new InvalidOperationException("RGB SIMD tail mismatch: " + width);
            var bgra=new byte[width*2*4];
            for(int i=0;i<width*2;i++) { bgra[i*4]=rgb[i*3+2];bgra[i*4+1]=rgb[i*3+1];bgra[i*4+2]=rgb[i*3];bgra[i*4+3]=255; }
            if (!AvaloniaImageFrame.Create(new Bgra32ImageFrame(width,2,bgra)).CopyRgb24().SequenceEqual(rgb)) throw new InvalidOperationException("Direct BGRA display mismatch: " + width);
        }
        Console.WriteLine("PASS: RGB SIMD short/tail pixels and direct BGRA display are byte-identical."); return 2;
    }
    /// <summary>24MP 实际显示帧连续缩放/拖拽，断言静态全图图像层不会随白框更新重绘。</summary>
    internal static async Task<int> LargeViewport(string root)
    {
        var frame = await Task.Run(() => { var rgb = new byte[6000 * 4000 * 3]; Array.Fill(rgb, (byte)95); return AvaloniaImageFrame.Create(new Rgb24ImageFrame(6000, 4000, rgb)); });
        var photo = new PhotoItem { FilePath = Path.Combine(root, "viewport.jpg") };
        var full = new PhotoPane { IsInteractive = true }; var detail = new PhotoPane { IsDetail = true, IsInteractive = true };
        var grid = new Grid { ColumnDefinitions = new("*,*") }; grid.Children.Add(full); Grid.SetColumn(detail, 1); grid.Children.Add(detail);
        var window = new Window { Width = 1200, Height = 720, Content = grid }; window.Show();
        try
        {
            detail.ViewChanged += _ => full.VisibleRegion = detail.VisibleRegion;
            full.ShowPhoto(photo, frame); detail.ShowPhoto(photo, frame); await Flush();
            using (window.CaptureRenderedFrame()) { }
            int before = full.ImageRenderCount; var point = detail.TranslatePoint(new Point(300, 300), window)!.Value;
            var timer = Stopwatch.StartNew();
            for (int i = 0; i < 12; i++) { window.MouseWheel(point, new Vector(0, 1)); await Flush(); using var capture = window.CaptureRenderedFrame(); }
            window.MouseDown(point, MouseButton.Left);
            for (int i = 0; i < 30; i++) { window.MouseMove(point + new Vector(i * 2, i), RawInputModifiers.LeftMouseButton); await Flush(); using var capture = window.CaptureRenderedFrame(); }
            window.MouseUp(point + new Vector(58, 29), MouseButton.Left); timer.Stop();
            var visible = detail.VisibleRegion;
            if (before == 0 || full.ImageRenderCount != before || detail.ImageRenderCount < 3) throw new InvalidOperationException($"Viewport invalidation spread: full {before}->{full.ImageRenderCount}, detail {detail.ImageRenderCount}");
            if (visible.X < 0 || visible.Y < 0 || visible.X + visible.Width > 1 || visible.Y + visible.Height > 1) throw new InvalidOperationException("Locator escaped bounds.");
            Console.WriteLine($"PASS: 24MP viewport: 42 rendered gestures in {timer.Elapsed.TotalMilliseconds:0.0}ms; full image renders {before}->{full.ImageRenderCount}; detail {detail.ImageRenderCount}."); return 2;
        }
        finally { window.Close(); }
    }
    /// <summary>让真实样式、布局与输入作业完成，不人为等待固定动画时长。</summary>
    private static async Task Flush() { Dispatcher.UIThread.RunJobs(); await Task.Delay(5); Dispatcher.UIThread.RunJobs(); }
    /// <summary>保存可人工复核的真实 Skia 渲染结果，避免只验证属性值。</summary>
    private static void Capture(Window window, string name)
    {
        string folder = Path.Combine(Directory.GetCurrentDirectory(), "artifacts/desktop-2.0-alpha8"); Directory.CreateDirectory(folder);
        using var frame = window.CaptureRenderedFrame()!; frame.Save(Path.Combine(folder, name), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

}
