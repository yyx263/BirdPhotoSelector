using System.ComponentModel;
using System.IO;

namespace BirdPhotoSelector;

/// <summary>A lightweight result checkpoint; contains no image bitmap or original-file collection.</summary>
public sealed class SpeciesEditCheckpoint
{
    internal SpeciesEditCheckpoint(SpeciesEditHistory owner, PhotoItem photo, string description)
    { Owner = owner; Photo = photo; Description = description; State = new SpeciesEditState(photo); }
    internal SpeciesEditHistory Owner { get; }
    internal PhotoItem Photo { get; }
    internal SpeciesEditState State { get; }
    internal bool Committed { get; set; }
    public string Description { get; }
}

public sealed class SpeciesEditHistory : INotifyPropertyChanged
{
    public const int Limit = 50;
    private readonly List<SpeciesEditCheckpoint> _entries = new();
    public bool CanUndo => _entries.Count > 0;
    public string Description => CanUndo ? _entries[^1].Description : "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public SpeciesEditCheckpoint Capture(PhotoItem photo, string description)
    { ArgumentNullException.ThrowIfNull(photo); return new(this, photo, description ?? "编辑鸟种"); }
    public bool Commit(SpeciesEditCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (!ReferenceEquals(checkpoint.Owner, this)) throw new ArgumentException("检查点属于另一个编辑历史。", nameof(checkpoint));
        if (checkpoint.Committed) return false;
        checkpoint.Committed = true;
        if (checkpoint.State.SameResults(checkpoint.Photo)) return false;
        _entries.Add(checkpoint);
        if (_entries.Count > Limit) _entries.RemoveAt(0);
        Notify(); return true;
    }
    public PhotoItem? Undo()
    {
        if (!CanUndo) return null;
        var checkpoint = _entries[^1]; _entries.RemoveAt(_entries.Count - 1);
        checkpoint.State.Restore(checkpoint.Photo); Notify(); return checkpoint.Photo;
    }
    public void Clear() { _entries.Clear(); Notify(); }
    /// <summary>One undoable reset of recognition results; never changes files, ratings or recent choices.</summary>
    public bool Clear(PhotoItem photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var checkpoint = Capture(photo, "清空本张识别结果");
        photo.SetBirds(Array.Empty<DetectedBird>(), nextBirdNumber: photo.NextBirdNumber);
        photo.HasManualSpeciesEdits = false;
        photo.DetectionStatus = "待识别";
        photo.SetGeometryReference(null, PhotoGeometryStatus.Unspecified);
        photo.ViewZoom = photo.ViewCenterX = photo.ViewCenterY = null;
        return Commit(checkpoint);
    }

    public DetectedBird Add(PhotoItem photo, SpeciesIdentity identity)
    {
        var species = SpeciesNameCatalog.Normalize(identity);
        var checkpoint = Capture(photo, "新增人工鸟种");
        var added = new DetectedBird { ManualSpecies = species, RecognitionStatus = "人工指定" };
        photo.SetBirds(photo.Birds.Append(added), photo.Birds.Count, photo.NextBirdNumber);
        photo.HasManualSpeciesEdits = true;
        Commit(checkpoint); return added;
    }
    public DetectedBird Replace(PhotoItem photo, DetectedBird bird, SpeciesIdentity identity)
    {
        var species = SpeciesNameCatalog.Normalize(identity);
        int index = photo.Birds.IndexOf(bird);
        if (index < 0) throw new ArgumentException("待更换条目已不在照片中。", nameof(bird));
        if (bird.ManualSpecies == species) return bird;
        var checkpoint = Capture(photo, "更换鸟种");
        var replacement = photo.Birds.ToArray();
        replacement[index] = new DetectedBird { ManualSpecies = species, RecognitionStatus = "人工指定" };
        photo.SetBirds(replacement, index, photo.NextBirdNumber);
        photo.HasManualSpeciesEdits = true;
        Commit(checkpoint); return replacement[index];
    }
    public bool Delete(PhotoItem photo, DetectedBird bird)
    {
        if (!photo.Birds.Contains(bird)) return false;
        var checkpoint = Capture(photo, "删除鸟种");
        photo.RemoveBird(bird); photo.HasManualSpeciesEdits = photo.Birds.Count > 0;
        return Commit(checkpoint);
    }
    private void Notify()
    { PropertyChanged?.Invoke(this, new(nameof(CanUndo))); PropertyChanged?.Invoke(this, new(nameof(Description))); }
}

