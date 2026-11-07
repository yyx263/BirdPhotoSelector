using Avalonia.Threading;
using BirdPhotoSelector.Desktop.Imaging;
using BirdPhotoSelector.Imaging.Portable;

namespace BirdPhotoSelector.Desktop.ViewModels;

/// <summary>
/// 新桌面宿主的会话协调器。复用 Application 用例，只管理异步操作、预览生命周期和保存路径。
/// 公开操作由 UI 线程调用；后台返回独立结果后在 UI 应用，所有过期预览和关闭后回调均丢弃。
/// </summary>
public sealed partial class DesktopController : IDisposable
{
    private readonly ApplicationServices _services;
    private readonly PreviewImageCache _cache;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private CancellationTokenSource? _previewRequest, _operation;
    private TaskCompletionSource _idle = Completed();
    private int _previewVersion;
    private bool _disposed, _autoSaving;
    /// <summary>唯一的可观察挑选状态，不复制领域照片、评级或鸟种记录。</summary>
    public PhotoSelectionSession State { get; } = new();
    /// <summary>最近成功加载的图像，切图期间保留；交互应同时检查 State.IsPreviewReady。</summary>
    public AvaloniaImageFrame? Preview { get; private set; }
    /// <summary>Preview 实际所属照片；加载期间可能与当前选择不同，不得据此修改新照片。</summary>
    public PhotoItem? PreviewPhoto { get; private set; }
    /// <summary>当前预览任务，供窗口生命周期和检查等待，不要求阻塞 UI。</summary>
    public Task PreviewTask { get; private set; } = Task.CompletedTask;
    /// <summary>当前手动工程路径，未命名/恢复会话为 null。</summary>
    public string? ProjectPath { get; private set; }
    /// <summary>自动保存恢复目录，由共享业务服务按平台选择。</summary>
    public string RecoveryDirectory => _services.AutoSave.RecoveryDirectory;
    /// <summary>最近一项操作的非致命提示，由宿主选择显示方式。</summary>
    public string? Notice { get; private set; }
    /// <summary>显示帧保留预算，不包含模型、临时解码或当前显示引用。</summary>
    public long CacheBudget => _cache.BudgetBytes;
    /// <summary>当前保留帧计费。</summary>
    public long CachedBytes => _cache.CachedBytes;
    /// <summary>预览内容或就绪状态变化，窗口应同步两个预览并更新操作可用性。</summary>
    public event Action? PreviewChanged;
    /// <summary>默认宿主组装，不在构造时加载 AI 模型。</summary>
    public DesktopController() : this(new(new PortablePhotoImageDecoder(), new SystemFileOperations())) { }
    /// <summary>可替换完整业务依赖，真实窗口与无窗口检查使用同一协调逻辑。</summary>
    public DesktopController(ApplicationServices services, long cacheBudgetBytes = 512L * 1024 * 1024)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _cache = new((path, token) => AvaloniaImageFrame.Create(services.Images.Decode(path, cancellationToken: token), token), cacheBudgetBytes);
        State.PhotoChanged += OnPhotoChanged;
    }
    /// <summary>开始新预览时立即禁止旧图交互，但不清空旧像素，避免切图黑屏。</summary>
    private void OnPhotoChanged(PhotoItem? photo) => PreviewTask = LoadPreviewAsync(photo);
    /// <summary>取消只影响当前等待者；版本校验避免慢图覆盖后选照片。</summary>
    private async Task LoadPreviewAsync(PhotoItem? photo)
    {
        if (_disposed) return;
        int version = ++_previewVersion;
        _previewRequest?.Cancel(); _previewRequest?.Dispose(); _previewRequest = null;
        State.IsPreviewReady = false;
        if (photo is null) { Preview = null; PreviewPhoto = null; PreviewChanged?.Invoke(); return; }
        State.Status = "正在载入照片：" + photo.FileName; PreviewChanged?.Invoke();
        _previewRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _previewRequest.Token;
        try
        {
            // 在进入后台前固定来源；解码线程不得读取可能被导入关联更新的 PhotoItem。
            var source = Source(photo);
            var neighbors = NearbySources(photo);
            PhotoImageInfo? rawInfo = source.ForegroundOnly
                ? await Task.Run(() => _services.Images.ReadInfo(source.Path, token), token) : null;
            token.ThrowIfCancellationRequested();
            var pending = _cache.GetAsync(source, token);
            var frame = (AvaloniaImageFrame)await pending;
            if (_disposed || version != _previewVersion) return;
            photo.PixelWidth = frame.PixelWidth; photo.PixelHeight = frame.PixelHeight;
            if (rawInfo is not null) photo.Metadata = rawInfo.Metadata;
            Preview = frame; PreviewPhoto = photo; State.IsPreviewReady = true;
            if (!State.IsBusy) State.Status = "右侧滚轮缩放，拖动平移 · 方向键 / WASD 切图 · Page Up / Down 切组";
            PreviewChanged?.Invoke();
            // 已命中的帧立即显示；邻居的文件状态查询不占用 UI 线程。
            _ = UpdateNeighborhoodAsync(neighbors, source, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed && version == _previewVersion) { State.Status = "预览失败（保留上一张画面）：" + ex.Message; PreviewChanged?.Invoke(); }
        }
    }
    /// <summary>只快照前后各两张，不在每次切图时为整个照片库分配来源对象。</summary>
    private PreviewSource[] NearbySources(PhotoItem current)
    {
        var group = State.CurrentGroup; if (group is null) return [Source(current)];
        int groupIndex = State.Groups.IndexOf(group), photoIndex = group.Photos.IndexOf(current);
        var before = Walk(-1).Reverse(); var after = Walk(1);
        return before.Concat([current]).Concat(after).Select(Source).ToArray();
        IEnumerable<PhotoItem> Walk(int direction)
        {
            int g = groupIndex, p = photoIndex;
            for (int n = 0; n < 2; n++)
            {
                p += direction;
                while (g >= 0 && g < State.Groups.Count && (p < 0 || p >= State.Groups[g].Photos.Count))
                { g += direction; if (g >= 0 && g < State.Groups.Count) p = direction > 0 ? 0 : State.Groups[g].Photos.Count - 1; }
                if (g < 0 || g >= State.Groups.Count) yield break;
                yield return State.Groups[g].Photos[p];
            }
        }
    }
    /// <summary>后台查询文件版本；取消和关闭时忽略迟到工作，真正的请求提交仍由缓存锁内验证。</summary>
    private async Task UpdateNeighborhoodAsync(PreviewSource[] neighbors, PreviewSource source, CancellationToken token)
    {
        try { await Task.Run(() => _cache.UpdateNeighborhood(neighbors, source, token), token).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
    /// <summary>在 UI 线程冻结真实来源和完整尺寸，后台不读取可变照片对象。</summary>
    private static PreviewSource Source(PhotoItem photo) => new(photo.Id, photo.FilePath, photo.PixelWidth, photo.PixelHeight, photo.ActivePreviewAsset?.Kind == PhotoAssetKind.Raw);
    /// <summary>列表变动后统一恢复选择并淘汰已移出原件的缓存；移除不触及原文件。</summary>
    private void SetGroups(IEnumerable<PhotoGroup> groups, string? selected = null)
    {
        State.SetGroups(groups.ToArray(), selected);
        _cache.TrimToPaths(State.AllPhotos.Select(p => p.FilePath));
    }
    /// <summary>导入任意来源目录的照片并应用原件关联提案，格式与实体去重规则均复用业务层。</summary>
    public Task<bool> ImportAsync(IEnumerable<string> paths) => RunAsync(async token =>
    {
        var existing = State.AllPhotos.ToArray();
        var result = await _services.Import.ImportAsync(paths, existing, true, token, Progress<PhotoOperationProgress>(p =>
        { State.Status = p.Message; State.Progress = p.Total > 0 ? p.Completed * 100d / p.Total : 0; }));
        token.ThrowIfCancellationRequested();
        foreach (var update in result.Updates) update.ApplyTo(existing.Single(p => p.Id == update.PhotoId));
        var groups = State.Groups.Select(g => g.Model).ToList();
        foreach (var photo in result.Added) { var group = new PhotoGroup(); group.Photos.Add(photo); groups.Add(group); }
        SetGroups(groups, State.CurrentPhoto?.Id ?? result.Added.FirstOrDefault()?.Id);
        if (result.Added.Count + result.Updates.Count > 0) State.Dirty = true;
        State.Status = $"已添加 {result.Added.Count} 张 · 关联更新 {result.Updates.Count} 张 · 跳过重复 {result.SkippedDuplicate} · 不支持 {result.SkippedUnsupported} · 配对冲突 {result.SkippedConflict}";
        Notice = result.Errors.Count == 0 ? null : string.Join("\n", result.Errors.Take(12));
    });
    /// <summary>只按连续背景分组，不更改已有鸟种、选区、评级和视图。</summary>
    public Task<bool> GroupAsync() => RunAsync(async token =>
    {
        var photos = State.AllPhotos.ToArray(); if (photos.Length == 0) return;
        string? selected = State.CurrentPhoto?.Id;
        using var analysis = _services.CreateAnalysis();
        var result = await analysis.AnalyzeAsync(photos, GroupingThreshold.ToActual(State.Threshold), Progress<AnalyzeProgress>(p =>
        { State.Status = p.Message; State.Progress = p.Total > 0 ? p.Completed * 100d / p.Total : 0; }), token);
        token.ThrowIfCancellationRequested();
        foreach (var outcome in result.Outcomes)
        { var photo = photos.Single(p => p.Id == outcome.PhotoId); photo.PixelWidth = outcome.PixelWidth; photo.PixelHeight = outcome.PixelHeight; }
        SetGroups(result.Groups.Select(list => { var group = new PhotoGroup(); foreach (var photo in list) group.Photos.Add(photo); return group; }), selected);
        State.Dirty = true; State.Status = $"分组完成 · {photos.Length} 张照片 · {State.Groups.Count} 个组";
        Notice = result.Errors.Count == 0 ? null : string.Join("\n", result.Errors.Take(12));
    });
    /// <summary>串行打开工程，原恢复和保存完成后再替换会话；保留已有识别与最近选择。</summary>
    public Task<bool> OpenAsync(string path, bool recovery = false) => RunAsync(async token =>
    {
        await _saveGate.WaitAsync(token);
        try
        {
            var result = await _services.Projects.LoadAsync(path, token); token.ThrowIfCancellationRequested();
            _cache.Clear(); State.SpeciesEdits.Clear(); State.RecentSpecies.Replace(result.RecentSpecies);
            State.Threshold = result.GroupingThreshold; SetGroups(result.Groups);
            _services.AutoSave.StartSession(); ProjectPath = recovery ? null : Path.GetFullPath(path);
            State.ProjectName = recovery ? "恢复的挑选" : Path.GetFileNameWithoutExtension(path);
            State.Dirty = recovery; State.Refresh();
            State.Status = $"工程已打开 · {State.PhotoCount} 张照片 · 跳过缺失 {result.SkippedMissing} · 无效 {result.SkippedInvalid} · 重复 {result.SkippedDuplicate}";
            State.AutoSaveSummary = "自动保存 · 5分钟"; State.AutoSavePath = path;
            Notice = result.Errors.Count == 0 && result.SkippedMissing + result.SkippedInvalid + result.MissingAssetCount == 0 ? null : State.Status + "\n" + string.Join("\n", result.Errors.Take(8));
        }
        finally { _saveGate.Release(); }
    });
    /// <summary>保存现有 v3 工程格式；仅保存修订仍与当前状态一致时清除修改标记。</summary>
    public Task<bool> SaveAsync(string path) => RunAsync(async token =>
    {
        await _saveGate.WaitAsync(token);
        try
        {
            long revision = State.Revision;
            await _services.Projects.SaveAsync(path, State.Groups.Select(g => g.Model), State.Threshold, token, State.RecentSpecies.Items);
            ProjectPath = Path.GetFullPath(path); State.ProjectName = Path.GetFileNameWithoutExtension(path);
            if (revision == State.Revision) State.Dirty = false;
            _services.AutoSave.NoteManualSave(revision); State.AutoSavePath = path; State.Refresh(); State.Status = "工程已保存";
        }
        finally { _saveGate.Release(); }
    });
    /// <summary>到期且有新修订才保存；写入与手动保存/打开共用门，关闭后不再发布状态。</summary>
    public async Task AutoSaveIfDueAsync()
    {
        if (_disposed || _autoSaving || State.IsBusy || !State.Dirty || !_services.AutoSave.IsDue || _services.AutoSave.LastSavedRevision == State.Revision) return;
        _autoSaving = true;
        try
        {
            await _saveGate.WaitAsync(_lifetime.Token);
            try
            {
                if (_disposed || State.IsBusy || !State.Dirty) return;
                long revision = State.Revision;
                var result = await _services.AutoSave.SaveAsync(State.Groups.Select(g => g.Model), State.Threshold, ProjectPath, revision, _lifetime.Token, State.RecentSpecies.Items);
                if (_disposed || result.SessionGeneration != _services.AutoSave.SessionGeneration) return;
                if (!result.IsRecovery && revision == State.Revision) State.Dirty = false;
                State.AutoSavePath = result.Path; State.AutoSaveSummary = $"自动保存 {DateTime.Now:HH:mm}";
            }
            finally { _saveGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) { State.AutoSaveSummary = "自动保存失败"; State.AutoSavePath = ex.Message; } }
        finally { _autoSaving = false; }
    }
    /// <summary>清空列表但保留工程位置与原照片；与保存串行，避免写入中更换状态。</summary>
    public Task<bool> ClearAsync() => RunAsync(async token =>
    {
        await _saveGate.WaitAsync(token);
        try { _cache.Clear(); SetGroups([]); State.Dirty = true; State.Status = "列表已清空 · 原照片保留"; }
        finally { _saveGate.Release(); }
    });
    /// <summary>按选择移除记录，组中仍有成员则保留；照片删除不属于此 UI 操作。</summary>
    public void Remove(IEnumerable<PhotoItem> photos)
    {
        if (State.IsBusy) return;
        var removed = photos.ToHashSet(); if (removed.Count == 0) return;
        var groups = State.Groups.Select(g => g.Model).ToArray(); string? selected = State.CurrentPhoto?.Id;
        foreach (var group in groups) foreach (var photo in group.Photos.Where(removed.Contains).ToArray()) group.Photos.Remove(photo);
        SetGroups(groups, selected); State.Dirty = true; State.Status = "已从列表中移除 · 原照片保留";
    }
    /// <summary>拆分整组或仅拆出当前照片，其他顺序、评级和识别对象保持原样。</summary>
    public void Split(bool wholeGroup)
    {
        if (State.IsBusy || State.CurrentGroup is not { } current || current.Photos.Count < 2 || State.CurrentPhoto is not { } selected) return;
        var groups = State.Groups.Select(g => g.Model).ToList(); int index = groups.IndexOf(current.Model);
        if (wholeGroup) groups.RemoveAt(index); else { current.Photos.Remove(selected); index++; }
        foreach (var photo in wholeGroup ? current.Photos.ToArray() : new[] { selected })
        { var group = new PhotoGroup(); group.Photos.Add(photo); groups.Insert(index++, group); }
        SetGroups(groups, selected.Id); State.Dirty = true;
    }
    /// <summary>只取消当前操作，已完成的持久化由用例自身报告，预览不依赖此令牌。</summary>
    public void Cancel() => _operation?.Cancel();
    /// <summary>关闭流程可等待当前任务完成回滚，不阻塞 UI 事件循环。</summary>
    public Task WhenIdleAsync() => _idle.Task;
    /// <summary>统一忙碌、取消与进度生命周期；异常交给尚未关闭的宿主展示。</summary>
    private async Task<bool> RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || State.IsBusy) return false;
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _idle = new();
        State.IsBusy = true; State.Progress = 0; Notice = null;
        try { await action(_operation.Token); if (!_disposed) State.Progress = 100; return true; }
        catch (OperationCanceledException) { if (!_disposed) State.Status = "操作已取消"; return false; }
        finally { _operation.Dispose(); _operation = null; if (!_disposed) { State.IsBusy = false; State.Refresh(); } _idle.TrySetResult(); }
    }
    /// <summary>进度只能回到 UI 线程，过期操作或关闭之后的排队进度不能覆盖新状态。</summary>
    private IProgress<T> Progress<T>(Action<T> update)
    {
        var operation = _operation;
        return new Progress<T>(value => Dispatcher.UIThread.Post(() => { if (!_disposed && ReferenceEquals(operation, _operation)) update(value); }));
    }
    /// <summary>初始化已完成的空闲信号，避免无操作时关闭等待永不结束。</summary>
    private static TaskCompletionSource Completed() { var source = new TaskCompletionSource(); source.SetResult(); return source; }
    /// <summary>终止所有后台等待、断开事件和缓存；不释放仍在显示的共享图像。</summary>
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; ++_previewVersion;
        State.PhotoChanged -= OnPhotoChanged; _lifetime.Cancel(); _previewRequest?.Cancel(); _cache.Dispose();
        PreviewChanged = null; ResultsChanged = null;
    }
}
