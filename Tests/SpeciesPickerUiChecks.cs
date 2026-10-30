using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        foreach (string query in new[] { "歐亞大山雀", "欧亞大山雀", "ouyadashanque", "ＯＹＤＳＱ", "ōu yà dà shān què", "ou1-ya4-da4-shan1-que4" })
        {
            search.Text = query;
            Check(list.Items.Count == 1 && ReferenceEquals(list.SelectedItem, all[0]) && picker.SelectedSpecies is null,
                $"the real picker resolves {query} to the original species without applying it");
        }
        foreach (string query in new[] { "綠背山雀", "lvbeishanque", "lǜ bèi shān què", "lu:beishanque", "lbsq" })
        {
            search.Text = query;
            Check(list.Items.Count == 1 && ReferenceEquals(list.SelectedItem, all[1]),
                $"the picker accepts traditional spelling or pinyin variant {query}");
        }
        search.Text = "bai lu";
        Layout(surface, 640, 520);
        Save(surface, "species-picker-pinyin.png", 640, 520);
        Check(list.Items.Count == 1 && ReferenceEquals(list.SelectedItem, all[2]), "spaced pinyin reaches the displayed Chinese species row");
        CheckSearchTextRendering(picker, surface, Check);
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
              ((TextBlock)empty.FindName("EmptyMessageText")).Text.Contains("拼音或首字母", StringComparison.Ordinal) &&
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

    private static void CheckSearchTextRendering(SpeciesPickerDialog picker, FrameworkElement surface,
        Action<bool, string> check)
    {
        var search = (TextBox)picker.FindName("SearchBox");
        foreach (var (width, height, sizeName) in new[] { (640, 520, "normal"), (520, 380, "minimum") })
        {
            foreach (var (query, queryName) in new[] { ("欧亞大山雀", "chinese"), ("lǜ bèi shān què", "pinyin") })
            {
                search.Text = query;
                Layout(surface, width, height);
                Save(surface, $"species-picker-input-{queryName}-{sizeName}.png", width, height);
                var host = (ScrollViewer)search.Template.FindName("PART_ContentHost", search);
                var viewport = Descendants(host).OfType<ScrollContentPresenter>().First();
                bool visibleCharacters = true;
                for (int index = 0; index < query.Length; index++)
                {
                    Rect character = search.GetRectFromCharacterIndex(index);
                    Rect visible = character.IsEmpty ? Rect.Empty : search.TransformToVisual(viewport).TransformBounds(character);
                    visibleCharacters &= !visible.IsEmpty && visible.Height >= search.FontSize &&
                        visible.Top >= -.5 && visible.Bottom <= viewport.ActualHeight + .5 &&
                        visible.Left >= -.5 && visible.Right <= viewport.ActualWidth + .5;
                }
                check(visibleCharacters,
                    $"every {queryName} search character has a full-height line box inside its real text viewport at {width} × {height}");

                var reference = new TextBlock
                {
                    Text = query, FontFamily = search.FontFamily, FontSize = search.FontSize,
                    FontStyle = search.FontStyle, FontWeight = search.FontWeight, FontStretch = search.FontStretch,
                    Foreground = search.Foreground, FlowDirection = search.FlowDirection
                };
                reference.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                reference.Arrange(new Rect(reference.DesiredSize)); reference.UpdateLayout();
                Rect expectedInk = BrightInkBounds(reference);
                Rect renderedInk = BrightInkBounds(search, surface);
                if (renderedInk.IsEmpty || renderedInk.Height < expectedInk.Height - 1)
                    foreach (var item in Descendants(search).Prepend(search).OfType<FrameworkElement>())
                        Console.WriteLine($"  SEARCH VISUAL {item.GetType().Name} {item.Name}: render={item.RenderSize}, desired={item.DesiredSize}, " +
                            $"offset={VisualTreeHelper.GetOffset(item)}, clip={item.Clip?.Bounds}, layoutClip={LayoutInformation.GetLayoutClip(item)?.Bounds}, " +
                            $"drawing={VisualTreeHelper.GetDrawing(item)?.Bounds}");
                check(!expectedInk.IsEmpty && !renderedInk.IsEmpty && renderedInk.Height >= expectedInk.Height - 1 &&
                      renderedInk.Width >= expectedInk.Width - 2,
                    $"rendered {queryName} input retains the full reference glyph height and width instead of a clipped text strip at {width} × {height} " +
                    $"(reference {expectedInk.Width:0}×{expectedInk.Height:0}, actual {renderedInk.Width:0}×{renderedInk.Height:0})");
            }
        }
        Layout(surface, 640, 520);
    }

    private static Rect BrightInkBounds(FrameworkElement element, FrameworkElement? captureRoot = null)
    {
        captureRoot ??= element;
        int rootWidth = Math.Max(1, (int)Math.Ceiling(captureRoot.ActualWidth));
        int rootHeight = Math.Max(1, (int)Math.Ceiling(captureRoot.ActualHeight));
        var image = new RenderTargetBitmap(rootWidth, rootHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(captureRoot);
        // Capture the production tree, then crop in root coordinates. Rendering a nested
        // element directly can retain its parent offset and invent clipping in the test.
        Rect area = ReferenceEquals(element, captureRoot) ? new Rect(element.RenderSize)
            : element.TransformToAncestor(captureRoot).TransformBounds(new Rect(element.RenderSize));
        int cropLeft = Math.Max(0, (int)Math.Floor(area.Left));
        int cropTop = Math.Max(0, (int)Math.Floor(area.Top));
        int width = Math.Min(rootWidth, (int)Math.Ceiling(area.Right)) - cropLeft;
        int height = Math.Min(rootHeight, (int)Math.Ceiling(area.Bottom)) - cropTop;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        image.CopyPixels(new Int32Rect(cropLeft, cropTop, width, height), pixels, stride, 0);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int index = y * stride + x * 4;
            if (pixels[index] < 140 || pixels[index + 1] < 140 || pixels[index + 2] < 140) continue;
            left = Math.Min(left, x); right = Math.Max(right, x);
            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        return right < left ? Rect.Empty : new Rect(left, top, right - left + 1, bottom - top + 1);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
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
