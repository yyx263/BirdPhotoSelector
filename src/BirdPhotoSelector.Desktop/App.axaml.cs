using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using BirdPhotoSelector.Desktop.Views;

namespace BirdPhotoSelector.Desktop;

/// <summary>主题和应用生命周期；无窗口检查复用主题，但不会自动创建桌面窗口。</summary>
public sealed partial class App : Application
{
    /// <summary>加载项目内主题，不下载字体或其他运行时资源。</summary>
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
    /// <summary>只在桌面生命周期中创建主窗口，平台选择不进入领域对象。</summary>
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { StartupFiles = desktop.Args ?? [] };
        base.OnFrameworkInitializationCompleted();
    }
}
