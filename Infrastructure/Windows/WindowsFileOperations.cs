using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.VisualBasic.FileIO;

namespace BirdPhotoSelector;

/// <summary>Windows 文件适配。文件身份来自已打开句柄，可发现硬链接；移除原件沿用系统回收站语义。</summary>
public sealed class WindowsFileOperations : IExportFilePlatform
{
    /// <summary>读取卷号和完整 64 位文件索引；系统无法提供身份时返回未知，不降低覆盖保护。</summary>
    public FileIdentity? TryGetIdentity(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info)) return null;
        if (info.FileIndexHigh == 0 && info.FileIndexLow == 0) return null;
        return new(info.VolumeSerialNumber, ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow);
    }
    /// <summary>只在调用方完成复制与校验后执行，保留 Windows shell 的错误和取消报告。</summary>
    public void RecycleOrDelete(string path) => FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs,
        RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);

    /// <summary>Win32 BY_HANDLE_FILE_INFORMATION 的固定布局，字段顺序与宽度必须与系统 ABI 一致。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    /// <summary>只读取调用方持有的句柄身份，不取得句柄所有权，也不以路径重新打开文件。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
