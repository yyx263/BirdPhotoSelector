namespace BirdPhotoSelector.Desktop.ViewModels;

/// <summary>导出协调只提交实际文件结果，不重写复制、冲突、分类或原件保护算法。</summary>
public sealed partial class DesktopController
{
    /// <summary>最近批次完整结果，取消后也保留已完成的真实成员状态，便于宿主报告。</summary>
    public FileOperationResult? LastFileResult { get; private set; }
    /// <summary>宿主确认前创建不可变原件计划；业务层校验路径、格式与鸟种目标。</summary>
    public PhotoFileOperationPlan PrepareExport(ExportOptions options) => ExportService.CreatePlan(State.AllPhotos.Where(options.Includes), options.Scope, !options.DeleteOnly && options.GroupBySpecies);
    /// <summary>确认完成后执行固定计划；冲突回调交给窗口，移除后仅应用实际成功的成员。</summary>
    public async Task<bool> ExecuteExportAsync(PhotoFileOperationPlan plan, ExportOptions options,
        Func<ExportConflict, CancellationToken, Task<ExportConflictDecision>>? resolve = null)
    {
        if (_disposed || State.IsBusy) return false;
        var allPhotos = State.AllPhotos.ToArray();
        var protectedSources = allPhotos.SelectMany(p => p.Assets).Select(a => a.CanonicalPath).Distinct(FilePaths.Comparer).ToArray();
        LastFileResult = null;
        return await RunAsync(async token =>
        {
            var progress = Progress<PhotoOperationProgress>(p => { State.Status = p.Message; State.Progress = p.Total > 0 ? p.Completed * 100d / p.Total : 0; });
            var result = options.DeleteOnly ? await _services.Export.DeleteAsync(plan, token, progress) :
                await _services.Export.ExportAsync(plan, options.Destination, options.RemoveOriginal, token, progress, resolve, protectedSources);
            // 取消可能发生在一个成功移除之后，必须先提交逐成员结果，不能先抛取消而丢失真实状态。
            LastFileResult = result;
            var empty = result.ApplyRemovedAssets(allPhotos).ToHashSet();
            if (result.RemovedFileCount > 0)
            {
                State.SpeciesEdits.Clear();
                var selected = State.CurrentPhoto?.Id;
                var groups = State.Groups.Select(g => g.Model).ToArray();
                foreach (var group in groups) foreach (var photo in group.Photos.Where(empty.Contains).ToArray()) group.Photos.Remove(photo);
                SetGroups(groups, selected); State.Dirty = true;
            }
            State.Status = $"{(token.IsCancellationRequested || result.CanceledFileCount > 0 ? "操作已取消" : "操作完成")} · 校验副本 {result.VerifiedCopyCount} · 移除原件 {result.RemovedFileCount} · 跳过 {result.SkippedFileCount} · 失败 {result.FailedFileCount}";
            Notice = $"范围：{options.ScopeLabel}{(options.GroupBySpecies && !options.DeleteOnly ? " · 按鸟种分类" : "")}\n处理：{plan.PhotoCount} 张照片 / {plan.FileCount} 个原件\n成功复制校验：{result.VerifiedCopyCount} 份副本\n已移除原文件：{result.RemovedFileCount} 个\n已无剩余原件：{empty.Count} 张照片\n跳过：{result.SkippedFileCount} · 失败：{result.FailedFileCount} · 取消：{result.CanceledFileCount}" +
                (result.Skipped.Count + result.Errors.Count > 0 ? "\n\n" + string.Join("\n", result.Skipped.Concat(result.Errors).Take(12)) : "");
        });
    }
}
