using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

/// <summary>Exercises the local single-choice picker without displaying a desktop window.</summary>
internal static class SpeciesPickerUiChecks
{
    public static int Run()
    {
        if (Application.Current is null) throw new InvalidOperationException("Initialize App resources before picker checks.");
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK SPECIES PICKER " + description);
        }
        var all = new[]
        {
            new SpeciesIdentity("欧亚大山雀", "Parus major"), new SpeciesIdentity("绿背山雀", "Parus monticolus"),
            new SpeciesIdentity("白鹭", "Egretta garzetta"), new SpeciesIdentity("", "Testus blank")
        }.Concat(Enumerable.Range(4, 14).Select(index => new SpeciesIdentity("测试鸟" + index, "Testus species" + index))).ToArray();
        var recent = new[] { all[17], all[17], new SpeciesIdentity("旧中文名", all[16].ScientificName) }
            .Concat(all.Skip(4).Take(12).Reverse()).ToArray();
        var originalRecent = recent.ToArray();
        var picker = new SpeciesPickerDialog(all, recent, all[16]);
        var surface = Surface(picker, 640, 520);
        var search = (TextBox)picker.FindName("SearchBox");
        var list = (ListBox)picker.FindName("SpeciesList");
        var confirm = (Button)picker.FindName("ConfirmButton");
        Check(list.SelectionMode == SelectionMode.Single && list.Items.Count == 10 &&
              list.Items.Cast<SpeciesIdentity>().Select(species => species.Key).Distinct().Count() == 10 &&
              list.Items.Cast<SpeciesIdentity>().Take(2).SequenceEqual(new[] { all[17], all[16] }),
            "empty search shows at most ten distinct recent species in supplied recency order");
        Check(ReferenceEquals(list.SelectedItem, all[16]) && picker.SelectedSpecies is null && confirm.IsEnabled &&
              ((TextBlock)picker.FindName("CurrentSpeciesText")).Text.Contains(all[16].DisplayName, StringComparison.Ordinal),
            "initial selection is highlighted using the current catalog name without applying it");
        Check(((TextBlock)picker.FindName("ResultHeadingText")).Text == "最近选择" &&
              ((TextBlock)picker.FindName("EmptyMessageText")).Visibility == Visibility.Collapsed,
            "the recent list has a clear heading and no empty-state overlay");
        Save(surface, "species-picker-recent.png", 640, 520);

        search.Text = " 欧亚大山雀 ";
        Check(list.Items.Count == 1 && Equals(list.Items[0], all[0]) && confirm.IsEnabled && picker.SelectedSpecies is null,
            "trimmed Chinese-name search finds one candidate without committing data");
        search.Text = "pArUs";
        Check(list.Items.Count == 2 && list.Items.Contains(all[0]) && list.Items.Contains(all[1]) && list.SelectedIndex == 0,
            "scientific-name search is case-insensitive and makes keyboard confirmation available");
        Layout(surface, 640, 520);
        Save(surface, "species-picker-search.png", 640, 520);
        Check(picker.HandleDialogKey(Key.Enter) && picker.SelectedSpecies == all[0],
            "Enter confirms exactly the currently selected species");
        picker.CancelSelection();
        Check(picker.SelectedSpecies is null && recent.SequenceEqual(originalRecent),
            "cancel clears the proposal without modifying recent choices or caller data");

        search.Text = "no matching candidate";
        Check(list.Items.Count == 0 && !confirm.IsEnabled && !picker.TryConfirmSelection() && picker.SelectedSpecies is null &&
              ((TextBlock)picker.FindName("EmptyMessageText")).Visibility == Visibility.Visible,
            "no matches disables confirmation and explains how to continue searching");
        search.Text = "  ";
        Check(list.Items.Count == 10 && ((TextBlock)picker.FindName("ResultHeadingText")).Text == "最近选择",
            "clearing the query restores recent choices rather than the full catalog");
        search.Text = "testus blank";
        Check(list.Items.Count == 1 && ((SpeciesIdentity)list.Items[0]).DisplayName == "Testus blank",
            "a candidate without a Chinese name remains searchable by scientific name");
        Check(!picker.HandleDialogKey(Key.ImeProcessed) && !picker.HandleDialogKey(Key.F12) && picker.SelectedSpecies is null,
            "IME processing and unrelated keys cannot accidentally confirm a choice");
        Check(picker.HandleDialogKey(Key.Escape) && picker.SelectedSpecies is null,
            "Escape cancels a highlighted search result without applying it");

        search.Text = "白鹭";
        Layout(surface, 640, 520);
        var emptyDoubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Control.MouseDoubleClickEvent, Source = list };
        list.RaiseEvent(emptyDoubleClick);
        Check(picker.SelectedSpecies is null, "double-clicking list background does not commit an old highlighted row");
        var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        var rowDoubleClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        { RoutedEvent = Control.MouseDoubleClickEvent, Source = row };
        list.RaiseEvent(rowDoubleClick);
        Check(rowDoubleClick.Handled && picker.SelectedSpecies == all[2], "double-clicking a result row confirms that one species");

        var empty = new SpeciesPickerDialog(all, Array.Empty<SpeciesIdentity>());
        var emptySurface = Surface(empty, 520, 380);
        Check(((ListBox)empty.FindName("SpeciesList")).Items.Count == 0 &&
              ((TextBlock)empty.FindName("EmptyMessageText")).Text.Contains("输入中文名或学名", StringComparison.Ordinal) &&
              !((Button)empty.FindName("ConfirmButton")).IsEnabled && empty.SelectedSpecies is null,
            "a picker with no recent species starts with a search prompt and no implicit choice");
        var recentOnly = new SpeciesPickerDialog(Array.Empty<SpeciesIdentity>(), new[] { all[2] });
        Check(((ListBox)recentOnly.FindName("SpeciesList")).Items.Count == 1 && recentOnly.SelectedSpecies is null,
            "valid recent manual choices remain available even when absent from the current search catalog");
        foreach (var dialog in new[] { (picker, surface), (empty, emptySurface) })
        {
            foreach (string name in new[] { "CancelButton", "ConfirmButton" })
            {
                var button = (Button)dialog.Item1.FindName(name);
                Rect bounds = button.TransformToAncestor(dialog.Item2).TransformBounds(new Rect(button.RenderSize));
                Check(bounds.Left >= 0 && bounds.Right <= dialog.Item2.ActualWidth && bounds.Bottom <= dialog.Item2.ActualHeight && button.ActualWidth >= 50,
                    $"{name} remains fully visible at picker content width {dialog.Item2.ActualWidth:0}");
            }
        }
        Check(!picker.IsVisible && !empty.IsVisible, "picker checks never show a desktop window");
        return count;
    }

    private static Grid Surface(Window dialog, int width, int height)
    {
        var content = (FrameworkElement)dialog.Content;
        dialog.Content = null;
        var surface = new Grid { Background = dialog.Background };
        TextElement.SetFontFamily(surface, dialog.FontFamily);
        TextElement.SetFontSize(surface, dialog.FontSize);
        TextElement.SetForeground(surface, dialog.Foreground);
        surface.Children.Add(content);
        Layout(surface, width, height);
        return surface;
    }
    private static void Layout(FrameworkElement element, int width, int height)
    { element.Measure(new Size(width, height)); element.Arrange(new Rect(0, 0, width, height)); element.UpdateLayout(); }
    private static void Save(FrameworkElement surface, string name, int width, int height)
    {
        string? directory = Environment.GetEnvironmentVariable("BIRD_UI_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        System.IO.Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = System.IO.File.Create(System.IO.Path.Combine(directory, name));
        encoder.Save(output);
    }
}
