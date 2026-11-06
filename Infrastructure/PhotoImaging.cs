namespace BirdPhotoSelector;

/// <summary>
/// 当前 Windows 宿主的默认图像依赖。具体后端只在此处选择，业务服务通过构造参数接收接口。
/// 迁移到 Avalonia 时由新宿主提供对应的跨平台实现，无需修改缓存或识别的图像契约。
/// </summary>
internal static class PhotoImaging
{
    /// <summary>无会话状态、线程安全的默认后端；LibRaw 操作由后端内部串行控制。</summary>
    internal static IPhotoImageDecoder Decoder { get; } = new WindowsPhotoImageDecoder();
}
