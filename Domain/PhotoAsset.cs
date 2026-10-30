using System.IO;
using System.Text.Json.Serialization;

namespace BirdPhotoSelector;

public enum PhotoAssetKind { Jpg, Raw }

/// <summary>An immutable original file belonging to one logical photo; it need not currently exist.</summary>
public sealed record PhotoAsset
{
    private static readonly HashSet<string> RawExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".crw", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".sr2", ".srf", ".dng", ".raf", ".rw2", ".orf", ".pef", ".srw",
        ".raw", ".rwl", ".3fr", ".fff", ".iiq", ".mos", ".mrw", ".x3f" };

    public PhotoAsset(string filePath, PhotoAssetKind kind, string? id = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!Enum.IsDefined(kind)) throw new ArgumentOutOfRangeException(nameof(kind));
        if (!TryGetKind(filePath, out var actual) || actual != kind)
            throw new ArgumentException("文件扩展名与照片成员格式不匹配。", nameof(filePath));
        if (id is not null && string.IsNullOrWhiteSpace(id)) throw new ArgumentException("成员 ID 不能为空。", nameof(id));
        Id = id ?? Guid.NewGuid().ToString("N");
        Kind = kind;
        FilePath = filePath;
        CanonicalPath = Canonicalize(filePath);
        PairKey = Path.Combine(Path.GetDirectoryName(CanonicalPath)!, Path.GetFileNameWithoutExtension(CanonicalPath));
    }

    public string Id { get; }
    public PhotoAssetKind Kind { get; }
    public string FilePath { get; }
    [JsonIgnore] public string CanonicalPath { get; }
    [JsonIgnore] public string PairKey { get; }

    public bool CanPairWith(PhotoAsset other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind != other.Kind && StringComparer.OrdinalIgnoreCase.Equals(PairKey, other.PairKey);
    }

    public static bool TryGetKind(string? filePath, out PhotoAssetKind kind)
    {
        kind = PhotoAssetKind.Jpg;
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        string extension = Path.GetExtension(filePath);
        if (extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)) return true;
        if (!RawExtensions.Contains(extension)) return false;
        kind = PhotoAssetKind.Raw;
        return true;
    }

    internal static string Canonicalize(string filePath) => Path.GetFullPath(filePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
