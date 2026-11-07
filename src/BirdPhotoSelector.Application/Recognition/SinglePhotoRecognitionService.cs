using System.IO;
using System.Security.Cryptography;

namespace BirdPhotoSelector;

/// <summary>A proposed complete replacement; no old region is changed until explicit UI application.</summary>
public sealed class SinglePhotoRecognitionResult
{
    private readonly SinglePhotoSnapshot _snapshot;
    private readonly DetectedBird[] _birds;
    private readonly SinglePhotoSourceSignature? _source;
    private bool _applied;

    /// <summary>独立持有可应用结果与原状态快照，失败、取消或来源变化时不能替换旧结果。</summary>
    internal SinglePhotoRecognitionResult(SinglePhotoSnapshot snapshot, bool success, DetectedBird[] birds,
        int nextBirdNumber, int width, int height, string status, bool missingModel,
        IEnumerable<string> errors, SinglePhotoSourceSignature? source)
    {
        _snapshot = snapshot; _source = source;
        _birds = birds.Select(SinglePhotoSnapshot.CopyBird).ToArray();
        Success = success; NextBirdNumber = nextBirdNumber; PixelWidth = width; PixelHeight = height;
        DetectionStatus = status; MissingModel = missingModel; Errors = Array.AsReadOnly(errors.ToArray());
    }

    public bool Success { get; }
    public bool IsEmpty => Success && _birds.Length == 0;
    public bool MissingModel { get; }
    public IReadOnlyList<string> Errors { get; }
    public IReadOnlyList<DetectedBird> Birds => Array.AsReadOnly(_birds.Select(SinglePhotoSnapshot.CopyBird).ToArray());
    public int NextBirdNumber { get; }
    public int PixelWidth { get; }
    public int PixelHeight { get; }
    public string DetectionStatus { get; }
    public string PhotoId => _snapshot.Id;

    /// <summary>Called on the UI thread. For an empty proposal the caller explicitly passes allowEmpty; the current UI applies it immediately and records undo.</summary>
    public void ApplyTo(PhotoItem photo, bool allowEmpty = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Success || _source is null) throw new InvalidOperationException("本次重新识别未成功，原选区和识别结果已保留。");
        if (IsEmpty && !allowEmpty) throw new InvalidOperationException("未检测到鸟，需要确认后才能清空旧选区和识别结果。");
        if (_applied || !_snapshot.Matches(photo))
            throw new InvalidOperationException("照片或选区在重新识别期间已变化，未应用过期结果。");
        if (!_source.Matches(SinglePhotoSourceSignature.Read(_snapshot.Source.CanonicalPath, cancellationToken)))
            throw new IOException("原照片在重新识别后已变化，原选区和识别结果已保留，请重新读取后重试。");
        cancellationToken.ThrowIfCancellationRequested();
        if (!_snapshot.Matches(photo))
            throw new InvalidOperationException("照片或选区在校验期间已变化，未应用过期结果。");

        // Prepare and validate everything before publishing any replacement to UI bindings.
        var birds = _birds.Select(SinglePhotoSnapshot.CopyBird).ToArray();
        var reference = new PhotoGeometryReference(_snapshot.Source.Id, _snapshot.Source.CanonicalPath,
            PixelWidth, PixelHeight, _source.Length, _source.LastWriteTicks);
        photo.PixelWidth = PixelWidth; photo.PixelHeight = PixelHeight;
        photo.SetBirds(birds, selectedIndex: 0, nextBirdNumber: NextBirdNumber);
        photo.SetGeometryReference(reference, PhotoGeometryStatus.Current);
        photo.DetectionStatus = DetectionStatus;
        photo.ViewZoom = photo.ViewCenterX = photo.ViewCenterY = null;
        photo.HasManualSpeciesEdits = false;
        _applied = true;
    }
}

/// <summary>Rebuilds one source's automatic regions and species as an all-or-nothing proposal.</summary>
public sealed class SinglePhotoRecognitionService : IDisposable
{
    private readonly IBirdDetector _detector;
    private readonly IBirdSpeciesRecognizer _recognizer;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IPhotoImageDecoder _images;
    private bool _disposed;

    /// <summary>创建单张重建用例；宿主可注入图像后端，推理器生命周期随用例结束，来源一致性检查不会被替换。</summary>
    public SinglePhotoRecognitionService(IPhotoImageDecoder images, IBirdDetector detector, IBirdSpeciesRecognizer recognizer)
    {
        _images = images ?? throw new ArgumentNullException(nameof(images));
        _detector = detector ?? throw new ArgumentNullException(nameof(detector));
        _recognizer = recognizer ?? throw new ArgumentNullException(nameof(recognizer));
    }

