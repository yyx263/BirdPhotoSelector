using System.IO;
using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

public sealed class ImportResult
{
    public List<PhotoItem> Added { get; } = new();
    public int SkippedUnsupported { get; internal set; }
    public int SkippedDuplicate { get; internal set; }
    public List<string> Errors { get; } = new();
}

public sealed class PhotoImportService
{
    public Task<ImportResult> ImportAsync(IEnumerable<string> paths, IEnumerable<string> existingFileNames,
        CancellationToken cancellationToken = default)
    {
        var inputs = paths.ToArray();
        var names = new HashSet<string>(existingFileNames, StringComparer.OrdinalIgnoreCase);
        return Task.Run(() =>
        {
            var result = new ImportResult();
            foreach (string path in inputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsJpegExtension(path)) { result.SkippedUnsupported++; continue; }
                if (names.Contains(Path.GetFileName(path))) { result.SkippedDuplicate++; continue; }
                try
                {
                    var item = LoadPhoto(path);
                    result.Added.Add(item);
                    names.Add(item.FileName);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { result.Errors.Add($"{Path.GetFileName(path)}：{ex.Message}"); }
            }
            return result;
        }, cancellationToken);
    }

    public static bool IsJpegExtension(string path) =>
        Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

    internal static PhotoItem LoadPhoto(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8)
            throw new InvalidDataException("文件内容不是有效的 JPEG 图片。");
        stream.Position = 0;
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        var metadata = frame.Metadata as BitmapMetadata;
        int orientation = ImageLoader.GetOrientation(metadata);
        var item = new PhotoItem
        {
            FilePath = Path.GetFullPath(path),
            PixelWidth = orientation is >= 5 and <= 8 ? frame.PixelHeight : frame.PixelWidth,
            PixelHeight = orientation is >= 5 and <= 8 ? frame.PixelWidth : frame.PixelHeight,
            Metadata = MetadataService.Read(metadata),
            Thumbnail = ImageLoader.Load(path, 160)
        };
        return item;
    }
}
