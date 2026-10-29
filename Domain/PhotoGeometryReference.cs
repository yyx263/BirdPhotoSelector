using System.IO;
using System.Text.Json.Serialization;

namespace BirdPhotoSelector;

public enum PhotoGeometryStatus { Unspecified, Current, NeedsReview }

/// <summary>Original coordinate frame for saved bird boxes, retained even after that file is removed.</summary>
public sealed record PhotoGeometryReference
{
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

    internal static PhotoGeometryReference Capture(PhotoAsset asset, int width, int height)
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
