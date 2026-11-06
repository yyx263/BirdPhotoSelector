using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace BirdPhotoSelector.Desktop.Dialogs;

/// <summary>离线单选鸟种窗口，只返回明确确认的身份；最近选择由工程会话提供，不加载识别模型。</summary>
public sealed class SpeciesPickerDialog : Window
{
    private readonly SpeciesSearchIndex _search;
    private readonly SpeciesIdentity[] _recent;
    private readonly SpeciesIdentity? _initial;
    private readonly TextBox _query = new() { Name = "SpeciesQuery", MinHeight = 48, FontSize = 16, Padding = new Thickness(12, 8), VerticalContentAlignment = VerticalAlignment.Center, PlaceholderText = "中文 / 学名 / 拼音 / 首字母" };
    private readonly ListBox _results = new() { Name = "SpeciesChoices" };
    private readonly TextBlock _heading = new() { FontWeight = FontWeight.SemiBold };
    private readonly TextBlock _empty = new() { Foreground = Brush.Parse("#95AAB5"), TextWrapping = TextWrapping.Wrap };
    private readonly Button _confirm = new() { Name = "ConfirmSpecies", Content = "添加鸟种", IsDefault = true };
    /// <summary>固定有效名录与最近十个身份，输入简繁/声调/拼音规则由 Catalog 统一处理。</summary>
    public SpeciesPickerDialog(IReadOnlyList<SpeciesIdentity> records, IReadOnlyList<SpeciesIdentity> recent, SpeciesIdentity? initial = null)
    {
        _search = new(records); _recent = recent.Where(s => s.IsValid).DistinctBy(s => s.Key).Take(10).ToArray(); _initial = initial;
        Title = "羽选 · 选择鸟种"; Width = 700; Height = 640; MinWidth = 540; MinHeight = 460; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { RowDefinitions = new("Auto,Auto,Auto,*,Auto"), Margin = new Thickness(24) };
        grid.Children.Add(new TextBlock { Text = initial is null ? "选择鸟种" : "更换鸟种 · " + initial.DisplayName, FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 18) });
        var search = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 16) };
        search.Children.Add(new TextBlock { Text = "中文 / 学名 / 拼音 / 首字母（支持简繁体）", Foreground = Brush.Parse("#9EB4C0") }); search.Children.Add(_query); Grid.SetRow(search, 1); grid.Children.Add(search);
        Grid.SetRow(_heading, 2); grid.Children.Add(_heading);
        var body = new Grid { Margin = new Thickness(0, 10, 0, 14) }; body.Children.Add(_results); body.Children.Add(_empty); Grid.SetRow(body, 3); grid.Children.Add(body);
        _results.ItemTemplate = new FuncDataTemplate<SpeciesIdentity>((species, _) =>
        {
            var row = new StackPanel { Margin = new Thickness(10, 6), Spacing = 3 };
            row.Children.Add(new TextBlock { Text = species?.DisplayName, FontSize = 15, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = species?.ScientificName, Foreground = Brush.Parse("#9BAFBA"), FontSize = 12, FontStyle = FontStyle.Italic }); return row;
        });
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        var cancel = new Button { Content = "取消", IsCancel = true }; cancel.Click += (_, _) => Close(); footer.Children.Add(cancel); footer.Children.Add(_confirm); Grid.SetRow(footer, 4); grid.Children.Add(footer);
        _query.TextChanged += (_, _) => RefreshResults(); _results.SelectionChanged += (_, _) => _confirm.IsEnabled = _results.SelectedItem is SpeciesIdentity;
        _confirm.Click += (_, _) => Confirm(); _results.DoubleTapped += (_, e) => { if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>() is not null) Confirm(); };
        if (initial is not null) _confirm.Content = "更换鸟种";
        Content = grid; RefreshResults(); Opened += (_, _) => _query.Focus();
    }
    /// <summary>空输入只展示最近十种；有关键词时按共享检索结果排序，不支持多选。</summary>
    private void RefreshResults()
    {
        string query = _query.Text?.Trim() ?? "";
        var matches = query.Length == 0 ? _recent : _search.Search(query).ToArray();
        _results.ItemsSource = matches;
        _results.SelectedItem = _initial is null ? null : matches.FirstOrDefault(s => s.Key == _initial.Key);
        if (query.Length > 0 && _results.SelectedIndex < 0 && matches.Length > 0) _results.SelectedIndex = 0;
        _heading.Text = (query.Length == 0 ? "最近选择" : "搜索结果") + $" · {matches.Length} 种";
        _empty.Text = query.Length == 0 ? "还没有最近选择，请输入名称、拼音或首字母搜索。" : "没有匹配的鸟种，请调整关键词。";
        _empty.IsVisible = matches.Length == 0; _confirm.IsEnabled = _results.SelectedItem is SpeciesIdentity;
    }
    /// <summary>确认后才向所属窗口移交所选身份，关闭标题栏始终视为取消。</summary>
    private void Confirm() { if (_results.SelectedItem is SpeciesIdentity { IsValid: true } species) Close(species); }
}
