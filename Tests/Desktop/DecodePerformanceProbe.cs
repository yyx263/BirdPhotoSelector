using System.Diagnostics;
using System.Text.Json;
using BirdPhotoSelector.Desktop.Imaging;
using BirdPhotoSelector.Imaging.Portable;
using SkiaSharp;

/// <summary>在私有生成 JPEG 上分开测量解码与显示帧转换，记录实际分配量而非只测通知。</summary>
internal static class DecodePerformanceProbe
{
    internal static void Run(string root, string label)
    {
        string path = Path.Combine(root, "generated-24mp.jpg");
        using (var bitmap = new SKBitmap(6000, 4000))
        {
            using var canvas = new SKCanvas(bitmap); canvas.Clear(new SKColor(65, 105, 130));
            using var paint = new SKPaint { Color = SKColors.Gold }; canvas.DrawCircle(3000, 2000, 1400, paint);
            using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Jpeg, 95); using var output = File.Create(path); data.SaveTo(output);
        }
        var records = new List<object>();
        for (int i = 0; i < 4; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread(); var timer = Stopwatch.StartNew();
            var decoded = new PortablePhotoImageDecoder().Decode(path); double decode = timer.Elapsed.TotalMilliseconds;
            var display = AvaloniaImageFrame.Create(decoded); double total = timer.Elapsed.TotalMilliseconds;
            records.Add(new { DecodeMs = decode, DisplayMs = total - decode, TotalMs = total, ManagedBytes = GC.GetAllocatedBytesForCurrentThread() - before, FrameType = decoded.GetType().Name });
            GC.KeepAlive(display);
        }
        string json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(Directory.GetCurrentDirectory(), "artifacts/decode-" + label + ".json"), json); Console.WriteLine(json);
    }
}
