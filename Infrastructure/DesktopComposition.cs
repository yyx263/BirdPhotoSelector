namespace BirdPhotoSelector;

/// <summary>当前 Windows 宿主唯一的默认实现选择点。迁移后的 Avalonia 宿主在自己的入口提供其他后端。</summary>
internal static class DesktopComposition
{
    /// <summary>无会话状态的 Windows 图像后端；原生 RAW 操作在后端内部串行。</summary>
    internal static IPhotoImageDecoder Images { get; } = new WindowsPhotoImageDecoder();
    /// <summary>无会话状态的 Windows 文件身份与回收站能力。</summary>
    internal static IExportFilePlatform Files { get; } = new WindowsFileOperations();
    /// <summary>为一个窗口创建独立的业务服务，禁止业务项目反向读取此静态宿主入口。</summary>
    internal static ApplicationServices Create() => new(Images, Files);
}
