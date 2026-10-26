using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BirdPhotoSelector;

public partial class ExportDialog : Window
{
    private readonly IReadOnlyList<PhotoItem> _photos;
    public bool DeleteOnly => ActionChoice.SelectedIndex == 3;
    public bool RemoveOriginal => !DeleteOnly && RemoveOriginalBox.IsChecked == true;
    public string Destination => DestinationBox.Text;
    public ExportDialog(IReadOnlyList<PhotoItem> photos) { _photos = photos; InitializeComponent(); UpdateChoice(); }
    public bool Includes(PhotoItem p) => ActionChoice.SelectedIndex switch { 0 => p.Rating is PhotoRating.Keep or PhotoRating.Favorite, 1 => p.Rating == PhotoRating.Favorite, _ => p.Rating == PhotoRating.Reject };
    private void ActionChoice_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsInitialized) UpdateChoice(); }
    private void UpdateChoice()
    {
        CountText.Text = $"当前范围：{_photos.Count(Includes)} 张照片";
        DestinationPanel.Visibility = DeleteOnly ? Visibility.Collapsed : Visibility.Visible;
        DeleteWarning.Visibility = DeleteOnly ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.Content = DeleteOnly ? "删除舍弃" : "开始导出";
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择接收照片的现有目录（不自动创建文件夹）", Multiselect = false };
        if (picker.ShowDialog(this) == true) DestinationBox.Text = picker.FolderName;
    }
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!DeleteOnly && !Directory.Exists(Destination)) { MessageBox.Show(this, "请先选择一个现有目标目录。", "选择目标目录"); return; }
        DialogResult = true;
    }
}
