using System.Globalization;
using System.Windows.Data;

namespace BirdPhotoSelector;

/// <summary>仅在 WPF 数据绑定显示缩略图时读取平台位图，领域模型继续持有 ImageFrame。</summary>
public sealed class ImageFrameConverter : IValueConverter
{
    /// <summary>Windows 帧直接复用其冻结位图；空缩略图返回空图源。</summary>
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ImageFrame image ? WpfImageBridge.ToBitmap(image) : null;
    /// <summary>缩略图绑定单向显示，不接受界面回写。</summary>
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
