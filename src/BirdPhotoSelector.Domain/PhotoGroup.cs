using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BirdPhotoSelector;

/// <summary>按列表顺序保存同组逻辑照片，观察封面变化并通知界面，不拥有图像解码或文件操作。</summary>
public sealed class PhotoGroup : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _label;
    /// <summary>创建空组并订阅成员变化，封面相关通知随成员增删更新。</summary>
    public PhotoGroup(string label = "")
    {
        _label = label;
        Photos.CollectionChanged += (_, e) =>
        {
            if (e.OldItems is not null)
                foreach (PhotoItem photo in e.OldItems) photo.PropertyChanged -= OnPhotoChanged;
            if (e.NewItems is not null)
                foreach (PhotoItem photo in e.NewItems) photo.PropertyChanged += OnPhotoChanged;
            Raise(nameof(Count)); Raise(nameof(Cover)); Raise(nameof(DisplayLabel));
        };
    }
    public string Id { get => _id; set { _id = value; Raise(); } }
    public string Label { get => _label; set { _label = value; Raise(); Raise(nameof(DisplayLabel)); } }
    public ObservableCollection<PhotoItem> Photos { get; } = new();
    public int Count => Photos.Count;
    public PhotoItem? Cover => Photos.FirstOrDefault();
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Cover?.FileName ?? "空组" : Label;
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>只有封面照片变化需要刷新组封面和默认标签。</summary>
    private void OnPhotoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Cover)) { Raise(nameof(Cover)); Raise(nameof(DisplayLabel)); }
    }
    /// <summary>发布组属性变化，不访问任何具体界面框架。</summary>
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
