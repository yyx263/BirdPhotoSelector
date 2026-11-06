using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Globalization;
using System.Runtime.CompilerServices;
using BirdPhotoSelector.Imaging;

namespace BirdPhotoSelector;

/// <summary>一张逻辑照片的聚合：维护 JPG/RAW 成员、活动坐标来源、结果顺序和连续序号。所有修改应在会话所属线程串行执行。</summary>
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
    private ImageFrame? _thumbnail;
    private int _pixelWidth, _pixelHeight;

    /// <summary>初始化只读原件视图和有序结果集合，集合变化统一经过聚合维护。</summary>
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
            var existing = _assets.FirstOrDefault(asset => FilePaths.Comparer.Equals(asset.CanonicalPath, canonical));
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

    /// <summary>整体替换原件成员并验证唯一性；换源时保留旧坐标参照并使其待核对，不自动套用旧鸟框。</summary>
    public void SetAssets(IEnumerable<PhotoAsset> assets, string? preferredPreviewAssetId = null, string? activePreviewAssetId = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        SetAssetsCore(assets.ToArray(), preferredPreviewAssetId, activePreviewAssetId,
            preferredPreviewAssetId is not null, activePreviewAssetId is not null);
    }

    /// <summary>为逻辑照片补充另一格式原件，已有活动预览与识别结果在不换源时保持不变。</summary>
    public void AddAsset(PhotoAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        SetAssetsCore(_assets.Append(asset).ToArray(), _explicitPreferredPreview ? _preferredPreviewAssetId : null,
            _explicitActivePreview ? _activePreviewAssetId : null, _explicitPreferredPreview, _explicitActivePreview);
    }

    /// <summary>移除一个受管理成员，仅修改列表记录；最后一个成员可使逻辑照片为空，由上层移出列表。</summary>
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

    /// <summary>修改首选来源，并按存在性选择可用活动成员，不修改磁盘文件。</summary>
    public void SetPreferredPreviewAsset(string assetId)
    {
        if (!_assets.Any(asset => asset.Id == assetId)) throw new ArgumentException("首选预览成员不属于该照片。", nameof(assetId));
        SetAssetsCore(_assets.ToArray(), assetId, null, true, false);
    }

    /// <summary>显式切换活动预览，联动缩略图、视图和坐标来源的有效性。</summary>
    public void SetActivePreviewAsset(string assetId)
    {
        if (!_assets.Any(asset => asset.Id == assetId)) throw new ArgumentException("当前预览成员不属于该照片。", nameof(assetId));
        SetAssetsCore(_assets.ToArray(), _preferredPreviewAssetId, assetId, _explicitPreferredPreview, true);
    }

    /// <summary>设置鸟框所属原件及其核对状态，只有匹配活动来源的 Current 参照可自动用于推理。</summary>
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

    /// <summary>原件变更的唯一提交点：先验证快照，再更新来源并发布成组通知，避免暴露中间状态。</summary>
    private void SetAssetsCore(PhotoAsset[] snapshot, string? preferredId, string? activeId,
        bool explicitPreferred, bool explicitActive)
    {
        // Validate every candidate before touching collections, preview state or recognition evidence.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(FilePaths.Comparer);
        var kinds = new HashSet<PhotoAssetKind>();
        string? pairKey = null;
        foreach (var asset in snapshot)
        {
            if (asset is null) throw new ArgumentException("照片成员不能为空。", nameof(snapshot));
            if (!ids.Add(asset.Id) || !paths.Add(asset.CanonicalPath) || !kinds.Add(asset.Kind))
                throw new ArgumentException("同一张照片不能包含重复的成员 ID、路径或格式候选。", nameof(snapshot));
            if (pairKey is not null && !FilePaths.Comparer.Equals(pairKey, asset.PairKey))
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
            !FilePaths.Comparer.Equals(old?.CanonicalPath, active?.CanonicalPath);
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

    /// <summary>优先选已存在的首选原件，其次选择可读成员；不在这里解码或猜测同名文件。</summary>
    private static PhotoAsset? ChooseAvailable(PhotoAsset[] assets, PhotoAsset? preferred)
    {
        if (preferred is not null && File.Exists(preferred.CanonicalPath)) return preferred;
        return assets.FirstOrDefault(asset => asset.Kind == PhotoAssetKind.Jpg && File.Exists(asset.CanonicalPath)) ??
            assets.FirstOrDefault(asset => File.Exists(asset.CanonicalPath)) ?? preferred ?? assets.FirstOrDefault();
    }

    /// <summary>比较稳定成员 ID 与原路径，防止不同成员偶然同名而错误复用坐标。</summary>
    private bool ReferenceMatchesActive(PhotoGeometryReference? reference) => reference is not null &&
        ActivePreviewAsset is { } active && active.Id == reference.AssetId &&
        FilePaths.Comparer.Equals(active.CanonicalPath, reference.CanonicalPath);

    /// <summary>发布原件、格式、文件名和预览来源相关属性通知。</summary>
    private void NotifyAssets()
    {
        OnPropertyChanged(nameof(Assets)); OnPropertyChanged(nameof(PreferredPreviewAssetId)); OnPropertyChanged(nameof(ActivePreviewAssetId));
        OnPropertyChanged(nameof(PreferredPreviewAsset)); OnPropertyChanged(nameof(ActivePreviewAsset));
        OnPropertyChanged(nameof(FilePath)); OnPropertyChanged(nameof(FileName)); OnPropertyChanged(nameof(FormatLabel));
        OnPropertyChanged(nameof(CompactFormatLabel));
        OnPropertyChanged(nameof(CanUseBirdGeometry));
    }

    /// <summary>统一通知几何参照及其可用性，供业务判断和界面提示更新。</summary>
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
    /// <summary>快照后替换结果，维护有效唯一身份、选中对象和不回退的序号高水位，并应用有框/人工排序。</summary>
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
    /// <summary>在当前显示坐标中增加人工框，分配新身份并将照片标为人工修改以保护后续批量识别。</summary>
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
    /// <summary>删除指定记录及关联选区，维护选择和可见编号；删除最后一条时复位识别保护。</summary>
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
    /// <summary>替换自动框，同时保留人工内容；编号分配仍由统一集合逻辑管理。</summary>
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
    /// <summary>联动鸟框、当前鸟和相关展示属性通知，并保持选择处于有效范围。</summary>
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
    /// <summary>仅为有框记录生成从一开始的可见编号，无框人工记录不占数字序号。</summary>
    private void RefreshDisplayNumbers()
    {
        int number = 0;
        foreach (var bird in Birds) bird.DisplayNumber = bird.HasRegion ? ++number : 0;
    }
    /// <summary>增删结果时维护对象事件订阅、编号和空结果复位，防止丢失选择或遗留保护。</summary>
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
    /// <summary>鸟种变为人工或名称变化时重新整理结果顺序及依赖属性。</summary>
    private void OnBirdPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DetectedBird.ManualSpecies)) return;
        HasManualSpeciesEdits = true;
        NotifyBirds();
    }
    /// <summary>保留有框结果的相对顺序，人工无框结果按中文名、学名稳定排序。</summary>
    private static DetectedBird[] OrderedResults(IEnumerable<DetectedBird> birds) => birds
        .OrderBy(bird => bird.HasRegion ? 0 : 1)
        .ThenBy(bird => bird.HasRegion ? "" : bird.EffectiveSpecies?.DisplayName ?? bird.SpeciesName, ManualNameComparer)
        .ThenBy(bird => bird.HasRegion ? "" : bird.ScientificName, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>在集合事件完成后排序，保留当前对象并防止 WPF/其他观察者触发集合重入。</summary>
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

    /// <summary>全部结果删除后清除人工保护、旧几何和缩放，让下一次批量识别重新定位。</summary>
    private void ResetEmptyRecognition()
    {
        HasManualSpeciesEdits = false;
        DetectionStatus = "待识别";
        if (_geometryReference is not null || _geometryStatus != PhotoGeometryStatus.Unspecified)
            SetGeometryReference(null, PhotoGeometryStatus.Unspecified);
        ViewZoom = ViewCenterX = ViewCenterY = null;
    }

    /// <summary>聚合拥有的结果集合，在完成观察者通知后整理顺序，避免集合事件重入。</summary>
    private sealed class OrderedBirdCollection(PhotoItem owner) : ObservableCollection<DetectedBird>
    {
        /// <summary>完成集合插入通知后交还聚合排序，避免观察者仍处理事件时发生重入。</summary>
        protected override void InsertItem(int index, DetectedBird item)
        { ArgumentNullException.ThrowIfNull(item); base.InsertItem(index, item); owner.NormalizeResultOrder(); }
        /// <summary>替换记录后统一整理顺序和编号。</summary>
        protected override void SetItem(int index, DetectedBird item)
        { ArgumentNullException.ThrowIfNull(item); base.SetItem(index, item); owner.NormalizeResultOrder(); }
        /// <summary>移除记录后整理剩余结果。</summary>
        protected override void RemoveItem(int index)
        { base.RemoveItem(index); owner.NormalizeResultOrder(); }
        /// <summary>清空后触发聚合的空结果状态复位。</summary>
        protected override void ClearItems()
        { base.ClearItems(); owner.NormalizeResultOrder(); }
        /// <summary>外部改变顺序后仍遵守有框在前的业务规则。</summary>
        protected override void MoveItem(int oldIndex, int newIndex)
        { base.MoveItem(oldIndex, newIndex); owner.NormalizeResultOrder(); }
    }

    public string DetectionStatus { get => _detectionStatus; set => Set(ref _detectionStatus, value); }
    public double? ViewZoom { get => _viewZoom; set => Set(ref _viewZoom, value); }
    public double? ViewCenterX { get => _viewCenterX; set => Set(ref _viewCenterX, value); }
    public double? ViewCenterY { get => _viewCenterY; set => Set(ref _viewCenterY, value); }
    public PhotoMetadata Metadata { get => _metadata; set => Set(ref _metadata, value); }
    public ImageFrame? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, value); }
    public int PixelWidth { get => _pixelWidth; set { if (Set(ref _pixelWidth, value)) CompleteGeometryDimensions(); } }
    public int PixelHeight { get => _pixelHeight; set { if (Set(ref _pixelHeight, value)) CompleteGeometryDimensions(); } }
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>相等值不重复通知；统一设置可观察字段，避免无意义的会话修订。</summary>
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(property);
        return true;
    }
    /// <summary>已确认的来源参照在尺寸可用时补齐显示尺寸，不修改旧来源或待核对参照。</summary>
    private void CompleteGeometryDimensions()
    {
        if (_geometryStatus != PhotoGeometryStatus.Current || !ReferenceMatchesActive(_geometryReference) || _geometryReference is not { } reference) return;
        int? width = reference.DisplayWidth ?? (PixelWidth > 0 ? PixelWidth : null);
        int? height = reference.DisplayHeight ?? (PixelHeight > 0 ? PixelHeight : null);
        if (width != reference.DisplayWidth || height != reference.DisplayHeight)
            SetGeometryReference(new(reference.AssetId, reference.CanonicalPath, width, height, reference.FileLength, reference.LastWriteTimeUtcTicks),
                PhotoGeometryStatus.Current);
    }
    /// <summary>发布指定属性的变更；不依赖任何平台调度器。</summary>
    private void OnPropertyChanged([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
