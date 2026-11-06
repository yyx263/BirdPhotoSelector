using BirdPhotoSelector.Imaging;
using System.IO;
using System.Globalization;
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

    /// <summary>通过共享串行门读取 LibRaw 元数据；拍摄时间保留相机本地文本，曝光补偿保留真实零值。</summary>
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
                Iso = Positive(info.Iso) ? $"{info.Iso:0}" : "—",
                ExposureBias = info.HasExposureBias != 0 && double.IsFinite(info.ExposureBias)
                    ? info.ExposureBias.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " EV" : "—"
            };
            return new(metadata, info.Width, info.Height, info.Orientation);
        }
        finally { DecoderGate.Release(); }
    }

    /// <summary>可取消的真实 RAW 解码。复制原生像素后冻结位图；无论成功、异常或取消都释放原生句柄。</summary>
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

    /// <summary>先检查取消、扩展名与文件存在性，原件不会被修改。</summary>
    private static string ValidatePath(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!PhotoImageFormats.IsRaw(path))
            throw new ArgumentException("请选择受支持扩展名的 RAW 文件。", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("RAW 原文件不存在。", fullPath);
        return fullPath;
    }

    /// <summary>将桥接库缺失、架构不符或入口版本错误转成可读的组件错误。</summary>
    private static InvalidOperationException MissingDecoder(Exception exception) =>
        new("RAW 解码组件未能加载，请重新构建或保留程序目录中的 BirdPhotoSelector.Raw.dll、libraw.dll 和相关运行库。", exception);

    /// <summary>把原生状态转换为取消或解码异常；不把不支持的 RAW 伪装为成功。</summary>
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

    /// <summary>由原生解码器轮询的取消回调，调用期间由托管代码保持存活。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CancelCallback();
    /// <summary>与 bpr_read_info_v2 严格对应的元数据 ABI，修改顺序或字段时必须同步原生桥接。</summary>
    [StructLayout(LayoutKind.Sequential)] private struct NativeInfo
    {
        public int Width, Height, Orientation;
        public double Iso, Shutter, Aperture, Focal;
        public long Timestamp;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Camera;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] CapturedAt;
        public double ExposureBias;
        public int HasExposureBias;
    }
    /// <summary>由原生桥接拥有的像素及释放句柄；只能由 bpr_release 释放。</summary>
    [StructLayout(LayoutKind.Sequential)] private struct NativeImage
    {
        public IntPtr Handle, Pixels;
        public int Width, Height, Stride, Bytes;
    }
    /// <summary>当前 Windows UTF-16 元数据入口；跨平台阶段将迁移到显式 UTF-8 C ABI。</summary>
    [DllImport(Library, EntryPoint = "bpr_read_info_v2", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NativeReadInfo(string path, CancelCallback cancel, out NativeInfo info);
    /// <summary>原生解码入口；输出句柄及像素始终由本桥接拥有，托管方只负责复制和最终释放。</summary>
    [DllImport(Library, EntryPoint = "bpr_decode", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NativeDecode(string path, int maxWidth, CancelCallback cancel, out NativeImage image);
    /// <summary>释放同一桥接分配的所有者，不能对像素指针调用其他分配器的释放函数。</summary>
    [DllImport(Library, EntryPoint = "bpr_release", CallingConvention = CallingConvention.Cdecl)] private static extern void NativeRelease(IntPtr handle);
    /// <summary>读取桥接静态错误说明；返回值不归托管方拥有。</summary>
    [DllImport(Library, EntryPoint = "bpr_error", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr NativeError(int status);
}
