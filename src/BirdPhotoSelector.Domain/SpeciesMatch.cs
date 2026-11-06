using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Globalization;
using System.Runtime.CompilerServices;
using BirdPhotoSelector.Imaging;

namespace BirdPhotoSelector;

/// <remarks>Cosine similarity is a visual/text embedding score, not a calibrated probability.</remarks>
/// <summary>自动识别证据：保留原始余弦相似度和候选竞争置信度，后者未经准确率校准；人工鸟种不使用此类型伪造分数。</summary>
public sealed record SpeciesMatch(string ChineseName, string ScientificName, double Similarity,
    string CandidateScope = "China", string Model = "BioCLIP 2", double? Confidence = null,
    string? AlternativeName = null, double? RunnerUpSimilarity = null, string? ConfidenceMethod = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ChineseName) ? ScientificName : ChineseName;
    public bool IsValid => !string.IsNullOrWhiteSpace(ScientificName) && double.IsFinite(Similarity) &&
        Similarity is >= -1 and <= 1;
    public bool HasValidConfidence => Confidence is { } score && double.IsFinite(score) && score is >= 0 and <= 1;
}
