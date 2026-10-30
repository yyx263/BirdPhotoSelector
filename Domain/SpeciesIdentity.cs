namespace BirdPhotoSelector;

/// <summary>A taxon identity without an inferred score or a bounding box.</summary>
public sealed record SpeciesIdentity(string ChineseName, string ScientificName, string? SourceTaxonId = null)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(ScientificName);
    public string DisplayName => string.IsNullOrWhiteSpace(ChineseName) ? ScientificName?.Trim() ?? "" : ChineseName.Trim();
    public string Key => ScientificName?.Trim().ToUpperInvariant() ?? "";
    public static SpeciesIdentity? FromMatch(SpeciesMatch? match) => match is { IsValid: true }
        ? new(match.ChineseName?.Trim() ?? "", match.ScientificName.Trim()) : null;
}
