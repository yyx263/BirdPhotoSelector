using System.Buffers.Binary;
using System.IO;
using System.Text.Json;

namespace BirdPhotoSelector;

/// <summary>Validates the row-aligned local candidate names and BioCLIP 2 text embeddings.</summary>
public sealed class SpeciesCandidateCatalog
{
    public const int EmbeddingDimension = 768;
    private const int MaximumCandidates = 20000;
    private readonly Candidate[] _candidates;
    private readonly float[] _embeddings;
    public int Count => _candidates.Length;
    public string Scope { get; }

    private SpeciesCandidateCatalog(Candidate[] candidates, float[] embeddings, string scope)
    { _candidates = candidates; _embeddings = embeddings; Scope = scope; }

    public static SpeciesCandidateCatalog Load(string directory, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 16 };
        var metadata = JsonSerializer.Deserialize<Metadata>(ReadBoundedJson(Path.Combine(directory, "metadata.json"), 1024 * 1024), options)
            ?? throw new InvalidDataException("鸟种模型元数据为空。");
        if (metadata.Model != "BioCLIP 2" || metadata.Dimension != EmbeddingDimension ||
            metadata.CandidateScope is not ("China" or "Global"))
            throw new InvalidDataException("需要 BioCLIP 2、768 维和明确候选范围的鸟种模型资源。");
        if (metadata.CropPolicy is not null && metadata.CropPolicy != SpeciesImagePreprocessor.CropPolicy ||
            metadata.InputSize is not null and not SpeciesImagePreprocessor.InputSize ||
            metadata.Preprocessing is not null and not "openclip-bicubic-shortest-224-center-crop" ||
            !MatchesNormalization(metadata.Mean, new[] { .48145466, .4578275, .40821073 }) ||
            !MatchesNormalization(metadata.Std, new[] { .26862954, .26130258, .27577711 }))
            throw new InvalidDataException("鸟种模型资源的图像预处理版本不兼容。");
        if (metadata.ConfidenceMethod is not null && metadata.ConfidenceMethod != SpeciesConfidence.Method ||
            metadata.LearnedLogitScale is { } learned && (!double.IsFinite(learned) || Math.Abs(learned - SpeciesConfidence.LearnedLogitScale) > 1e-7) ||
            metadata.ConfidenceLogitScale is { } scale && (!double.IsFinite(scale) || Math.Abs(scale - SpeciesConfidence.LogitScale) > 1e-5))
            throw new InvalidDataException("鸟种模型资源的置信度方法或模型温度不兼容。");
        var candidates = JsonSerializer.Deserialize<Candidate[]>(ReadBoundedJson(Path.Combine(directory, "candidates.json"), 32 * 1024 * 1024), options)
            ?? throw new InvalidDataException("鸟种候选表为空。");
        if (candidates.Length is < 1 or > MaximumCandidates ||
            metadata.CandidateCount is { } expected && expected != candidates.Length)
            throw new InvalidDataException("鸟种候选数量无效或与元数据不一致。");
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.ScientificName) || !unique.Add(candidate.ScientificName.Trim()))
                throw new InvalidDataException("鸟种候选表包含空学名或重复学名。");
            candidate.ScientificName = candidate.ScientificName.Trim();
            candidate.ChineseName = candidate.ChineseName?.Trim() ?? "";
        }
        string embeddingsPath = Path.Combine(directory, "embeddings.f32");
        long expectedBytes = checked((long)candidates.Length * EmbeddingDimension * sizeof(float));
        using var stream = File.OpenRead(embeddingsPath);
        if (stream.Length != expectedBytes) throw new InvalidDataException("鸟种名称与文本向量行数不匹配。");
        var embeddings = new float[checked(candidates.Length * EmbeddingDimension)];
        var row = new byte[EmbeddingDimension * sizeof(float)];
        for (int index = 0; index < candidates.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(row);
            double normSquared = 0;
            int offset = index * EmbeddingDimension;
            for (int dimension = 0; dimension < EmbeddingDimension; dimension++)
            {
                float value = BinaryPrimitives.ReadSingleLittleEndian(row.AsSpan(dimension * sizeof(float), sizeof(float)));
                if (!float.IsFinite(value)) throw new InvalidDataException("鸟种文本向量包含非有限值。");
                embeddings[offset + dimension] = value;
                normSquared += (double)value * value;
            }
            if (normSquared < 1e-20) throw new InvalidDataException("鸟种文本向量不能是零向量。");
            float inverseNorm = (float)(1 / Math.Sqrt(normSquared));
            for (int dimension = 0; dimension < EmbeddingDimension; dimension++) embeddings[offset + dimension] *= inverseNorm;
        }
        return new(candidates, embeddings, metadata.CandidateScope!);
    }

    private static string ReadBoundedJson(string path, long maximumBytes)
    {
        if (new FileInfo(path).Length > maximumBytes) throw new InvalidDataException("鸟种资源 JSON 文件超出合理大小。");
        return File.ReadAllText(path);
    }

    private static bool MatchesNormalization(double[]? actual, double[] expected) => actual is null ||
        actual.Length == expected.Length && actual.Zip(expected).All(pair =>
            double.IsFinite(pair.First) && Math.Abs(pair.First - pair.Second) < 1e-7);

    public SpeciesMatch FindBest(ReadOnlySpan<float> imageEmbedding, bool chinaOnly = true,
        CancellationToken cancellationToken = default)
    {
        if (chinaOnly && Scope != "China")
            throw new InvalidOperationException("当前候选表不是中国鸟类候选库。");
        if (!chinaOnly && Scope != "Global")
            throw new InvalidOperationException("当前仅安装中国鸟类候选库，请启用中国候选范围。");
        if (imageEmbedding.Length != EmbeddingDimension) throw new InvalidDataException("鸟种图像向量必须为 768 维。");
        double normSquared = 0;
        foreach (float value in imageEmbedding)
        {
            if (!float.IsFinite(value)) throw new InvalidDataException("鸟种图像向量包含非有限值。");
            normSquared += (double)value * value;
        }
        if (normSquared < 1e-20) throw new InvalidDataException("鸟种图像向量不能是零向量。");
        double inverseNorm = 1 / Math.Sqrt(normSquared), bestScore = double.NegativeInfinity;
        int best = -1, second = -1;
        var similarities = new double[_candidates.Length];
        for (int row = 0; row < _candidates.Length; row++)
        {
            if ((row & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
            double score = 0;
            int offset = row * EmbeddingDimension;
            for (int dimension = 0; dimension < EmbeddingDimension; dimension++)
                score += imageEmbedding[dimension] * inverseNorm * _embeddings[offset + dimension];
            score = Math.Clamp(score, -1, 1);
            similarities[row] = score;
            if (score > bestScore) { second = best; bestScore = score; best = row; }
            else if (second < 0 || score > similarities[second]) second = row;
        }
        var candidate = _candidates[best];
        string? alternative = second < 0 ? null : string.IsNullOrWhiteSpace(_candidates[second].ChineseName)
            ? _candidates[second].ScientificName
            : $"{_candidates[second].ChineseName}（{_candidates[second].ScientificName}）";
        return new(candidate.ChineseName ?? "", candidate.ScientificName!, bestScore, Scope,
            Confidence: SpeciesConfidence.Calculate(similarities, best, cancellationToken),
            AlternativeName: alternative, RunnerUpSimilarity: second < 0 ? null : similarities[second],
            ConfidenceMethod: second < 0 ? null : SpeciesConfidence.Method);
    }

    private sealed class Metadata
    {
        public Metadata() { }
        public string? Model { get; set; }
        public int Dimension { get; set; }
        public int? CandidateCount { get; set; }
        public string? CandidateScope { get; set; }
        public string? CropPolicy { get; set; }
        public int? InputSize { get; set; }
        public string? Preprocessing { get; set; }
        public double[]? Mean { get; set; }
        public double[]? Std { get; set; }
        public string? ConfidenceMethod { get; set; }
        public double? LearnedLogitScale { get; set; }
        public double? ConfidenceLogitScale { get; set; }
    }
    private sealed class Candidate
    {
        public Candidate() { }
        public string? ChineseName { get; set; }
        public string? ScientificName { get; set; }
    }
}