internal sealed class SpeciesEditState
{
    private readonly DetectedBird[] _birds;
    private readonly int _selectedIndex, _next, _width, _height;
    private readonly bool _manual;
    private readonly string _status;
    private readonly PhotoGeometryReference? _geometry;
    private readonly PhotoGeometryStatus _geometryStatus;
    private readonly double? _zoom, _x, _y;
    private readonly string? _sourceId, _sourcePath;
    private readonly long? _length, _modified;
    internal SpeciesEditState(PhotoItem photo)
    {
        _birds = photo.Birds.Select(bird => bird.Copy()).ToArray(); _selectedIndex = photo.SelectedBirdIndex;
        _next = photo.NextBirdNumber; _manual = photo.HasManualSpeciesEdits; _status = photo.DetectionStatus;
        _geometry = photo.GeometryReference; _geometryStatus = photo.GeometryStatus;
        _zoom = photo.ViewZoom; _x = photo.ViewCenterX; _y = photo.ViewCenterY;
        _width = photo.PixelWidth; _height = photo.PixelHeight;
        _sourceId = photo.ActivePreviewAssetId; _sourcePath = photo.ActivePreviewAsset?.CanonicalPath;
        try
        {
            if (_sourcePath is not null && new FileInfo(_sourcePath) is { Exists: true } file)
            { _length = file.Length; _modified = file.LastWriteTimeUtc.Ticks; }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    internal bool SameResults(PhotoItem photo) => photo.Birds.Count == _birds.Length &&
        photo.Birds.Zip(_birds).All(pair => SameBird(pair.First, pair.Second)) &&
        photo.SelectedBirdIndex == _selectedIndex && photo.NextBirdNumber == _next &&
        photo.HasManualSpeciesEdits == _manual && photo.DetectionStatus == _status &&
        photo.GeometryReference == _geometry && photo.GeometryStatus == _geometryStatus &&
        photo.ViewZoom == _zoom && photo.ViewCenterX == _x && photo.ViewCenterY == _y &&
        photo.PixelWidth == _width && photo.PixelHeight == _height;
    private static bool SameBird(DetectedBird a, DetectedBird b) => a.Number == b.Number && a.IsManual == b.IsManual &&
        a.ManualSpecies == b.ManualSpecies && a.Species == b.Species && a.RecognitionStatus == b.RecognitionStatus &&
        a.Box.X == b.Box.X && a.Box.Y == b.Box.Y && a.Box.Width == b.Box.Width && a.Box.Height == b.Box.Height && a.Box.Confidence == b.Box.Confidence;
    internal void Restore(PhotoItem photo)
    {
        bool sameSource = photo.ActivePreviewAssetId == _sourceId &&
            string.Equals(photo.ActivePreviewAsset?.CanonicalPath, _sourcePath, StringComparison.OrdinalIgnoreCase);
        if (sameSource && _length is not null)
        {
            try
            { var file = new FileInfo(_sourcePath!); sameSource = file.Exists && file.Length == _length && file.LastWriteTimeUtc.Ticks == _modified; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { sameSource = false; }
        }
        photo.SetBirds(_birds.Select(bird => bird.Copy()), _selectedIndex, Math.Max(_next, photo.NextBirdNumber));
        photo.HasManualSpeciesEdits = _manual; photo.DetectionStatus = _status;
        photo.SetGeometryReference(_geometry, sameSource ? _geometryStatus : PhotoGeometryStatus.NeedsReview);
        if (sameSource) { photo.PixelWidth = _width; photo.PixelHeight = _height; }
        photo.ViewZoom = sameSource ? _zoom : null; photo.ViewCenterX = sameSource ? _x : null; photo.ViewCenterY = sameSource ? _y : null;
    }
}
