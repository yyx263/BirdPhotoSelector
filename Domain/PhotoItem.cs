using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

public enum PhotoRating { Unmarked, Reject, Keep, Favorite }

public sealed class BirdBox
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Confidence { get; set; }

    public bool IsValid => double.IsFinite(X) && double.IsFinite(Y) &&
        double.IsFinite(Width) && double.IsFinite(Height) && double.IsFinite(Confidence) &&
        X >= 0 && Y >= 0 && Width > 0 && Height > 0 && X + Width <= 1.00001 && Y + Height <= 1.00001 &&
        Confidence >= 0 && Confidence <= 1;

    public BirdBox Copy() => new() { X = X, Y = Y, Width = Width, Height = Height, Confidence = Confidence };
}

/// <summary>Cosine similarity is a visual/text embedding score, not a calibrated probability.</summary>
public sealed record SpeciesMatch(string ChineseName, string ScientificName, double Similarity,
    string CandidateScope = "China", string Model = "BioCLIP 2", double? Confidence = null,
    string? AlternativeName = null, double? RunnerUpSimilarity = null, string? ConfidenceMethod = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(ChineseName) ? ScientificName : ChineseName;
    public bool IsValid => !string.IsNullOrWhiteSpace(ScientificName) && double.IsFinite(Similarity) &&
        Similarity is >= -1 and <= 1;
    public bool HasValidConfidence => Confidence is { } score && double.IsFinite(score) && score is >= 0 and <= 1;
}

public sealed class DetectedBird : INotifyPropertyChanged
{
    private SpeciesMatch? _species;
    private int _number;
    private string _recognitionStatus = "待识别";
    public int Number { get => _number; set { if (_number == value) return; _number = value; Notify(); } }
    public bool IsManual { get; init; }
    public BirdBox Box { get; init; } = new();
    public SpeciesMatch? Species
    {
        get => _species;
        set { if (_species == value) return; _species = value; Notify(); Notify(nameof(SpeciesName)); Notify(nameof(ScientificName)); Notify(nameof(SimilarityText)); Notify(nameof(ConfidenceText)); Notify(nameof(EvidenceTooltip)); }
    }
    public string RecognitionStatus
    {
        get => _recognitionStatus;
        set { if (_recognitionStatus == value) return; _recognitionStatus = value; Notify(); Notify(nameof(SpeciesName)); }
    }
    public string SpeciesName => Species?.DisplayName ?? RecognitionStatus;
    public string ScientificName => Species?.ScientificName ?? "";
    public string SimilarityText => Species is { IsValid: true } match ? $"{match.Similarity:0.000}" : "—";
    public string ConfidenceText => Species is { IsValid: true, HasValidConfidence: true } match ? $"{match.Confidence:P1}" : "—";
    public string EvidenceTooltip => Species is { IsValid: true } match
        ? $"原始相似度：{match.Similarity:0.000}\n" +
          (match.HasValidConfidence ? "置信度反映候选区分程度，未经准确率校准。" : "旧结果尚无置信度，请重新识别。") +
          (string.IsNullOrWhiteSpace(match.AlternativeName) ? "" : $"\n相近候选：{match.AlternativeName}（{match.RunnerUpSimilarity:0.000}）")
        : RecognitionStatus;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class PhotoMetadata
{
    public string CapturedAt { get; set; } = "—";
    public string Camera { get; set; } = "—";
    public string FocalLength { get; set; } = "—";
    public string Aperture { get; set; } = "—";
    public string Shutter { get; set; } = "—";
    public string Iso { get; set; } = "—";
    public string ExposureBias { get; set; } = "—";
}

public sealed class PhotoItem : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _filePath = "";
    private PhotoRating _rating;
    private int _selectedBirdIndex;
    public int NextBirdNumber { get; private set; } = 1;
    private string _detectionStatus = "尚未分析";
    private double? _viewZoom, _viewCenterX, _viewCenterY;
    private PhotoMetadata _metadata = new();
    private BitmapSource? _thumbnail;
    private int _pixelWidth, _pixelHeight;

