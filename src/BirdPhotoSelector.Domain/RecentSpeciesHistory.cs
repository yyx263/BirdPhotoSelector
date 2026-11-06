using System.ComponentModel;

namespace BirdPhotoSelector;

/// <summary>最近选择的十个有效鸟种，按使用时间去重；内容随工程保存，搜索框空输入时展示。</summary>
public sealed class RecentSpeciesHistory : INotifyPropertyChanged
{
    public const int Limit = 10;
    private readonly List<SpeciesIdentity> _items = new();
    public IReadOnlyList<SpeciesIdentity> Items => _items.AsReadOnly();
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>清理身份并移到最近位置，按学名去重，最多保留十条。</summary>
    public void Use(SpeciesIdentity identity)
    {
        var normalized = SpeciesIdentity.Normalize(identity);
        _items.RemoveAll(item => item.Key == normalized.Key);
        _items.Insert(0, normalized);
        if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
        Notify();
    }
    /// <summary>从工程恢复有效且去重的最近记录，保留保存顺序并应用数量上限。</summary>
    public void Replace(IEnumerable<SpeciesIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var replacement = identities.Where(item => item is { IsValid: true })
            .Select(SpeciesIdentity.Normalize).Where(item => unique.Add(item.Key)).Take(Limit).ToArray();
        _items.Clear(); _items.AddRange(replacement); Notify();
    }
    /// <summary>清空本会话最近选择，不修改任何照片结果。</summary>
    public void Clear() { _items.Clear(); Notify(); }
    /// <summary>通知最近列表发生整体替换，供空搜索显示刷新。</summary>
    private void Notify() => PropertyChanged?.Invoke(this, new(nameof(Items)));
}
