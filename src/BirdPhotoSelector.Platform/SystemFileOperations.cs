using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualBasic.FileIO;

namespace BirdPhotoSelector;

/// <summary>回收站只有成功移入或明确不支持两种正常返回值，权限及 IO 错误必须抛出，不能降级为永久删除。</summary>
public enum TrashOutcome { Moved, Unsupported }
/// <summary>系统回收站接口，便于未来替换桌面 portal；业务规则不依赖具体系统 API。</summary>
public interface ITrashBackend
{
    /// <summary>尝试移入回收站，只有确实没有该能力才返回 Unsupported；其他失败抛出异常。</summary>
    TrashOutcome MoveToTrash(string path);
}
/// <summary>三平台原件文件能力；只有系统明确无回收站时才永久删除，其他失败均保留文件并上报。</summary>
public sealed class SystemFileOperations : IExportFilePlatform
{
    private readonly IFileIdentityProvider _identities;
    private readonly ITrashBackend _trash;
    /// <summary>根据实际宿主选择系统适配，不由领域或用例判断操作系统。</summary>
    public SystemFileOperations() : this(new NativeIdentities(), CurrentTrash()) { }
    /// <summary>显式提供系统实现，适配器可独立验证；生产代码不包含故障注入委托。</summary>
    public SystemFileOperations(IFileIdentityProvider identities, ITrashBackend trash)
    { _identities = identities ?? throw new ArgumentNullException(nameof(identities)); _trash = trash ?? throw new ArgumentNullException(nameof(trash)); }
    /// <summary>系统未知身份导致保守禁用覆盖，仍允许安全重命名导出。</summary>
    public FileIdentity? TryGetIdentity(FileStream stream) => _identities.TryGetIdentity(stream);
    /// <summary>回收站正常成功或明确不支持后才完成移除；权限、磁盘满和组件错误不能触发直接删除。</summary>
    public void RecycleOrDelete(string path)
    {
        string full = FilePaths.Canonicalize(path);
        if (!File.Exists(full)) throw new FileNotFoundException("待移除原件不存在。", full);
        if (_trash.MoveToTrash(full) == TrashOutcome.Unsupported) File.Delete(full);
    }
    /// <summary>只支持已实现的三个桌面系统，未知系统不静默采用永久删除。</summary>
    private static ITrashBackend CurrentTrash() => OperatingSystem.IsWindows() ? new WindowsTrash() :
        OperatingSystem.IsLinux() ? new LinuxTrash() : OperatingSystem.IsMacOS() ? new MacTrash() : throw new PlatformNotSupportedException();

