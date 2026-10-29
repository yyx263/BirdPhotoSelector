using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

/// <summary>Opt-in real RAW integration. Public CC0 samples remain read-only.</summary>
internal static class RawNativeChecks
{
    public static async Task<int> RunAsync()
    {
        string? sampleRoot = Environment.GetEnvironmentVariable("BIRD_RAW_SAMPLES");
        if (string.IsNullOrWhiteSpace(sampleRoot))
        {
            Console.WriteLine("SKIP: real RAW integration requires BIRD_RAW_SAMPLES.");
            return 0;
        }
        sampleRoot = Path.GetFullPath(sampleRoot);
        string manifestPath = Path.GetFullPath(Environment.GetEnvironmentVariable("BIRD_RAW_MANIFEST") ??
            Path.Combine("scripts", "RawCompatibility.samples.json"));
        string reportDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("BIRD_RAW_REPORT_DIRECTORY") ??
            Path.Combine("artifacts", "raw-stage3"));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var samples = manifest.RootElement.GetProperty("Samples").EnumerateArray().Select(sample => new Sample(
            sample.GetProperty("Id").GetString()!, sample.GetProperty("FileName").GetString()!,
            sample.GetProperty("Sha256").GetString()!, sample.GetProperty("ExpectedStatus").GetString()!)).ToArray();
        // Prevent an accidental report setting from overwriting a read-only original.
        string samplePrefix = sampleRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (reportDirectory.Equals(sampleRoot, StringComparison.OrdinalIgnoreCase) || reportDirectory.StartsWith(samplePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("RAW integration artifacts must not be written inside the original sample directory.");
        Directory.CreateDirectory(reportDirectory);
        int assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
            Console.WriteLine("  OK RAW NATIVE " + message);
        }

        string runId = Guid.NewGuid().ToString("N");
        bool completed = false;
        var cases = samples.Select(sample => new CaseResult { Id = sample.Id, Expected = sample.Expected,
                Input = Path.Combine(sampleRoot, sample.FileName) })
            .Concat(new[] { "unicode-path", "pre-canceled", "cancel-during-decode", "raw-only-import" }
                .Select(id => new CaseResult { Id = id, Expected = id.Contains("cancel") ? "Cancelled" : "Decoded" })).ToList();
        string summaryPath = Path.Combine(reportDirectory, "summary.json");
        async Task WriteReport()
        {
            string temporary = summaryPath + "." + runId + ".tmp";
            var summary = new
            {
                RunId = runId, RecordedAtUtc = DateTimeOffset.UtcNow, Completed = completed,
                Status = completed && cases.All(item => item.Status == "Passed") ? "Passed" :
                    cases.Any(item => item.Status == "Failed") ? "Failed" : "Running",
                Assertions = assertions, SampleManifest = manifestPath,
                Decoder = "Pinned LibRaw 0.22.3 native bridge; full RAW unpack and color processing before resizing; no embedded JPEG",
                Orientation = "LibRaw camera orientation applied once by the decoder; ReadInfo dimensions are display dimensions",
                GeometryMapping = "JPG/RAW same-shot frame mapping is not validated by these unrelated samples",
                Cases = cases
            };
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, summaryPath, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        async Task RunCase(CaseResult result, Func<CaseResult, Task> action)
        {
            result.Status = "Running";
            await WriteReport();
            var watch = Stopwatch.StartNew();
            try
            {
                await action(result);
                result.Status = "Passed";
            }
            catch (Exception ex)
            {
                result.Status = "Failed"; result.Error = ex.ToString();
                throw;
            }
            finally { result.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds; await WriteReport(); }
        }

        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.RawNativeChecks");
        string temporaryRoot = Path.Combine(allowed, runId);
        Directory.CreateDirectory(temporaryRoot);
        await WriteReport();
        try
        {
            foreach (var sample in samples)
            {
                var result = cases.Single(item => item.Id == sample.Id);
                await RunCase(result, async outcome =>
                {
                    string path = outcome.Input!;
                    Check(File.Exists(path), sample.Id + " has its recorded local sample");
                    outcome.InputBytes = new FileInfo(path).Length;
                    outcome.Sha256Before = await Hash(path);
                    Check(outcome.Sha256Before.Equals(sample.Sha256, StringComparison.OrdinalIgnoreCase),
                        sample.Id + " matches the public sample fingerprint");
                    if (sample.Expected == "Unsupported")
                    {
                        bool unsupported = false;
                        try { _ = ImageLoader.Load(path, 960); }
                        catch (InvalidDataException ex)
                        { unsupported = ex.Message.Contains("-2", StringComparison.Ordinal); outcome.NativeDiagnostic = ex.Message; }
                        Check(unsupported, sample.Id + " clearly reports unsupported HE compression instead of an embedded-preview success");
                    }
                    else
                    {
                        var watch = Stopwatch.StartNew();
                        outcome.Info = RawImageDecoder.ReadInfo(path);
                        if (sample.Id == "canon-r7-cr3-craw")
                            Check(outcome.Info.Metadata.CapturedAt == "2022-06-26 10:45:32",
                                "RAW capture time retains the original EXIF wall clock without an eight-hour UTC shift");
                        outcome.InfoMilliseconds = watch.Elapsed.TotalMilliseconds;
                        watch.Restart();
                        var image = ImageLoader.Load(path, 960);
                        outcome.DecodeMilliseconds = watch.Elapsed.TotalMilliseconds;
                        outcome.PreviewWidth = image.PixelWidth; outcome.PreviewHeight = image.PixelHeight;
                        Check(outcome.Info.PixelWidth > 0 && outcome.Info.PixelHeight > 0 &&
                              outcome.Info.Metadata.Camera is { Length: > 0 } camera && camera != "—",
                            sample.Id + " reads real camera metadata and oriented full-size dimensions");
                        int expectedWidth = Math.Min(960, outcome.Info.PixelWidth);
                        int expectedHeight = Math.Max(1, (int)Math.Round(outcome.Info.PixelHeight *
                            (expectedWidth / (double)outcome.Info.PixelWidth)));
                        Check(image.IsFrozen && image.PixelWidth == expectedWidth && Math.Abs(image.PixelHeight - expectedHeight) <= 2,
                            sample.Id + " returns a frozen display-oriented preview with matching aspect and no second rotation");
                        Check(HasColorVariation(image), sample.Id + " returns varying RGB pixels rather than a black or empty frame");
                        outcome.PreviewPath = Path.Combine(reportDirectory, sample.Id + ".png");
                        SavePng(image, outcome.PreviewPath);
                    }
                    outcome.Sha256After = await Hash(path);
                    Check(outcome.Sha256Before == outcome.Sha256After, sample.Id + " leaves original RAW bytes unchanged");
                });
            }

            var positive = samples.First(sample => sample.Expected == "Decoded" && sample.FileName.EndsWith(".CR2", StringComparison.OrdinalIgnoreCase));
            string original = Path.Combine(sampleRoot, positive.FileName);
            string unicode = Path.Combine(temporaryRoot, "中文 空格 原片" + Path.GetExtension(original));
            File.Copy(original, unicode);
            await RunCase(cases.Single(item => item.Id == "unicode-path"), async outcome =>
            {
                outcome.Input = unicode; outcome.InputBytes = new FileInfo(unicode).Length;
                outcome.Sha256Before = await Hash(unicode);
                outcome.Info = RawImageDecoder.ReadInfo(unicode);
                var image = ImageLoader.Load(unicode, 640);
                outcome.PreviewWidth = image.PixelWidth; outcome.PreviewHeight = image.PixelHeight;
                Check(image.IsFrozen && image.PixelWidth > 0 && HasColorVariation(image),
                    "a Unicode and space path reaches actual RAW decoding without an ANSI-path fallback");
                outcome.Sha256After = await Hash(unicode);
                Check(outcome.Sha256Before == outcome.Sha256After && outcome.Sha256Before.Equals(positive.Sha256, StringComparison.OrdinalIgnoreCase),
                    "Unicode decode preserves the exact copied RAW bytes");
            });

            await RunCase(cases.Single(item => item.Id == "pre-canceled"), async outcome =>
            {
                outcome.Input = original; outcome.InputBytes = new FileInfo(original).Length;
                outcome.Sha256Before = await Hash(original);
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                bool canceled = false;
                try { _ = ImageLoader.Load(original, 640, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                outcome.Sha256After = await Hash(original);
                Check(canceled && outcome.Sha256Before == outcome.Sha256After,
                    "pre-canceled RAW requests return cancellation without an image or original-file changes");
            });

            string large = Path.Combine(sampleRoot, samples.First(sample => sample.Expected == "Decoded").FileName);
            await RunCase(cases.Single(item => item.Id == "cancel-during-decode"), async outcome =>
            {
                outcome.Input = large; outcome.InputBytes = new FileInfo(large).Length;
                outcome.Sha256Before = await Hash(large);
                using var cancellation = new CancellationTokenSource(); cancellation.CancelAfter(TimeSpan.FromMilliseconds(20));
                bool canceled = false;
                var watch = Stopwatch.StartNew();
                try { _ = ImageLoader.Load(large, 960, cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                outcome.DecodeMilliseconds = watch.Elapsed.TotalMilliseconds;
                outcome.Sha256After = await Hash(large);
                Check(canceled && outcome.Sha256Before == outcome.Sha256After && watch.Elapsed < TimeSpan.FromSeconds(15),
                    "cancellation during a real RAW decode aborts promptly, returns no partial image and preserves the source");
            });

            await RunCase(cases.Single(item => item.Id == "raw-only-import"), async outcome =>
            {
                outcome.Input = original; outcome.InputBytes = new FileInfo(original).Length;
                outcome.Sha256Before = await Hash(original);
                var result = await new PhotoImportService().ImportAsync(new[] { original }, Array.Empty<PhotoItem>());
                var photo = result.Added.Single();
                outcome.Info = RawImageDecoder.ReadInfo(original);
                outcome.PreviewWidth = photo.Thumbnail?.PixelWidth; outcome.PreviewHeight = photo.Thumbnail?.PixelHeight;
                Check(result.Errors.Count == 0 && photo.Assets.Count == 1 && photo.ActivePreviewAsset?.Kind == PhotoAssetKind.Raw &&
                      photo.Thumbnail is { IsFrozen: true } thumbnail && thumbnail.PixelWidth <= 160 &&
                      HasColorVariation(thumbnail) && photo.PixelWidth > 160 && photo.PixelHeight > 160 && photo.Metadata.Camera != "—",
                    "RAW-only import supplies its own real demosaiced thumbnail, camera metadata and full display dimensions");
                outcome.Sha256After = await Hash(original);
                Check(outcome.Sha256Before == outcome.Sha256After, "real RAW-only import never rewrites the original");
            });
            completed = true;
            return assertions;
        }
        finally
        {
            await WriteReport();
            string resolved = Path.GetFullPath(temporaryRoot);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task<string> Hash(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }
    private static bool HasColorVariation(BitmapSource image)
    {
        int stride = checked(image.PixelWidth * ((image.Format.BitsPerPixel + 7) / 8));
        var pixels = new byte[checked(stride * image.PixelHeight)];
        image.CopyPixels(pixels, stride, 0);
        return pixels.Distinct().Take(3).Count() >= 3;
    }
    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
    private sealed record Sample(string Id, string FileName, string Sha256, string Expected);
    private sealed class CaseResult
    {
        public string Id { get; init; } = "";
        public string Expected { get; init; } = "";
        public string Status { get; set; } = "NotRun";
        public string? Input { get; set; }
        public long? InputBytes { get; set; }
        public string? Sha256Before { get; set; }
        public string? Sha256After { get; set; }
        public RawImageInfo? Info { get; set; }
        public int? PreviewWidth { get; set; }
        public int? PreviewHeight { get; set; }
        public string? PreviewPath { get; set; }
        public string? NativeDiagnostic { get; set; }
        public double? InfoMilliseconds { get; set; }
        public double? DecodeMilliseconds { get; set; }
        public double? ElapsedMilliseconds { get; set; }
        public string? Error { get; set; }
    }
}
