using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

public sealed class MetadataService
{
    public PhotoMetadata Read(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = new JpegBitmapDecoder(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        return Read(decoder.Frames[0].Metadata as BitmapMetadata);
    }

    internal static PhotoMetadata Read(BitmapMetadata? metadata)
    {
        object? Query(params string[] queries)
        {
            foreach (string query in queries)
            {
                try { if (metadata?.GetQuery(query) is { } result) return result; }
                catch (Exception ex) when (ex is NotSupportedException or ArgumentException or System.Runtime.InteropServices.COMException) { }
            }
            return null;
        }
        const string exif = "/app1/ifd/exif/";
        static string Text(object? value) => string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))
            ? "—" : Convert.ToString(value, CultureInfo.InvariantCulture)!.Trim('\0', ' ');
        var model = Query("/app1/ifd/{ushort=272}");
        var date = Text(Query(exif + "{ushort=36867}", "/app1/ifd/{ushort=306}"));
        if (DateTime.TryParseExact(date, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var captured)) date = captured.ToString("yyyy-MM-dd HH:mm:ss");
        double? exposure = Rational(Query(exif + "{ushort=33434}"));
        double? aperture = Rational(Query(exif + "{ushort=33437}"));
        double? focal = Rational(Query(exif + "{ushort=37386}"));
        double? bias = Rational(Query(exif + "{ushort=37380}"), signed: true);
        object? iso = Query(exif + "{ushort=34855}");
        if (iso is ushort[] isoValues) iso = isoValues.FirstOrDefault();
        return new PhotoMetadata
        {
            CapturedAt = date,
            Camera = Text(model),
            FocalLength = focal is { } f ? $"{f:0.#} mm" : "—",
            Aperture = aperture is { } a ? $"f/{a:0.#}" : "—",
            Shutter = exposure is > 0 and < 1 ? $"1/{Math.Round(1 / exposure.Value):0} s" :
                exposure is { } s ? $"{s:0.###} s" : "—",
            Iso = Text(iso),
            ExposureBias = bias is { } b ? b.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " EV" : "—"
        };
    }

    private static double? Rational(object? value, bool signed = false)
    {
        if (value is null) return null;
        try
        {
            ulong packed = value switch { ulong u => u, long l => unchecked((ulong)l), _ => Convert.ToUInt64(value) };
            double numerator = signed ? unchecked((int)(packed & 0xffffffff)) : (uint)(packed & 0xffffffff);
            double denominator = signed ? unchecked((int)(packed >> 32)) : (uint)(packed >> 32);
            return denominator == 0 ? null : numerator / denominator;
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException) { return null; }
    }
}
