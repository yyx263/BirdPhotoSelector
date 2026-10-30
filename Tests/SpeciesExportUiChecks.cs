using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using BirdPhotoSelector;

internal static class SpeciesExportUiChecks
{
    public static int Run()
    {
        if (Application.Current is null) throw new InvalidOperationException("Initialize application resources first.");
        int count = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); count++; Console.WriteLine("  OK SPECIES EXPORT UI " + message); }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SpeciesExportUiChecks");
        string root = Directory.CreateDirectory(Path.Combine(allowed, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var photo = new PhotoItem { Rating = PhotoRating.Favorite };
            photo.SetAssets(new[] { new PhotoAsset(Path.Combine(root, "scene.JPG"), PhotoAssetKind.Jpg),
                new PhotoAsset(Path.Combine(root, "scene.NEF"), PhotoAssetKind.Raw) });
            var dialog = new ExportDialog(new[] { photo });
            var content = (FrameworkElement)dialog.Content; dialog.Content = null;
            var surface = new Grid { Background = dialog.Background }; surface.Children.Add(content);
            TextElement.SetFontFamily(surface, dialog.FontFamily); TextElement.SetFontSize(surface, dialog.FontSize);
            TextElement.SetForeground(surface, dialog.Foreground);
            var group = (CheckBox)dialog.FindName("GroupBySpeciesBox");
            var action = (ComboBox)dialog.FindName("ActionChoice");
            var scope = (ComboBox)dialog.FindName("FileScopeChoice");
            var destination = (TextBox)dialog.FindName("DestinationBox");
            var hint = (TextBlock)dialog.FindName("FolderHint");
            var remove = (CheckBox)dialog.FindName("RemoveOriginalBox");
            var confirm = (Button)dialog.FindName("ConfirmButton");
            Layout();
            Check(!dialog.GroupBySpecies && group.IsChecked == false && remove.IsChecked == false &&
                  hint.Text.Contains("不创建子目录") && dialog.SelectedFileCount == 2,
                "species classification defaults off independently of original removal and preserves flat export scope");
            destination.Text = root; group.IsChecked = true; Layout();
            Check(dialog.GroupBySpecies && hint.Text.Contains("按鸟种") && remove.Content.ToString()!.Contains("全部目标副本") &&
                  dialog.SelectedPhotoCount == 1 && dialog.SelectedFileCount == 2 && confirm.IsEnabled,
                "enabling classification updates destination and removal explanations without changing rating or member counts");
            scope.SelectedIndex = 2; Layout();
            Check(dialog.GroupBySpecies && dialog.Scope == PhotoFileScope.RawOnly && dialog.SelectedFileCount == 1,
                "classification combines with the existing single RAW member scope");
            foreach (FrameworkElement element in new FrameworkElement[] { group, remove, destination, confirm })
            {
                Rect bounds = element.TransformToAncestor(surface).TransformBounds(new Rect(element.RenderSize));
                Check(bounds.Top >= 0 && bounds.Bottom <= 401 && bounds.Right <= 645 && element.ActualHeight > 0,
                    element.Name + " remains usable in the compact 644 x 400 export surface");
            }
            photo.Rating = PhotoRating.Reject; action.SelectedIndex = 3; Layout();
            Check(dialog.DeleteOnly && !dialog.GroupBySpecies && !dialog.RemoveOriginal &&
                  ((StackPanel)dialog.FindName("DestinationPanel")).Visibility == Visibility.Collapsed &&
                  ((TextBlock)dialog.FindName("DeleteWarning")).Visibility == Visibility.Visible,
                "delete-only hides the classification checkbox and cannot turn a delete into a multi-target operation");
            action.SelectedIndex = 2; group.IsChecked = false; Layout();
            Check(!dialog.GroupBySpecies && hint.Text.Contains("不创建子目录") && dialog.Scope == PhotoFileScope.RawOnly,
                "returning to flat export restores its original folder behavior without resetting member scope");
            var fresh = new ExportDialog(new[] { photo });
            Check(!fresh.GroupBySpecies && ((CheckBox)fresh.FindName("GroupBySpeciesBox")).IsChecked == false,
                "every new export dialog starts with classification unchecked");
            return count;

            void Layout()
            {
                surface.Width = 644; surface.Height = 400;
                surface.Measure(new Size(644, 400)); surface.Arrange(new Rect(0, 0, 644, 400)); surface.UpdateLayout();
            }
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe UI fixture cleanup.");
            Directory.Delete(root, true);
        }
    }
}
