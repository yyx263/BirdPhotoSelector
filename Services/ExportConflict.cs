using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace BirdPhotoSelector;

public enum ExportConflictAction { Cancel, Overwrite, Rename }

public sealed record ExportConflict(string SourcePath, string TargetPath, long SourceLength, long TargetLength,
    bool CanOverwrite = true, string? Explanation = null);

public sealed record ExportConflictDecision(ExportConflictAction Action, bool ApplyToRemaining = false);

// Windows file identity also catches hard links and directory aliases. Path text
// alone cannot distinguish an existing backup from another name for an original.
internal readonly record struct ExportFileIdentity(uint Volume, uint High, uint Low)
{
    internal static ExportFileIdentity? TryRead(FileStream stream)
    {
        if (!GetFileInformationByHandle(stream.SafeFileHandle, out var info))
            return null;
        if (info.FileIndexHigh == 0 && info.FileIndexLow == 0) return null;
        return new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
