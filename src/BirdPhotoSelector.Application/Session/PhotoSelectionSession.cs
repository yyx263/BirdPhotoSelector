using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BirdPhotoSelector;

/// <summary>
/// 可被任意桌面界面复用的挑选会话：统一照片选择、跨组导航、评级、人工编辑和状态通知。
/// 不识别具体按键、不打开窗口、不执行图像解码；调用方在自己的 UI 线程串行修改此对象。
/// </summary>
public class PhotoSelectionSession : INotifyPropertyChanged
{
    /// <summary>有序照片组，界面可直接观察变更，不保存任何控件引用。</summary>
    public ObservableCollection<GroupCard> Groups { get; } = new();
    /// <summary>本会话的人工操作历史；原文件操作不进入撤销栈。</summary>
    public SpeciesEditHistory SpeciesEdits { get; } = new();
    /// <summary>随工程保存的最近十个鸟种，撤销结果编辑不倒退最近选择。</summary>
    public RecentSpeciesHistory RecentSpecies { get; } = new();
    private GroupCard? _currentGroup;
    private PhotoItem? _currentPhoto;
    private bool _isBusy;
    private bool _dirty;
    private string _status = "准备就绪 · 添加照片，开始本次挑选";
    private double _threshold = GroupingThreshold.Default;
    private double _progress;
    private bool _isPreviewReady;
    private bool _updatingSelection;
    private readonly HashSet<PhotoItem> _observedPhotos = new();
    // 结构变化时重建索引，单次评级只更新所属组与四级计数，不遍历整库或刷新无关缩略图。
    private readonly Dictionary<PhotoItem, (GroupCard Group, PhotoRating Rating)> _ratingIndex = new();
    private readonly int[] _ratingCounts = new int[4];
    private int _photoCount;
    private string _autoSaveSummary = "自动保存 · 5分钟";
    private string _autoSavePath = "尚未自动保存；未命名工程会写入本机恢复文件。";
    public long Revision { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<PhotoItem?>? PhotoChanged;

    public GroupCard? CurrentGroup
    {
        get => _currentGroup;
        set
        {
            if (_updatingSelection || _currentGroup == value) return;
            SelectPhoto(value, value?.Photos.FirstOrDefault());
        }
    }
    public PhotoItem? CurrentPhoto
    {
        get => _currentPhoto;
        set
        {
            if (_updatingSelection || _currentPhoto == value || (value != null && _currentGroup?.Photos.Contains(value) != true)) return;
            SelectPhoto(_currentGroup, value);
        }
    }
    public bool IsBusy { get => _isBusy; set { _isBusy = value; Notify(); Notify(nameof(CanEdit)); Notify(nameof(CanRate)); Notify(nameof(CanEditBirdRegions)); Notify(nameof(CanReidentifyCurrentPhoto)); } }
    public bool CanEdit => !IsBusy;
    public bool HasPhoto => CurrentPhoto != null && IsPreviewReady;
    public bool IsPreviewReady { get => _isPreviewReady; set { _isPreviewReady = value; Notify(); Notify(nameof(HasPhoto)); Notify(nameof(CanRate)); Notify(nameof(CanEditBirdRegions)); Notify(nameof(CanReidentifyCurrentPhoto)); } }
    public bool CanRate => CanEdit && HasPhoto;
    public bool CanReidentifyCurrentPhoto => CanRate;
    public bool CanEditBirdRegions => CanRate;
    public string CurrentFormatLabel => CurrentPhoto is not { } photo ? "—" : photo.FormatLabel +
        (photo.Assets.FirstOrDefault(asset => asset.Kind == PhotoAssetKind.Raw) is { } raw
            ? $" ({System.IO.Path.GetExtension(raw.FilePath).ToUpperInvariant()})" : "");
    public string PreviewSourceLabel => CurrentPhoto?.ActivePreviewAsset is { } asset
        ? "预览：" + (asset.Kind == PhotoAssetKind.Jpg ? "JPG" : "RAW") : "本地照片";
    public string GeometryNotice => CurrentPhoto?.GeometryStatus == PhotoGeometryStatus.NeedsReview
        ? "图像来源已变化，请注意核对" : "";
    public PhotoRating CurrentRating
    {
        get => CurrentPhoto?.Rating ?? PhotoRating.Unmarked;
        set { if (CanRate && CurrentPhoto != null) SetRating(CurrentPhoto, value); }
    }
    public bool Dirty
    {
        get => _dirty;
        set
        {
            // 每次视图编辑仍推进保存修订；只有脏标记实际变化才更新系统标题，避免拖拽触发标题栏重绘。
            if (value) Revision++;
            if (_dirty == value) return;
            _dirty = value; Notify(); Notify(nameof(WindowTitle));
        }
    }
    public string AutoSaveSummary { get => _autoSaveSummary; set { _autoSaveSummary = value; Notify(); } }
    public string AutoSavePath { get => _autoSavePath; set { _autoSavePath = value; Notify(); } }
    public string ProjectName { get; set; } = "未命名工程";
    public string WindowTitle => $"{(Dirty ? "● " : "")}{ProjectName} · 羽选 Bird Photo Selector";
    public string Status { get => _status; set { _status = value; Notify(); } }
    public double Threshold
    {
        get => _threshold;
        set
        {
            if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
            _threshold = Math.Clamp(value, 0, 1);
            Notify(); Dirty = true;
        }
    }
    public double Progress { get => _progress; set { _progress = value; Notify(); } }
    public IEnumerable<PhotoItem> AllPhotos => Groups.SelectMany(g => g.Photos);
    public int PhotoCount => _photoCount;
    public bool HasPhotos => PhotoCount > 0;
    public int UnmarkedCount => _ratingCounts[(int)PhotoRating.Unmarked];
    public string LibrarySummary => $"{PhotoCount} 张照片 · {Groups.Count} 个组";
    public string RatingSummary => $"未标记 {UnmarkedCount}    保留 {_ratingCounts[(int)PhotoRating.Keep]}    精选 {_ratingCounts[(int)PhotoRating.Favorite]}    舍弃 {_ratingCounts[(int)PhotoRating.Reject]}";
    public string GroupPosition => CurrentGroup == null ? "—" : $"{Groups.IndexOf(CurrentGroup) + 1} / {Groups.Count}";
    public string PhotoPosition => CurrentPhoto == null || CurrentGroup == null ? "—" : $"{CurrentGroup.Photos.IndexOf(CurrentPhoto) + 1} / {CurrentGroup.Photos.Count}";

    /// <summary>重建观察订阅和有序组列表，最后一次性发布有效组/照片选择，避免瞬间空选择引起预览闪烁。</summary>
    public void SetGroups(IEnumerable<PhotoGroup> groups, string? selectPhotoId = null)
    {
        var snapshot = groups.Where(g => g.Photos.Count > 0).ToList();
        var retained = snapshot.SelectMany(group => group.Photos).ToHashSet();
        if (_observedPhotos.Any(photo => !retained.Contains(photo))) SpeciesEdits.Clear();
        foreach (var photo in _observedPhotos) photo.PropertyChanged -= OnPhotoChanged;
        _observedPhotos.Clear();
        _updatingSelection = true;
        try
        {
            Groups.Clear();
            foreach (var group in snapshot)
            {
                Groups.Add(new GroupCard(group));
                foreach (var photo in group.Photos)
                    if (_observedPhotos.Add(photo)) photo.PropertyChanged += OnPhotoChanged;
            }
        }
        finally { _updatingSelection = false; }
        Refresh();
        var selectedGroup = Groups.FirstOrDefault(g => g.Photos.Any(p => p.Id == selectPhotoId)) ?? Groups.FirstOrDefault();
        SelectPhoto(selectedGroup, selectedGroup?.Photos.FirstOrDefault(p => p.Id == selectPhotoId) ?? selectedGroup?.Photos.FirstOrDefault(), forceReload: true);
    }

    /// <summary>将组和照片作为一次状态提交发布，外部观察者看到的选择必须始终一致。</summary>
    private void SelectPhoto(GroupCard? group, PhotoItem? photo, bool forceReload = false)
    {
        bool groupChanged = _currentGroup != group;
        bool photoChanged = _currentPhoto != photo;
        _updatingSelection = true;
        try
        {
            // Publish both values together. Rebuilding a ListBox's ItemsSource can otherwise
            // push a transient null selection back and clear the retained preview.
            _currentGroup = group;
            _currentPhoto = photo;
            if (groupChanged) { Notify(nameof(CurrentGroup)); Notify(nameof(GroupPosition)); }
            if (photoChanged || forceReload) Notify(nameof(CurrentPhoto));
            Notify(nameof(PhotoPosition)); Notify(nameof(HasPhoto)); Notify(nameof(CanRate)); Notify(nameof(CurrentRating));
            NotifySourceState();
        }
        finally { _updatingSelection = false; }
        if (photoChanged || forceReload) PhotoChanged?.Invoke(photo);
    }

    /// <summary>更新组编号和派生计数/来源状态，不触发图像加载。</summary>
    public void Refresh()
    {
        _ratingIndex.Clear(); Array.Clear(_ratingCounts); _photoCount = 0;
        for (int i = 0; i < Groups.Count; i++)
        {
            var group = Groups[i]; group.Number = i + 1;
            foreach (var photo in group.Photos)
            { _photoCount++; _ratingCounts[(int)photo.Rating]++; _ratingIndex[photo] = (group, photo.Rating); }
            group.Refresh();
        }
        Notify(nameof(PhotoCount)); Notify(nameof(HasPhotos)); Notify(nameof(LibrarySummary)); Notify(nameof(RatingSummary)); Notify(nameof(UnmarkedCount)); Notify(nameof(CurrentRating));
        Notify(nameof(GroupPosition)); Notify(nameof(PhotoPosition)); Notify(nameof(WindowTitle));
        NotifySourceState();
    }

    /// <summary>统一刷新当前来源、格式、几何提示和操作可用性。</summary>
    private void NotifySourceState()
    {
        Notify(nameof(CanEditBirdRegions)); Notify(nameof(CanReidentifyCurrentPhoto)); Notify(nameof(CurrentFormatLabel));
        Notify(nameof(PreviewSourceLabel)); Notify(nameof(GeometryNotice));
    }
    /// <summary>在当前列表定位指定对象，只有属于本会话的照片才可成为当前选择。</summary>
    public bool LocatePhoto(PhotoItem photo)
    {
        foreach (var group in Groups)
            if (group.Photos.Contains(photo)) { SelectPhoto(group, photo); return true; }
        return false;
    }

    /// <summary>按偏移切换组并限制在列表边界，忙碌时保持当前选择。</summary>
    public void NavigateGroup(int delta)
    {
        if (IsBusy || Groups.Count == 0) return;
        CurrentGroup = Groups[Math.Clamp(Groups.IndexOf(CurrentGroup!) + delta, 0, Groups.Count - 1)];
    }
    /// <summary>按组内偏移连续导航，越过组首/组尾时进入相邻组，保持选中状态一致。</summary>
    public void NavigatePhoto(int delta)
    {
        if (IsBusy || CurrentGroup == null || CurrentGroup.Photos.Count == 0 || delta == 0) return;
        int groupIndex = Groups.IndexOf(CurrentGroup);
        if (groupIndex < 0) return;
        long photoIndex = (long)Math.Max(0, CurrentGroup.Photos.IndexOf(CurrentPhoto!)) + delta;
        while (photoIndex < 0 && groupIndex > 0) photoIndex += Groups[--groupIndex].Photos.Count;
        while (photoIndex >= Groups[groupIndex].Photos.Count && groupIndex < Groups.Count - 1)
            photoIndex -= Groups[groupIndex++].Photos.Count;
        var targetGroup = Groups[groupIndex];
        int targetIndex = (int)Math.Clamp(photoIndex, 0, targetGroup.Photos.Count - 1);
        SelectPhoto(targetGroup, targetGroup.Photos[targetIndex]);
    }

    /// <summary>为当前已加载照片添加或更换人工鸟种，同时维护撤销、最近选择和工程修改标记。</summary>
    public bool ApplyManualSpecies(PhotoItem photo, SpeciesIdentity species, DetectedBird? replaced = null)
    {
        if (!CanRate || !ReferenceEquals(CurrentPhoto, photo) || replaced is not null && !photo.Birds.Contains(replaced)) return false;
        if (replaced is null) SpeciesEdits.Add(photo, species);
        else SpeciesEdits.Replace(photo, replaced, species);
        RecentSpecies.Use(species); Dirty = true;
        return true;
    }

    /// <summary>删除当前照片的一条鸟种及关联选区；结果为空时自动解除下一次批量识别的人工保护。</summary>
    public bool DeleteBirdResult(PhotoItem photo, DetectedBird bird)
    {
        if (!CanEditBirdRegions || !ReferenceEquals(CurrentPhoto, photo) || !SpeciesEdits.Delete(photo, bird)) return false;
        Dirty = true; return true;
    }

    /// <summary>清空当前照片的选区与人工鸟种并记录一次可撤销操作，不改变照片文件、评级或分组。</summary>
    public bool ClearRecognitionResults(PhotoItem photo)
    {
        if (!CanEditBirdRegions || !ReferenceEquals(CurrentPhoto, photo) || !SpeciesEdits.Clear(photo)) return false;
        Dirty = true; return true;
    }

    /// <summary>撤销最近的结果编辑，返回应由界面显示的照片；已移出列表的旧记录不会变成当前选择。</summary>
    public PhotoItem? UndoSpeciesEdit()
    {
        if (IsBusy || !SpeciesEdits.CanUndo) return null;
        var photo = SpeciesEdits.Undo();
        if (photo is null || !AllPhotos.Contains(photo)) return null;
        Dirty = true; return photo;
    }

    /// <summary>按组顺序定位首张未标记照片并给出剩余数量，供导出返回继续筛选。</summary>
    public bool LocateFirstUnmarked()
    {
        foreach (var group in Groups)
        {
            var photo = group.Photos.FirstOrDefault(p => p.Rating == PhotoRating.Unmarked);
            if (photo == null) continue;
            SelectPhoto(group, photo);
            Status = $"已定位未标记照片：{photo.FileName} · 还剩 {UnmarkedCount} 张未标记";
            return true;
        }
        return false;
    }

    /// <summary>只允许给本会话观察的照片赋有效评级，忙碌时不修改。</summary>
    public void SetRating(PhotoItem photo, PhotoRating rating)
    {
        if (IsBusy || !_observedPhotos.Contains(photo) || !Enum.IsDefined(rating)) return;
        photo.Rating = rating;
    }

    /// <summary>监听来源、选择和评级变化，更新修订与计数，只有实际活动来源变化才要求重新预览。</summary>
    private void OnPhotoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PhotoItem.Assets) or nameof(PhotoItem.PreferredPreviewAssetId)
            or nameof(PhotoItem.ActivePreviewAssetId) or nameof(PhotoItem.GeometryReference)
            or nameof(PhotoItem.GeometryStatus))
        {
            Dirty = true;
            Refresh();
            return;
        }
        if (e.PropertyName == nameof(PhotoItem.FilePath))
        {
            Dirty = true;
            Refresh();
            if (ReferenceEquals(sender, _currentPhoto)) PhotoChanged?.Invoke(_currentPhoto);
            return;
        }
        if (e.PropertyName == nameof(PhotoItem.SelectedBirdIndex)) { Dirty = true; return; }
        if (e.PropertyName != nameof(PhotoItem.Rating) || sender is not PhotoItem photo || !_ratingIndex.TryGetValue(photo, out var previous)) return;
        if (previous.Rating == photo.Rating) return;
        _ratingCounts[(int)previous.Rating]--; _ratingCounts[(int)photo.Rating]++;
        _ratingIndex[photo] = (previous.Group, photo.Rating);
        Dirty = true; previous.Group.RefreshRating();
        Notify(nameof(RatingSummary)); Notify(nameof(UnmarkedCount));
        if (ReferenceEquals(photo, _currentPhoto)) Notify(nameof(CurrentRating));
    }
    /// <summary>发布可观察会话属性，不调用任何 UI 调度器。</summary>
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
