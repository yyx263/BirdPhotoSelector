using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

/// <summary>Metadata dimensions already account for LibRaw's camera orientation.</summary>
public sealed record RawImageInfo(PhotoMetadata Metadata, int PixelWidth, int PixelHeight, int Orientation);

/// <summary>True RAW pixel decoding through the pinned native bridge; never uses embedded JPEGs.</summary>
public static class RawImageDecoder
{
    // One native RAW operation across import, preview, AI and metadata requests.
    private static readonly SemaphoreSlim DecoderGate = new(1, 1);
    private const string Library = "BirdPhotoSelector.Raw.dll";

    public static RawImageInfo ReadInfo(string path, CancellationToken cancellationToken = default)
    {
        string fullPath = ValidatePath(path, cancellationToken);
        DecoderGate.Wait(cancellationToken);
        try
        {
            CancelCallback cancel = () => cancellationToken.IsCancellationRequested ? 1 : 0;
            int status;
            NativeInfo info;
            try { status = NativeReadInfo(fullPath, cancel, out info); }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            { throw MissingDecoder(ex); }
            GC.KeepAlive(cancel);
            CheckStatus(status, cancellationToken);
            string camera = Encoding.UTF8.GetString(info.Camera).TrimEnd('\0', ' ');
            // Native code reverses LibRaw's mktime in the same CRT timezone;
            // this is the EXIF wall-clock text, not a claimed UTC instant.
            string captured = Encoding.ASCII.GetString(info.CapturedAt).TrimEnd('\0');
            static bool Positive(double number) => double.IsFinite(number) && number > 0;
            var metadata = new PhotoMetadata
            {
                Camera = string.IsNullOrWhiteSpace(camera) ? "—" : camera,
                CapturedAt = string.IsNullOrWhiteSpace(captured) ? "—" : captured,
                FocalLength = Positive(info.Focal) ? $"{info.Focal:0.#} mm" : "—",
                Aperture = Positive(info.Aperture) ? $"f/{info.Aperture:0.#}" : "—",
                Shutter = Positive(info.Shutter) ? info.Shutter < 1 ? $"1/{Math.Round(1 / info.Shutter):0} s" : $"{info.Shutter:0.###} s" : "—",
                Iso = Positive(info.Iso) ? $"{info.Iso:0}" : "—"
            };
            return new(metadata, info.Width, info.Height, info.Orientation);
        }
        finally { DecoderGate.Release(); }
    }

    public static BitmapSource Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
    {
        if (maxWidth < 0) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        string fullPath = ValidatePath(path, cancellationToken);
        DecoderGate.Wait(cancellationToken);
        NativeImage image = default;
        try
        {
            CancelCallback cancel = () => cancellationToken.IsCancellationRequested ? 1 : 0;
            int status;
            try { status = NativeDecode(fullPath, maxWidth, cancel, out image); }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            { throw MissingDecoder(ex); }
            GC.KeepAlive(cancel);
            CheckStatus(status, cancellationToken);
            if (image.Handle == IntPtr.Zero || image.Pixels == IntPtr.Zero || image.Width <= 0 || image.Height <= 0 ||
                image.Stride != checked(image.Width * 3) || image.Bytes != checked(image.Stride * image.Height))
                throw new InvalidDataException("RAW 解码器返回的图像尺寸无效。");
            // BitmapSource.Create copies pixels; the native owner is always freed below.
            var result = BitmapSource.Create(image.Width, image.Height, 96, 96, PixelFormats.Rgb24,
                null, image.Pixels, image.Bytes, image.Stride);
            result.Freeze();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            if (image.Handle != IntPtr.Zero) NativeRelease(image.Handle);
            DecoderGate.Release();
        }
    }

    private static string ValidatePath(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!PhotoAsset.TryGetKind(path, out var kind) || kind != PhotoAssetKind.Raw)
            throw new ArgumentException("请选择受支持扩展名的 RAW 文件。", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("RAW 原文件不存在。", fullPath);
        return fullPath;
    }

    private static InvalidOperationException MissingDecoder(Exception exception) =>
        new("RAW 解码组件未能加载，请重新构建或保留程序目录中的 BirdPhotoSelector.Raw.dll、libraw.dll 和相关运行库。", exception);

    private static void CheckStatus(int status, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (status == 0) return;
        if (status == -100010) throw new OperationCanceledException("RAW 解码已取消。", token);
        string detail = Marshal.PtrToStringUTF8(NativeError(status)) ?? "Unknown error";
        string message = status switch
        {
            -2 => "当前 RAW 机型、格式或压缩模式不受解码器支持。",
            -100007 or -100012 => "RAW 图像过大或解码内存不足。",
            -100008 or -100009 => "RAW 文件损坏、数据不完整或无法读取。",
            _ => "RAW 解码失败。"
        };
        throw new InvalidDataException($"{message}（{status}: {detail}）");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CancelCallback();
    [StructLayout(LayoutKind.Sequential)] private struct NativeInfo
    {
        public int Width, Height, Orientation;
        public double Iso, Shutter, Aperture, Focal;
        public long Timestamp;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Camera;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] CapturedAt;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeImage
    {
        public IntPtr Handle, Pixels;
        public int Width, Height, Stride, Bytes;
    }
    [DllImport(Library, EntryPoint = "bpr_read_info", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NativeReadInfo(string path, CancelCallback cancel, out NativeInfo info);
    [DllImport(Library, EntryPoint = "bpr_decode", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NativeDecode(string path, int maxWidth, CancelCallback cancel, out NativeImage image);
    [DllImport(Library, EntryPoint = "bpr_release", CallingConvention = CallingConvention.Cdecl)] private static extern void NativeRelease(IntPtr handle);
    [DllImport(Library, EntryPoint = "bpr_error", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr NativeError(int status);
}
