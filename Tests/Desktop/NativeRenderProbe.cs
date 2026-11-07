using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using BirdPhotoSelector.Desktop.Imaging;
using BirdPhotoSelector.Imaging;
using System.Diagnostics;
using System.Text.Json;

/// <summary>真实平台窗口/显卡测量；不是 Headless 截图，两个标记包围实际图像绘制。</summary>
public sealed class NativeRenderProbe : Application
{
    internal static string Report = "artifacts/render-default.json";
    internal static bool Expanded;
    internal static Exception? Error;
    public override void Initialize() { RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark; Styles.Add(new Avalonia.Themes.Fluent.FluentTheme()); }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var surface = new ProbeSurface();
            var window = new Window { Title = "羽选 · 24MP 渲染测量（自动关闭）", Width = 1100, Height = 720, Content = surface };
            desktop.MainWindow = window;
            window.Opened += async (_, _) =>
            {
                try
                {
                    surface.Frames = await Task.Run(() => Enumerable.Range(0, 2).Select(seed =>
                    {
                        var bytes = new byte[6000 * 4000 * 3];
                        for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(i + seed * 93);
                        return AvaloniaImageFrame.Create(new Rgb24ImageFrame(6000, 4000, bytes));
                    }).ToArray());
                    surface.Done = () =>
                    {
                        var ordered = surface.DrawMilliseconds.Skip(10).Order().ToArray();
                        var result = new { Passed = ordered.Length >= 50, Expanded, surface.Backend, surface.CacheLimit, surface.PeakCacheBytes, Frames = surface.DrawMilliseconds.Count,
                            MedianDrawMs = ordered[ordered.Length / 2], P95DrawMs = ordered[(int)((ordered.Length - 1) * .95)], MaxDrawMs = ordered[^1], ElapsedMs = surface.Elapsed.Elapsed.TotalMilliseconds };
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(Report))!);
                        File.WriteAllText(Report, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true })); window.Close();
                    };
                    surface.Elapsed.Start(); surface.InvalidateVisual();
                }
                catch (Exception ex) { Error = ex; window.Close(); }
            };
            DispatcherTimer.RunOnce(() => { if (window.IsVisible) { Error = new TimeoutException("Native rendering probe timed out."); window.Close(); } }, TimeSpan.FromSeconds(45));
        }
        base.OnFrameworkInitializationCompleted();
    }
    /// <summary>重现两侧预览、逐帧缩放以及每 20 帧在缓存中的两张大图间切换。</summary>
    private sealed class ProbeSurface : Control
    {
        internal AvaloniaImageFrame[] Frames = [];
        internal readonly List<double> DrawMilliseconds = new();
        internal readonly Stopwatch Elapsed = new();
        internal long CacheLimit, PeakCacheBytes;
        internal string Backend = "unknown";
        internal Action? Done;
        private long _begin; private int _frame;
        public override void Render(DrawingContext context)
        {
            base.Render(context); context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
            if (Frames.Length == 0 || _frame >= 70) return;
            int frame = _frame; var image = Frames[(frame / 20) % 2].Image;
            context.Custom(new Marker(this, true, frame));
            context.DrawImage(image, new Rect(image.Size), new Rect(0, 150, Bounds.Width / 2, Bounds.Width / 3));
            double size = 3500 / Math.Pow(1.01, frame % 20);
            context.DrawImage(image, new Rect(1100 + frame % 20 * 4, 700, size, size * .8), new Rect(Bounds.Width / 2, 0, Bounds.Width / 2, Bounds.Height));
            context.Custom(new Marker(this, false, frame));
        }
        /// <summary>仅诊断代码借用 Skia 上下文，读取真实 GPU 缓存；不持有或改变上下文资源。</summary>
        private sealed class Marker(ProbeSurface owner, bool begin, int frame) : ICustomDrawOperation
        {
            public Rect Bounds => new(owner.Bounds.Size);
            public void Dispose() { }
            public bool HitTest(Point point) => false;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Render(ImmediateDrawingContext context)
            {
                if (begin) { owner._begin = Stopwatch.GetTimestamp(); return; }
                if (frame != owner._frame) return;
                using var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease();
                if (lease?.GrContext is { } gpu)
                {
                    owner.Backend = gpu.Backend.ToString(); owner.CacheLimit = (long)gpu.GetResourceCacheLimit();
                    gpu.GetResourceCacheUsage(out _, out long bytes); owner.PeakCacheBytes = Math.Max(owner.PeakCacheBytes, bytes);
                }
                else owner.Backend = "Software";
                owner.DrawMilliseconds.Add(Stopwatch.GetElapsedTime(owner._begin).TotalMilliseconds); owner._frame++;
                Dispatcher.UIThread.Post(() => { if (owner._frame >= 70) owner.Done?.Invoke(); else owner.InvalidateVisual(); }, DispatcherPriority.Render);
            }
        }
    }
}
