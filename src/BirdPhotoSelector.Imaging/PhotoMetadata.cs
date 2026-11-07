namespace BirdPhotoSelector;

/// <summary>照片拍摄参数的显示文本。该数据对象不依赖图形框架，JSON 字段沿用现有工程格式。</summary>
public sealed class PhotoMetadata
{
    /// <summary>相机本地拍摄时间，缺失为破折号；不推断 UTC。</summary>
    public string CapturedAt { get; set; } = "—";
    /// <summary>相机型号。</summary>
    public string Camera { get; set; } = "—";
    /// <summary>焦距及单位。</summary>
    public string FocalLength { get; set; } = "—";
    /// <summary>光圈值。</summary>
    public string Aperture { get; set; } = "—";
    /// <summary>曝光时间。</summary>
    public string Shutter { get; set; } = "—";
    /// <summary>ISO 感光度。</summary>
    public string Iso { get; set; } = "—";
    /// <summary>拍摄曝光补偿；真实零值与缺失分开显示。</summary>
    public string ExposureBias { get; set; } = "—";
}