    /// <summary>快照当前照片状态后异步重建；显式重建允许更换旧待核对框，但只返回事务提案。</summary>
    public async Task<SinglePhotoRecognitionResult> RebuildAsync(PhotoItem photo, bool chinaOnly = true,
        IProgress<SpeciesProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(photo);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        // Snapshot mutable UI state before leaving the caller's thread. Pending geometry is
        // allowed here because this explicit action rebuilds it from the current source.
        var snapshot = new SinglePhotoSnapshot(photo);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Rebuild(snapshot, chinaOnly, progress, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>读取和哈希当前原件，重新定位并逐鸟识别，全部成功且来源未变才生成可应用结果。</summary>
    private SinglePhotoRecognitionResult Rebuild(SinglePhotoSnapshot snapshot, bool chinaOnly,
        IProgress<SpeciesProgress>? progress, CancellationToken token)
    {
        string path = snapshot.Source.CanonicalPath;
        var errors = new List<string>();
        bool missingModel = false;
        SinglePhotoRecognitionResult Failed(string error) => new(snapshot, false, Array.Empty<DetectedBird>(),
            snapshot.NextBirdNumber, 0, 0, "重新识别未完成", missingModel,
            errors.Append($"{path}：{error}"), null);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            progress?.Report(new(0, 1, $"正在读取本张原图 · {path}"));
            var source = SinglePhotoSourceSignature.Read(path, token);
            var image = _images.Decode(path, cancellationToken: token);
            if (image is null || image.PixelWidth <= 0 || image.PixelHeight <= 0)
                return Failed("图像解码没有返回有效像素。");
            token.ThrowIfCancellationRequested();
            progress?.Report(new(0, 1, $"正在重新定位本张鸟类 · {path}"));
            var detection = _detector.Detect(image, token);
            token.ThrowIfCancellationRequested();
            missingModel = detection.MissingModel;
            if (detection.MissingModel || detection.Error is not null)
                return Failed(detection.Error ?? detection.Status);
            if (detection.Birds.Any(box => box is null || !box.IsValid))
                return Failed("定位返回了无效选区，未替换旧结果。");
            var boxes = detection.Birds.OrderByDescending(box => box.Width * box.Height)
                .ThenByDescending(box => box.Confidence).ThenBy(box => box.X).ThenBy(box => box.Y).ToArray();
            if (boxes.Length > int.MaxValue - snapshot.NextBirdNumber)
                return Failed("选区序号已达到上限。");
            int next = snapshot.NextBirdNumber;
            var birds = new List<DetectedBird>();
            for (int index = 0; index < boxes.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report(new(index, Math.Max(1, boxes.Length),
                    $"正在重新识别本张鸟种 · {path} · 选区 {index + 1} / {boxes.Length}（鸟 {index + 1}）"));
                var box = boxes[index].Copy();
                var recognition = _recognizer.Recognize(image, box, chinaOnly, token);
                token.ThrowIfCancellationRequested();
                missingModel |= recognition.MissingModel;
                if (recognition.MissingModel || recognition.Error is not null || recognition.Match is not { IsValid: true })
                    return Failed(recognition.Error ?? recognition.Status);
                if (!box.IsValid) return Failed("识别适配器改变了选区范围，未替换旧结果。");
                birds.Add(new DetectedBird { Number = next++, Box = box, IsManual = false,
                    Species = recognition.Match, RecognitionStatus = recognition.Status });
            }
            if (!source.Matches(SinglePhotoSourceSignature.Read(path, token)))
                return Failed("原照片在识别期间已变化，未应用结果。");
            token.ThrowIfCancellationRequested();
            string status = boxes.Length == 0 ? "重新定位完成 · 未检测到鸟" : $"重新识别完成 · {birds.Count} 只鸟";
            progress?.Report(new(1, 1, $"{status} · {path}"));
            token.ThrowIfCancellationRequested();
            return new(snapshot, true, birds.ToArray(), next, image.PixelWidth, image.PixelHeight,
                status, false, errors, source);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is not OutOfMemoryException and not ObjectDisposedException)
        { return Failed(ex.Message); }
    }

    /// <summary>结束重建用例并释放其拥有的定位与鸟种模型。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _detector.Dispose(); _recognizer.Dispose();
    }
}

/// <summary>重建事务的原状态快照，包含编辑保护和来源身份，用于阻止异步覆盖新状态。</summary>
internal sealed class SinglePhotoSnapshot
{
    private readonly PhotoAsset[] _assets;
    private readonly string? _preferredId;
    private readonly DetectedBird[] _oldObjects, _oldValues;
    private readonly int _selectedIndex;
    private readonly PhotoRating _rating;
    private readonly bool _manualSpeciesEdits;
    private readonly string _detectionStatus;
    private readonly PhotoGeometryReference? _geometry;
    private readonly PhotoGeometryStatus _geometryStatus;
    private readonly int _width, _height;
    private readonly double? _zoom, _centerX, _centerY;
    public string Id { get; }
    public PhotoAsset Source { get; }
    public int NextBirdNumber { get; }

