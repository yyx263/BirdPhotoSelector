using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace BirdPhotoSelector;

/// <summary>Small offline name index, including taxa excluded from the model's candidate vectors.</summary>
public sealed class SpeciesNameCatalog
{
    private readonly SpeciesIdentity[] _records;
    private readonly SpeciesSearchIndex _search;
    private SpeciesNameCatalog(SpeciesIdentity[] records)
    {
        _records = records;
        _search = new(records);
    }
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

    public IReadOnlyList<SpeciesIdentity> Search(string? query) => _search.Search(query);

    /// <summary>名称文件和人工编辑共用领域归一化规则，检索索引不参与业务数据修改。</summary>
    internal static SpeciesIdentity Normalize(SpeciesIdentity identity) => SpeciesIdentity.Normalize(identity);
}
