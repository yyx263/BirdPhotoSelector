using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using BirdPhotoSelector.Desktop.ViewModels;

namespace BirdPhotoSelector.Desktop.Dialogs;

/// <summary>只收集导出选项，真正的确认、分类提醒与文件操作由所属窗口和共享服务完成。</summary>
public sealed class ExportDialog : Window
{
    private readonly IReadOnlyList<PhotoItem> _photos;
    private readonly ComboBox _action = new() { Name = "ExportAction", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 42, ItemsSource = new[] { "导出保留 + 精选", "仅导出精选", "导出舍弃", "导出全部", "删除舍弃的原文件" }, SelectedIndex = 0 };
    private readonly ComboBox _scope = new() { Name = "ExportScope", HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 42, ItemsSource = new[] { "全部原件（JPG + RAW）", "仅 JPG 原件", "仅 RAW 原件" }, SelectedIndex = 0 };
    private readonly TextBox _destination = new() { Name = "ExportDestination", MinHeight = 42, PlaceholderText = "选择已有目标目录" };
    private readonly CheckBox _group = new() { Name = "GroupBySpecies", Content = "按已识别鸟种创建子文件夹", IsChecked = false };
    private readonly CheckBox _remove = new() { Name = "RemoveOriginal", Content = "全部副本复制并校验成功后，移除对应原件", IsChecked = false };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#A1B5BF") };
    private readonly TextBlock _warning = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#F0C77B"), Text = "原文件优先放入回收站；所在磁盘不支持回收站时将永久删除。执行前会再次确认。" };
    private readonly StackPanel _destinationPanel = new() { Spacing = 10 };
    private readonly Button _confirm = new() { Name = "ConfirmExport", Content = "开始导出", IsDefault = true };
    /// <summary>固定当前照片快照；默认复制保留和精选，默认不分类、不删除原件。</summary>
    public ExportDialog(IReadOnlyList<PhotoItem> photos)
    {
        _photos = photos.ToArray(); Title = "羽选 · 导出 / 清理"; Width = 660; MinWidth = 580; CanResize = false; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        layout.Children.Add(new TextBlock { Text = "导出 / 清理", FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        layout.Children.Add(new TextBlock { Text = "操作方式" }); layout.Children.Add(_action);
        layout.Children.Add(new TextBlock { Text = "原件范围" }); layout.Children.Add(_scope);
        _destinationPanel.Children.Add(new TextBlock { Text = "目标目录" });
        var row = new Grid { ColumnDefinitions = new("*,Auto") }; row.Children.Add(_destination);
        var browse = new Button { Content = "选择目录", Margin = new Thickness(8, 0, 0, 0) }; Grid.SetColumn(browse, 1); row.Children.Add(browse); _destinationPanel.Children.Add(row);
        _destinationPanel.Children.Add(_group); _destinationPanel.Children.Add(_remove); _destinationPanel.Children.Add(_hint); layout.Children.Add(_destinationPanel); layout.Children.Add(_warning);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = new Button { Content = "取消", IsCancel = true }; cancel.Click += (_, _) => Close(); footer.Children.Add(cancel); footer.Children.Add(_confirm); layout.Children.Add(footer);
        _action.SelectionChanged += (_, _) => Update(); _scope.SelectionChanged += (_, _) => Update(); _destination.TextChanged += (_, _) => Update(); _group.IsCheckedChanged += (_, _) => Update(); _remove.IsCheckedChanged += (_, _) => Update();
        _confirm.Click += (_, _) => { var options = ReadOptions(); if (_confirm.IsEnabled) Close(options); };
        browse.Click += async (_, _) =>
        {
            try
            {
                var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择现有导出目录" });
                if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path) _destination.Text = path;
            }
            catch (Exception ex) { _hint.Text = "目录选择未完成：" + ex.Message; }
        };
        Content = layout; Update();
    }
    /// <summary>读取并规范化删除模式的互斥选项，避免隐藏的勾选仍触发分类或复制后移除。</summary>
    private ExportOptions ReadOptions()
    {
        var choice = (ExportChoice)Math.Clamp(_action.SelectedIndex, 0, 4);
        return new(choice, (PhotoFileScope)Math.Clamp(_scope.SelectedIndex, 0, 2), _destination.Text?.Trim() ?? "", choice != ExportChoice.DeleteRejected && _remove.IsChecked == true, choice != ExportChoice.DeleteRejected && _group.IsChecked == true);
    }
    /// <summary>按真实可选原件和目标目录存在性启用操作，不预估空间、不创建目录，也不在窗口中修改文件。</summary>
    private void Update()
    {
        var options = ReadOptions(); bool any = _photos.Where(options.Includes).Any(p => p.Assets.Any(a => options.Scope == PhotoFileScope.All || (options.Scope == PhotoFileScope.JpgOnly ? a.Kind == PhotoAssetKind.Jpg : a.Kind == PhotoAssetKind.Raw)));
        _destinationPanel.IsVisible = !options.DeleteOnly; _warning.IsVisible = options.DeleteOnly || _remove.IsChecked == true;
        _hint.Text = options.GroupBySpecies ? "按有效鸟种创建子目录；多种鸟会各复制一份，没有有效鸟种进入“未分类”。分类导出会自动提醒待核对结果。" : "直接复制到所选现有目录，不自动创建文件夹。同名内容相同自动跳过，内容不同询问覆盖或重命名。";
        _confirm.Content = options.DeleteOnly ? "删除舍弃" : "开始导出"; _confirm.IsEnabled = any && (options.DeleteOnly || Directory.Exists(options.Destination));
    }
}
