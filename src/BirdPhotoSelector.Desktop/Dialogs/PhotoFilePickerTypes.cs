using Avalonia.Platform.Storage;

namespace BirdPhotoSelector.Desktop.Dialogs;

/// <summary>本机文件选择器的照片格式列表；每个后缀仅显示一个小写模式，避免系统筛选文字过长。</summary>
internal static class PhotoFilePickerTypes
{
    /// <summary>保持既有 JPG/RAW 范围；实际导入格式校验继续由共享图像层按大小写不敏感规则处理。</summary>
    internal static FilePickerFileType Photos { get; } = new("照片")
    {
        Patterns = ["*.jpg", "*.jpeg", "*.crw", "*.cr2", "*.cr3", "*.nef", "*.nrw", "*.arw", "*.sr2", "*.srf", "*.orf", "*.rw2", "*.dng", "*.raf", "*.pef", "*.srw", "*.raw", "*.rwl", "*.3fr", "*.fff", "*.iiq", "*.mos", "*.mrw", "*.x3f"]
    };
}
