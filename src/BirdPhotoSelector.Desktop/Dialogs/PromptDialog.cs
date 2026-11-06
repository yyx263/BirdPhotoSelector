using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace BirdPhotoSelector.Desktop.Dialogs;

/// <summary>通用选择结果，关闭窗口等同于取消；按钮文字由具体操作提供。</summary>
public enum PromptResult { Cancel, Continue, Save }

/// <summary>异步模态提示，不运行嵌套同步消息循环；调用方必须确认所属主窗口仍可显示对话框。</summary>
public sealed class PromptDialog : Window
{
    /// <summary>固定内容和选择，不捕获照片或后台任务，关闭后不留下事件订阅。</summary>
    private PromptDialog(string title, string text, IReadOnlyList<(string Label, PromptResult Result)> choices)
    {
        Title = "羽选 · " + title; Width = 540; MinWidth = 400; MaxHeight = 720;
        SizeToContent = SizeToContent.Height; CanResize = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new StackPanel { Margin = new Thickness(24), Spacing = 20 };
        layout.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold });
        layout.Children.Add(new ScrollViewer { MaxHeight = 460, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 } });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var choice in choices)
        {
            var button = new Button { Content = choice.Label, IsDefault = choice.Result == PromptResult.Save || choices.Count == 1, IsCancel = choice.Result == PromptResult.Cancel };
            button.Click += (_, _) => Close(choice.Result); buttons.Children.Add(button);
        }
        layout.Children.Add(buttons); Content = layout;
    }
    /// <summary>用普通任务返回选择；点击标题栏关闭按钮返回 Cancel，不代替用户同意。</summary>
    public static Task<PromptResult> AskAsync(Window owner, string title, string message, params (string Label, PromptResult Result)[] choices) =>
        new PromptDialog(title, message, choices).ShowDialog<PromptResult>(owner);
}
