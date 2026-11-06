using System.Globalization;
using System.Runtime.InteropServices;
using BirdPhotoSelector.Imaging.Native;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SkiaSharp;

namespace BirdPhotoSelector.Imaging.Portable;

/// <summary>
/// 三平台统一照片后端：Skia 解码 JPEG，MetadataExtractor 读取 EXIF，固定 LibRaw 处理真实 RAW。
/// 返回已经定向的不可变 RGB24，所有文件流和 Skia 临时位图在返回前释放，不保留文件锁。
/// </summary>
public sealed class PortablePhotoImageDecoder : IPhotoImageDecoder
{
    /// <summary>按格式解码。JPEG 可用原生缩小解码减少临时内存，然后按统一像素中心规则精确缩放并应用方向。</summary>
    public ImageFrame Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
    {
        Validate(path, maxWidth, cancellationToken);
        if (PhotoImageFormats.IsRaw(path)) return NativeRawDecoder.Decode(path, maxWidth, cancellationToken);
        using var stream = File.OpenRead(path);
        using var codec = OpenJpeg(stream);
        int orientation = (int)codec.EncodedOrigin;
        int width = codec.Info.Width, height = codec.Info.Height;
        int wantedWidth = maxWidth > 0 ? Math.Min(width, maxWidth) : width;
        int wantedHeight = Math.Max(1, (int)Math.Round((double)height * wantedWidth / width));
        var scaled = codec.GetScaledDimensions((float)wantedWidth / width);
        using var srgb = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Opaque, srgb);
        _ = checked(info.Width * info.Height * 4);
        using var bitmap = new SKBitmap(info);
        if (bitmap.GetPixels() == IntPtr.Zero) throw new InvalidDataException("JPEG 像素分配失败。");
        cancellationToken.ThrowIfCancellationRequested();
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success) throw new InvalidDataException($"JPEG 解码未完成：{result}。");
        var rgb = new byte[checked(info.Width * info.Height * 3)];
        var row = new byte[checked(info.Width * 4)];
        for (int y = 0; y < info.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Marshal.Copy(IntPtr.Add(bitmap.GetPixels(), checked(y * bitmap.RowBytes)), row, 0, row.Length);
            for (int x = 0; x < info.Width; x++)
            { int target = (y * info.Width + x) * 3; rgb[target] = row[x * 4]; rgb[target + 1] = row[x * 4 + 1]; rgb[target + 2] = row[x * 4 + 2]; }
        }
        ImageFrame frame = new Rgb24ImageFrame(info.Width, info.Height, rgb);
        if (frame.PixelWidth != wantedWidth || frame.PixelHeight != wantedHeight)
            frame = frame.Resize(wantedWidth, wantedHeight, cancellationToken);
        return ImageResampling.Orient(frame, orientation, cancellationToken);
    }

    /// <summary>读取完整显示尺寸与原始拍摄参数。EXIF 可选字段损坏只作缺失处理，不能借用另一原件的数据。</summary>
    public PhotoImageInfo ReadInfo(string path, CancellationToken cancellationToken = default)
    {
        Validate(path, 0, cancellationToken);
        if (PhotoImageFormats.IsRaw(path)) return NativeRawDecoder.ReadInfo(path, cancellationToken);
        int width, height, orientation;
        using (var stream = File.OpenRead(path))
        using (var codec = OpenJpeg(stream))
        { width = codec.Info.Width; height = codec.Info.Height; orientation = (int)codec.EncodedOrigin; }
        PhotoMetadata metadata;
        try { using var stream = File.OpenRead(path); metadata = ReadMetadata(ImageMetadataReader.ReadMetadata(stream)); }
        catch (ImageProcessingException) { metadata = new(); }
        cancellationToken.ThrowIfCancellationRequested();
        return new(metadata, orientation is >= 5 and <= 8 ? height : width,
            orientation is >= 5 and <= 8 ? width : height, orientation);
    }

    /// <summary>只接收真正 JPEG 内容，拒绝改扩展名的 PNG 及无法建立解码器的损坏文件。</summary>
    private static SKCodec OpenJpeg(Stream stream)
    {
        if (stream.ReadByte() != 0xff || stream.ReadByte() != 0xd8) throw new InvalidDataException("文件内容不是有效的 JPEG 图片。");
        stream.Position = 0;
        var codec = SKCodec.Create(stream) ?? throw new InvalidDataException("JPEG 头信息损坏或不受支持。");
        if (codec.EncodedFormat != SKEncodedImageFormat.Jpeg || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        { codec.Dispose(); throw new InvalidDataException("文件内容不是有效的 JPEG 图片。"); }
        return codec;
    }

    /// <summary>把标准 EXIF 字段转换为既有显示文本；曝光补偿使用有符号有理数，保留 0 EV 与缺失区别。</summary>
    private static PhotoMetadata ReadMetadata(IReadOnlyList<MetadataExtractor.Directory> directories)
    {
        var primary = directories.OfType<ExifIfd0Directory>().FirstOrDefault();
        var exif = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        static string Text(MetadataExtractor.Directory? dir, int tag) => dir is not null && dir.ContainsTag(tag)
            ? dir.GetString(tag)?.Trim('\0', ' ') is { Length: > 0 } value ? value : "—" : "—";
        static double? Number(MetadataExtractor.Directory? dir, int tag) => dir is not null && dir.TryGetRational(tag, out var value) &&
            value.Denominator != 0 && double.IsFinite(value.ToDouble()) ? value.ToDouble() : null;
        string date = Text(exif, ExifDirectoryBase.TagDateTimeOriginal);
        if (date == "—") date = Text(primary, ExifDirectoryBase.TagDateTime);
        if (DateTime.TryParseExact(date, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var captured)) date = captured.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        double? exposure = Number(exif, ExifDirectoryBase.TagExposureTime), aperture = Number(exif, ExifDirectoryBase.TagFNumber),
            focal = Number(exif, ExifDirectoryBase.TagFocalLength), bias = Number(exif, ExifDirectoryBase.TagExposureBias);
        return new()
        {
            Camera = Text(primary, ExifDirectoryBase.TagModel), CapturedAt = date,
            FocalLength = focal is { } f ? FormattableString.Invariant($"{f:0.#} mm") : "—",
            Aperture = aperture is { } a ? FormattableString.Invariant($"f/{a:0.#}") : "—",
            Shutter = exposure is > 0 and < 1 ? FormattableString.Invariant($"1/{Math.Round(1 / exposure.Value):0} s") : exposure is { } s ? FormattableString.Invariant($"{s:0.###} s") : "—",
            Iso = Text(exif, ExifDirectoryBase.TagIsoEquivalent),
            ExposureBias = bias is { } b ? b.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " EV" : "—"
        };
    }
    /// <summary>读取前检查取消和格式，不能把任意文件交给扩展名不匹配的解码器。</summary>
    private static void Validate(string path, int maxWidth, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (maxWidth < 0) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        if (!PhotoImageFormats.IsJpeg(path) && !PhotoImageFormats.IsRaw(path)) throw new NotSupportedException("只支持 JPG/JPEG 和已登记的 RAW 格式。");
    }
}
