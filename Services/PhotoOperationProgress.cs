using System.IO;

namespace BirdPhotoSelector;

/// <summary>Progress for one input file, including files that are skipped or fail.</summary>
public sealed record PhotoOperationProgress(int Completed, int Total, int CurrentIndex, string FilePath,
    string Stage, bool FileCompleted = false, int PhotoIndex = 0, int PhotoTotal = 0)
{
    public string FilePath { get; init; } = ResolvePath(FilePath);
    public string FileName => Path.GetFileName(FilePath);
    public string Message => PhotoTotal > 0
        ? $"照片 {PhotoIndex} / {PhotoTotal} · 文件 {CurrentIndex} / {Total} · {Stage} · {FileName}"
        : $"{Stage} · {CurrentIndex} / {Total} · {FileName}";

    private static string ResolvePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return path; }
    }
}
