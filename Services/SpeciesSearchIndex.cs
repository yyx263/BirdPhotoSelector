using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BirdPhotoSelector;

/// <summary>Offline search keys; the original identities and display names are never rewritten.</summary>
public sealed class SpeciesSearchIndex
{
    private static readonly Lazy<IReadOnlyDictionary<string, PinyinEntry>> Pinyin = new(LoadPinyin);
    private readonly SearchEntry[] _entries;
    private readonly IReadOnlyList<SpeciesIdentity> _all;

    public SpeciesSearchIndex(IEnumerable<SpeciesIdentity> species)
    {
        ArgumentNullException.ThrowIfNull(species);
        _entries = species.Where(item => item is { IsValid: true }).Select(item =>
        {
            string chinese = NormalizeText(item.ChineseName ?? "");
            Pinyin.Value.TryGetValue(chinese, out var pronunciation);
            return new SearchEntry(item, chinese, NormalizeText(item.ScientificName),
                pronunciation?.FullPinyin ?? "", pronunciation?.Initials ?? "");
        }).ToArray();
        _all = Array.AsReadOnly(_entries.Select(item => item.Identity).ToArray());
    }

    public IReadOnlyList<SpeciesIdentity> Search(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _all;
        string text = NormalizeText(query.Trim());
        string pinyin = NormalizePinyin(query);
        return _entries.Select(entry => (Entry: entry, Rank: MatchRank(entry, text, pinyin)))
            .Where(match => match.Rank >= 0).OrderBy(match => match.Rank)
            .ThenBy(match => match.Entry.Identity.ScientificName, StringComparer.OrdinalIgnoreCase)
            .Select(match => match.Entry.Identity).ToArray();
    }

    private static int MatchRank(SearchEntry entry, string text, string pinyin)
    {
        if (entry.Chinese == text) return 0;
        if (entry.Scientific == text) return 1;
        if (pinyin.Length > 0 && entry.FullPinyin == pinyin) return 2;
        if (pinyin.Length > 0 && entry.Initials == pinyin) return 3;
        if (entry.Chinese.Contains(text, StringComparison.Ordinal)) return 4;
        if (entry.Scientific.Contains(text, StringComparison.Ordinal)) return 5;
        if (pinyin.Length == 0) return -1;
        if (entry.FullPinyin.StartsWith(pinyin, StringComparison.Ordinal)) return 6;
        if (entry.Initials.StartsWith(pinyin, StringComparison.Ordinal)) return 7;
        if (entry.FullPinyin.Contains(pinyin, StringComparison.Ordinal)) return 8;
        if (entry.Initials.Contains(pinyin, StringComparison.Ordinal)) return 9;
        return -1;
    }

    private static IReadOnlyDictionary<string, PinyinEntry> LoadPinyin()
    {
        using var stream = typeof(SpeciesSearchIndex).Assembly.GetManifestResourceStream("BirdPhotoSelector.SpeciesSearchData.json")
            ?? throw new InvalidDataException("离线鸟种搜索索引缺失，请重新安装完整程序。");
        var data = JsonSerializer.Deserialize<SearchData>(stream)
            ?? throw new InvalidDataException("离线鸟种搜索索引为空。");
        if (data.Entries.Length == 0) throw new InvalidDataException("离线鸟种搜索索引没有记录。");
        var result = new Dictionary<string, PinyinEntry>(StringComparer.Ordinal);
        foreach (var entry in data.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || string.IsNullOrWhiteSpace(entry.FullPinyin) ||
                string.IsNullOrWhiteSpace(entry.Initials)) throw new InvalidDataException("离线鸟种搜索索引包含无效记录。");
            result.TryAdd(NormalizeText(entry.Name), entry);
        }
        return result;
    }

    private static string NormalizeText(string value)
    {
        string text = value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant();
        if (text.Length == 0) return text;
        // Windows NLS folds mixed simplified/traditional input character by character.
        const uint simplifiedChinese = 0x02000000;
        int count = LCMapStringEx("zh-CN", simplifiedChinese, text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (count == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new char[count];
        int written = LCMapStringEx("zh-CN", simplifiedChinese, text, text.Length, buffer, count,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (written == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Rare bird-name variants are not consistently covered by Windows language tables.
        return new string(buffer, 0, written).Replace('鸊', '䴙').Replace('鷿', '䴙')
            .Replace('鷉', '䴘').Replace('鷈', '䴘').Replace('鴷', '䴕');
    }

    private static string NormalizePinyin(string value)
    {
        string text = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
            .Replace("u:", "v", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (c == '\u0308' && result.Length > 0 && result[^1] == 'u') { result[^1] = 'v'; continue; }
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'a' and <= 'z') { result.Append(c); continue; }
            if (c is >= '1' and <= '5' || char.IsWhiteSpace(c) || c is '-' or '\'' or '’') continue;
            return ""; // Chinese, digits outside tone markers, and punctuation are not empty pinyin queries.
        }
        return result.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern int LCMapStringEx(string locale, uint flags, string source, int sourceLength,
        [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.U2)] char[]? destination,
        int destinationLength, IntPtr version, IntPtr reserved, IntPtr sortHandle);

    private sealed record SearchEntry(SpeciesIdentity Identity, string Chinese, string Scientific, string FullPinyin, string Initials);
    private sealed class SearchData { public PinyinEntry[] Entries { get; set; } = []; }
    private sealed class PinyinEntry
    {
        public string Name { get; set; } = "";
        public string FullPinyin { get; set; } = "";
        public string Initials { get; set; } = "";
    }
}
