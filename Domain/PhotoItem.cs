using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Globalization;
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
    private SpeciesIdentity? _manualSpecies;
    private int _number;
    private int _displayNumber;
    private string _recognitionStatus = "待识别";
    // Number remains the persisted identity used to match asynchronous results.
    public int Number { get => _number; set { if (_number == value) return; _number = value; Notify(); } }
    public int DisplayNumber
    {
        get => _displayNumber;
        internal set { if (_displayNumber == value) return; _displayNumber = value; Notify(); Notify(nameof(DisplayNumberText)); }
    }
    public bool IsManual { get; init; }
    public BirdBox Box { get; init; } = new();
    public SpeciesIdentity? ManualSpecies
    {
        get => _manualSpecies;
        set
        {
            if (value is not null && !value.IsValid) throw new ArgumentException("人工鸟种必须包含有效学名。", nameof(value));
            if (_manualSpecies == value) return;
            _manualSpecies = value;
            if (value is not null) _species = null;
            Notify(); Notify(nameof(Species)); Notify(nameof(HasRegion)); Notify(nameof(DisplayNumberText)); NotifySpecies();
        }
    }
    public bool HasRegion => ManualSpecies is null && Box.IsValid;
    public string DisplayNumberText => HasRegion ? $"{DisplayNumber:00}" : "手动";
    public SpeciesIdentity? EffectiveSpecies => ManualSpecies ?? SpeciesIdentity.FromMatch(Species);
    public DetectedBird Copy() => new()
    {
        Number = Number, IsManual = IsManual, Box = Box.Copy(), Species = ManualSpecies is null ? Species : null,
        ManualSpecies = ManualSpecies, RecognitionStatus = RecognitionStatus
    };
    public SpeciesMatch? Species
    {
        get => _species;
        set { if (_species == value) return; _species = value; Notify(); NotifySpecies(); }
    }
    public string RecognitionStatus
    {
        get => _recognitionStatus;
        set { if (_recognitionStatus == value) return; _recognitionStatus = value; Notify(); Notify(nameof(SpeciesName)); }
    }
    public string SpeciesName => EffectiveSpecies?.DisplayName ?? RecognitionStatus;
    public string ScientificName => EffectiveSpecies?.ScientificName ?? "";
    public string SimilarityText => ManualSpecies is null && Species is { IsValid: true } match ? $"{match.Similarity:0.000}" : "—";
    public string ConfidenceText => ManualSpecies is null && Species is { IsValid: true, HasValidConfidence: true } match ? $"{match.Confidence:P1}" : "—";
    public string EvidenceTooltip => ManualSpecies is not null ? "人工指定鸟种，无识别框及 AI 分数。" : Species is { IsValid: true } match
        ? $"原始相似度：{match.Similarity:0.000}\n" +
          (match.HasValidConfidence ? "置信度反映候选区分程度，未经准确率校准。" : "旧结果尚无置信度，请重新识别。") +
          (string.IsNullOrWhiteSpace(match.AlternativeName) ? "" : $"\n相近候选：{match.AlternativeName}（{match.RunnerUpSimilarity:0.000}）")
        : RecognitionStatus;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void NotifySpecies()
    {
        Notify(nameof(EffectiveSpecies)); Notify(nameof(SpeciesName)); Notify(nameof(ScientificName));
        Notify(nameof(SimilarityText)); Notify(nameof(ConfidenceText)); Notify(nameof(EvidenceTooltip));
    }
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
    private readonly ObservableCollection<PhotoAsset> _assets = new();
    private string? _preferredPreviewAssetId, _activePreviewAssetId;
    private bool _explicitPreferredPreview, _explicitActivePreview;
    private PhotoGeometryReference? _geometryReference;
    private PhotoGeometryStatus _geometryStatus;
    private PhotoRating _rating;
    private bool _hasManualSpeciesEdits;
    private int _selectedBirdIndex;
    private DetectedBird? _selectedBirdReference;
    private readonly HashSet<DetectedBird> _observedBirds = new();
    private bool _updatingBirds, _sortingBirds;
    private static readonly StringComparer ManualNameComparer = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);
    public int NextBirdNumber { get; private set; } = 1;
    private string _detectionStatus = "尚未分析";
    private double? _viewZoom, _viewCenterX, _viewCenterY;
    private PhotoMetadata _metadata = new();
    private BitmapSource? _thumbnail;
    private int _pixelWidth, _pixelHeight;

    public PhotoItem()
    {
        Assets = new(_assets);
        Birds = new OrderedBirdCollection(this);
        Birds.CollectionChanged += OnBirdCollectionChanged;
    }

    public string Id { get => _id; set => Set(ref _id, value); }
    public ReadOnlyObservableCollection<PhotoAsset> Assets { get; }
    public string? PreferredPreviewAssetId => _preferredPreviewAssetId;
    public string? ActivePreviewAssetId => _activePreviewAssetId;
    public PhotoAsset? PreferredPreviewAsset => _assets.FirstOrDefault(asset => asset.Id == _preferredPreviewAssetId);
    public PhotoAsset? ActivePreviewAsset => _assets.FirstOrDefault(asset => asset.Id == _activePreviewAssetId);
    public string FormatLabel => _assets.Count == 2 ? "JPG + RAW" : _assets.FirstOrDefault()?.Kind switch
    { PhotoAssetKind.Jpg => "JPG", PhotoAssetKind.Raw => "RAW", _ => "" };
    public string CompactFormatLabel => _assets.Count == 2 ? "J+R" : FormatLabel;
    public PhotoGeometryReference? GeometryReference => _geometryReference;
    public PhotoGeometryStatus GeometryStatus => _geometryStatus;
    public bool CanUseBirdGeometry => _geometryStatus == PhotoGeometryStatus.Current && ReferenceMatchesActive(_geometryReference);

    // Backward-compatible single-file facade; strings such as relative JPG fixtures remain unchanged.
    public string FilePath
    {
        get => ActivePreviewAsset?.FilePath ?? "";
        set
        {
            if (FilePath == value) return;
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (!PhotoAsset.TryGetKind(value, out var kind)) throw new ArgumentException("不支持的照片成员格式。", nameof(value));
            string canonical = PhotoAsset.Canonicalize(value);
            var existing = _assets.FirstOrDefault(asset => StringComparer.OrdinalIgnoreCase.Equals(asset.CanonicalPath, canonical));
            if (existing is not null)
            {
                var updated = new PhotoAsset(value, kind, existing.Id);
                SetAssetsCore(_assets.Select(asset => asset.Id == existing.Id ? updated : asset).ToArray(),
                    _preferredPreviewAssetId, existing.Id, _explicitPreferredPreview, true);
            }
            else
            {
                if (_assets.Count > 1) throw new InvalidOperationException("多成员照片请显式添加或替换成员，不能通过 FilePath 丢弃其他原件。");
                SetAssets(new[] { new PhotoAsset(value, kind) });
            }
        }
    }
    public string FileName => System.IO.Path.GetFileName(FilePath);

    public void SetAssets(IEnumerable<PhotoAsset> assets, string? preferredPreviewAssetId = null, string? activePreviewAssetId = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        SetAssetsCore(assets.ToArray(), preferredPreviewAssetId, activePreviewAssetId,
            preferredPreviewAssetId is not null, activePreviewAssetId is not null);
    }

    public void AddAsset(PhotoAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        SetAssetsCore(_assets.Append(asset).ToArray(), _explicitPreferredPreview ? _preferredPreviewAssetId : null,
            _explicitActivePreview ? _activePreviewAssetId : null, _explicitPreferredPreview, _explicitActivePreview);
    }

    public bool RemoveAsset(string assetId)
    {
        var remaining = _assets.Where(asset => asset.Id != assetId).ToArray();
        if (remaining.Length == _assets.Count) return false;
        bool preferredRemains = remaining.Any(asset => asset.Id == _preferredPreviewAssetId);
        bool activeRemains = remaining.Any(asset => asset.Id == _activePreviewAssetId);
        SetAssetsCore(remaining, preferredRemains ? _preferredPreviewAssetId : null,
            activeRemains ? _activePreviewAssetId : null,
            preferredRemains && _explicitPreferredPreview, activeRemains && _explicitActivePreview);
        return true;
    }

    public void SetPreferredPreviewAsset(string assetId)
    {
        if (!_assets.Any(asset => asset.Id == assetId)) throw new ArgumentException("首选预览成员不属于该照片。", nameof(assetId));
        SetAssetsCore(_assets.ToArray(), assetId, null, true, false);
    }

    public void SetActivePreviewAsset(string assetId)
    {
        if (!_assets.Any(asset => asset.Id == assetId)) throw new ArgumentException("当前预览成员不属于该照片。", nameof(assetId));
        SetAssetsCore(_assets.ToArray(), _preferredPreviewAssetId, assetId, _explicitPreferredPreview, true);
    }

    public void SetGeometryReference(PhotoGeometryReference? reference, PhotoGeometryStatus status)
    {
        if (!Enum.IsDefined(status)) throw new ArgumentOutOfRangeException(nameof(status));
        if (Birds.Any(bird => bird.HasRegion) && (status != PhotoGeometryStatus.Current || !ReferenceMatchesActive(reference)))
            status = PhotoGeometryStatus.NeedsReview;
        else if (status == PhotoGeometryStatus.Current && !ReferenceMatchesActive(reference))
            status = Birds.Any(bird => bird.HasRegion) || reference is not null ? PhotoGeometryStatus.NeedsReview : PhotoGeometryStatus.Unspecified;
        _geometryReference = reference; _geometryStatus = status;
        NotifyGeometry();
    }

    private void SetAssetsCore(PhotoAsset[] snapshot, string? preferredId, string? activeId,
        bool explicitPreferred, bool explicitActive)
    {
        // Validate every candidate before touching collections, preview state or recognition evidence.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kinds = new HashSet<PhotoAssetKind>();
        string? pairKey = null;
        foreach (var asset in snapshot)
        {
            if (asset is null) throw new ArgumentException("照片成员不能为空。", nameof(snapshot));
            if (!ids.Add(asset.Id) || !paths.Add(asset.CanonicalPath) || !kinds.Add(asset.Kind))
                throw new ArgumentException("同一张照片不能包含重复的成员 ID、路径或格式候选。", nameof(snapshot));
            if (pairKey is not null && !StringComparer.OrdinalIgnoreCase.Equals(pairKey, asset.PairKey))
                throw new ArgumentException("仅允许同目录、同主体文件名的 JPG 与 RAW 配对。", nameof(snapshot));
            pairKey = asset.PairKey;
        }
        if (preferredId is not null && !ids.Contains(preferredId)) throw new ArgumentException("首选预览成员无效。", nameof(preferredId));
        if (activeId is not null && !ids.Contains(activeId)) throw new ArgumentException("当前预览成员无效。", nameof(activeId));
        var preferred = snapshot.FirstOrDefault(asset => asset.Id == preferredId) ??
            snapshot.FirstOrDefault(asset => asset.Kind == PhotoAssetKind.Jpg) ?? snapshot.FirstOrDefault();
        var active = snapshot.FirstOrDefault(asset => asset.Id == activeId) ?? ChooseAvailable(snapshot, preferred);
        var old = ActivePreviewAsset;
        bool sourceChanged = old?.Id != active?.Id ||
            !StringComparer.OrdinalIgnoreCase.Equals(old?.CanonicalPath, active?.CanonicalPath);
        var priorReference = _geometryReference;
        if (sourceChanged && priorReference is null && old is not null && Birds.Any(bird => bird.HasRegion) &&
            _geometryStatus != PhotoGeometryStatus.NeedsReview)
            priorReference = PhotoGeometryReference.Capture(old, PixelWidth, PixelHeight);
        bool invalidatePreview = sourceChanged && (old is not null || priorReference is not null);
        if (sourceChanged)
        {
            _geometryReference = priorReference;
            if (priorReference is not null || Birds.Any(bird => bird.HasRegion)) _geometryStatus = PhotoGeometryStatus.NeedsReview;
            if (invalidatePreview)
            {
                _thumbnail = null; _viewZoom = _viewCenterX = _viewCenterY = null;
                _pixelWidth = _pixelHeight = 0;
            }
            // Old initializers may assign Bird before the first FilePath; no old source existed.
            if (old is null && priorReference is null && active is not null && Birds.Any(bird => bird.HasRegion))
            {
                _geometryReference = PhotoGeometryReference.Capture(active, PixelWidth, PixelHeight);
                _geometryStatus = PhotoGeometryStatus.Current;
            }
        }
        _preferredPreviewAssetId = preferred?.Id; _activePreviewAssetId = active?.Id;
        _explicitPreferredPreview = explicitPreferred; _explicitActivePreview = explicitActive;
        _assets.Clear();
        foreach (var asset in snapshot) _assets.Add(asset);
        if (sourceChanged)
        {
            if (invalidatePreview)
            {
                OnPropertyChanged(nameof(Thumbnail)); OnPropertyChanged(nameof(ViewZoom));
                OnPropertyChanged(nameof(ViewCenterX)); OnPropertyChanged(nameof(ViewCenterY));
                OnPropertyChanged(nameof(PixelWidth)); OnPropertyChanged(nameof(PixelHeight));
            }
            NotifyGeometry();
        }
        NotifyAssets();
    }

    private static PhotoAsset? ChooseAvailable(PhotoAsset[] assets, PhotoAsset? preferred)
    {
        if (preferred is not null && File.Exists(preferred.CanonicalPath)) return preferred;
        return assets.FirstOrDefault(asset => asset.Kind == PhotoAssetKind.Jpg && File.Exists(asset.CanonicalPath)) ??
            assets.FirstOrDefault(asset => File.Exists(asset.CanonicalPath)) ?? preferred ?? assets.FirstOrDefault();
    }

    private bool ReferenceMatchesActive(PhotoGeometryReference? reference) => reference is not null &&
        ActivePreviewAsset is { } active && active.Id == reference.AssetId &&
        StringComparer.OrdinalIgnoreCase.Equals(active.CanonicalPath, reference.CanonicalPath);

    private void NotifyAssets()
    {
        OnPropertyChanged(nameof(Assets)); OnPropertyChanged(nameof(PreferredPreviewAssetId)); OnPropertyChanged(nameof(ActivePreviewAssetId));
        OnPropertyChanged(nameof(PreferredPreviewAsset)); OnPropertyChanged(nameof(ActivePreviewAsset));
        OnPropertyChanged(nameof(FilePath)); OnPropertyChanged(nameof(FileName)); OnPropertyChanged(nameof(FormatLabel));
        OnPropertyChanged(nameof(CompactFormatLabel));
        OnPropertyChanged(nameof(CanUseBirdGeometry));
    }

    private void NotifyGeometry()
    {
        OnPropertyChanged(nameof(GeometryReference)); OnPropertyChanged(nameof(GeometryStatus));
        OnPropertyChanged(nameof(CanUseBirdGeometry)); OnPropertyChanged(nameof(Bird));
    }
    public PhotoRating Rating { get => _rating; set => Set(ref _rating, value); }
    public bool HasManualSpeciesEdits
    {
        get => Birds.Count > 0 && _hasManualSpeciesEdits;
        set => Set(ref _hasManualSpeciesEdits, value && Birds.Count > 0);
    }
    public ObservableCollection<DetectedBird> Birds { get; }
    public DetectedBird? SelectedBird => _selectedBirdIndex >= 0 && _selectedBirdIndex < Birds.Count ? Birds[_selectedBirdIndex] : null;
    public int SelectedBirdIndex
    {
        get => _selectedBirdIndex;
        set
        {
            int index = Birds.Count == 0 ? 0 : Math.Clamp(value, 0, Birds.Count - 1);
            if (!Set(ref _selectedBirdIndex, index)) return;
            _selectedBirdReference = SelectedBird;
            OnPropertyChanged(nameof(SelectedBird)); OnPropertyChanged(nameof(Bird));
        }
    }
    // The single-bird facade keeps the existing detail-view and single-bird project behavior.
    public BirdBox? Bird
    {
        get => _geometryStatus == PhotoGeometryStatus.NeedsReview || SelectedBird is not { HasRegion: true } bird ? null : bird.Box;
        set
        {
            if (_geometryStatus != PhotoGeometryStatus.NeedsReview && ReferenceEquals(SelectedBird?.Box, value)) return;
            SetBirds(value is { IsValid: true } ? new[] { new DetectedBird { Box = value.Copy() } } : Array.Empty<DetectedBird>());
        }
    }
    public void SetBirds(IEnumerable<DetectedBird> birds, int selectedIndex = 0, int nextBirdNumber = 1)
    {
        var snapshot = birds.Where(bird => bird.HasRegion || bird.ManualSpecies is { IsValid: true }).Distinct().ToArray();
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
        var selected = snapshot.Length == 0 ? null : snapshot[Math.Clamp(selectedIndex, 0, snapshot.Length - 1)];
        snapshot = OrderedResults(snapshot);
        _updatingBirds = true;
        try
        {
            Birds.Clear();
            foreach (var bird in snapshot) Birds.Add(bird);
        }
        finally { _updatingBirds = false; }
        NextBirdNumber = Math.Max(next, used.Count == 0 ? 1 : used.Max() + 1);
        _selectedBirdIndex = selected is null ? 0 : Math.Max(0, Birds.IndexOf(selected));
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
        HasManualSpeciesEdits = true;
        _selectedBirdIndex = Birds.IndexOf(bird);
        NotifyBirds();
        return bird;
    }
    public bool RemoveBird(DetectedBird bird)
    {
        int index = Birds.IndexOf(bird);
        if (index < 0) return false;
        HasManualSpeciesEdits = true;
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
        var manual = Birds.Where(bird => bird.IsManual || bird.ManualSpecies is not null).ToArray();
        var automatic = boxes.Where(box => box.IsValid)
            .Select(box => new DetectedBird { Box = box.Copy() }).ToArray();
        var merged = automatic.Concat(manual).ToArray();
        int selectedIndex = selected is { IsManual: true } ? Array.IndexOf(merged, selected) : 0;
        SetBirds(merged, Math.Max(0, selectedIndex), NextBirdNumber);
    }
    private void NotifyBirds()
    {
        _selectedBirdReference = SelectedBird;
        NormalizeResultOrder();
        if (Birds.Count == 0) ResetEmptyRecognition();
        RefreshDisplayNumbers();
        if (!Birds.Any(bird => bird.HasRegion) && _geometryStatus != PhotoGeometryStatus.NeedsReview)
            SetGeometryReference(null, PhotoGeometryStatus.Unspecified);
        else if (Birds.Any(bird => bird.HasRegion) && _geometryReference is null && _geometryStatus != PhotoGeometryStatus.NeedsReview && ActivePreviewAsset is { } active)
            SetGeometryReference(PhotoGeometryReference.Capture(active, PixelWidth, PixelHeight), PhotoGeometryStatus.Current);
        else if (_geometryReference is not null && (_geometryStatus != PhotoGeometryStatus.Current || !ReferenceMatchesActive(_geometryReference)))
            SetGeometryReference(_geometryReference, PhotoGeometryStatus.NeedsReview);
        OnPropertyChanged(nameof(Birds)); OnPropertyChanged(nameof(SelectedBirdIndex));
        OnPropertyChanged(nameof(NextBirdNumber));
        OnPropertyChanged(nameof(SelectedBird)); OnPropertyChanged(nameof(Bird));
    }
    private void RefreshDisplayNumbers()
    {
        int number = 0;
        foreach (var bird in Birds) bird.DisplayNumber = bird.HasRegion ? ++number : 0;
    }
    private void OnBirdCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var bird in _observedBirds.Where(bird => !Birds.Contains(bird)).ToArray())
        { bird.PropertyChanged -= OnBirdPropertyChanged; _observedBirds.Remove(bird); }
        foreach (var bird in Birds)
            if (_observedBirds.Add(bird)) bird.PropertyChanged += OnBirdPropertyChanged;
        if (_updatingBirds) return;
        // Reordering display rows must not silently select a different bird.
        var selected = _selectedBirdReference;
        int retained = selected is null ? -1 : Birds.IndexOf(selected);
        int index = retained >= 0 ? retained : Birds.Count == 0 ? 0 : Math.Clamp(_selectedBirdIndex, 0, Birds.Count - 1);
        var current = Birds.Count == 0 ? null : Birds[index];
        bool selectionChanged = index != _selectedBirdIndex || !ReferenceEquals(selected, current);
        _selectedBirdIndex = index;
        _selectedBirdReference = current;
        RefreshDisplayNumbers();
        if (selectionChanged)
        {
            OnPropertyChanged(nameof(SelectedBirdIndex)); OnPropertyChanged(nameof(SelectedBird)); OnPropertyChanged(nameof(Bird));
        }
    }
    private void OnBirdPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DetectedBird.ManualSpecies)) return;
        HasManualSpeciesEdits = true;
        NotifyBirds();
    }
    private static DetectedBird[] OrderedResults(IEnumerable<DetectedBird> birds) => birds
        .OrderBy(bird => bird.HasRegion ? 0 : 1)
        .ThenBy(bird => bird.HasRegion ? "" : bird.EffectiveSpecies?.DisplayName ?? bird.SpeciesName, ManualNameComparer)
        .ThenBy(bird => bird.HasRegion ? "" : bird.ScientificName, StringComparer.OrdinalIgnoreCase).ToArray();

    private void NormalizeResultOrder()
    {
        if (_updatingBirds || _sortingBirds) return;
        var selected = _selectedBirdReference ?? SelectedBird;
        var ordered = OrderedResults(Birds);
        _sortingBirds = true;
        try
        {
            // Move runs after the outer collection event has finished; WPF and other
            // subscribers can retain the exact selected object without Reset/replacement.
            for (int index = 0; index < ordered.Length; index++)
                if (!ReferenceEquals(Birds[index], ordered[index])) Birds.Move(Birds.IndexOf(ordered[index]), index);
        }
        finally { _sortingBirds = false; }
        if (selected is not null && Birds.Contains(selected))
        {
            _selectedBirdIndex = Birds.IndexOf(selected);
            _selectedBirdReference = selected;
        }
        if (Birds.Count == 0) ResetEmptyRecognition();
    }

    private void ResetEmptyRecognition()
    {
        HasManualSpeciesEdits = false;
        DetectionStatus = "待识别";
        if (_geometryReference is not null || _geometryStatus != PhotoGeometryStatus.Unspecified)
            SetGeometryReference(null, PhotoGeometryStatus.Unspecified);
        ViewZoom = ViewCenterX = ViewCenterY = null;
    }

    private sealed class OrderedBirdCollection(PhotoItem owner) : ObservableCollection<DetectedBird>
    {
        protected override void InsertItem(int index, DetectedBird item)
        { ArgumentNullException.ThrowIfNull(item); base.InsertItem(index, item); owner.NormalizeResultOrder(); }
        protected override void SetItem(int index, DetectedBird item)
        { ArgumentNullException.ThrowIfNull(item); base.SetItem(index, item); owner.NormalizeResultOrder(); }
        protected override void RemoveItem(int index)
        { base.RemoveItem(index); owner.NormalizeResultOrder(); }
        protected override void ClearItems()
        { base.ClearItems(); owner.NormalizeResultOrder(); }
        protected override void MoveItem(int oldIndex, int newIndex)
        { base.MoveItem(oldIndex, newIndex); owner.NormalizeResultOrder(); }
    }

    public string DetectionStatus { get => _detectionStatus; set => Set(ref _detectionStatus, value); }
    public double? ViewZoom { get => _viewZoom; set => Set(ref _viewZoom, value); }
    public double? ViewCenterX { get => _viewCenterX; set => Set(ref _viewCenterX, value); }
    public double? ViewCenterY { get => _viewCenterY; set => Set(ref _viewCenterY, value); }
    public PhotoMetadata Metadata { get => _metadata; set => Set(ref _metadata, value); }
    public BitmapSource? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
    public int PixelWidth { get => _pixelWidth; set { if (Set(ref _pixelWidth, value)) CompleteGeometryDimensions(); } }
    public int PixelHeight { get => _pixelHeight; set { if (Set(ref _pixelHeight, value)) CompleteGeometryDimensions(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }
    private void CompleteGeometryDimensions()
    {
        if (_geometryStatus != PhotoGeometryStatus.Current || !ReferenceMatchesActive(_geometryReference) || _geometryReference is not { } reference) return;
        int? width = reference.DisplayWidth ?? (PixelWidth > 0 ? PixelWidth : null);
        int? height = reference.DisplayHeight ?? (PixelHeight > 0 ? PixelHeight : null);
        if (width != reference.DisplayWidth || height != reference.DisplayHeight)
            SetGeometryReference(new(reference.AssetId, reference.CanonicalPath, width, height, reference.FileLength, reference.LastWriteTimeUtcTicks),
                PhotoGeometryStatus.Current);
    }
    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
