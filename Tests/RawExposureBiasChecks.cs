using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using BirdPhotoSelector;

/// <summary>Real native metadata path, generated TIFF/DNG fixtures and private copies of the public CR3 sample.</summary>
internal static class RawExposureBiasChecks
{
    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            assertions++; Console.WriteLine("  OK RAW EXPOSURE " + message);
        }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.RawExposureBiasChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool bigEndian in new[] { false, true })
            foreach (var test in new[]
            {
                (Name: "positive", Numerator: (int?)2, Denominator: 3, Expected: "+0.67 EV"),
                (Name: "negative", Numerator: (int?)-1, Denominator: 3, Expected: "-0.33 EV"),
                (Name: "zero", Numerator: (int?)0, Denominator: 1, Expected: "0 EV"),
                (Name: "missing", Numerator: (int?)null, Denominator: 1, Expected: "—"),
                (Name: "invalid-denominator", Numerator: (int?)1, Denominator: 0, Expected: "—")
            })
            {
                string path = Path.Combine(root, $"{(bigEndian ? "MM" : "II")}-{test.Name}.dng");
                await File.WriteAllBytesAsync(path, MakeDng(bigEndian, test.Numerator, test.Denominator));
                byte[] before = SHA256.HashData(await File.ReadAllBytesAsync(path));
                var info = RawImageDecoder.ReadInfo(path);
                Check(info.Metadata.ExposureBias == test.Expected,
                    $"{(bigEndian ? "big" : "little")}-endian TIFF/DNG {test.Name} reads shooting EV as {test.Expected}");
                Check(before.SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(path))),
                    "metadata-only inspection leaves the generated TIFF/DNG unchanged");
            }

            string? samples = Environment.GetEnvironmentVariable("BIRD_RAW_SAMPLES");
            string? original = string.IsNullOrWhiteSpace(samples) ? null : Path.Combine(samples, "Canon_EOS_R7_CRAW.CR3");
            if (original is not null && File.Exists(original))
            {
                byte[] originalBytes = await File.ReadAllBytesAsync(original);
                byte[] originalHash = SHA256.HashData(originalBytes);
                var tags = FindCr3ExposureTags(originalBytes);
                Check(tags.Count > 0, "the independent CR3 CMT2 parser finds the actual ExposureBiasValue tag");
                var source = tags[0];
                int sourceNumerator = I32(originalBytes, source.ValueOffset, source.BigEndian);
                int sourceDenominator = I32(originalBytes, source.ValueOffset + 4, source.BigEndian);
                string expected = ((double)sourceNumerator / sourceDenominator)
                    .ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " EV";
                Check(sourceDenominator != 0 && RawImageDecoder.ReadInfo(original).Metadata.ExposureBias == expected,
                    "real CR3 shooting exposure agrees with its independently read CMT2 signed rational");
                foreach (var test in new[]
                {
                    (Name: "positive", Numerator: (int?)2, Denominator: 3, Expected: "+0.67 EV"),
                    (Name: "negative", Numerator: (int?)-1, Denominator: 3, Expected: "-0.33 EV"),
                    (Name: "zero", Numerator: (int?)0, Denominator: 1, Expected: "0 EV"),
                    (Name: "missing", Numerator: (int?)null, Denominator: 1, Expected: "—"),
                    (Name: "invalid-denominator", Numerator: (int?)1, Denominator: 0, Expected: "—")
                })
                {
                    var copy = (byte[])originalBytes.Clone();
                    foreach (var tag in tags)
                    {
                        if (test.Numerator is null) Put16(copy, tag.EntryOffset, 0xfde0, tag.BigEndian);
                        else
                        {
                            Put32(copy, tag.ValueOffset, unchecked((uint)test.Numerator.Value), tag.BigEndian);
                            Put32(copy, tag.ValueOffset + 4, unchecked((uint)test.Denominator), tag.BigEndian);
                        }
                    }
                    string path = Path.Combine(root, test.Name + ".CR3");
                    await File.WriteAllBytesAsync(path, copy);
                    Check(RawImageDecoder.ReadInfo(path).Metadata.ExposureBias == test.Expected,
                        $"real CR3 private {test.Name} metadata fixture reads {test.Expected} without guessing missing EV");
                }
                Check(originalHash.SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(original))),
                    "all CR3 metadata edits stay inside private generated copies; original SHA-256 is unchanged");
            }
            else Console.WriteLine("SKIP: CR3 exposure integration requires BIRD_RAW_SAMPLES/Canon_EOS_R7_CRAW.CR3.");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe exposure fixture cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
        return assertions;
    }

    // A small uncompressed Bayer DNG with standard EXIF. BaselineExposure is deliberately
    // +5 EV in every case, so a missing/zero shooting bias cannot accidentally borrow it.
    private static byte[] MakeDng(bool bigEndian, int? numerator, int denominator)
    {
        const int width = 64, height = 48;
        var tags = new List<(ushort Tag, ushort Type, uint Count, byte[] Data)>();
        byte[] U16(params ushort[] values)
        {
            var bytes = new byte[values.Length * 2];
            for (int i = 0; i < values.Length; i++) Put16(bytes, i * 2, values[i], bigEndian);
            return bytes;
        }
        byte[] U32(params uint[] values)
        {
            var bytes = new byte[values.Length * 4];
            for (int i = 0; i < values.Length; i++) Put32(bytes, i * 4, values[i], bigEndian);
            return bytes;
        }
        void Short(ushort tag, ushort value) => tags.Add((tag, 3, 1, U16(value)));
        void Long(ushort tag, uint value) => tags.Add((tag, 4, 1, U32(value)));
        void Text(ushort tag, string value)
        { var bytes = System.Text.Encoding.ASCII.GetBytes(value + '\0'); tags.Add((tag, 2, (uint)bytes.Length, bytes)); }
        Long(256, width); Long(257, height); Short(258, 16); Short(259, 1); Short(262, 32803);
        Text(271, "BirdPhotoSelector"); Text(272, "Metadata fixture"); Long(273, 0);
        Short(274, 1); Short(277, 1); Long(278, height); Long(279, width * height * 2); Short(284, 1);
        tags.Add((33421, 3, 2, U16(2, 2))); tags.Add((33422, 1, 4, new byte[] { 0, 1, 1, 2 }));
        Long(34665, 0); tags.Add((50706, 1, 4, new byte[] { 1, 4, 0, 0 }));
        tags.Add((50707, 1, 4, new byte[] { 1, 1, 0, 0 })); Text(50708, "BirdPhotoSelector metadata fixture");
        Long(50714, 0); Long(50717, 65535); tags.Add((50730, 10, 1, U32(5, 1)));
        tags.Sort((first, second) => first.Tag.CompareTo(second.Tag));
        int cursor = 8 + 2 + tags.Count * 12 + 4;
        var external = new Dictionary<ushort, int>();
        foreach (var tag in tags.Where(tag => tag.Data.Length > 4))
        { external[tag.Tag] = cursor; cursor += tag.Data.Length; if ((cursor & 1) != 0) cursor++; }
        int exif = cursor, entryCount = numerator.HasValue ? 1 : 0;
        cursor += 2 + entryCount * 12 + 4;
        int rational = cursor; if (numerator.HasValue) cursor += 8;
        int pixels = cursor;
        var result = new byte[pixels + width * height * 2];
        result[0] = result[1] = bigEndian ? (byte)'M' : (byte)'I'; Put16(result, 2, 42, bigEndian); Put32(result, 4, 8, bigEndian);
        Put16(result, 8, (ushort)tags.Count, bigEndian);
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i]; int entry = 10 + i * 12;
            Put16(result, entry, tag.Tag, bigEndian); Put16(result, entry + 2, tag.Type, bigEndian);
            Put32(result, entry + 4, tag.Count, bigEndian);
            if (tag.Tag == 273) Put32(result, entry + 8, (uint)pixels, bigEndian);
            else if (tag.Tag == 34665) Put32(result, entry + 8, (uint)exif, bigEndian);
            else if (tag.Data.Length <= 4) tag.Data.CopyTo(result, entry + 8);
            else { Put32(result, entry + 8, (uint)external[tag.Tag], bigEndian); tag.Data.CopyTo(result, external[tag.Tag]); }
        }
        Put16(result, exif, (ushort)entryCount, bigEndian);
        if (numerator is { } value)
        {
            Put16(result, exif + 2, 0x9204, bigEndian); Put16(result, exif + 4, 10, bigEndian);
            Put32(result, exif + 6, 1, bigEndian); Put32(result, exif + 10, (uint)rational, bigEndian);
            Put32(result, rational, unchecked((uint)value), bigEndian); Put32(result, rational + 4, unchecked((uint)denominator), bigEndian);
        }
        for (int i = 0; i < width * height; i++) Put16(result, pixels + i * 2, (ushort)(1000 + i % 2000), bigEndian);
        return result;
    }

    private sealed record ExposureTag(int EntryOffset, int ValueOffset, bool BigEndian);
    private static List<ExposureTag> FindCr3ExposureTags(byte[] bytes)
    {
        var result = new List<ExposureTag>();
        for (int i = 4; i <= bytes.Length - 16; i++)
        {
            if (!bytes.AsSpan(i, 4).SequenceEqual("CMT2"u8)) continue;
            uint boxBytes = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i - 4, 4));
            if (boxBytes < 16 || boxBytes > bytes.Length - (i - 4)) continue;
            int origin = i + 4, end = checked(i - 4 + (int)boxBytes);
            bool big = bytes[origin] == 'M' && bytes[origin + 1] == 'M';
            if (!big && !(bytes[origin] == 'I' && bytes[origin + 1] == 'I')) continue;
            if (U16(bytes, origin + 2, big) != 42) continue;
            long first = origin + (long)U32(bytes, origin + 4, big);
            if (first < origin + 8 || first + 2 > end) continue;
            int count = U16(bytes, (int)first, big);
            if (first + 2 + count * 12L + 4 > end) continue;
            for (int entry = (int)first + 2; entry < first + 2 + count * 12L; entry += 12)
            {
                if (U16(bytes, entry, big) != 0x9204 || U16(bytes, entry + 2, big) != 10 || U32(bytes, entry + 4, big) != 1) continue;
                long data = origin + (long)U32(bytes, entry + 8, big);
                if (data >= origin && data + 8 <= end) result.Add(new(entry, (int)data, big));
            }
        }
        return result;
    }
    private static ushort U16(byte[] bytes, int offset, bool big) => big
        ? BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2)) : BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset, bool big) => big
        ? BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static int I32(byte[] bytes, int offset, bool big) => unchecked((int)U32(bytes, offset, big));
    private static void Put16(byte[] bytes, int offset, ushort value, bool big)
    { if (big) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset, 2), value); else BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), value); }
    private static void Put32(byte[] bytes, int offset, uint value, bool big)
    { if (big) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset, 4), value); else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value); }
}
