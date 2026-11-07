using System.IO;
using System.Text.Json.Serialization;

namespace BirdPhotoSelector;

/// <summary>鸟框坐标来源状态：未指定、已确认匹配当前原件、或来源变化后需要人工核对。</summary>
public enum PhotoGeometryStatus { Unspecified, Current, NeedsReview }

/// <summary>Original coordinate frame for saved bird boxes, retained even after that file is removed.</summary>
public sealed record PhotoGeometryReference
{
    /// <summary>验证原件 ID、显示尺寸与可选文件版本，固定保存鸟框所属的原始坐标来源。</summary>
    public PhotoGeometryReference(string assetId, string filePath, int? displayWidth, int? displayHeight,
        long? fileLength = null, long? lastWriteTimeUtcTicks = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (displayWidth is <= 0 || displayHeight is <= 0)
            throw new ArgumentOutOfRangeException(nameof(displayWidth), "已知的参考图像尺寸必须大于零。");
        if (fileLength is < 0) throw new ArgumentOutOfRangeException(nameof(fileLength));
        if (lastWriteTimeUtcTicks is { } ticks && (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks))
            throw new ArgumentOutOfRangeException(nameof(lastWriteTimeUtcTicks));
        AssetId = assetId; FilePath = filePath; DisplayWidth = displayWidth; DisplayHeight = displayHeight;
        FileLength = fileLength; LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
        CanonicalPath = PhotoAsset.Canonicalize(filePath);
    }

    public string AssetId { get; }
    public string FilePath { get; }
    public int? DisplayWidth { get; }
    public int? DisplayHeight { get; }
    public long? FileLength { get; }
    public long? LastWriteTimeUtcTicks { get; }
    [JsonIgnore] public string CanonicalPath { get; }

    /// <summary>记录已知显示尺寸及可读取的文件版本；文件信息失败时保留身份，不捏造版本。</summary>
    public static PhotoGeometryReference Capture(PhotoAsset asset, int width, int height)
    {
        long? length = null, modified = null;
        try
        {
            var file = new FileInfo(asset.CanonicalPath);
            if (file.Exists) { length = file.Length; modified = file.LastWriteTimeUtc.Ticks; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        // A legacy member may retain a relative facade path. Its captured frame must
        // stay anchored to the original file if the process working directory changes.
        return new(asset.Id, asset.CanonicalPath, width > 0 ? width : null, height > 0 ? height : null, length, modified);
    }
}