    /// <summary>快照完整照片编辑状态和当前原件，提交提案前检测并发变化。</summary>
    public SinglePhotoSnapshot(PhotoItem photo)
    {
        Id = photo.Id; Source = photo.ActivePreviewAsset ?? throw new InvalidOperationException("照片没有当前预览来源。");
        _assets = photo.Assets.ToArray(); _preferredId = photo.PreferredPreviewAssetId;
        _oldObjects = photo.Birds.ToArray(); _oldValues = _oldObjects.Select(CopyBird).ToArray();
        _selectedIndex = photo.SelectedBirdIndex; NextBirdNumber = photo.NextBirdNumber;
        _rating = photo.Rating; _detectionStatus = photo.DetectionStatus;
        _manualSpeciesEdits = photo.HasManualSpeciesEdits;
        _geometry = photo.GeometryReference; _geometryStatus = photo.GeometryStatus;
        _width = photo.PixelWidth; _height = photo.PixelHeight;
        _zoom = photo.ViewZoom; _centerX = photo.ViewCenterX; _centerY = photo.ViewCenterY;
    }

    /// <summary>比较快照身份、原件版本或结果状态，阻止异步结果覆盖用户的新编辑。</summary>
    public bool Matches(PhotoItem photo) => photo.Id == Id && photo.ActivePreviewAssetId == Source.Id &&
        photo.PreferredPreviewAssetId == _preferredId && photo.Assets.Count == _assets.Length &&
        photo.Assets.Zip(_assets).All(pair => pair.First.Id == pair.Second.Id && pair.First.Kind == pair.Second.Kind &&
            FilePaths.Comparer.Equals(pair.First.CanonicalPath, pair.Second.CanonicalPath)) &&
        photo.Birds.Count == _oldObjects.Length && photo.Birds.Zip(_oldObjects).All(pair => ReferenceEquals(pair.First, pair.Second)) &&
        photo.Birds.Zip(_oldValues).All(pair => SameBird(pair.First, pair.Second)) &&
        photo.SelectedBirdIndex == _selectedIndex && photo.NextBirdNumber == NextBirdNumber && photo.Rating == _rating &&
        photo.HasManualSpeciesEdits == _manualSpeciesEdits &&
        photo.DetectionStatus == _detectionStatus && photo.GeometryReference == _geometry && photo.GeometryStatus == _geometryStatus &&
        photo.PixelWidth == _width && photo.PixelHeight == _height &&
        photo.ViewZoom == _zoom && photo.ViewCenterX == _centerX && photo.ViewCenterY == _centerY;

    /// <summary>深拷贝结果，提案和撤销快照不共享可变框。</summary>
    internal static DetectedBird CopyBird(DetectedBird bird) => bird.Copy();

    /// <summary>核对稳定编号、人工状态和鸟种/坐标内容，防止重建期间覆盖并发编辑。</summary>
    private static bool SameBird(DetectedBird first, DetectedBird second) => first.Number == second.Number &&
        first.IsManual == second.IsManual && first.ManualSpecies == second.ManualSpecies && first.Species == second.Species && first.RecognitionStatus == second.RecognitionStatus &&
        first.Box.X == second.Box.X && first.Box.Y == second.Box.Y && first.Box.Width == second.Box.Width &&
        first.Box.Height == second.Box.Height && first.Box.Confidence == second.Box.Confidence;
}

/// <summary>以长度、修改时间和 SHA-256 定义识别来源版本，不将路径字符串当作内容证明。</summary>
internal sealed record SinglePhotoSourceSignature(long Length, long LastWriteTicks, byte[] Sha256)
{
    /// <summary>分块计算来源 SHA-256 并记录长度/时间，期间响应取消，不改写原文件。</summary>
    internal static SinglePhotoSourceSignature Read(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.SequentialScan);
        long length = stream.Length;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[131072];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        { token.ThrowIfCancellationRequested(); hash.AppendData(buffer, 0, read); }
        token.ThrowIfCancellationRequested();
        return new(length, File.GetLastWriteTimeUtc(path).Ticks, hash.GetHashAndReset());
    }

    /// <summary>比较快照身份、原件版本或结果状态，阻止异步结果覆盖用户的新编辑。</summary>
    internal bool Matches(SinglePhotoSourceSignature other) => Length == other.Length && LastWriteTicks == other.LastWriteTicks &&
        CryptographicOperations.FixedTimeEquals(Sha256, other.Sha256);
}
