namespace BirdPhotoSelector.Desktop.ViewModels;

/// <summary>结果变化后的显示动作；业务对象不持有画布，也不规定窗口或对话框。</summary>
public enum ResultsViewAction { Preserve, FocusBird, RestoreSaved }

/// <summary>识别与人工编辑的宿主协调，全部规则复用共享用例和照片会话。</summary>
public sealed partial class DesktopController
{
    /// <summary>结果与撤销已提交后通知窗口；此时忙碌状态已结束，可安全更新选择和视图。</summary>
    public event Action<PhotoItem, ResultsViewAction>? ResultsChanged;
    /// <summary>默认中国候选，批量定位所有鸟并识别；人工编辑过且仍有结果的照片由用例跳过。</summary>
    public async Task<bool> RecognizeAsync()
    {
        var selected = State.CurrentPhoto; bool focusNew = selected is { Birds.Count: 0 };
        bool completed = await RunAsync(async token =>
        {
            var photos = State.AllPhotos.ToArray(); if (photos.Length == 0) return;
            var byId = photos.ToDictionary(p => p.Id);
            State.Status = "正在定位并识别鸟种 · 中国候选…";
            using var recognition = _services.CreateRecognition();
            var result = await recognition.RecognizeAsync(photos, true, Progress<SpeciesProgress>(p =>
            { State.Progress = p.Total > 0 ? p.Completed * 100d / p.Total : 0; State.Status = p.Message; }), token);
            token.ThrowIfCancellationRequested();
            foreach (var outcome in result.Outcomes) if (byId.TryGetValue(outcome.PhotoId, out var photo)) outcome.ApplyTo(photo);
            if (result.Outcomes.Count > 0) State.Dirty = true;
            State.Status = $"识别完成 · {result.MatchedBirds} 只鸟有结果 · 跳过人工编辑 {result.ManuallyEditedSkippedPhotos} 张";
            Notice = result.MissingModel ? "鸟类定位或鸟种识别资源不完整，请恢复完整模型资源后重试；已有结果已保留。" :
                result.Errors.Count > 0 ? string.Join("\n", result.Errors.Take(12)) : null;
        });
        if (completed && selected is not null) PublishResults(selected, focusNew ? ResultsViewAction.FocusBird : ResultsViewAction.Preserve);
        return completed;
    }
    /// <summary>单张重新定位和识别成功才整体替换，正常零检出可清空，均记录一次撤销；不再额外确认。</summary>
    public async Task<bool> ReidentifyAsync()
    {
        if (!State.CanReidentifyCurrentPhoto || State.CurrentPhoto is not { } photo) return false;
        bool replaced = false;
        bool completed = await RunAsync(async token =>
        {
            using var recognition = _services.CreateSinglePhotoRecognition();
            var result = await recognition.RebuildAsync(photo, true, Progress<SpeciesProgress>(p =>
            { State.Progress = p.Total > 0 ? p.Completed * 100d / p.Total : 0; State.Status = p.Message; }), token);
            token.ThrowIfCancellationRequested();
            if (!result.Success) { Notice = string.Join("\n", result.Errors); State.Status = "重新识别未完成 · 原结果已保留"; return; }
            var checkpoint = State.SpeciesEdits.Capture(photo, "重新识别本张");
            result.ApplyTo(photo, result.IsEmpty, token); State.SpeciesEdits.Commit(checkpoint); State.Dirty = true; replaced = true;
            State.Status = result.IsEmpty ? "本张未检出鸟 · 已清空旧结果 · 可撤销" : $"本张重新识别完成 · {result.Birds.Count} 只鸟 · 可撤销";
        });
        if (replaced) PublishResults(photo, ResultsViewAction.RestoreSaved);
        return completed && replaced;
    }
    /// <summary>手动框在当前显示图像坐标中创建并识别；失败/取消保留框且可以撤销，不能改用旧图像。</summary>
    public async Task<bool> RecognizeRegionAsync(PhotoItem photo, BirdBox box)
    {
        if (!State.CanEditBirdRegions || !ReferenceEquals(State.CurrentPhoto, photo) || !ReferenceEquals(PreviewPhoto, photo) || Preview is not { } image || !box.IsValid) return false;
        var region = box.Copy();
        try
        {
            return await RunAsync(async token =>
            {
                var checkpoint = State.SpeciesEdits.Capture(photo, "绘制并识别手动选区");
                try
                {
                    var bird = photo.AddManualRegion(region); State.Dirty = true;
                    State.Status = $"正在识别手动选区 {bird.DisplayNumber} · 中国候选…";
                    using var recognition = _services.CreateSpeciesRecognizer();
                    var result = await Task.Run(() => recognition.Recognize(image, region, true, token), token);
                    token.ThrowIfCancellationRequested();
                    if (_disposed || !photo.Birds.Contains(bird)) return;
                    if (result.Match is { IsValid: true } match) bird.Species = match;
                    bird.RecognitionStatus = result.Status; State.Dirty = true;
                    State.Status = result.Match is { IsValid: true } ? $"{bird.SpeciesName} · 置信度 {bird.ConfidenceText}" : "选区已保留 · " + result.Status;
                }
                finally { State.SpeciesEdits.Commit(checkpoint); }
            });
        }
        finally { PublishResults(photo, ResultsViewAction.FocusBird); }
    }
    /// <summary>清空当前全部结果并解除批量识别保护，保存一次可撤销记录。</summary>
    public bool ClearResults()
    {
        if (State.CurrentPhoto is not { } photo || !State.ClearRecognitionResults(photo)) return false;
        PublishResults(photo, ResultsViewAction.RestoreSaved); State.Status = "已清空本张结果 · 可重新识别或撤销"; return true;
    }
    /// <summary>删除框与删除鸟种共用同一入口，连续重排可见编号。</summary>
    public bool DeleteResult(PhotoItem photo, DetectedBird bird)
    {
        bool selected = ReferenceEquals(photo.SelectedBird, bird);
        if (!State.DeleteBirdResult(photo, bird)) return false;
        if (selected && photo.Birds.Count == 0) photo.ViewZoom = photo.ViewCenterX = photo.ViewCenterY = null;
        PublishResults(photo, selected ? photo.Birds.Count == 0 ? ResultsViewAction.RestoreSaved : ResultsViewAction.FocusBird : ResultsViewAction.Preserve);
        State.Status = "已删除鸟种及关联选区 · 可撤销"; return true;
    }
    /// <summary>添加/更换均创建无框人工结果，维护最近十种、排序、保护和撤销，不虚构分数。</summary>
    public bool ApplySpecies(PhotoItem photo, SpeciesIdentity species, DetectedBird? replaced = null)
    {
        if (!State.ApplyManualSpecies(photo, species, replaced)) return false;
        PublishResults(photo, ResultsViewAction.Preserve); State.Status = "已更新人工鸟种 · 可撤销"; return true;
    }
    /// <summary>撤销后定位对应照片；同张恢复旧视图，跨图由预览加载器恢复。</summary>
    public bool UndoResults()
    {
        var photo = State.UndoSpeciesEdit(); if (photo is null) return false;
        State.LocatePhoto(photo); PublishResults(photo, ResultsViewAction.RestoreSaved); State.Status = "已撤销最近一次鸟种或选区编辑"; return true;
    }
    /// <summary>关闭或结果已移出会话时忽略通知，窗口不接收迟到后台结果。</summary>
    private void PublishResults(PhotoItem photo, ResultsViewAction action)
    { if (!_disposed && State.AllPhotos.Contains(photo)) ResultsChanged?.Invoke(photo, action); }
}
