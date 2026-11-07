using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BirdPhotoSelector.Imaging;
using Microsoft.Win32.SafeHandles;

namespace BirdPhotoSelector.Imaging.Native;

/// <summary>通过 ABI 3 的 UTF-8 C 接口解码真实 RAW，所有平台共用一个串行门、取消和错误语义。</summary>
public static class NativeRawDecoder
{
    private const string Library = "BirdPhotoSelector.Raw";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    /// <summary>读取已应用相机方向的完整尺寸与拍摄参数，不展开传感器像素。</summary>
    public static PhotoImageInfo ReadInfo(string path, CancellationToken cancellationToken = default)
    {
        string fullPath = Validate(path, cancellationToken); Gate.Wait(cancellationToken);
        try
        {
            CheckAbi(); Cancel cancel = () => cancellationToken.IsCancellationRequested ? 1 : 0;
            int status = ReadNative(fullPath, cancel, out var info); GC.KeepAlive(cancel); Check(status, cancellationToken);
            string camera = Encoding.UTF8.GetString(info.Camera).TrimEnd('\0', ' '), captured = Encoding.ASCII.GetString(info.CapturedAt).TrimEnd('\0');
            static bool Positive(double x) => double.IsFinite(x) && x > 0;
            var metadata = new PhotoMetadata
            {
                Camera = string.IsNullOrWhiteSpace(camera) ? "—" : camera, CapturedAt = string.IsNullOrWhiteSpace(captured) ? "—" : captured,
                FocalLength = Positive(info.Focal) ? FormattableString.Invariant($"{info.Focal:0.#} mm") : "—",
                Aperture = Positive(info.Aperture) ? FormattableString.Invariant($"f/{info.Aperture:0.#}") : "—",
                Shutter = Positive(info.Shutter) ? info.Shutter < 1 ? FormattableString.Invariant($"1/{Math.Round(1 / info.Shutter):0} s") : FormattableString.Invariant($"{info.Shutter:0.###} s") : "—",
                Iso = Positive(info.Iso) ? FormattableString.Invariant($"{info.Iso:0}") : "—",
                ExposureBias = info.HasExposureBias != 0 && double.IsFinite(info.ExposureBias) ? info.ExposureBias.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + " EV" : "—"
            };
            return new(metadata, info.Width, info.Height, info.Orientation);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException) { throw Missing(ex); }
        finally { Gate.Release(); }
    }
    /// <summary>解码到可移植的不可变 RGB 帧，复制一次后立即释放原生像素，不持有输入文件。</summary>
    public static ImageFrame Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
    {
        using var buffer = DecodeBuffer(path, maxWidth, cancellationToken);
        var pixels = new byte[buffer.Bytes]; Marshal.Copy(buffer.Pixels, pixels, 0, pixels.Length);
        cancellationToken.ThrowIfCancellationRequested();
        return new Rgb24ImageFrame(buffer.Width, buffer.Height, pixels);
    }
    /// <summary>低层只读像素租约，仅供显示适配直接复制；调用方必须 Dispose，禁止缓存此临时租约。</summary>
    public static RawPixelBuffer DecodeBuffer(string path, int maxWidth = 0, CancellationToken cancellationToken = default)
    {
        if (maxWidth < 0) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        string fullPath = Validate(path, cancellationToken); Gate.Wait(cancellationToken); NativeImage image = default;
        try
        {
            CheckAbi(); Cancel cancel = () => cancellationToken.IsCancellationRequested ? 1 : 0;
            int status = DecodeNative(fullPath, maxWidth, cancel, out image); GC.KeepAlive(cancel); Check(status, cancellationToken);
            if (image.Handle == IntPtr.Zero || image.Pixels == IntPtr.Zero || image.Width <= 0 || image.Height <= 0 ||
                image.Stride != checked(image.Width * 3) || image.Bytes != checked(image.Stride * image.Height)) throw new InvalidDataException("RAW 解码器返回的图像尺寸无效。");
            var result = new RawPixelBuffer(image.Handle, image.Pixels, image.Width, image.Height, image.Stride, image.Bytes);
            image.Handle = IntPtr.Zero; return result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException) { throw Missing(ex); }
        finally { if (image.Handle != IntPtr.Zero) ReleaseNative(image.Handle); Gate.Release(); }
    }
    /// <summary>检查固定布局及 ABI，拒绝加载旧版宽字符桥接，避免结构体误读。</summary>
    private static void CheckAbi()
    {
        if (Marshal.SizeOf<NativeInfo>() != 232 || AbiNative() != 3) throw new InvalidDataException("RAW 桥接版本不匹配，需要 ABI 3。");
    }
    /// <summary>取消优先于原生错误；缺失或损坏的 RAW 不得冒充正常零检出。</summary>
    private static void Check(int status, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (status == 0) return;
        if (status == -100010) throw new OperationCanceledException("RAW 解码已取消。", token);
        string detail = Marshal.PtrToStringUTF8(ErrorNative(status)) ?? "Unknown error";
        string message = status switch { -2 => "当前 RAW 机型、格式或压缩模式不受解码器支持。", -100007 or -100012 => "RAW 图像过大或解码内存不足。", -100008 or -100009 => "RAW 文件损坏、数据不完整或无法读取。", _ => "RAW 解码失败。" };
        throw new InvalidDataException($"{message}（{status}: {detail}）");
    }
    /// <summary>固定真实原件路径，不修改或重命名输入文件。</summary>
    private static string Validate(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!PhotoImageFormats.IsRaw(path)) throw new ArgumentException("请选择受支持扩展名的 RAW 文件。", nameof(path));
        string full = Path.GetFullPath(path); if (!File.Exists(full)) throw new FileNotFoundException("RAW 原文件不存在。", full); return full;
    }
    /// <summary>把组件缺失或架构不符报告为安装错误，不能降级到内嵌 JPEG。</summary>
    private static InvalidOperationException Missing(Exception ex) => new("RAW 解码组件未能加载，请保留当前系统对应的 BirdPhotoSelector.Raw 原生库、LibRaw 和相关运行库。", ex);
    /// <summary>释放唯一原生所有者，供安全句柄最终释放使用。</summary>
    internal static void Release(IntPtr handle) => ReleaseNative(handle);
    /// <summary>原生处理定期查询的取消回调；调用期间必须保活委托。</summary>
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Cancel();
    /// <summary>ABI 3 拍摄信息布局，必须与 RawInfoV2 的 232 字节和字段顺序一致。</summary>
    [StructLayout(LayoutKind.Sequential)] private struct NativeInfo
    {
        public int Width, Height, Orientation; public double Iso, Shutter, Aperture, Focal; public long Timestamp;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Camera;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] CapturedAt;
        public double ExposureBias; public int HasExposureBias;
    }
    /// <summary>唯一原生所有者和只读紧密 RGB24 视图，字节数由托管端再次校验。</summary>
    [StructLayout(LayoutKind.Sequential)] private struct NativeImage { public IntPtr Handle, Pixels; public int Width, Height, Stride, Bytes; }
    /// <summary>查询已加载桥接 ABI，拒绝误用旧版宽字符接口。</summary>
    [DllImport(Library, EntryPoint = "bpr_get_abi_version", CallingConvention = CallingConvention.Cdecl)] private static extern int AbiNative();
    /// <summary>只读 UTF-8 路径元数据，成功返回固定结构，不移交像素所有权。</summary>
    [DllImport(Library, EntryPoint = "bpr_read_info_utf8", CallingConvention = CallingConvention.Cdecl)] private static extern int ReadNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, Cancel cancel, out NativeInfo info);
    /// <summary>解包并处理真实 RAW；成功时将一个必须释放的所有者移交托管端。</summary>
    [DllImport(Library, EntryPoint = "bpr_decode_utf8", CallingConvention = CallingConvention.Cdecl)] private static extern int DecodeNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int maxWidth, Cancel cancel, out NativeImage image);
    /// <summary>通过原分配库释放像素所有者，禁止以其他分配器释放。</summary>
    [DllImport(Library, EntryPoint = "bpr_release", CallingConvention = CallingConvention.Cdecl)] private static extern void ReleaseNative(IntPtr handle);
    /// <summary>借用原生静态 UTF-8 错误文本，调用方不能释放该指针。</summary>
    [DllImport(Library, EntryPoint = "bpr_error", CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr ErrorNative(int status);
}

/// <summary>原生解码输出的独占短期租约；Dispose 或最终释放都会调用同一分配库的释放函数。
/// 仅限一个调用方在 using 范围读取，不允许在另一线程读取期间并发 Dispose。</summary>
public sealed class RawPixelBuffer : IDisposable
{
    private readonly Owner _owner;
    private readonly IntPtr _pixels;
    /// <summary>只读 RGB24 指针，仅在租约未释放期间有效；释放后读取会明确报错。</summary>
    public IntPtr Pixels { get { ObjectDisposedException.ThrowIf(_owner.IsClosed, this); return _pixels; } }
    /// <summary>显示宽度。</summary>
    public int Width { get; }
    /// <summary>显示高度。</summary>
    public int Height { get; }
    /// <summary>每行字节数。</summary>
    public int Stride { get; }
    /// <summary>总字节数。</summary>
    public int Bytes { get; }
    /// <summary>接管已验证的原生分配，构造失败前仍由调用方负责释放。</summary>
    internal RawPixelBuffer(IntPtr owner, IntPtr pixels, int width, int height, int stride, int bytes)
    { _owner = new Owner(owner); _pixels = pixels; Width = width; Height = height; Stride = stride; Bytes = bytes; }
    /// <summary>只释放自身拥有的原生句柄，不释放解码器全局组件。</summary>
    public void Dispose() => _owner.Dispose();
    /// <summary>确保异常路径或遗忘 Dispose 时也能释放像素所有者。</summary>
    private sealed class Owner : SafeHandleZeroOrMinusOneIsInvalid
    {
        /// <summary>接管解码器独占句柄，确保异常退出也能最终释放。</summary>
        internal Owner(IntPtr value) : base(true) => SetHandle(value);
        /// <summary>最终释放不访问 UI，也不依赖外部取消令牌。</summary>
        protected override bool ReleaseHandle() { NativeRawDecoder.Release(handle); return true; }
    }
}