    public string Id { get => _id; set => Set(ref _id, value); }
    public string FilePath
    {
        get => _filePath;
        set { if (Set(ref _filePath, value)) OnPropertyChanged(nameof(FileName)); }
    }
    public string FileName => System.IO.Path.GetFileName(FilePath);
    public PhotoRating Rating { get => _rating; set => Set(ref _rating, value); }
    public ObservableCollection<DetectedBird> Birds { get; } = new();
    public DetectedBird? SelectedBird => _selectedBirdIndex >= 0 && _selectedBirdIndex < Birds.Count ? Birds[_selectedBirdIndex] : null;
    public int SelectedBirdIndex
    {
        get => _selectedBirdIndex;
        set
        {
            int index = Birds.Count == 0 ? 0 : Math.Clamp(value, 0, Birds.Count - 1);
            if (!Set(ref _selectedBirdIndex, index)) return;
            OnPropertyChanged(nameof(SelectedBird)); OnPropertyChanged(nameof(Bird));
        }
    }
    // The single-bird facade keeps the existing detail-view and single-bird project behavior.
    public BirdBox? Bird
    {
        get => SelectedBird?.Box;
        set
        {
            if (ReferenceEquals(Bird, value)) return;
            SetBirds(value is { IsValid: true } ? new[] { new DetectedBird { Box = value.Copy() } } : Array.Empty<DetectedBird>());
        }
    }
    public void SetBirds(IEnumerable<DetectedBird> birds, int selectedIndex = 0, int nextBirdNumber = 1)
    {
        var snapshot = birds.Where(bird => bird.Box.IsValid).Distinct().ToArray();
        var used = new HashSet<int>();
        int next = Math.Max(nextBirdNumber, 1);
        foreach (var bird in snapshot)
            if (bird.Number > 0 && bird.Number < int.MaxValue && used.Add(bird.Number))
                next = Math.Max(next, bird.Number + 1);
        used.Clear();
        foreach (var bird in snapshot)
        {
            if (bird.Number <= 0 || bird.Number == int.MaxValue || !used.Add(bird.Number))
            {
                if (next >= int.MaxValue) throw new InvalidOperationException("选区序号已达到上限。");
                bird.Number = next;
                used.Add(next);
                next++;
            }
        }
        Birds.Clear();
        foreach (var bird in snapshot) Birds.Add(bird);
        NextBirdNumber = Math.Max(next, used.Count == 0 ? 1 : used.Max() + 1);
        _selectedBirdIndex = Birds.Count == 0 ? 0 : Math.Clamp(selectedIndex, 0, Birds.Count - 1);
        NotifyBirds();
    }
    public DetectedBird AddManualRegion(BirdBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        if (!box.IsValid) throw new ArgumentException("选区范围无效。", nameof(box));
        int number = NextBirdNumber;
        if (number >= int.MaxValue) throw new InvalidOperationException("选区序号已达到上限。");
        var bird = new DetectedBird { Number = number, Box = box.Copy(), IsManual = true };
        Birds.Add(bird); NextBirdNumber++;
        _selectedBirdIndex = Birds.Count - 1;
        NotifyBirds();
        return bird;
    }
    public bool RemoveBird(DetectedBird bird)
    {
        int index = Birds.IndexOf(bird);
        if (index < 0) return false;
        var selected = SelectedBird;
        Birds.RemoveAt(index);
        _selectedBirdIndex = selected is not null && selected != bird ? Birds.IndexOf(selected) : Math.Min(index, Birds.Count - 1);
        _selectedBirdIndex = Math.Max(0, _selectedBirdIndex);
        NotifyBirds();
        return true;
    }
    public void ReplaceAutomaticRegions(IEnumerable<BirdBox> boxes)
    {
        var selected = SelectedBird;
        var manual = Birds.Where(bird => bird.IsManual).ToArray();
        var automatic = boxes.Where(box => box.IsValid)
            .Select(box => new DetectedBird { Box = box.Copy() }).ToArray();
        var merged = automatic.Concat(manual).ToArray();
        int selectedIndex = selected is { IsManual: true } ? Array.IndexOf(merged, selected) : 0;
        SetBirds(merged, Math.Max(0, selectedIndex), NextBirdNumber);
    }
    private void NotifyBirds()
    {
        OnPropertyChanged(nameof(Birds)); OnPropertyChanged(nameof(SelectedBirdIndex));
        OnPropertyChanged(nameof(NextBirdNumber));
        OnPropertyChanged(nameof(SelectedBird)); OnPropertyChanged(nameof(Bird));
    }
    public string DetectionStatus { get => _detectionStatus; set => Set(ref _detectionStatus, value); }
    public double? ViewZoom { get => _viewZoom; set => Set(ref _viewZoom, value); }
    public double? ViewCenterX { get => _viewCenterX; set => Set(ref _viewCenterX, value); }
    public double? ViewCenterY { get => _viewCenterY; set => Set(ref _viewCenterY, value); }
    public PhotoMetadata Metadata { get => _metadata; set => Set(ref _metadata, value); }
    public BitmapSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
    public int PixelWidth { get => _pixelWidth; set => Set(ref _pixelWidth, value); }
    public int PixelHeight { get => _pixelHeight; set => Set(ref _pixelHeight, value); }
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
