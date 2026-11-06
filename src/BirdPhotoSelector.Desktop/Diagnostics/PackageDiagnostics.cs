using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using BirdPhotoSelector.Desktop.Imaging;
using BirdPhotoSelector.Desktop.Views;
using BirdPhotoSelector.Imaging.Portable;
using SkiaSharp;

namespace BirdPhotoSelector.Desktop.Diagnostics;

/// <summary>发布目录自检；只生成私有临时图，不写用户照片或工程，结果为可复制的 JSON。</summary>
internal static class PackageDiagnostics
{
    private static readonly string Parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.PackageCheck"));
    /// <summary>无桌面会话也可检测运行时、图像、两个原生 ABI、文件身份和完整模型推理。</summary>
    internal static int Run(string report)
    {
        var results = new List<object>(); bool success = true;
        void Check(string name, Action work)
        {
            try { work(); results.Add(new { Name = name, Passed = true, Error = (string?)null }); }
            catch (Exception ex) { success = false; results.Add(new { Name = name, Passed = false, Error = ex.ToString() }); }
        }
        string temporary = CreateTemporary();
        try
        {
            string image = Path.Combine(temporary, "sample.jpg");
            Check("Generate JPEG fixture", () => WriteSample(temporary));
            Check("Portable JPEG + EXIF", () =>
            {
                var decoder = new PortablePhotoImageDecoder(); var info = decoder.ReadInfo(image); var pixels = decoder.Decode(image);
                if (info.PixelWidth != 128 || pixels.CopyRgb24().Length != 128 * 96 * 3) throw new InvalidDataException("Unexpected JPEG pixels.");
            });
            Check("RAW ABI 3", () => Abi("BirdPhotoSelector.Raw", "bpr_get_abi_version", 3));
            Check("Platform ABI 1 + file identity", () =>
            {
                Abi("BirdPhotoSelector.Platform.Native", "bpp_get_abi_version", 1);
                using var stream = File.OpenRead(image);
                if (new SystemFileOperations().TryGetIdentity(stream) is null) throw new IOException("File identity bridge unavailable.");
            });
            Check("YOLO inference", () =>
            {
                using var detector = new BirdDetectionService(); var result = detector.Detect(new PortablePhotoImageDecoder().Decode(image));
                if (result.MissingModel || !string.IsNullOrWhiteSpace(result.Error)) throw new InvalidDataException(result.Error ?? result.Status);
            });
            Check("BioCLIP China inference", () =>
            {
                using var recognizer = new BirdSpeciesRecognitionService();
                var result = recognizer.Recognize(new PortablePhotoImageDecoder().Decode(image), new BirdBox { X = .1, Y = .1, Width = .8, Height = .8 }, true);
                if (result.Match is not { IsValid: true } || result.MissingModel || !string.IsNullOrWhiteSpace(result.Error)) throw new InvalidDataException(result.Error ?? result.Status);
            });
            WriteReport(report, success, results); return success ? 0 : 1;
        }
        finally { Cleanup(temporary); }
    }
    /// <summary>真实窗口最小化启动、导入、显示生成图后关闭，验证打包的 GUI 原生依赖和关闭流程。</summary>
    internal static void AttachWindow(MainWindow window, string report)
    {
        window.WindowState = WindowState.Minimized;
        window.Opened += async (_, _) =>
        {
            string temporary = CreateTemporary(); bool success = false; string? error = null;
            try
            {
                if (!await window.Controller.ImportAsync([WriteSample(temporary)])) throw new InvalidOperationException("Import canceled.");
                await window.Controller.PreviewTask;
                if (!window.Controller.State.IsPreviewReady || window.Controller.Preview is not AvaloniaImageFrame) throw new InvalidOperationException(window.Controller.State.Status);
                await Task.Delay(300); success = true;
            }
            catch (Exception ex) { error = ex.ToString(); }
            finally
            {
                WriteReport(report, success, new object[] { new { Name = "Native desktop import and preview", Passed = success, Error = error } });
                window.Controller.State.Dirty = false; window.Close(); Cleanup(temporary);
            }
        };
    }
    /// <summary>取得明确的应用目录库，不从任意工作目录加载；查询后及时释放探测句柄。</summary>
    private static void Abi(string library, string symbol, int expected)
    {
        string name = OperatingSystem.IsWindows() ? library + ".dll" : "lib" + library + (OperatingSystem.IsMacOS() ? ".dylib" : ".so");
        var handle = NativeLibrary.Load(Path.Combine(AppContext.BaseDirectory, name));
        try
        {
            var query = Marshal.GetDelegateForFunctionPointer<VersionQuery>(NativeLibrary.GetExport(handle, symbol));
            if (query() != expected) throw new InvalidDataException("Native ABI does not match application.");
        }
        finally { NativeLibrary.Free(handle); }
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VersionQuery();
    /// <summary>报告只写调用者明确指定的文件；异常时保留详细诊断供目标平台反馈。</summary>
    private static void WriteReport(string report, bool passed, IEnumerable<object> checks)
    {
        var data = new { Version = typeof(PackageDiagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            OS = RuntimeInformation.OSDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Passed = passed, Checks = checks };
        string path = Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
    }
    /// <summary>验证图是程序生成的梯度图，不携带用户信息或第三方照片。</summary>
    private static string WriteSample(string directory)
    {
        string path = Path.Combine(directory, "sample.jpg"); using var bitmap = new SKBitmap(128, 96);
        using (var canvas = new SKCanvas(bitmap)) { canvas.Clear(new SKColor(50, 110, 65)); using var paint = new SKPaint { Color = new SKColor(220, 190, 50) }; canvas.DrawCircle(64, 48, 30, paint); }
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90); using var stream = File.Create(path); encoded.SaveTo(stream); return path;
    }
    /// <summary>每次自检只分配固定父目录下的随机子目录，便于安全清理。</summary>
    private static string CreateTemporary() { string path = Path.Combine(Parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    /// <summary>只有本次随机临时子目录允许递归清理，拒绝越界或非随机名称。</summary>
    private static void Cleanup(string directory)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(directory)) != Parent || !Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) throw new InvalidOperationException("Unsafe diagnostic cleanup path.");
        Directory.Delete(directory, true);
    }
}
