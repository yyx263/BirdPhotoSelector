using System.IO;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;
using BirdPhotoSelector.Imaging;
using BirdPhotoSelector.Imaging.Windows;
using Microsoft.ML.OnnxRuntime.Tensors;

/// <summary>验证现有宿主与新图像接口的衔接，既检查 WIC 行为，也用纯托管帧走真实业务流程。</summary>
internal static class ImagingIntegrationChecks
{
    /// <summary>只使用本次生成的临时文件和小图像，不读取或改写用户照片，不加载模型。</summary>
    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); assertions++; Console.WriteLine("  OK IMAGE BRIDGE " + message); }
        var bitmap = BitmapSource.Create(17, 11, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Range(0, 17 * 11 * 3).Select(i => (byte)(i * 17 % 256)).ToArray(), 17 * 3);
        Check(!bitmap.IsFrozen, "fixture starts as caller-owned mutable Windows bitmap");
        var frame = WpfImageBridge.FromBitmap(bitmap);
        Check(!bitmap.IsFrozen && frame.Bitmap.IsFrozen, "wrapping freezes a private clone without changing caller ownership");
        Check(ReferenceEquals(frame, WpfImageBridge.FromBitmap(frame.Bitmap)) && ReferenceEquals(frame.Bitmap, WpfImageBridge.ToBitmap(frame)),
            "Windows frame and display bridge share the exact frozen pixel owner");
        var managed = new Rgb24ImageFrame(frame.PixelWidth, frame.PixelHeight, frame.CopyRgb24());
        Check(managed.CopyRgb24().SequenceEqual(frame.CopyRgb24()), "managed and Windows frames expose identical RGB bytes");
        Check(WpfImageBridge.ToBitmap(managed).IsFrozen && ReferenceEquals(WpfImageBridge.ToBitmap(managed), WpfImageBridge.ToBitmap(managed)),
            "non-Windows frames can display through a weakly cached frozen image");
        var crop = new BirdBox { X = .75, Y = .02, Width = .25, Height = .5, Confidence = .9 };
        var windowsTensor = SpeciesImagePreprocessor.CreateInput(frame, crop);
        var managedTensor = SpeciesImagePreprocessor.CreateInput(managed, crop);
        Check(windowsTensor.ToArray().SequenceEqual(managedTensor.ToArray()),
            "BioCLIP crop, edge padding, bicubic bytes and normalized tensor are identical across frame implementations");
        var red = new Rgb24ImageFrame(8, 4, Enumerable.Repeat(new byte[] { 255, 0, 0 }, 32).SelectMany(p => p).ToArray());
        var redWindows = WpfImageBridge.FromBitmap(WpfImageBridge.ToBitmap(red));
        Check(BackgroundSimilarity.Compare(BackgroundSimilarity.Describe(red), BackgroundSimilarity.Describe(redWindows)) == 1,
            "background features preserve red/blue channels across the new image boundary");
        using (var detector = new BirdDetectionService())
        {
            var method = typeof(BirdDetectionService).GetMethod("CreateInput", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var input = ((DenseTensor<float> Input, int Width, int Height, int PadX, int PadY))method.Invoke(detector, new object[] { red, CancellationToken.None })!;
            Check(input.Input[0, 0, 320, 320] == 1 && input.Input[0, 1, 320, 320] == 0 && input.Input[0, 2, 320, 320] == 0,
                "detector receives RGB NCHW instead of accidentally reversing the new RGB contract");
            Check(input.PadY == 160 && input.PadX == 0 && input.Input[0, 2, 0, 0] == 114f / 255,
                "detector letterbox dimensions and padding remain unchanged");
        }
        string tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.ImageBridge"));
        string root = Path.Combine(tempRoot, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "source.jpg");
            byte[] sourceBytes = { 1, 3, 5, 7 }; await File.WriteAllBytesAsync(path, sourceBytes);
            var decoder = new ManagedDecoder(red);
            var imported = await new PhotoImportService(decoder).ImportAsync(new[] { path }, Array.Empty<PhotoItem>());
            var photo = imported.Added.Single();
            Check(imported.Errors.Count == 0 && ReferenceEquals(photo.Thumbnail, red) && photo.Metadata.Camera == "Managed test source",
                "import delegates header and thumbnail loading to an injected non-WPF decoder");
            Check(decoder.Widths.SequenceEqual(new[] { 160 }) && photo.PixelWidth == 8 && photo.PixelHeight == 4,
                "import requests a small thumbnail while preserving complete displayed dimensions");
            using (var grouping = new AnalysisService(images: decoder))
            {
                var grouped = await grouping.AnalyzeAsync(new[] { photo }, .5);
                Check(grouped.Errors.Count == 0 && grouped.Groups.Single().Single() == photo && decoder.Widths.Last() == 1280,
                    "background grouping consumes the injected image backend without touching WIC");
            }
            int detected = 0, recognized = 0;
            BirdDetectionResult Detect(ImageFrame image, CancellationToken token)
            { Check(ReferenceEquals(image, red), "localization receives the exact managed decoded frame"); detected++; return new(crop, "located"); }
            SpeciesRecognitionResult Recognize(ImageFrame image, BirdBox box, bool chinaOnly, CancellationToken token)
            { Check(ReferenceEquals(image, red) && chinaOnly, "species recognition shares the same frame and China candidate scope"); recognized++; return new(new("大山雀", "Parus major", .7, Confidence: .9), "recognized"); }
            using (var workflow = new BirdRecognitionWorkflow(detectorOverride: Detect, recognizerOverride: Recognize, images: decoder))
            {
                var result = await workflow.RecognizeAsync(new[] { photo });
                Check(result.Errors.Count == 0 && result.Outcomes.Single().MatchedBirds == 1 && detected == 1 && recognized == 1,
                    "complete batch workflow accepts a non-WPF image implementation");
                Check(photo.Birds.Count == 0, "background recognition remains detached until UI application");
            }
            using (var single = new SinglePhotoRecognitionService(detectorOverride: Detect, recognizerOverride: Recognize, images: decoder))
            {
                var result = await single.RebuildAsync(photo);
                Check(result.Success && result.Birds.Count == 1 && detected == 2 && recognized == 2,
                    "single-photo rebuilding uses the injected decoder rather than the Windows default");
            }
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(sourceBytes), "all image-layer integration checks preserve source file bytes");
            bool rejected = false;
            try { _ = new WindowsPhotoImageDecoder().Decode(path); } catch (Exception ex) when (ex is InvalidDataException or FileFormatException or NotSupportedException) { rejected = true; }
            Check(rejected, "production Windows decoder still rejects invalid JPEG bytes instead of trusting extension alone");
        }
        finally
        {
            if (Path.GetDirectoryName(Path.GetFullPath(root)) != tempRoot || !Guid.TryParseExact(Path.GetFileName(root), "N", out _))
                throw new InvalidOperationException("Refusing to clean outside the generated integration root.");
            Directory.Delete(root, recursive: true);
        }
        return assertions;
    }

    /// <summary>不依赖 WPF 的小型后端，验证业务层确实尊重注入的图像契约。</summary>
    private sealed class ManagedDecoder(ImageFrame image) : IPhotoImageDecoder
    {
        public List<int> Widths { get; } = new();
        /// <summary>记录请求尺寸并返回共享的不可变帧。</summary>
        public ImageFrame Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Widths.Add(maxWidth); return image; }
        /// <summary>返回与显示帧一致的元数据和尺寸。</summary>
        public PhotoImageInfo ReadInfo(string path, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return new(new PhotoMetadata { Camera = "Managed test source" }, image.PixelWidth, image.PixelHeight, 1); }
    }
}
