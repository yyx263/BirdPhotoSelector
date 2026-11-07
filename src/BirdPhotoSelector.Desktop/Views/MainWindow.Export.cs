using Avalonia.Interactivity;
using Avalonia.Threading;
using BirdPhotoSelector.Desktop.Dialogs;
using BirdPhotoSelector.Desktop.ViewModels;

namespace BirdPhotoSelector.Desktop.Views;

/// <summary>导出前的用户决定和冲突提示；所有文件校验、复制、分类和移除均由共享用例执行。</summary>
public sealed partial class MainWindow
{
    /// <summary>先收集选项，再依次检查未标记、分类结果与原件移除确认；取消不会开始文件操作。</summary>
    private async void OpenExport(object? sender, RoutedEventArgs e) => await GuardUi(async () =>
    {
        if (Controller.State.IsBusy || !Controller.State.HasPhotos) { Controller.State.Status = "请先添加照片"; return; }
        var dialog = new ExportDialog(Controller.State.AllPhotos.ToArray());
        var options = await dialog.ShowDialog<ExportOptions?>(this);
        if (_closed || options is null) return;
        if (Controller.State.UnmarkedCount > 0)
        {
            var answer = await PromptDialog.AskAsync(this, "还有未标记照片", $"列表中还有 {Controller.State.UnmarkedCount} 张照片未标记，是否继续{(options.DeleteOnly ? "清理" : "导出")}？返回后会自动定位未标记照片。", ("返回筛选", PromptResult.Cancel), ("继续", PromptResult.Continue));
            if (answer != PromptResult.Continue) { Controller.State.LocateFirstUnmarked(); return; }
        }
        var plan = Controller.PrepareExport(options);
        if (plan.FileCount == 0) { await Message("没有待处理文件", "所选评级和文件范围内没有可处理的原件。"); return; }
        if (options.GroupBySpecies && !options.DeleteOnly)
        {
            var review = SpeciesExportReview.Review(plan.Photos.Select(p => p.Photo), options.Scope);
            if (review.Count > 0)
            {
                string text = "以下照片存在未分类、未识别选区或低置信度结果：\n\n" + string.Join("\n", review.Take(12).Select(r => r.Photo.FileName + " · " + r.ReasonText)) +
                    "\n\n继续导出时，有其他有效鸟种的照片仍会进入对应鸟种目录；整张没有有效鸟种时进入“未分类”。";
                var answer = await PromptDialog.AskAsync(this, "核对鸟种结果", text, ("返回核对", PromptResult.Cancel), ("继续导出", PromptResult.Continue));
                if (answer != PromptResult.Continue) { LocateSpeciesReview(review[0].Photo); return; }
            }
        }
        if (options.DeleteOnly || options.RemoveOriginal)
        {
            string action = options.DeleteOnly ? "删除所选舍弃原件" : "在全部目标副本复制并校验成功后移除原件";
            string text = $"即将{action}。\n范围：{options.ScopeLabel} · {plan.PhotoCount} 张照片 / {plan.FileCount} 个原件。\n\n原件优先放入回收站，不支持回收站时永久删除。权限或其他错误会保留原件。\n取消会停止后续操作，已完成部分会按实际结果记录。";
            if (await PromptDialog.AskAsync(this, "确认原文件操作", text, ("取消", PromptResult.Cancel), ("确认继续", PromptResult.Continue)) != PromptResult.Continue) return;
        }
        if (!_closed) await Execute(() => Controller.ExecuteExportAsync(plan, options, ResolveExportConflict));
    });
    /// <summary>返回核对时定位实际有问题的照片及其首个未识别/低置信度框，坐标待核对不单独阻止分类。</summary>
    private void LocateSpeciesReview(PhotoItem photo)
    {
        var bird = photo.Birds.FirstOrDefault(b => b.HasRegion && (b.EffectiveSpecies is null || b.ManualSpecies is null && b.Species is { HasValidConfidence: true } match && match.Confidence < SpeciesExportReview.DefaultLowConfidenceThreshold));
        if (bird is not null) photo.SelectedBirdIndex = photo.Birds.IndexOf(bird);
        Controller.State.LocatePhoto(photo);
        if (bird is not null && Controller.State.IsPreviewReady) SelectBird(photo.SelectedBirdIndex);
        Controller.State.Status = "已返回待核对照片 · 可更换、删除或手动添加鸟种";
    }
    /// <summary>导出在后台触发冲突，显式调回 UI 并异步等待，避免阻塞调度器。</summary>
    private async Task<ExportConflictDecision> ResolveExportConflict(ExportConflict conflict, CancellationToken token) =>
        await Dispatcher.UIThread.InvokeAsync<ExportConflictDecision>(() => ResolveConflictOnUi(conflict, token));
    /// <summary>关闭/取消会关闭尚未决定的冲突窗口并返回取消；记住操作只交给本次导出会话。</summary>
    private async Task<ExportConflictDecision> ResolveConflictOnUi(ExportConflict conflict, CancellationToken token)
    {
        if (_closed || token.IsCancellationRequested) return new(ExportConflictAction.Cancel);
        var dialog = new ExportConflictDialog(conflict);
        using var cancellation = token.Register(() => Dispatcher.UIThread.Post(() => { if (dialog.IsVisible) dialog.Close(new ExportConflictDecision(ExportConflictAction.Cancel)); }));
        if (token.IsCancellationRequested) return new(ExportConflictAction.Cancel);
        var answer = await dialog.ShowDialog<ExportConflictDecision?>(this);
        return _closed || token.IsCancellationRequested || answer is null ? new(ExportConflictAction.Cancel) : answer;
    }
}
