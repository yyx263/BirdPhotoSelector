using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace BirdPhotoSelector;

/// <summary>Session presentation state. File system and image work stay in services.</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    public ObservableCollection<GroupCard> Groups { get; } = new();
    private GroupCard? _currentGroup;
    private PhotoItem? _currentPhoto;
    private bool _isBusy;
    private bool _dirty;
    private string _status = "准备就绪 · 添加照片，开始本次挑选";
    private double _threshold = .82;
    private double _progress;
    private bool _isPreviewReady;
    private bool _updatingSelection;
    private readonly HashSet<PhotoItem> _observedPhotos = new();
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
    public bool IsBusy { get => _isBusy; set { _isBusy = value; Notify(); Notify(nameof(CanEdit)); Notify(nameof(CanRate)); } }
    public bool CanEdit => !IsBusy;
    public bool HasPhoto => CurrentPhoto != null && IsPreviewReady;
    public bool IsPreviewReady { get => _isPreviewReady; set { _isPreviewReady = value; Notify(); Notify(nameof(HasPhoto)); Notify(nameof(CanRate)); } }
    public bool CanRate => CanEdit && HasPhoto;
    public PhotoRating CurrentRating
    {
        get => CurrentPhoto?.Rating ?? PhotoRating.Unmarked;
        set { if (CanRate && CurrentPhoto != null) SetRating(CurrentPhoto, value); }
    }
    public bool Dirty { get => _dirty; set { if (value) Revision++; _dirty = value; Notify(); Notify(nameof(WindowTitle)); } }
    public string AutoSaveSummary { get => _autoSaveSummary; set { _autoSaveSummary = value; Notify(); } }
    public string AutoSavePath { get => _autoSavePath; set { _autoSavePath = value; Notify(); } }
    public string ProjectName { get; set; } = "未命名工程";
    public string WindowTitle => $"{(Dirty ? "● " : "")}{ProjectName} · 羽选 Bird Photo Selector";
    public string Status { get => _status; set { _status = value; Notify(); } }
    public double Threshold { get => _threshold; set { _threshold = Math.Clamp(value, .3, .99); Notify(); Dirty = true; } }
    public double Progress { get => _progress; set { _progress = value; Notify(); } }
    public IEnumerable<PhotoItem> AllPhotos => Groups.SelectMany(g => g.Photos);
    public int PhotoCount => Groups.Sum(g => g.Photos.Count);
    public bool HasPhotos => PhotoCount > 0;
    public int UnmarkedCount => AllPhotos.Count(p => p.Rating == PhotoRating.Unmarked);
    public string LibrarySummary => $"{PhotoCount} 张照片 · {Groups.Count} 个组";
    public string RatingSummary => $"未标记 {UnmarkedCount}    保留 {AllPhotos.Count(p => p.Rating == PhotoRating.Keep)}    精选 {AllPhotos.Count(p => p.Rating == PhotoRating.Favorite)}    舍弃 {AllPhotos.Count(p => p.Rating == PhotoRating.Reject)}";
    public string GroupPosition => CurrentGroup == null ? "—" : $"{Groups.IndexOf(CurrentGroup) + 1} / {Groups.Count}";
    public string PhotoPosition => CurrentPhoto == null || CurrentGroup == null ? "—" : $"{CurrentGroup.Photos.IndexOf(CurrentPhoto) + 1} / {CurrentGroup.Photos.Count}";

    public void SetGroups(IEnumerable<PhotoGroup> groups, string? selectPhotoId = null)
    {
        var snapshot = groups.Where(g => g.Photos.Count > 0).ToList();
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
        }
        finally { _updatingSelection = false; }
        if (photoChanged || forceReload) PhotoChanged?.Invoke(photo);
    }

    public void Refresh()
    {
        for (int i = 0; i < Groups.Count; i++) { Groups[i].Number = i + 1; Groups[i].Refresh(); }
        Notify(nameof(PhotoCount)); Notify(nameof(HasPhotos)); Notify(nameof(LibrarySummary)); Notify(nameof(RatingSummary)); Notify(nameof(UnmarkedCount)); Notify(nameof(CurrentRating));
        Notify(nameof(GroupPosition)); Notify(nameof(PhotoPosition)); Notify(nameof(WindowTitle));
    }
    public void NavigateGroup(int delta)
    {
        if (IsBusy || Groups.Count == 0) return;
        CurrentGroup = Groups[Math.Clamp(Groups.IndexOf(CurrentGroup!) + delta, 0, Groups.Count - 1)];
    }
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

    public bool HandleNavigationKey(Key key)
    {
        switch (key)
        {
            case Key.A: case Key.W: case Key.Left: case Key.Up: NavigatePhoto(-1); return true;
            case Key.D: case Key.S: case Key.Right: case Key.Down: NavigatePhoto(1); return true;
            case Key.PageUp: NavigateGroup(-1); return true;
            case Key.PageDown: NavigateGroup(1); return true;
            default: return false;
        }
    }

    public bool HandleRatingKey(Key key)
    {
        switch (key)
        {
            case Key.D1: case Key.NumPad1: CurrentRating = PhotoRating.Unmarked; return true;
            case Key.D2: case Key.NumPad2: CurrentRating = PhotoRating.Reject; return true;
            case Key.D3: case Key.NumPad3: CurrentRating = PhotoRating.Keep; return true;
            case Key.D4: case Key.NumPad4: CurrentRating = PhotoRating.Favorite; return true;
            default: return false;
        }
    }

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

    public void SetRating(PhotoItem photo, PhotoRating rating)
    {
        if (IsBusy || !_observedPhotos.Contains(photo) || !Enum.IsDefined(rating)) return;
        photo.Rating = rating;
    }

    private void OnPhotoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotoItem.SelectedBirdIndex)) { Dirty = true; return; }
        if (e.PropertyName != nameof(PhotoItem.Rating)) return;
        Dirty = true;
        Refresh();
    }
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class GroupCard : INotifyPropertyChanged
{
    public PhotoGroup Model { get; }
    public ObservableCollection<PhotoItem> Photos => Model.Photos;
    public int Number { get; set; }
    public PhotoItem? Cover => Photos.FirstOrDefault();
    public string Title => Photos.Count > 1 ? $"连拍组 {Number:00}" : Cover?.FileName ?? "空组";
    public string Summary => Photos.Count > 1 ? $"{Photos.Count} 张照片" : "独立照片";
    public PhotoRating Rating => Photos.Count == 0 || Photos.Any(p => p.Rating == PhotoRating.Unmarked) ? PhotoRating.Unmarked :
        Photos.Any(p => p.Rating == PhotoRating.Favorite) ? PhotoRating.Favorite :
        Photos.Any(p => p.Rating == PhotoRating.Keep) ? PhotoRating.Keep : PhotoRating.Reject;
    public GroupCard(PhotoGroup model) => Model = model;
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
