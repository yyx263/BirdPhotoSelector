using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace BirdPhotoSelector;

/// <summary>Small offline name index, including taxa excluded from the model's candidate vectors.</summary>
public sealed class SpeciesNameCatalog
{
    private readonly SpeciesIdentity[] _records;
    private SpeciesNameCatalog(SpeciesIdentity[] records) => _records = records;
    public IReadOnlyList<SpeciesIdentity> Records => Array.AsReadOnly(_records);
    public IReadOnlyList<SpeciesIdentity> Species => Records;

    public static SpeciesNameCatalog Load(string? directory = null)
    {
        directory ??= Path.Combine(AppContext.BaseDirectory, "models", "bioclip2");
        try
        {
            var records = new Dictionary<string, SpeciesIdentity>(StringComparer.Ordinal);
            foreach (string name in new[] { "candidates.json", "china-unmatched.json" })
            {
                string path = Path.Combine(directory, name);
                var file = new FileInfo(path);
                if (!file.Exists) throw new FileNotFoundException("鸟种名称文件缺失。", path);
                if (file.Length > 32 * 1024 * 1024) throw new InvalidDataException("鸟种名称文件大小异常。");
                var items = JsonSerializer.Deserialize<SpeciesIdentity[]>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 16 })
                    ?? throw new InvalidDataException("鸟种名称文件为空。");
                foreach (var identity in items)
                {
                    if (identity is null || !identity.IsValid) throw new InvalidDataException("鸟种名称包含无效学名。");
                    var normalized = Normalize(identity);
                    records.TryAdd(normalized.Key, normalized);
                }
                if (records.Count > 50000) throw new InvalidDataException("鸟种名称数量异常。");
            }
            if (records.Count == 0) throw new InvalidDataException("没有可用鸟种名称。");
            return new(records.Values.OrderBy(item => item.ScientificName, StringComparer.OrdinalIgnoreCase).ToArray());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or ArgumentException)
        {
            throw new IOException("鸟种名称数据暂不可用。请恢复 models/bioclip2 中的 candidates.json 和 china-unmatched.json 后重试，无需加载识别模型。", ex);
        }
    }

    public IReadOnlyList<SpeciesIdentity> Search(string? query)
    {
        string text = query?.Trim() ?? "";
        if (text.Length == 0) return Records;
        return _records.Where(item => item.ChineseName.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                item.ScientificName.Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => string.Equals(item.ChineseName, text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.ScientificName, text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(item => item.ScientificName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static SpeciesIdentity Normalize(SpeciesIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.IsValid) throw new ArgumentException("鸟种必须包含学名。", nameof(identity));
        return new(identity.ChineseName?.Trim() ?? "", identity.ScientificName.Trim(),
            string.IsNullOrWhiteSpace(identity.SourceTaxonId) ? null : identity.SourceTaxonId.Trim());
    }
}

public sealed class RecentSpeciesHistory : INotifyPropertyChanged
{
    public const int Limit = 10;
    private readonly List<SpeciesIdentity> _items = new();
    public IReadOnlyList<SpeciesIdentity> Items => _items.AsReadOnly();
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Use(SpeciesIdentity identity)
    {
        var normalized = SpeciesNameCatalog.Normalize(identity);
        _items.RemoveAll(item => item.Key == normalized.Key);
        _items.Insert(0, normalized);
        if (_items.Count > Limit) _items.RemoveRange(Limit, _items.Count - Limit);
        Notify();
    }
    public void Replace(IEnumerable<SpeciesIdentity> identities)
    {
        ArgumentNullException.ThrowIfNull(identities);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var replacement = identities.Where(item => item is { IsValid: true })
            .Select(SpeciesNameCatalog.Normalize).Where(item => unique.Add(item.Key)).Take(Limit).ToArray();
        _items.Clear(); _items.AddRange(replacement); Notify();
    }
    public void Clear() { _items.Clear(); Notify(); }
    private void Notify() => PropertyChanged?.Invoke(this, new(nameof(Items)));
}
