using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

/// <summary>Verifies the real export dialog's independent rating and member scopes without touching originals.</summary>
internal static class ExportScopeUiChecks
{
    public static int Run()
    {
        if (Application.Current is null) throw new InvalidOperationException("Initialize App resources before export dialog checks.");
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
            Console.WriteLine("  OK EXPORT SCOPE UI " + message);
        }

        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.ExportScopeUiChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var dialogs = new List<ExportDialog>();
        try
        {
            PhotoItem Photo(string name, PhotoRating rating, params PhotoAssetKind[] kinds)
            {
                var photo = new PhotoItem { Rating = rating };
                photo.SetAssets(kinds.Select(kind => new PhotoAsset(Path.Combine(root, name +
                    (kind == PhotoAssetKind.Jpg ? ".JPG" : ".NEF")), kind)));
                return photo;
            }

            // Member records need not contain decodable pixels to inspect the dialog.
            // There are deliberately no source files and no file-operation methods are invoked.
            var favoritePair = Photo("paired", PhotoRating.Favorite, PhotoAssetKind.Jpg, PhotoAssetKind.Raw);
            var keptJpg = Photo("jpg-only", PhotoRating.Keep, PhotoAssetKind.Jpg);
            var favoriteRaw = Photo("raw-only", PhotoRating.Favorite, PhotoAssetKind.Raw);
            var rejectedPair = Photo("rejected", PhotoRating.Reject, PhotoAssetKind.Jpg, PhotoAssetKind.Raw);
            var unmarked = Photo("unmarked", PhotoRating.Unmarked, PhotoAssetKind.Raw);
            var dialog = new ExportDialog(new[] { favoritePair, keptJpg, favoriteRaw, rejectedPair, unmarked });
            dialogs.Add(dialog);
            var actions = (ComboBox)dialog.FindName("ActionChoice");
            var scopes = (ComboBox)dialog.FindName("FileScopeChoice");
            var confirm = (Button)dialog.FindName("ConfirmButton");
            var destination = (TextBox)dialog.FindName("DestinationBox");
            var removeOriginal = (CheckBox)dialog.FindName("RemoveOriginalBox");
            var surface = Surface(dialog);

            Check(scopes.Items.Count == 3 && scopes.SelectedIndex == 0 && dialog.Scope == PhotoFileScope.All &&
                  dialog.FileScope == PhotoFileScope.All && !dialog.DeleteOnly && !dialog.RemoveOriginal,
                "one selector defaults to all member types while export defaults to retaining originals");
            Check(dialog.SelectedPhotoCount == 3 && dialog.SelectedFileCount == 4 &&
                  dialog.Includes(favoritePair) && dialog.Includes(keptJpg) && dialog.Includes(favoriteRaw) &&
                  !dialog.Includes(rejectedPair) && !dialog.Includes(unmarked),
                "default rating selection counts a JPG/RAW pair once and counts both managed files");
            Check(!confirm.IsEnabled, "export remains disabled until an existing destination is selected");
            string missingDestination = Path.Combine(root, "not-created");
            destination.Text = missingDestination;
            Check(!confirm.IsEnabled && !Directory.Exists(missingDestination),
                "an absent destination is not created and cannot enable export");
            destination.Text = root;
            Check(confirm.IsEnabled && dialog.Destination == root,
                "choosing an existing directory enables export without changing file scope");

            scopes.SelectedIndex = 1;
            Check(dialog.Scope == PhotoFileScope.JpgOnly && dialog.ScopeLabel == "仅 JPG" &&
                  dialog.SelectedPhotoCount == 2 && dialog.SelectedFileCount == 2 && actions.SelectedIndex == 0 &&
                  dialog.Includes(favoriteRaw),
                "JPG-only selects matching members and leaves the independent rating predicate unchanged");
            scopes.SelectedIndex = 2;
            Check(dialog.Scope == PhotoFileScope.RawOnly && dialog.ScopeLabel == "仅 RAW" &&
                  dialog.SelectedPhotoCount == 2 && dialog.SelectedFileCount == 2 && actions.SelectedIndex == 0 &&
                  ((ComboBoxItem)scopes.SelectedItem).Content?.ToString() == "仅 RAW",
                "the single selected RAW-only option excludes a standalone JPG from the processing count");
            Check(((TextBlock)dialog.FindName("CountText")).Text.Contains("2 张照片 · 2 个文件 · 仅 RAW", StringComparison.Ordinal),
                "the visible summary distinguishes logical photos, files and the chosen member scope");

            actions.SelectedIndex = 1;
            Check(dialog.Scope == PhotoFileScope.RawOnly && dialog.SelectedPhotoCount == 2 &&
                  dialog.SelectedFileCount == 2 && !dialog.Includes(keptJpg),
                "favorite-only selection combines with RAW-only scope without resetting it");
            scopes.SelectedIndex = 1;
            Check(dialog.SelectedPhotoCount == 1 && dialog.SelectedFileCount == 1 && actions.SelectedIndex == 1,
                "favorite JPG-only processing excludes the favorite RAW-only photo");
            scopes.SelectedIndex = 0;
            Check(dialog.SelectedPhotoCount == 2 && dialog.SelectedFileCount == 3 && dialog.Scope == PhotoFileScope.All,
                "returning to all files keeps the favorite filter and restores both members of a pair");

            removeOriginal.IsChecked = true;
            actions.SelectedIndex = 3;
            scopes.SelectedIndex = 2;
            Layout(surface);
            Check(dialog.DeleteOnly && !dialog.RemoveOriginal && dialog.SelectedPhotoCount == 1 &&
                  dialog.SelectedFileCount == 1 && confirm.IsEnabled && confirm.Content?.ToString() == "删除舍弃" &&
                  ((StackPanel)dialog.FindName("DestinationPanel")).Visibility == Visibility.Collapsed &&
                  ((TextBlock)dialog.FindName("DeleteWarning")).Visibility == Visibility.Visible,
                "delete-rejected mode applies RAW-only independently and does not require an export destination");
            destination.Text = missingDestination;
            Check(confirm.IsEnabled && !Directory.Exists(missingDestination) &&
                  ((TextBlock)dialog.FindName("DeleteWarning")).Text.Contains("剩余关联文件", StringComparison.Ordinal),
                "delete mode keeps a visible remaining-member warning and never creates the unused destination");
            SaveScreenshot(surface, "export-delete-raw-scope.png");

            actions.SelectedIndex = 0;
            destination.Text = root;
            Layout(surface);
            Check(!dialog.DeleteOnly && dialog.RemoveOriginal && scopes.SelectedIndex == 2 && confirm.IsEnabled,
                "returning to export retains both the member scope and the explicit remove-original preference");
            foreach (var control in Descendants(surface).OfType<Control>().Where(control =>
                         control is Button || control is ComboBox || control is CheckBox))
            {
                Rect bounds = control.TransformToAncestor(surface).TransformBounds(new Rect(control.RenderSize));
                Check(control.ActualWidth > 0 && control.ActualHeight > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
                      bounds.Right <= 645 && bounds.Bottom <= 401,
                    $"the scope dialog's {control.Name ?? control.GetType().Name} fits the compact 644 × 400 content surface");
            }
            var removalBounds = removeOriginal.TransformToAncestor(surface).TransformBounds(new Rect(removeOriginal.RenderSize));
            var memberArea = (FrameworkElement)((FrameworkElement)dialog.FindName("DestinationPanel")).Parent;
            var areaBounds = memberArea.TransformToAncestor(surface).TransformBounds(new Rect(memberArea.RenderSize));
            Check(removalBounds.Bottom <= areaBounds.Bottom + 1 && removalBounds.Top >= areaBounds.Top,
                "the remove-original checkbox stays completely inside its allocated row without clipping under the footer");
            SaveScreenshot(surface, "export-raw-scope.png");

            var noMatching = new ExportDialog(new[] { keptJpg });
            dialogs.Add(noMatching);
            ((ComboBox)noMatching.FindName("FileScopeChoice")).SelectedIndex = 2;
            ((TextBox)noMatching.FindName("DestinationBox")).Text = root;
            Check(noMatching.SelectedPhotoCount == 0 && noMatching.SelectedFileCount == 0 &&
                  !((Button)noMatching.FindName("ConfirmButton")).IsEnabled,
                "an existing destination does not enable a scope containing no matching members");
            ((ComboBox)noMatching.FindName("ActionChoice")).SelectedIndex = 3;
            Check(!((Button)noMatching.FindName("ConfirmButton")).IsEnabled,
                "delete remains disabled when neither the rating nor the selected member type matches");
            return count;
        }
        finally
        {
            foreach (var dialog in dialogs) dialog.Close();
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe export UI test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static Grid Surface(ExportDialog dialog)
    {
        var content = (FrameworkElement)dialog.Content;
        dialog.Content = null;
        var surface = new Grid { Background = dialog.Background };
        TextElement.SetFontFamily(surface, dialog.FontFamily);
        TextElement.SetFontSize(surface, dialog.FontSize);
        TextElement.SetForeground(surface, dialog.Foreground);
        surface.Children.Add(content);
        Layout(surface);
        return surface;
    }

    private static void Layout(FrameworkElement surface)
    {
        surface.Width = 644; surface.Height = 400;
        surface.Measure(new Size(644, 400)); surface.Arrange(new Rect(0, 0, 644, 400)); surface.UpdateLayout();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void SaveScreenshot(FrameworkElement surface, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("BIRD_UI_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(644, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, name)); encoder.Save(output);
    }
}
