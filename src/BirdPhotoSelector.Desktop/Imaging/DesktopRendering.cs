using Avalonia;

namespace BirdPhotoSelector.Desktop.Imaging;

/// <summary>集中配置真实桌面渲染；显存缓存与 CPU 解码缓存独立计费，均有固定上限。</summary>
internal static class DesktopRendering
{
    // 默认约 28MiB 连一张 24MP 纹理都容纳不下，会在评级/缩放重绘时反复上传整图。
    internal const long GpuCacheBytes = 512L * 1024 * 1024;
    /// <summary>按需保留最近大图纹理，不预分配、不改变像素/插值规则，硬件不可用时仍由框架回退软件绘制。</summary>
    internal static AppBuilder Configure(AppBuilder builder) => builder.With(new SkiaOptions { MaxGpuResourceSizeBytes = GpuCacheBytes });
}
