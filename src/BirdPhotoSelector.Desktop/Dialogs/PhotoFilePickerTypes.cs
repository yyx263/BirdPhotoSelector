using Avalonia.Platform.Storage;

namespace BirdPhotoSelector.Desktop.Dialogs;

/// <summary>本机文件选择器的照片筛选；GTK/Portal 的 glob 可能区分大小写，不能只声明小写后缀。</summary>
internal static class PhotoFilePickerTypes
{
    /// <summary>显式展开扩展名大小写，避免依赖平台对 bracket glob 或大小写折叠的不一致支持。</summary>
    internal static FilePickerFileType Photos { get; } = new("照片")
    {
        Patterns = ExpandCases(["*.jpg", "*.jpeg", "*.crw", "*.cr2", "*.cr3", "*.nef", "*.nrw", "*.arw", "*.sr2", "*.srf", "*.orf", "*.rw2", "*.dng", "*.raf", "*.pef", "*.srw", "*.raw", "*.rwl", "*.3fr", "*.fff", "*.iiq", "*.mos", "*.mrw", "*.x3f"]).Distinct(StringComparer.Ordinal).ToArray()
    };
    /// <summary>保留原有格式范围，只展开字母；数字及通配符不变，最长扩展名为四个字母。</summary>
    private static IEnumerable<string> ExpandCases(IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            IEnumerable<string> variants = [""];
            foreach (char c in pattern)
                variants = variants.SelectMany(prefix => char.IsLetter(c)
                    ? new[] { prefix + char.ToLowerInvariant(c), prefix + char.ToUpperInvariant(c) }
                    : new[] { prefix + c }).ToArray();
            foreach (var variant in variants) yield return variant;
        }
    }
}
