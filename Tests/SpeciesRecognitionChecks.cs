using System.IO;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>Offline deterministic checks; native model integration is explicitly enabled by environment.</summary>
internal static class SpeciesRecognitionChecks
{
    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
            Console.WriteLine("  OK SPECIES " + message);
        }
        void Reject<T>(Action action, string message) where T : Exception
        {
            bool rejected = false;
            try { action(); } catch (T) { rejected = true; }
            Check(rejected, message);
        }
        async Task Canceled(Task task, string message)
        {
            bool canceled = false;
            try { await task; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, message);
        }
        string root = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SpeciesChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string assets = Path.Combine(root, "catalog");
            WriteCatalog(assets);
            var catalog = SpeciesCandidateCatalog.Load(assets);
            Check(catalog.Count == 2 && catalog.Scope == "China", "candidate row count and geographic scope are explicit");
            var match = catalog.FindBest(Vector(3, 4));
            Check(match.ChineseName == "测试乙" && match.ScientificName == "Testus beta" && Near(match.Similarity, .8),
                "Chinese and scientific names follow normalized text-vector rows; score is raw cosine");
            Check(match.Confidence is > .99 and <= 1 && match.RunnerUpSimilarity is { } runnerUp && Near(runnerUp, .6) &&
                match.AlternativeName?.Contains("Testus alpha") == true && match.ConfidenceMethod == "BioCLIP2CandidateSoftmax-v1",
                "recognition preserves raw evidence and a runner-up while computing learned-temperature candidate confidence");
            double? tied = SpeciesConfidence.Calculate(new[] { .7, .7 }, 0);
            double? uncertain = SpeciesConfidence.Calculate(new[] { .7, .6999 }, 0);
            double? crowd = SpeciesConfidence.Calculate(new[] { .7, .6999, .69 }, 0);
            Check(tied == .5 && uncertain is > .5 and < .51 && crowd < uncertain,
                "ties and close alternatives lower confidence, and every installed candidate contributes to competition");
            Check(Near(SpeciesConfidence.Calculate(new[] { .7, .64 }, 0)!.Value,
                    1 / (1 + Math.Exp(-SpeciesConfidence.LogitScale * .06))) &&
                SpeciesConfidence.Calculate(new[] { .7 }, 0) is null,
                "confidence follows the pinned model temperature instead of linearly rescaling cosine or inventing certainty without alternatives");
            Reject<ArgumentException>(() => SpeciesConfidence.Calculate(new[] { .7, double.NaN }, 0),
                "non-finite confidence evidence is rejected");
            var negative = catalog.FindBest(Vector(-3, -4));
            Check(negative.ScientificName == "Testus alpha" && Near(negative.Similarity, -.6),
                "negative cosine remains negative rather than being converted into a probability");
            Reject<InvalidOperationException>(() => catalog.FindBest(Vector(3, 4), chinaOnly: false),
                "China-only candidates cannot silently claim to be a global search");
            Reject<InvalidDataException>(() => catalog.FindBest(new float[3]), "wrong image embedding dimension is rejected");
            Reject<InvalidDataException>(() => catalog.FindBest(new float[768]), "zero image embedding is rejected");
            var invalidVector = Vector(3, 4); invalidVector[3] = float.NaN;
            Reject<InvalidDataException>(() => catalog.FindBest(invalidVector), "non-finite image embedding is rejected");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Reject<OperationCanceledException>(() => catalog.FindBest(Vector(3, 4), cancellationToken: cancellation.Token),
                    "candidate search honors cancellation");
            }
            string malformed = Path.Combine(root, "malformed");
            WriteCatalog(malformed);
            File.WriteAllBytes(Path.Combine(malformed, "embeddings.f32"), new byte[12]);
            Reject<InvalidDataException>(() => SpeciesCandidateCatalog.Load(malformed), "truncated or misaligned embedding tables are rejected");
            WriteCatalog(malformed, zeroRow: true);
            Reject<InvalidDataException>(() => SpeciesCandidateCatalog.Load(malformed), "zero text embedding rows are rejected");
            WriteCatalog(malformed, nonFiniteRow: true);
            Reject<InvalidDataException>(() => SpeciesCandidateCatalog.Load(malformed), "non-finite text embedding rows are rejected");
            WriteCatalog(malformed, duplicateNames: true);
            Reject<InvalidDataException>(() => SpeciesCandidateCatalog.Load(malformed), "duplicate scientific names cannot break row alignment");
            WriteCatalog(malformed, dimension: 512);
            Reject<InvalidDataException>(() => SpeciesCandidateCatalog.Load(malformed), "a different model embedding dimension is rejected");
            WriteCatalog(malformed);
            File.WriteAllText(Path.Combine(malformed, "metadata.json"), JsonSerializer.Serialize(new
            { Model = "BioCLIP 2", Dimension = 768, CandidateScope = "China", CropPolicy = "UnknownCropPolicy" }));
            Reject<InvalidDataException>(() => SpeciesCandidateCatalog.Load(malformed), "a conflicting crop policy cannot silently use different image semantics");
            File.WriteAllText(Path.Combine(malformed, "metadata.json"), JsonSerializer.Serialize(new
            { Model = "BioCLIP 2", Dimension = 768, CandidateScope = "China", Mean = new[] { .5, .5, .5 } }));
            Reject<InvalidDataException>(() => SpeciesCandidateCatalog.Load(malformed), "conflicting RGB normalization metadata is rejected");
            File.WriteAllText(Path.Combine(malformed, "metadata.json"), JsonSerializer.Serialize(new
            { Model = "BioCLIP 2", Dimension = 768, CandidateScope = "China", ConfidenceLogitScale = 1 }));
            Reject<InvalidDataException>(() => SpeciesCandidateCatalog.Load(malformed), "a conflicting confidence temperature cannot silently change the pinned model method");

            var full = Box(0, 0, 1, 1);
            var red = Bitmap(17, 11, (_, _) => (255, 0, 0));
            var input = SpeciesImagePreprocessor.CreateInput(red, full);
            Check(input.Dimensions.SequenceEqual(new[] { 1, 3, 224, 224 }), "encoder input is RGB float32 NCHW at the model's fixed 224 pixels");
            Check(Near(input[0, 0, 73, 129], (1 - .48145466f) / .26862954f) &&
                Near(input[0, 1, 73, 129], -.4578275f / .26130258f) &&
                Near(input[0, 2, 73, 129], -.40821073f / .27577711f),
                "RGB order and official OpenCLIP mean/std are applied after bicubic resize");
            var longBird = Bitmap(100, 20, (x, _) => x < 30 ? (255, 0, 0) : x > 70 ? (0, 0, 255) : (0, 255, 0));
            var longInput = SpeciesImagePreprocessor.CreateInput(longBird, Box(.1, .4, .8, .2));
            Check(longInput[0, 0, 112, 20] > 1.5f && longInput[0, 2, 112, 205] > 1.5f,
                "square context with edge extension retains a long bird's head and tail through center cropping");
            var split = Bitmap(160, 80, (x, _) => x < 80 ? (255, 0, 0) : (0, 0, 255));
            var leftInput = SpeciesImagePreprocessor.CreateInput(split, Box(.05, .25, .25, .5));
            var rightInput = SpeciesImagePreprocessor.CreateInput(split, Box(.70, .25, .25, .5));
            Check(leftInput[0, 0, 112, 112] > 1.5f && rightInput[0, 2, 112, 112] > 1.5f &&
                rightInput[0, 0, 112, 112] < -1,
                "each selected bird box creates its own crop rather than reusing the full-photo center");
            Reject<ArgumentException>(() => SpeciesImagePreprocessor.CreateInput(red, Box(0, 0, 0, 1)),
                "invalid crop boxes fail before image allocation");

            // Generated independently by Pillow 11.0.0, not by the C# implementation.
            // Source/provenance: artifacts/species-preprocess-reference.py and its JSON output.
            foreach (var golden in new[]
            {
                (Width: 17, Height: 11, Box: Box(.2, .1, .6, .7), Hash: "f6e56046b14e495f42dbdcb4892de333254b204e65a6da47795c62e55db91772"),
                (Width: 517, Height: 311, Box: Box(.1, .1, .6, .6), Hash: "5d90600249d582d0f69752476eb0798fdbac99c14d5b8c2677ad6ee0295cd6e9"),
                (Width: 17, Height: 11, Box: Box(.75, .02, .25, .5), Hash: "a8644ad8dbbdfb53721aef3f32b06d71f19fa441dc80b23797297fe75abfb305")
            })
            {
                var pattern = Bitmap(golden.Width, golden.Height, (x, y) =>
                    ((x * 17 + y * 13) % 256, (x * 7 + y * 31) % 256, (x * 47 + y * 3) % 256));
                var tensor = SpeciesImagePreprocessor.CreateInput(pattern, golden.Box);
                var reconstructed = new byte[224 * 224 * 3];
                float[] means = { .48145466f, .4578275f, .40821073f }, deviations = { .26862954f, .26130258f, .27577711f };
                for (int y = 0; y < 224; y++) for (int x = 0; x < 224; x++) for (int channel = 0; channel < 3; channel++)
                    reconstructed[(y * 224 + x) * 3 + channel] = (byte)Math.Clamp(
                        Math.Round((tensor[0, channel, y, x] * deviations[channel] + means[channel]) * 255), 0, 255);
                Check(Convert.ToHexString(SHA256.HashData(reconstructed)).Equals(golden.Hash, StringComparison.OrdinalIgnoreCase),
                    $"Pillow golden RGB bytes match for {golden.Width} × {golden.Height} crop including antialiasing and edge padding");
            }

            int encoderCalls = 0;
            using (var service = new BirdSpeciesRecognitionService(assets, (tensor, ct) =>
            {
                ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref encoderCalls); return Vector(3, 4);
            }))
            {
                Check(encoderCalls == 0 && service.HasRequiredAssets, "model encoding is lazy and the injected encoder needs no native model file");
                var result = service.Recognize(red, full);
                Check(result.Match is { IsValid: true, ScientificName: "Testus beta" } && Near(result.Match.Similarity, .8) && result.Error is null,
                    "recognition returns a named detached cosine match with a compatible encoder");
                var noGlobal = service.Recognize(red, full, chinaOnly: false);
                Check(noGlobal.Match is null && noGlobal.Error is not null, "unsupported global search is an explicit result error");
            }
            using (var missing = new BirdSpeciesRecognitionService(Path.Combine(root, "missing")))
            {
                var result = missing.Recognize(red, full);
                Check(result.MissingModel && result.Match is null && result.Error?.Contains("Setup-SpeciesModel") == true,
                    "missing offline assets explain how to install the model without contacting a cloud service");
            }
            File.WriteAllText(Path.Combine(malformed, "vision.onnx"), "not an ONNX model");
            WriteCatalog(malformed);
            using (var invalidModel = new BirdSpeciesRecognitionService(malformed))
            {
                var result = invalidModel.Recognize(red, full);
                Check(result.Match is null && result.Error is not null && !result.MissingModel,
                    "a corrupt native model returns a recoverable load error");
            }

            string path = Path.Combine(root, "two-birds.jpg");
            WriteJpeg(path, split);
            var photo = new PhotoItem { FilePath = path };
            var existing = new SpeciesMatch("既有记录", "Existing species", .42);
            photo.SetBirds(new[]
            {
                new DetectedBird { Number = 2, Box = Box(.05, .25, .25, .5), Species = existing, RecognitionStatus = "已有记录" },
                new DetectedBird { Number = 6, Box = Box(.70, .25, .25, .5) }
            }, selectedIndex: 1);
            var noBird = new PhotoItem { FilePath = path };
            int mutations = 0;
            photo.PropertyChanged += (_, _) => mutations++;
            foreach (var bird in photo.Birds) bird.PropertyChanged += (_, _) => mutations++;
            var updates = new List<SpeciesProgress>();
            using (var service = new BirdSpeciesRecognitionService(assets, (_, ct) => { ct.ThrowIfCancellationRequested(); return Vector(3, 4); }))
            {
                var batch = await service.RecognizeAsync(new[] { photo, noBird }, progress: new InlineProgress(updates.Add));
                Check(batch.Outcomes.Count == 2 && batch.Outcomes.Select(outcome => outcome.BirdIndex).SequenceEqual(new[] { 0, 1 }) &&
                    batch.Outcomes.All(outcome => outcome.PhotoId == photo.Id && outcome.Species?.ScientificName == "Testus beta"),
                    "batch recognition processes every stored bird and preserves zero-based photo/bird identity");
                Check(batch.SkippedPhotos == 1 && batch.Errors.Count == 0 && updates.Last().Completed == 2 && updates.Last().Total == 2,
                    "photos without detections are skipped and progress counts individual birds");
                Check(updates.Any(update => update.Message.Contains("鸟 6")),
                    "batch progress uses stable region numbers after earlier regions have been deleted");
                Check(mutations == 0 && ReferenceEquals(photo.Birds[0].Species, existing) && photo.SelectedBirdIndex == 1 && photo.Birds[1].Species is null,
                    "worker results never mutate UI-owned species, selection, or photo state");
                var missingPhoto = new PhotoItem { FilePath = Path.Combine(root, "absent.jpg"), Bird = full.Copy() };
                var failed = await service.RecognizeAsync(new[] { missingPhoto });
                Check(failed.Outcomes.Count == 1 && failed.Outcomes[0].Species is null && failed.Errors.Count == 1,
                    "an unreadable image reports its own failure without fabricating a species");
                using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                await Canceled(service.RecognizeAsync(new[] { photo }, cancellationToken: cancellation.Token),
                    "a canceled batch stops before decoding or applying any UI changes");
            }
            using (var cancellation = new CancellationTokenSource())
            using (var service = new BirdSpeciesRecognitionService(assets, (_, ct) =>
            { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Vector(3, 4); }))
            {
                await Canceled(service.RecognizeAsync(new[] { photo }, cancellationToken: cancellation.Token),
                    "cancellation during encoding propagates rather than returning a misleading recognition failure");
            }
            using (var service = new BirdSpeciesRecognitionService(assets, (_, _) => throw new InvalidDataException("fixture encoder failure")))
            {
                var failed = await service.RecognizeAsync(new[] { photo });
                Check(failed.Errors.Any(error => error.Contains("鸟 6")) && failed.Outcomes[1].BirdIndex == 1,
                    "per-bird failures report stable UI numbers while application outcomes retain collection indices");
            }
            var disposed = new BirdSpeciesRecognitionService(assets, (_, _) => Vector(3, 4)); disposed.Dispose();
            Reject<ObjectDisposedException>(() => disposed.Recognize(red, full), "disposed model services reject further work");

            string? nativeAssets = Environment.GetEnvironmentVariable("BIRD_SPECIES_ASSETS");
            string? nativeSample = Environment.GetEnvironmentVariable("BIRD_SAMPLE_PATH");
            if (!string.IsNullOrWhiteSpace(nativeAssets) && !string.IsNullOrWhiteSpace(nativeSample))
            {
                var report = new Dictionary<string, object?>
                {
                    ["RecordedAtUtc"] = DateTimeOffset.UtcNow,
                    ["ModelPath"] = Path.GetFullPath(Path.Combine(nativeAssets, "vision.onnx")),
                    ["ModelBytes"] = new FileInfo(Path.Combine(nativeAssets, "vision.onnx")).Length,
                    ["CropPolicy"] = SpeciesImagePreprocessor.CropPolicy,
                    ["MemoryBefore"] = Memory()
                };
                string? reference = Environment.GetEnvironmentVariable("BIRD_SPECIES_REFERENCE");
                if (!string.IsNullOrWhiteSpace(reference))
                {
                    var referenceInput = ReadFloat32(Path.Combine(reference, "validation-input.f32"), 3 * 224 * 224);
                    var expected = ReadFloat32(Path.Combine(reference, "validation-embedding.f32"), 768);
                    using var options = new SessionOptions
                    {
                        InterOpNumThreads = 1, IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
                    };
                    var timer = Stopwatch.StartNew();
                    using var session = new InferenceSession(Path.Combine(nativeAssets, "vision.onnx"), options);
                    double loadMilliseconds = timer.Elapsed.TotalMilliseconds; timer.Restart();
                    using var output = session.Run(new[] { NamedOnnxValue.CreateFromTensor(session.InputNames[0],
                        new DenseTensor<float>(referenceInput, new[] { 1, 3, 224, 224 })) });
                    double inferenceMilliseconds = timer.Elapsed.TotalMilliseconds;
                    var actual = output.First().AsTensor<float>().ToArray();
                    double dot = actual.Zip(expected).Sum(pair => (double)pair.First * pair.Second);
                    double cosine = dot / Math.Sqrt(actual.Sum(x => (double)x * x) * expected.Sum(x => (double)x * x));
                    double maximumError = actual.Zip(expected).Max(pair => Math.Abs(pair.First - pair.Second));
                    Check(actual.Length == 768 && cosine > .9999 && maximumError < .001,
                        "native ONNX Runtime image embeddings numerically match the official PyTorch BioCLIP 2 export reference");
                    Console.WriteLine($"  NATIVE BIOCLIP REFERENCE cosine={cosine:0.000000000} maxAbsoluteError={maximumError:G9}");
                    report["NumericalReference"] = new { Cosine = cosine, MaximumAbsoluteError = maximumError,
                        LoadMilliseconds = loadMilliseconds, InferenceMilliseconds = inferenceMilliseconds, Memory = Memory() };
                }
                // Sequential scopes ensure that giant reference and production sessions never coexist.
                var source = ImageLoader.Load(nativeSample);
                BirdBox crop = full.Copy();
                string? detectorPath = Environment.GetEnvironmentVariable("BIRD_MODEL_PATH");
                if (!string.IsNullOrWhiteSpace(detectorPath))
                {
                    using var detector = new BirdDetectionService(detectorPath);
                    var detection = detector.Detect(source);
                    Check(detection.Bird is { IsValid: true } && detection.Error is null,
                        "the real sample has a detected bird for native species crop validation");
                    crop = detection.Bird!.Copy();
                }
                using (var service = new BirdSpeciesRecognitionService(nativeAssets))
                {
                    var timer = Stopwatch.StartNew();
                    var whole = service.Recognize(source, full);
                    double firstMilliseconds = timer.Elapsed.TotalMilliseconds; timer.Restart();
                    var cropped = service.Recognize(source, crop);
                    double cropMilliseconds = timer.Elapsed.TotalMilliseconds;
                    Check(whole.Match is { IsValid: true, CandidateScope: "China" } && whole.Error is null &&
                        cropped.Match is { IsValid: true, CandidateScope: "China" } && cropped.Error is null,
                        "the real offline BioCLIP 2 model names both the whole photo and detected bird crop");
                    Check(cropped.Match!.Confidence is > .95 and <= 1 && cropped.Match.RunnerUpSimilarity < cropped.Match.Similarity &&
                        !string.IsNullOrWhiteSpace(cropped.Match.AlternativeName),
                        "a clearly separated real bird receives high competition confidence with inspectable runner-up evidence");
                    string? controlsDirectory = Environment.GetEnvironmentVariable("BIRD_SPECIES_CONTROLS");
                    if (!string.IsNullOrWhiteSpace(controlsDirectory))
                    {
                        var controls = new List<object>();
                        foreach (string controlName in new[] { "blur-32.png", "blur-80.png", "background.png", "noise.png", "gray.png" })
                        {
                            using var inputStream = File.OpenRead(Path.Combine(controlsDirectory, controlName));
                            var decoder = BitmapDecoder.Create(inputStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            var controlImage = decoder.Frames[0]; controlImage.Freeze();
                            var controlBox = controlName.StartsWith("blur-", StringComparison.Ordinal) ? crop : full;
                            var control = service.Recognize(controlImage, controlBox);
                            Check(control.Match is { HasValidConfidence: true } && control.Error is null &&
                                control.Match.Confidence < .5 && control.Match.Confidence < cropped.Match.Confidence - .25,
                                $"real-model {controlName} control has substantially lower confidence than the clear bird");
                            controls.Add(new { Fixture = controlName, Match = control.Match });
                            Console.WriteLine("  NATIVE CONFIDENCE " + controlName + " " + JsonSerializer.Serialize(control.Match));
                        }
                        report["ConfidenceControls"] = controls;
                    }
                    var nativePhoto = new PhotoItem { FilePath = nativeSample };
                    nativePhoto.SetBirds(new[] { new DetectedBird { Box = crop.Copy() }, new DetectedBird { Box = crop.Copy() } });
                    var secondPhoto = new PhotoItem { FilePath = nativeSample, Bird = crop.Copy() };
                    timer.Restart();
                    var native = await service.RecognizeAsync(new[] { nativePhoto, secondPhoto });
                    double batchMilliseconds = timer.Elapsed.TotalMilliseconds;
                    Check(native.Errors.Count == 0 && !native.MissingModel && native.Outcomes.Count == 3 &&
                        native.Outcomes.All(outcome => outcome.Species is { IsValid: true, CandidateScope: "China" }) &&
                        native.Outcomes.Count(outcome => outcome.PhotoId == nativePhoto.Id) == 2,
                        "the real model batch recognizes every stored bird across multiple photos");
                    report["ActualSample"] = new { Path = Path.GetFullPath(nativeSample), Crop = crop,
                        WholePhotoMatch = whole.Match, BirdCropMatch = cropped.Match,
                        FirstLoadAndWholePhotoMilliseconds = firstMilliseconds, WarmCropMilliseconds = cropMilliseconds,
                        BatchBirdCount = native.Outcomes.Count, BatchMilliseconds = batchMilliseconds,
                        MillisecondsPerBird = batchMilliseconds / native.Outcomes.Count, Memory = Memory() };
                    Console.WriteLine("  NATIVE SPECIES WHOLE " + JsonSerializer.Serialize(whole.Match));
                    Console.WriteLine("  NATIVE SPECIES CROP " + JsonSerializer.Serialize(cropped.Match));
                }
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                report["MemoryAfterDisposedAndGc"] = Memory();
                string? reportPath = Environment.GetEnvironmentVariable("BIRD_SPECIES_REPORT");
                if (!string.IsNullOrWhiteSpace(reportPath))
                    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SpeciesChecks")) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, recursive: true);
        }
        return assertions;
    }

    private static bool Near(double a, double b) => Math.Abs(a - b) < 1e-5;
    private static object Memory()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { PrivateBytes = process.PrivateMemorySize64, WorkingSetBytes = process.WorkingSet64,
            PeakWorkingSetBytes = process.PeakWorkingSet64, ManagedBytes = GC.GetTotalMemory(forceFullCollection: false) };
    }
    private static float[] ReadFloat32(string path, int count)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (reader.BaseStream.Length != count * sizeof(float)) throw new InvalidDataException("Invalid BioCLIP reference tensor length.");
        var values = new float[count];
        for (int index = 0; index < count; index++) values[index] = reader.ReadSingle();
        return values;
    }
    private static float[] Vector(float x, float y) { var result = new float[768]; result[0] = x; result[1] = y; return result; }
    private static BirdBox Box(double x, double y, double width, double height) => new()
    { X = x, Y = y, Width = width, Height = height, Confidence = .9 };
    private static BitmapSource Bitmap(int width, int height, Func<int, int, (int R, int G, int B)> pixel)
    {
        var pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            var color = pixel(x, y); int offset = (y * width + x) * 3;
            pixels[offset] = (byte)color.R; pixels[offset + 1] = (byte)color.G; pixels[offset + 2] = (byte)color.B;
        }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null, pixels, width * 3);
        bitmap.Freeze(); return bitmap;
    }
    private static void WriteJpeg(string path, BitmapSource bitmap)
    {
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void WriteCatalog(string directory, bool zeroRow = false, bool nonFiniteRow = false,
        bool duplicateNames = false, int dimension = 768)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "metadata.json"), JsonSerializer.Serialize(new
        { Model = "BioCLIP 2", Dimension = dimension, CandidateCount = 2, CandidateScope = "China" }));
        File.WriteAllText(Path.Combine(directory, "candidates.json"), JsonSerializer.Serialize(new[]
        {
            new { ChineseName = "测试甲", ScientificName = "Testus alpha" },
            new { ChineseName = "测试乙", ScientificName = duplicateNames ? "TESTUS ALPHA" : "Testus beta" }
        }));
        using var writer = new BinaryWriter(File.Create(Path.Combine(directory, "embeddings.f32")));
        for (int row = 0; row < 2; row++) for (int dimensionIndex = 0; dimensionIndex < 768; dimensionIndex++)
            writer.Write(row == 0 && dimensionIndex == 0 ? nonFiniteRow ? float.NaN : zeroRow ? 0f : 2f :
                row == 1 && dimensionIndex == 1 ? 2f : 0f);
    }
    private sealed class InlineProgress(Action<SpeciesProgress> report) : IProgress<SpeciesProgress>
    { public void Report(SpeciesProgress value) => report(value); }
}
