namespace BirdPhotoSelector.Imaging;

/// <summary>所有后端共用的扩展名规则。只表示可尝试解码，不能代替文件内容校验。</summary>
public static class PhotoImageFormats
{
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".crw", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".sr2", ".srf", ".dng", ".raf", ".rw2", ".orf", ".pef", ".srw",
        ".raw", ".rwl", ".3fr", ".fff", ".iiq", ".mos", ".mrw", ".x3f" };
    /// <summary>判断 JPG/JPEG 扩展名；大小写比较仅用于格式名称，不用于文件路径身份。</summary>
    public static bool IsJpeg(string? path) => !string.IsNullOrWhiteSpace(path) &&
        (Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
         Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase));
    /// <summary>判断已登记的 RAW 扩展名；是否支持具体机型仍由 LibRaw 判定。</summary>
    public static bool IsRaw(string? path) => !string.IsNullOrWhiteSpace(path) && RawExtensions.Contains(Path.GetExtension(path));
}
