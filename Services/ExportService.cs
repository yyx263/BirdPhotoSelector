using System.IO;
using System.Security.Cryptography;
using Microsoft.VisualBasic.FileIO;

namespace BirdPhotoSelector;

public sealed class FileOperationResult
{
    public List<PhotoItem> Succeeded { get; } = new();
    public List<PhotoItem> SourceRemoved { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Skipped { get; } = new();
}

public sealed class ExportService
{
    /// <summary>Copies into an existing folder, verifies SHA-256, then optionally recycles each source.</summary>
    public Task<FileOperationResult> ExportAsync(IEnumerable<PhotoItem> photos, string existingDestination,
        bool removeOriginal, CancellationToken cancellationToken = default)
    {
        var items = photos.ToArray();
        string destination = Path.GetFullPath(existingDestination);
        if (!Directory.Exists(destination)) throw new DirectoryNotFoundException("请选择已经存在的导出目录，程序不会自动新建文件夹。");
        return Task.Run(async () =>
        {
            var result = new FileOperationResult();
            foreach (var photo in items)
            {
                if (cancellationToken.IsCancellationRequested) break;
                string? target = null;
                bool ownsTarget = false, verified = false;
                try
                {
                    target = Path.Combine(destination, photo.FileName);
                    if (Path.GetFullPath(photo.FilePath).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    { result.Skipped.Add($"{photo.FileName}：源文件和目标文件相同。"); continue; }
                    if (File.Exists(target)) { result.Skipped.Add($"{photo.FileName}：目标目录已存在同名文件。"); continue; }
                    await using (var source = new FileStream(photo.FilePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite,
                        FileShare.None, 131072, FileOptions.Asynchronous | FileOptions.SequentialScan))
                    {
                        ownsTarget = true;
                        await source.CopyToAsync(output, cancellationToken);
                        await output.FlushAsync(cancellationToken);
                        output.Flush(flushToDisk: true);
                        if (source.Length != output.Length) throw new IOException("复制后的文件大小校验失败，源文件已保留。");
                        source.Position = 0;
                        output.Position = 0;
                        byte[] sourceHash = await SHA256.HashDataAsync(source, cancellationToken);
                        byte[] outputHash = await SHA256.HashDataAsync(output, cancellationToken);
                        if (!CryptographicOperations.FixedTimeEquals(sourceHash, outputHash))
                            throw new IOException("复制后的文件内容校验失败，源文件已保留。");
                        verified = true;
                    }
                    result.Succeeded.Add(photo);
                    if (removeOriginal && !cancellationToken.IsCancellationRequested)
                    {
                        try { RemoveSource(photo.FilePath); result.SourceRemoved.Add(photo); }
                        catch (Exception ex) { result.Errors.Add($"{photo.FileName}：已导出，但原文件未删除：{ex.Message}"); }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                { break; }
                catch (Exception ex)
                { result.Errors.Add($"{photo.FileName}：{ex.Message}"); }
                finally
                {
                    if (ownsTarget && !verified && target is not null)
                    {
                        try { File.Delete(target); }
                        catch (Exception ex) { result.Errors.Add($"{photo.FileName}：未完成的目标文件无法清理：{ex.Message}"); }
                    }
                }
            }
            return result;
        }, CancellationToken.None);
    }

    /// <summary>The caller confirms the selected files before invoking this destructive operation.</summary>
    public Task<FileOperationResult> DeleteAsync(IEnumerable<PhotoItem> photos, CancellationToken cancellationToken = default)
    {
        var items = photos.ToArray();
        return Task.Run(() =>
        {
            var result = new FileOperationResult();
            foreach (var photo in items)
            {
                if (cancellationToken.IsCancellationRequested) break;
                try
                {
                    if (!File.Exists(photo.FilePath)) { result.Skipped.Add($"{photo.FileName}：文件已经不存在。"); continue; }
                    RemoveSource(photo.FilePath);
                    result.Succeeded.Add(photo);
                    result.SourceRemoved.Add(photo);
                }
                catch (Exception ex) { result.Errors.Add($"{photo.FileName}：{ex.Message}"); }
            }
            return result;
        }, CancellationToken.None);
    }

    private static void RemoveSource(string path)
    {
        // The Windows shell implements the recycle-bin fallback for volumes without one.
        // Do not catch recycle failures and retry a permanent delete: permission/I/O errors must preserve the source.
        FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin,
            UICancelOption.ThrowException);
    }
}
