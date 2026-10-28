using System.Windows;

namespace BirdPhotoSelector;

public partial class UnmarkedPhotosDialog : Window
{
    public UnmarkedPhotosDialog(int count, bool deleteOnly = false)
    {
        InitializeComponent();
        ContinueButton.Content = deleteOnly ? "继续删除" : "继续导出";
        string action = deleteOnly ? "删除已标记为舍弃的照片" : "导出已评级的照片";
        MessageText.Text = $"当前列表还有 {count} 张未标记照片。\n返回挑选会自动定位到第一张未标记照片，也可以继续{action}。";
    }
    private void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
