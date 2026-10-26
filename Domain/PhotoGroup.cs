using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace BirdPhotoSelector;

public sealed class PhotoGroup : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _label;
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
    private void OnPhotoChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, Cover)) { Raise(nameof(Cover)); Raise(nameof(DisplayLabel)); }
    }
    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
