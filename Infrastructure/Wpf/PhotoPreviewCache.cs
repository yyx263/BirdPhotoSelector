using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

/// <summary>
/// 现有 WPF 窗口的缓存入口。调度、去重、取消和预算均由独立图像核心实现；
/// 此处把来源转换为快照；跨平台解码结果在入库前转为冻结位图，缓存只保留一份完整显示图。
/// Avalonia 界面应直接使用 PreviewImageCache，不复用此 WPF 桥接类。
/// </summary>
public sealed class PhotoPreviewCache : IDisposable
{
    /// <summary>沿用核心的默认预算；主窗口仍显式设置 512 MiB。</summary>
    public const long DefaultBudgetBytes = PreviewImageCache.DefaultBudgetBytes;
    private readonly PreviewImageCache _cache;

    /// <summary>创建显示适配；可注入 Windows 位图解码函数，用于现有界面验证。</summary>
    public PhotoPreviewCache(long budgetBytes = DefaultBudgetBytes, Func<string, BitmapSource>? loader = null,
        Func<string, CancellationToken, BitmapSource>? cancellableLoader = null, IPhotoImageDecoder? images = null)
    {
        if (loader is not null && cancellableLoader is not null) throw new ArgumentException("只能指定一种预览解码器。");
        Func<string, CancellationToken, ImageFrame> decode = cancellableLoader is not null
            ? (path, token) => WpfImageBridge.FromBitmap(cancellableLoader(path, token))
            : loader is not null ? (path, _) => WpfImageBridge.FromBitmap(loader(path))
            : (path, token) => WpfImageBridge.FromBitmap(WpfImageBridge.ToBitmap((images ?? DesktopComposition.Images).Decode(path, cancellationToken: token)));
        _cache = new PreviewImageCache(decode, budgetBytes);
    }
    /// <summary>保留帧预算，解码临时内存和显示资源另计。</summary>
    public long BudgetBytes => _cache.BudgetBytes;
    /// <summary>当前帧缓存计费。</summary>
    public long CachedBytes => _cache.CachedBytes;
    /// <summary>当前缓存帧数量。</summary>
    public int CachedCount => _cache.CachedCount;
    /// <summary>当前正在解码的请求数。</summary>
    public int InFlightCount => _cache.InFlightCount;
    /// <summary>当前排队及正在处理的请求数。</summary>
    public int PendingCount => _cache.PendingCount;
    /// <summary>读取并共享已经入库的冻结位图；缓存命中到显示端不再复制整幅像素。</summary>
    public async Task<BitmapSource> GetAsync(PhotoItem photo, CancellationToken cancellationToken = default) =>
        WpfImageBridge.ToBitmap(await _cache.GetAsync(Snapshot(photo), cancellationToken).ConfigureAwait(false));
    /// <summary>在调用线程生成不可变来源快照，再交给与 UI 无关的预取调度器。</summary>
    public void UpdateNeighborhood(IReadOnlyList<PhotoItem> photos, PhotoItem current) =>
        _cache.UpdateNeighborhood(photos.Select(Snapshot).ToArray(), Snapshot(current));
    /// <summary>照片移出列表后释放对应缓存。</summary>
    public void TrimToPaths(IEnumerable<string> paths) => _cache.TrimToPaths(paths);
    /// <summary>清空整个会话的图像缓存。</summary>
    public void Clear() => _cache.Clear();
    /// <summary>终止缓存工作线程，不阻塞窗口退出。</summary>
    public void Dispose() => _cache.Dispose();
    /// <summary>冻结一次来源选择；RAW 前台解码规则属于来源属性，不让核心读取业务对象。</summary>
    private static PreviewSource Snapshot(PhotoItem photo)
    {
        ArgumentNullException.ThrowIfNull(photo);
        var asset = photo.ActivePreviewAsset;
        return new(photo.Id, asset?.CanonicalPath ?? photo.FilePath, photo.PixelWidth, photo.PixelHeight,
            asset is not null ? asset.Kind == PhotoAssetKind.Raw : PhotoImageFormats.IsRaw(photo.FilePath));
    }
}
