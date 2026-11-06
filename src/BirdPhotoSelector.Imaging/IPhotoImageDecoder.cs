namespace BirdPhotoSelector.Imaging;

/// <summary>读取照片的统一入口。实现负责格式校验、方向、文件释放及取消，不参与评级或工程状态修改。</summary>
public interface IPhotoImageDecoder
{
    /// <summary>
    /// 解码 JPG 或真正的 RAW 像素；maxWidth=0 表示全分辨率。
    /// maxWidth 沿用 v1 语义：JPG 按编码宽度缩小后应用方向，RAW 按已定向宽度缩小。
    /// 因而 JPG 竖图的最终显示宽度不承诺小于该值。
    /// 返回值必须是不可变、可跨线程使用、已关闭输入文件的显示方向图像。
    /// </summary>
    ImageFrame Decode(string path, int maxWidth = 0, CancellationToken cancellationToken = default);
    /// <summary>读取显示尺寸和拍摄参数，不进行全分辨率像素解码。</summary>
    PhotoImageInfo ReadInfo(string path, CancellationToken cancellationToken = default);
}

/// <summary>某个照片来源的拍摄参数及显示方向下的完整尺寸；Orientation 保留原始 EXIF 标记。</summary>
/// <param name="Metadata">用于界面和工程的拍摄参数。</param>
/// <param name="PixelWidth">应用方向后的完整像素宽度。</param>
/// <param name="PixelHeight">应用方向后的完整像素高度。</param>
/// <param name="Orientation">原始 EXIF 方向，通常为 1..8。</param>
public sealed record PhotoImageInfo(PhotoMetadata Metadata, int PixelWidth, int PixelHeight, int Orientation);

/// <summary>无 UI 依赖的预览请求快照。创建后不再访问 UI 或可变的 PhotoItem。</summary>
/// <param name="Id">列表内的逻辑照片标识，用于定位相邻项。</param>
/// <param name="Path">本次预览的实际文件路径。</param>
/// <param name="PixelWidth">完整显示宽度，未知时为 0。</param>
/// <param name="PixelHeight">完整显示高度，未知时为 0。</param>
/// <param name="ForegroundOnly">RAW 等昂贵来源为 true，禁止邻居预解码，且所有等待者放弃后应取消解码。</param>
public sealed record PreviewSource(string Id, string Path, int PixelWidth, int PixelHeight, bool ForegroundOnly = false);
