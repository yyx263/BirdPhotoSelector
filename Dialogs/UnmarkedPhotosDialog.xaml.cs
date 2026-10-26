using System.Windows;

namespace BirdPhotoSelector;

public partial class UnmarkedPhotosDialog : Window
{
    public UnmarkedPhotosDialog(int count)
    {
        InitializeComponent();
        MessageText.Text = $"当前列表还有 {count} 张未标记照片。\n返回挑选会自动定位到第一张未标记照片，也可以继续导出已评级的照片。";
    }
    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
