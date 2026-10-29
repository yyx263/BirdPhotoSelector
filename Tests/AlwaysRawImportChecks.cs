using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

/// <summary>The production default always discovers paired originals and prefers a real JPEG.</summary>
internal static class AlwaysRawImportChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
            Console.WriteLine("  OK ALWAYS RAW " + message);
        }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.AlwaysRawImportChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new PhotoImportService();
            string first = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            string second = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            string jpg = Path.Combine(first, "capture.JPG"), raw = Path.Combine(first, "capture.NEF");
            string otherJpg = Path.Combine(second, "capture.JPG"), otherRaw = Path.Combine(second, "capture.ARW");
            WriteJpeg(jpg); WriteJpeg(otherJpg);
            // A successful pair must never pass these opaque bytes to any RAW decoder.
            await File.WriteAllTextAsync(raw, "Opaque RAW member. This is not a decodable image.");
            await File.WriteAllTextAsync(otherRaw, "Opaque second RAW member. No embedded JPEG.");
            var originals = new[] { jpg, raw, otherJpg, otherRaw }.ToDictionary(path => path,
                path => SHA256.HashData(File.ReadAllBytes(path)));

            var jpgSelected = await service.ImportAsync(new[] { jpg }, Array.Empty<PhotoItem>());
            var pair = jpgSelected.Added.Single();
            Check(jpgSelected.Errors.Count == 0 && pair.Assets.Count == 2 && pair.FormatLabel == "JPG + RAW" &&
                  pair.Assets.Any(asset => asset.CanonicalPath == raw),
                "selecting only JPG automatically records its same-directory RAW and reports JPG plus RAW");
            Check(pair.ActivePreviewAsset?.Kind == PhotoAssetKind.Jpg && pair.FilePath == jpg &&
                  pair.Thumbnail is { IsFrozen: true, PixelWidth: 64, PixelHeight: 48 },
                "a valid JPG supplies the preview without decoding its opaque RAW companion");

            var rawSelected = await service.ImportAsync(new[] { raw }, Array.Empty<PhotoItem>());
            Check(rawSelected.Errors.Count == 0 && rawSelected.Added.Count == 1 &&
                  rawSelected.Added[0].FormatLabel == "JPG + RAW" &&
                  rawSelected.Added[0].ActivePreviewAsset?.CanonicalPath == jpg,
                "selecting only RAW still discovers and previews its available JPG first");

            var repeat = await service.ImportAsync(new[] { jpg, raw, jpg }, new[] { pair });
            Check(repeat.Added.Count == 0 && repeat.Updates.Count == 0 && pair.Assets.Count == 2,
                "reselecting either member never duplicates an existing logical photo or its rating record");

            var separate = await service.ImportAsync(new[] { jpg, otherJpg }, Array.Empty<PhotoItem>());
            Check(separate.Added.Count == 2 && separate.Added.All(photo => photo.Assets.Count == 2) &&
                  separate.Added.Select(photo => photo.Id).Distinct().Count() == 2,
                "same basenames in different directories stay separate while both discover their own RAW");

            var existing = new PhotoItem { FilePath = jpg, PixelWidth = 64, PixelHeight = 48, Rating = PhotoRating.Favorite };
            existing.SetBirds(new[] { new DetectedBird
            {
                Number = 7, IsManual = true,
                Box = new() { X = .1, Y = .2, Width = .3, Height = .4, Confidence = .9 },
                Species = new("大山雀", "Parus major", .42)
            } });
            string photoId = existing.Id, jpgId = existing.ActivePreviewAssetId!;
            var bird = existing.SelectedBird;
            var attached = await service.ImportAsync(new[] { jpg }, new[] { existing });
            Check(attached.Added.Count == 0 && attached.Updates.Count == 1 && existing.Assets.Count == 1,
                "selecting an existing JPG again prepares a detached companion update before any UI mutation");
            attached.Updates.Single().ApplyTo(existing);
            Check(existing.Id == photoId && existing.Assets.Count == 2 && existing.ActivePreviewAssetId == jpgId &&
                  existing.Rating == PhotoRating.Favorite && ReferenceEquals(existing.SelectedBird, bird) &&
                  bird?.Species?.ScientificName == "Parus major" && existing.FormatLabel == "JPG + RAW",
                "adding a discovered companion preserves photo identity, current JPG, rating and existing species object");

            string plain = Path.Combine(root, "plain.jpg");
            WriteJpeg(plain);
            var jpgOnly = await service.ImportAsync(new[] { plain }, Array.Empty<PhotoItem>());
            Check(jpgOnly.Added.Single().FormatLabel == "JPG" && jpgOnly.Added[0].Assets.Count == 1,
                "a JPG with no RAW companion remains a single JPG rather than inventing a member");

            string conflict = Path.ChangeExtension(raw, ".CR3");
            await File.WriteAllTextAsync(conflict, "Second conflicting RAW candidate.");
            var conflicting = await service.ImportAsync(new[] { jpg }, Array.Empty<PhotoItem>());
            Check(conflicting.Added.Count == 0 && conflicting.Updates.Count == 0 && conflicting.SkippedConflict == 1 &&
                  conflicting.Errors.Single().Contains("capture.NEF", StringComparison.Ordinal) &&
                  conflicting.Errors.Single().Contains("capture.CR3", StringComparison.Ordinal) &&
                  !conflicting.Errors.Single().Contains("勾选", StringComparison.Ordinal) &&
                  !conflicting.Errors.Single().Contains("关闭", StringComparison.Ordinal),
                "multiple RAW candidates remain an explicit conflict without suggesting a removed checkbox");
            Check(originals.All(original => File.Exists(original.Key) &&
                      SHA256.HashData(File.ReadAllBytes(original.Key)).SequenceEqual(original.Value)),
                "automatic pairing and conflict handling preserve every original byte");
            return count;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe import fixture cleanup.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static void WriteJpeg(string path)
    {
        const int width = 64, height = 48;
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat((byte)100, width * height * 3).ToArray(), width * 3);
        image.Freeze();
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
}
