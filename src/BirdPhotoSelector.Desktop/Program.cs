using Avalonia;

namespace BirdPhotoSelector.Desktop;

/// <summary>三平台桌面入口；具体图像/文件实现仅在本宿主组装，不传回业务核心。</summary>
internal static class Program
{
    /// <summary>主线程运行桌面事件循环；工程或照片参数由主窗口在打开后处理。</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--self-test") return Diagnostics.PackageDiagnostics.Run(args[1]);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    /// <summary>同时供真实桌面和无窗口验证入口复用相同应用主题、字体与渲染配置。</summary>
    public static AppBuilder BuildAvaloniaApp() => Imaging.DesktopRendering.Configure(AppBuilder.Configure<App>().UsePlatformDetect()).LogToTrace();
}
