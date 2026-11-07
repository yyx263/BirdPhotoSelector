using System.Collections.ObjectModel;
using System.ComponentModel;

namespace BirdPhotoSelector;

/// <summary>照片组的可观察展示数据。评级按未标记、精选、保留、舍弃的既定优先级汇总，不依赖 UI 控件。</summary>
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
    /// <summary>引用既有组模型，不复制照片或共享另一份排序状态。</summary>
    public GroupCard(PhotoGroup model) => Model = model;
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>单次评级只通知角标，不重新绑定封面、名称或整个列表。</summary>
    internal void RefreshRating() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Rating)));
    /// <summary>通知该组所有派生展示值可能变化。</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
