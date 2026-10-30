using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BirdPhotoSelector;

/// <summary>A single local-catalog choice. The owner applies it only after confirmation.</summary>
public partial class SpeciesPickerDialog : Window
{
    private readonly SpeciesIdentity[] _allSpecies;
    private readonly SpeciesSearchIndex _search;
    private readonly SpeciesIdentity[] _recentSpecies;
    private readonly SpeciesIdentity? _initial;
    private bool _confirmed;

    public SpeciesIdentity? SelectedSpecies { get; private set; }

    public SpeciesPickerDialog(IReadOnlyList<SpeciesIdentity> allSpecies,
        IReadOnlyList<SpeciesIdentity> recentSpecies, SpeciesIdentity? initial = null)
    {
        ArgumentNullException.ThrowIfNull(allSpecies);
        ArgumentNullException.ThrowIfNull(recentSpecies);
        _allSpecies = UniqueValid(allSpecies).ToArray();
        _search = new(_allSpecies);
        var catalog = _allSpecies.ToDictionary(species => species.Key, StringComparer.OrdinalIgnoreCase);
        _recentSpecies = UniqueValid(recentSpecies)
            .Select(species => catalog.GetValueOrDefault(species.Key) ?? species).Take(10).ToArray();
        _initial = initial;
        InitializeComponent();
        if (initial is { IsValid: true })
        {
            CurrentSpeciesText.Text = $"当前：{initial.DisplayName} · {initial.ScientificName}";
            CurrentSpeciesText.Visibility = Visibility.Visible;
        }
        RefreshResults();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private static IEnumerable<SpeciesIdentity> UniqueValid(IEnumerable<SpeciesIdentity> species)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in species)
            if (entry is { IsValid: true } && keys.Add(entry.Key)) yield return entry;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SpeciesList is not null) RefreshResults();
    }

    private void RefreshResults()
    {
        string query = SearchBox.Text.Trim();
        bool recent = query.Length == 0;
        var matches = recent ? _recentSpecies : _search.Search(query).ToArray();
        SpeciesList.ItemsSource = matches;
        SpeciesList.SelectedItem = _initial is not null
            ? matches.FirstOrDefault(species => StringComparer.OrdinalIgnoreCase.Equals(species.Key, _initial.Key)) : null;
        if (!recent && SpeciesList.SelectedIndex < 0 && matches.Length > 0) SpeciesList.SelectedIndex = 0;
        ResultHeadingText.Text = recent ? "最近选择" : "搜索结果";
        ResultCountText.Text = $"{matches.Length} 种";
        EmptyMessageText.Text = recent ? "还没有最近选择，输入中文名、学名、拼音或首字母搜索。" : "没有匹配的鸟种，请调整关键词。";
        EmptyMessageText.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ConfirmButton.IsEnabled = SpeciesList.SelectedItem is SpeciesIdentity;
    }

    private void SpeciesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConfirmButton is not null) ConfirmButton.IsEnabled = SpeciesList.SelectedItem is SpeciesIdentity;
    }

    public bool TryConfirmSelection()
    {
        if (SpeciesList.SelectedItem is not SpeciesIdentity { IsValid: true } selected) return false;
        SelectedSpecies = selected;
        _confirmed = true;
        if (IsVisible) DialogResult = true;
        return true;
    }

    public void CancelSelection()
    {
        SelectedSpecies = null;
        _confirmed = false;
        if (IsVisible) DialogResult = false;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_confirmed) SelectedSpecies = null;
        base.OnClosing(e);
    }

    private void SpeciesList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && e.OriginalSource is DependencyObject source &&
            ItemsControl.ContainerFromElement(SpeciesList, source) is ListBoxItem)
            e.Handled = TryConfirmSelection();
    }

    public bool HandleDialogKey(Key key)
    {
        if (key == Key.Escape) { CancelSelection(); return true; }
        return key == Key.Enter && TryConfirmSelection();
    }

    private void Dialog_KeyDown(object sender, KeyEventArgs e) => e.Handled = HandleDialogKey(e.Key);

    private void Confirm_Click(object sender, RoutedEventArgs e) => TryConfirmSelection();
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelSelection();
}
