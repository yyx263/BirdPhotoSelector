using System.IO;
using System.Text.Json.Serialization;

namespace BirdPhotoSelector;

public enum PhotoAssetKind { Jpg, Raw }

/// <summary>An immutable original file belonging to one logical photo; it need not currently exist.</summary>
public sealed record PhotoAsset
{
    /// <summary>验证原件格式和身份，固定绝对路径与配对键；对象只描述原件，不创建或删除文件。</summary>
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

    /// <summary>只允许同目录同名主体且格式不同的成员配对，禁止合并两张不同格式同类原件。</summary>
    public bool CanPairWith(PhotoAsset other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return Kind != other.Kind && FilePaths.Comparer.Equals(PairKey, other.PairKey);
    }

    /// <summary>使用共用格式表识别 JPG/RAW 扩展名，不以此替代实际解码校验。</summary>
    public static bool TryGetKind(string? filePath, out PhotoAssetKind kind)
    {
        kind = PhotoAssetKind.Jpg;
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        if (PhotoImageFormats.IsJpeg(filePath)) return true;
        if (!PhotoImageFormats.IsRaw(filePath)) return false;
        kind = PhotoAssetKind.Raw;
        return true;
    }

    /// <summary>按当前工作目录解析完整原件路径，保留根目录；路径比较采用宿主的保守大小写规则。</summary>
    public static string Canonicalize(string filePath) => FilePaths.Canonicalize(filePath);
}
