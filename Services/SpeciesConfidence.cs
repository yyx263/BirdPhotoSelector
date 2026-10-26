namespace BirdPhotoSelector;

/// <summary>
/// Conditional competition within the installed species candidate set, not calibrated accuracy.
/// The pinned official BioCLIP 2 checkpoint stores logit_scale=4.605170249938965.
/// Imageomics applies exp(logit_scale) * normalized image/text similarity then softmax:
/// https://github.com/Imageomics/pybioclip/blob/main/src/bioclip/predict.py#L220-L223
/// It is possible to receive a confident wrong prediction when the true class is absent.
/// </summary>
public static class SpeciesConfidence
{
    public const double LearnedLogitScale = 4.605170249938965;
    public const double LogitScale = 100.00000639508755;
    public const string Method = "BioCLIP2CandidateSoftmax-v1";

    public static double? Calculate(ReadOnlySpan<double> similarities, int bestIndex,
        CancellationToken cancellationToken = default)
    {
        if ((uint)bestIndex >= (uint)similarities.Length) throw new ArgumentOutOfRangeException(nameof(bestIndex));
        double best = similarities[bestIndex];
        if (!double.IsFinite(best)) throw new ArgumentException("相似度必须为有限值。", nameof(similarities));
        // A one-candidate catalog offers no competition and cannot provide meaningful confidence.
        if (similarities.Length < 2) return null;
        double denominator = 0;
        for (int index = 0; index < similarities.Length; index++)
        {
            if ((index & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
            double score = similarities[index];
            if (!double.IsFinite(score) || score > best)
                throw new ArgumentException("相似度无效，或首选不是最高分候选。", nameof(similarities));
            // Subtracting the maximum makes every exponent <= 0 and avoids overflow.
            denominator += Math.Exp(LogitScale * (score - best));
        }
        return Math.Clamp(1 / denominator, 0, 1);
    }
}
