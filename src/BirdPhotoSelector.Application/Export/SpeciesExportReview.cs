namespace BirdPhotoSelector;

[Flags]
/// <summary>分类导出前的鸟种提醒原因，可同时包含无有效鸟种、未识别选区和低置信度。</summary>
public enum SpeciesExportReviewReason { None = 0, NoSpecies = 1, UnidentifiedRegions = 2, LowConfidence = 4 }

/// <summary>一张待提示照片及其原因，不包含弹窗或导出决定。</summary>
public sealed record SpeciesExportReviewItem(PhotoItem Photo, SpeciesExportReviewReason Reasons)
{
    public string ReasonText => string.Join("、", new[]
    {
        Reasons.HasFlag(SpeciesExportReviewReason.NoSpecies) ? "整张照片没有有效鸟种" : null,
        Reasons.HasFlag(SpeciesExportReviewReason.UnidentifiedRegions) ? "有未识别的选区" : null,
        Reasons.HasFlag(SpeciesExportReviewReason.LowConfidence) ? "有低置信度识别结果" : null
    }.Where(text => text is not null));
}

/// <summary>Read-only review of the selected rating/member scope. Geometry review is a separate concern.</summary>
public static class SpeciesExportReview
{
    public const double DefaultLowConfidenceThreshold = .8;

    /// <summary>按选定格式范围只读检查鸟种提醒，来源坐标待核对本身不构成鸟种导出失败。</summary>
    public static IReadOnlyList<SpeciesExportReviewItem> Review(IEnumerable<PhotoItem> photos,
        PhotoFileScope scope = PhotoFileScope.All, double lowConfidenceThreshold = DefaultLowConfidenceThreshold)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        if (!double.IsFinite(lowConfidenceThreshold) || lowConfidenceThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(lowConfidenceThreshold));
        var result = new List<SpeciesExportReviewItem>();
        foreach (var photo in photos.Distinct())
        {
            if (!photo.Assets.Any(asset => scope == PhotoFileScope.All ||
                (scope == PhotoFileScope.JpgOnly ? asset.Kind == PhotoAssetKind.Jpg : asset.Kind == PhotoAssetKind.Raw))) continue;
            var reasons = SpeciesExportReviewReason.None;
            if (!photo.Birds.Any(bird => bird.EffectiveSpecies is { IsValid: true }))
                reasons |= SpeciesExportReviewReason.NoSpecies;
            if (photo.Birds.Any(bird => bird.HasRegion && bird.EffectiveSpecies is not { IsValid: true }))
                reasons |= SpeciesExportReviewReason.UnidentifiedRegions;
            if (photo.Birds.Any(bird => bird.ManualSpecies is null && bird.EffectiveSpecies is { IsValid: true } &&
                bird.Species is { HasValidConfidence: true } match && match.Confidence < lowConfidenceThreshold))
                reasons |= SpeciesExportReviewReason.LowConfidence;
            if (reasons != SpeciesExportReviewReason.None) result.Add(new(photo, reasons));
        }
        return result.AsReadOnly();
    }
}
