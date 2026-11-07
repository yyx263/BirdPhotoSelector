namespace BirdPhotoSelector;

/// <summary>A taxon identity without an inferred score or a bounding box.</summary>
public sealed record SpeciesIdentity(string ChineseName, string ScientificName, string? SourceTaxonId = null)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(ScientificName);
    public string DisplayName => string.IsNullOrWhiteSpace(ChineseName) ? ScientificName?.Trim() ?? "" : ChineseName.Trim();
    public string Key => ScientificName?.Trim().ToUpperInvariant() ?? "";
    /// <summary>仅从有效自动识别结果提取无分数的鸟种身份，供人工选择和导出分类使用。</summary>
    public static SpeciesIdentity? FromMatch(SpeciesMatch? match) => match is { IsValid: true }
        ? new(match.ChineseName?.Trim() ?? "", match.ScientificName.Trim()) : null;
    /// <summary>清理人工身份的首尾空白并验证学名，不改写原始语言、分类 ID 或推断任何分数。</summary>
    public static SpeciesIdentity Normalize(SpeciesIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!identity.IsValid) throw new ArgumentException("鸟种必须包含学名。", nameof(identity));
        return new(identity.ChineseName?.Trim() ?? "", identity.ScientificName.Trim(),
            string.IsNullOrWhiteSpace(identity.SourceTaxonId) ? null : identity.SourceTaxonId.Trim());
    }
}