    /// <summary>C ABI 避免直接把 Linux/macOS 依赖架构的 stat 布局硬编码到托管代码。</summary>
    private sealed class NativeIdentities : IFileIdentityProvider
    {
        /// <summary>在查询期间保活文件句柄；桥接不可用时返回未知身份，不能推断为新文件。</summary>
        public FileIdentity? TryGetIdentity(FileStream stream)
        {
            try
            {
                if (PlatformNative.Abi() != 1) return null;
                bool added = false;
                try
                {
                    stream.SafeFileHandle.DangerousAddRef(ref added);
                    return PlatformNative.Identity(stream.SafeFileHandle.DangerousGetHandle(), out var value) != 0 ? new(value.Device, value.Node) : null;
                }
                finally { if (added) stream.SafeFileHandle.DangerousRelease(); }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return null; }
        }
    }
    /// <summary>Windows 继续使用 shell 回收站，包括原有的用户取消和错误报告。</summary>
    private sealed class WindowsTrash : ITrashBackend
    {
        /// <summary>仅成功移入或明确不支持才正常返回；其他异常交给业务层报告并保留原件。</summary>
        public TrashOutcome MoveToTrash(string path)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
            return TrashOutcome.Moved;
        }
    }
    /// <summary>macOS 使用 Foundation 的当前卷回收站，缺少本程序桥接属于组件错误，不能当作没有回收站。</summary>
    private sealed class MacTrash : ITrashBackend
    {
        /// <summary>仅成功移入或明确不支持才正常返回；其他异常交给业务层报告并保留原件。</summary>
        public TrashOutcome MoveToTrash(string path)
        {
            if (PlatformNative.Abi() != 1) throw new IOException("系统文件桥接版本不匹配。");
            var error = new byte[4096]; int result = PlatformNative.Trash(path, error, error.Length);
            if (result == 1) return TrashOutcome.Moved;
            if (result == 2) return TrashOutcome.Unsupported;
            throw new IOException(Encoding.UTF8.GetString(error).TrimEnd('\0'));
        }
    }
    /// <summary>Ubuntu 桌面使用 GIO trash；仅库确实不存在或 G_IO_ERROR_NOT_SUPPORTED 才表示无回收站。</summary>
    private sealed class LinuxTrash : ITrashBackend
    {
        private static readonly Lazy<bool> Available = new(() => Load("libgio-2.0.so.0") && Load("libgobject-2.0.so.0") && Load("libglib-2.0.so.0"));
        /// <summary>仅成功移入或明确不支持才正常返回；其他异常交给业务层报告并保留原件。</summary>
        public TrashOutcome MoveToTrash(string path)
        {
            if (!Available.Value) return TrashOutcome.Unsupported;
            IntPtr file = Gio.File(path), error = IntPtr.Zero;
            if (file == IntPtr.Zero) throw new IOException("无法建立系统文件对象。");
            try
            {
                if (Gio.Trash(file, IntPtr.Zero, out error) != 0) return TrashOutcome.Moved;
                if (error == IntPtr.Zero) throw new IOException("回收站操作失败，原件已保留。");
                var value = Marshal.PtrToStructure<GioError>(error);
                if (value.Domain == Gio.ErrorDomain() && value.Code == 15) return TrashOutcome.Unsupported;
                throw new IOException(Marshal.PtrToStringUTF8(value.Message) ?? "回收站操作失败，原件已保留。");
            }
            finally { if (error != IntPtr.Zero) Gio.FreeError(error); Gio.Unref(file); }
        }
        /// <summary>只探测安装状态，立即释放探测句柄；真正调用由 P/Invoke 持有库生命周期。</summary>
        private static bool Load(string name)
        { if (!NativeLibrary.TryLoad(name, out var handle)) return false; NativeLibrary.Free(handle); return true; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct GioError { public uint Domain; public int Code; public IntPtr Message; }
    /// <summary>GIO 的稳定 C ABI；均只在 Linux 分支触发。</summary>
    private static class Gio
    {
        /// <summary>从 UTF-8 本机路径创建 GFile，调用方必须 unref。</summary>
        [DllImport("libgio-2.0.so.0", EntryPoint = "g_file_new_for_path")] internal static extern IntPtr File([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        /// <summary>同步请求系统回收站；失败时返回需 g_error_free 的错误。</summary>
        [DllImport("libgio-2.0.so.0", EntryPoint = "g_file_trash")] internal static extern int Trash(IntPtr file, IntPtr cancel, out IntPtr error);
        /// <summary>取得 GIO 错误域，防止将其他域的同值错误误认作不支持。</summary>
        [DllImport("libgio-2.0.so.0", EntryPoint = "g_io_error_quark")] internal static extern uint ErrorDomain();
        /// <summary>释放本次 GIO 调用产生的唯一错误对象。</summary>
        [DllImport("libglib-2.0.so.0", EntryPoint = "g_error_free")] internal static extern void FreeError(IntPtr error);
        /// <summary>释放借用期间持有的 GFile 引用。</summary>
        [DllImport("libgobject-2.0.so.0", EntryPoint = "g_object_unref")] internal static extern void Unref(IntPtr value);
    }
    /// <summary>文件身份桥接只链接系统库，不加载 LibRaw；元数据结构固定为两个 64 位整数。</summary>
    private static class PlatformNative
    {
        [StructLayout(LayoutKind.Sequential)] internal struct IdentityValue { internal ulong Device, Node; }
        /// <summary>查询无 LibRaw 依赖的系统桥接版本。</summary>
        [DllImport("BirdPhotoSelector.Platform.Native", EntryPoint = "bpp_get_abi_version", CallingConvention = CallingConvention.Cdecl)] internal static extern int Abi();
        /// <summary>借用打开的系统句柄取得设备和节点，零返回值表示未知。</summary>
        [DllImport("BirdPhotoSelector.Platform.Native", EntryPoint = "bpp_file_identity", CallingConvention = CallingConvention.Cdecl)] internal static extern int Identity(IntPtr handle, out IdentityValue identity);
        /// <summary>macOS 回收站接口，UTF-8 错误写入调用方提供的缓冲。</summary>
        [DllImport("BirdPhotoSelector.Platform.Native", EntryPoint = "bpp_trash_utf8", CallingConvention = CallingConvention.Cdecl)] internal static extern int Trash([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [Out] byte[] error, int capacity);
    }
}
