using System.Text;

namespace BirdPhotoSelector;

/// <summary>搜索专用的确定性简繁折叠，使用固定 OpenCC 1.1.9 单字表，不调用系统语言服务、不改写显示名。</summary>
internal static class ChineseSearchFold
{
    private static readonly Lazy<IReadOnlyDictionary<Rune, string>> Mapping = new(Load);
    /// <summary>先处理既有鸟名字形别名，再按 Unicode 标量折叠简体，支持扩展区字符而不拆坏代理对。</summary>
    internal static string Simplify(string value)
    {
        value = value.Replace('鸊', '䴙').Replace('鷿', '䴙').Replace('鷉', '䴘').Replace('鷈', '䴘').Replace('鴷', '䴕');
        var output = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes()) output.Append(Mapping.Value.TryGetValue(rune, out var mapped) ? mapped : rune.ToString());
        return output.ToString();
    }
    /// <summary>加载随程序固定的单字首选转换；不引入词义推断或多选搜索，也不影响原身份。</summary>
    private static IReadOnlyDictionary<Rune, string> Load()
    {
        using var stream = typeof(ChineseSearchFold).Assembly.GetManifestResourceStream("BirdPhotoSelector.TSCharacters.txt")
            ?? throw new InvalidDataException("离线简繁字符表缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var result = new Dictionary<Rune, string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = line.Split('\t');
            if (fields.Length != 2 || fields[0].EnumerateRunes().Count() != 1) throw new InvalidDataException("简繁字符表记录无效。");
            result.Add(fields[0].EnumerateRunes().First(), fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);
        }
        return result;
    }
}
