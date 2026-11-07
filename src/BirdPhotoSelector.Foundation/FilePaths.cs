namespace BirdPhotoSelector;

/// <summary>
/// 纯托管的路径语义。扩展名、鸟种身份和文件路径不能共用同一大小写规则。
/// Windows 沿用不区分大小写；Unix 保留大小写，特别避免合并 Linux 与 macOS 大小写敏感卷上的不同文件。
/// macOS 大小写不敏感卷上的别名由实体文件身份保护，不能仅凭文本推断同一原件。
/// </summary>
public static class FilePaths
{
    /// <summary>当前宿主的保守路径比较器，不访问原件或修改文件系统。</summary>
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    /// <summary>规范为完整路径并去除多余末尾分隔符，同时保留盘符根目录与 Unix 根目录。</summary>
    public static string Canonicalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    /// <summary>按文件路径规则比较两个完整路径，不把学名或扩展名的规则套用到这里。</summary>
    public static bool Equal(string? first, string? second) => first is null || second is null
        ? first == second : Comparer.Equals(Canonicalize(first), Canonicalize(second));
    /// <summary>创建用于缓存/快照的稳定文本键；只有 Windows 折叠大小写，Unix 不改写真实路径。</summary>
    public static string Key(string path) => OperatingSystem.IsWindows() ? Canonicalize(path).ToUpperInvariant() : Canonicalize(path);
}
