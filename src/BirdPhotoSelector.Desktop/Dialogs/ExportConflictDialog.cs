using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BirdPhotoSelector.Desktop.Dialogs;

/// <summary>仅处理同名但内容不同的文件，是否可覆盖由共享导出的实体保护结果决定。</summary>
public sealed class ExportConflictDialog : Window
{
    private readonly ExportConflict _conflict;
    private readonly CheckBox _remember = new() { Name = "RememberConflict", Content = "本次导出后面的重名照片使用相同操作", IsChecked = false };
    /// <summary>固定两条路径，禁止对受保护目标提供可执行覆盖按钮；关闭视为取消整个批次。</summary>
    public ExportConflictDialog(ExportConflict conflict)
    {
        _conflict = conflict; Title = "羽选 · 发现不同内容的同名照片"; Width = 660; CanResize = false; SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        layout.Children.Add(new TextBlock { Text = "发现不同内容的同名照片", FontSize = 21, FontWeight = FontWeight.SemiBold });
        layout.Children.Add(new TextBlock { Text = "原照片" }); layout.Children.Add(PathText(conflict.SourcePath));
        layout.Children.Add(new TextBlock { Text = "已有目标" }); layout.Children.Add(PathText(conflict.TargetPath));
        layout.Children.Add(new TextBlock { Text = conflict.CanOverwrite ? "可覆盖目标，或在文件名末尾追加 _1、_2…（已有数字后缀继续累加）。" : conflict.Explanation ?? "该目标属于受保护的原件或副本，只能重命名或取消。", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#E9C67E") });
        layout.Children.Add(_remember);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        AddChoice(buttons, "取消", ExportConflictAction.Cancel, true); AddChoice(buttons, "重命名", ExportConflictAction.Rename, true); AddChoice(buttons, "覆盖", ExportConflictAction.Overwrite, conflict.CanOverwrite);
        layout.Children.Add(buttons); Content = layout;
    }
    /// <summary>长路径可换行和复制，不因路径较长改变要操作的真实目标。</summary>
    private static Control PathText(string text) => new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, MaxHeight = 130 };
    /// <summary>记住选项只随本次返回值传递，取消不记住，禁止越过 CanOverwrite 保护。</summary>
    private void AddChoice(StackPanel panel, string label, ExportConflictAction action, bool enabled)
    {
        var button = new Button { Name = "Conflict" + action, Content = label, IsEnabled = enabled, IsCancel = action == ExportConflictAction.Cancel, IsDefault = action == ExportConflictAction.Rename };
        button.Click += (_, _) => { if (action != ExportConflictAction.Overwrite || _conflict.CanOverwrite) Close(new ExportConflictDecision(action, action != ExportConflictAction.Cancel && _remember.IsChecked == true)); }; panel.Children.Add(button);
    }
}
