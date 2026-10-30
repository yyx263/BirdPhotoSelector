using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BirdPhotoSelector;
using BirdPhotoSelector.Controls;

/// <summary>Renders the production window's real content and resources without opening a desktop window.</summary>
internal static class MainWindowUiChecks
{
    public static int Run()
    {
        if (Application.Current is null) throw new InvalidOperationException("Initialize App resources before UI checks.");
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK WINDOW " + description);
        }
        count += BalancedMetadataPanelChecks.Run();

        string fixtureRoot = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.WindowUiChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        MainViewModel? model = null;
        try
        {
            string[] photos = CreateFixtures(fixtureRoot);
            var imported = new PhotoImportService().ImportAsync(photos, Array.Empty<string>()).GetAwaiter().GetResult();
            Check(imported.Added.Count == 3, "three real JPEG fixtures import for full-window layout");
            Check(imported.Added.All(photo => photo.Rating == PhotoRating.Unmarked), "new photos start unmarked");
            var first = imported.Added[0];
            var second = imported.Added[1];
            var third = imported.Added[2];
            first.Rating = PhotoRating.Favorite;
            second.Rating = PhotoRating.Keep;
            third.Rating = PhotoRating.Reject;
            foreach (var photo in imported.Added)
            {
                photo.Bird = new BirdBox { X = .282543, Y = .323793, Width = .673815, Height = .547733, Confidence = .913758 };
                photo.DetectionStatus = "鸟类已定位 · 界面测试示例";
                photo.Metadata = new PhotoMetadata
                {
                    CapturedAt = "2026-10-17 08:24:16", Camera = "Canon EOS R5", FocalLength = "600 mm",
                    Aperture = "f/6.3", Shutter = "1/2500 s", Iso = "800", ExposureBias = "−0.3 EV"
                };
            }
            var sequence = new PhotoGroup("连拍测试");
            sequence.Photos.Add(first); sequence.Photos.Add(second);
            var single = new PhotoGroup("独立照片测试");
            single.Photos.Add(third);

            var window = new MainWindow();
            // Production preview continuations remain on this STA dispatcher. Tests explicitly
            // settle queued work without showing a native window and invalidate it before leaving.
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
            model = (MainViewModel)window.DataContext;
            model.SetGroups(new[] { sequence, single });
            model.ProjectName = "界面布局测试";
            model.Status = "布局验证 · 示例照片与拍摄参数 · 所有操作均未修改原文件";
            model.Dirty = false;
            var root = (Grid)window.Content;
            root.Background = window.Background;
            var viewer = (DualPhotoViewer)window.FindName("Viewer");
            var groupList = (ListBox)window.FindName("GroupList");
            var photoList = (ListBox)window.FindName("PhotoList");
            var bitmap = ImageLoader.Load(first.FilePath);
            viewer.ShowPhoto(first, bitmap);
            model.IsPreviewReady = true;
            Layout(root, 1560, 860);
            SettleBindings(root);

            Check(window.Background is SolidColorBrush background && background.Color == Color.FromRgb(17, 22, 28),
                "production App resources apply the charcoal window theme");
            Check(window.FontFamily.Source.Contains("Microsoft YaHei", StringComparison.Ordinal),
                "production window uses the Chinese-capable font stack");
            Check(groupList.Items.Count == 2 && photoList.Items.Count == 2,
                "real window bindings populate both group and in-group lists");
            Check(ReferenceEquals(photoList.SelectedItem, model.CurrentPhoto),
                "initial photo-list selection matches the displayed photo");
            var groupingThreshold = (Slider)window.FindName("GroupingThresholdSlider");
            Check(groupingThreshold.Minimum == 0 && groupingThreshold.Maximum == 1 &&
                  groupingThreshold.Value == .5 && model.Threshold == .5,
                "the real grouping slider exposes the full recalibrated range and defaults to 50%");
            string thresholdTip = groupingThreshold.ToolTip?.ToString() ?? "";
            Check(thresholdTip.Contains("50%", StringComparison.Ordinal) && thresholdTip.Contains("95%", StringComparison.Ordinal),
                "the real threshold control explains that the new midpoint equals the former 95% setting");
            var recognizeButton = (Button)window.FindName("RecognizeSpeciesButton");
            string recognitionTip = recognizeButton.ToolTip?.ToString() ?? "";
            Check(recognitionTip.Contains("无需先分组", StringComparison.Ordinal) && recognitionTip.Contains("定位", StringComparison.Ordinal),
                "the recognition action explains that bird localization runs without prior grouping");
            Check(window.FindName("IncludeRawCheckBox") is null,
                "automatic JPG/RAW pairing needs no import checkbox in the production window");
            var clearRecognition = (Button)window.FindName("ClearRecognitionResultsButton");
            var reidentify = (Button)window.FindName("ReidentifyCurrentPhotoButton");
            Check(clearRecognition.IsEnabled && clearRecognition.Content?.ToString() == "清空识别结果" &&
                  clearRecognition.ToolTip?.ToString()?.Contains("撤销", StringComparison.Ordinal) == true &&
                  clearRecognition.TransformToAncestor(root).TransformBounds(new Rect(clearRecognition.RenderSize)).Right <=
                      reidentify.TransformToAncestor(root).TransformBounds(new Rect(reidentify.RenderSize)).Left + 1,
                "the editable current photo exposes undoable clear-results immediately before single-photo recognition");
            string reidentifyTip = reidentify.ToolTip?.ToString() ?? "";
            Check(reidentifyTip.Contains("点击即运行", StringComparison.Ordinal) && reidentifyTip.Contains("Ctrl+Z", StringComparison.Ordinal) &&
                  reidentifyTip.Contains("手动选区", StringComparison.Ordinal) && reidentifyTip.Contains("失败或取消保留", StringComparison.Ordinal),
                "single-photo recognition explains immediate execution, replacement, undo and retained results on failure");
            var companion = new PhotoAsset(Path.ChangeExtension(first.FilePath, ".NEF"), PhotoAssetKind.Raw);
            first.AddAsset(companion);
            SettleBindings(root);
            Check(((TextBlock)window.FindName("MetadataFormat")).Text.Contains("JPG + RAW", StringComparison.Ordinal) &&
                  ((TextBlock)window.FindName("MetadataFormat")).Text.Contains(".NEF", StringComparison.Ordinal) &&
                  ((TextBlock)window.FindName("PreviewSourceText")).Text == "预览：JPG" && model.PhotoCount == 3,
                "the parameter bar shows paired format and RAW extension while one logical photo still previews JPG");
            first.RemoveAsset(companion.Id);
            model.Dirty = false;
            Check(((TextBlock)viewer.FindName("DetectionText")).Text.Contains("91%", StringComparison.Ordinal),
                "bird confidence reaches the real detail-pane label");
            foreach (string field in new[] { "CapturedAt", "Camera", "FocalLength", "Aperture", "Shutter", "Iso", "ExposureBias" })
                Check(((TextBlock)window.FindName("Metadata" + field)).FontSize >= 13,
                    $"metadata {field} uses readable 13-point text");
            TestSpeciesResults(window, root, model, viewer, first, bitmap, Check);
            TestNativeManualRecognition(window, root, model, viewer, first, bitmap, Check);
            TestRegionDeletion(window, root, model, viewer, first, bitmap, Check);
            TestLongSpeciesResults(window, root, model, viewer, first, bitmap, Check);
            TestFourVisibleSpeciesResults(window, root, model, viewer, first, bitmap, Check);
            TestRatingAndNavigationModel(Check);

            groupList.SelectedItems.Clear();
            groupList.SelectedItems.Add(model.Groups[0]);
            groupList.SelectedItems.Add(model.Groups[1]);
            Check(groupList.SelectionMode == SelectionMode.Extended && groupList.SelectedItems.Count == 2,
                "real group list retains extended selection for bulk removal");
            photoList.SelectedItems.Clear();
            photoList.SelectedItems.Add(first);
            photoList.SelectedItems.Add(second);
            Check(photoList.SelectionMode == SelectionMode.Extended && photoList.SelectedItems.Count == 2,
                "real in-group list retains extended selection for bulk removal");

            // Restore a single current photo for input and screenshot assertions.
            groupList.SelectedItems.Clear();
            model.CurrentGroup = model.Groups[0];
            photoList.SelectedItems.Clear();
            model.CurrentPhoto = first;
            viewer.ShowPhoto(first, bitmap);
            model.IsPreviewReady = true;
            Layout(root, 1560, 860);
            var fullViewport = (Border)viewer.FindName("FullViewport");
            var wheelOutside = Wheel(-120);
            fullViewport.RaiseEvent(wheelOutside);
            Check(!wheelOutside.Handled && ReferenceEquals(model.CurrentPhoto, first),
                "wheel outside the detail preview does not switch the selected photo");
            viewer.ShowPhoto(first, bitmap);
            model.IsPreviewReady = true;
            var detailViewport = (Border)viewer.FindName("DetailViewport");
            double zoomBefore = first.ViewZoom!.Value;
            var wheelInside = Wheel(120);
            detailViewport.RaiseEvent(wheelInside);
            Check(wheelInside.Handled && ReferenceEquals(model.CurrentPhoto, first) && first.ViewZoom > zoomBefore,
                "window lets detail pixels consume wheel input for zoom without switching photos");

            TestRatingControls(window, model, first, second, Check);
            TestContextMenus(groupList, photoList, model, first, second, third, Check);
            TestPreviewLoading(model, viewer, first, fixtureRoot, Check);
            TestBoundSelection(root, groupList, photoList, model, first, second, third, Check);
            TestListScrolling(root, groupList, photoList, model, first, Check);
            model.SetGroups(new[] { sequence, single });
            first.Rating = PhotoRating.Favorite; second.Rating = PhotoRating.Keep; third.Rating = PhotoRating.Reject;

            model.CurrentPhoto = first;
            viewer.ShowPhoto(first, bitmap);
            model.IsPreviewReady = true;
            model.Status = "布局验证 · 示例照片与拍摄参数 · 所有操作均未修改原文件";
            model.Dirty = false;
            Layout(root, 1560, 860);
            SettleBindings(root);
            Check(ReferenceEquals(photoList.SelectedItem, model.CurrentPhoto),
                "rebuilt thumbnail lists retain the displayed photo's selected highlight");
            model.CurrentGroup = model.Groups[1];
            Layout(root, 1560, 860);
            var boundChanges = new List<PhotoItem?>();
            void BoundChanged(PhotoItem? photo) => boundChanges.Add(photo);
            model.PhotoChanged += BoundChanged;
            model.HandleNavigationKey(Key.A);
            model.PhotoChanged -= BoundChanged;
            Layout(root, 1560, 860);
            SettleBindings(root);
            Check(boundChanges.Count == 1 && ReferenceEquals(boundChanges[0], second) && ReferenceEquals(photoList.SelectedItem, second),
                "bound cross-group navigation selects the previous group's last thumbnail with one target event");
            model.CurrentPhoto = first;
            var settle = new DispatcherFrame();
            var settleTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
            DateTime settleDeadline = DateTime.UtcNow.AddSeconds(3);
            settleTimer.Tick += (_, _) => { if (model.IsPreviewReady || DateTime.UtcNow >= settleDeadline) settle.Continue = false; };
            try { settleTimer.Start(); Dispatcher.PushFrame(settle); }
            finally { settleTimer.Stop(); }
            Check(ReferenceEquals(model.CurrentPhoto, first) && ReferenceEquals(photoList.SelectedItem, first) && model.IsPreviewReady,
                "bound photo selection remains on the requested target after queued binding and preview work settles");
            viewer.ShowPhoto(first, bitmap); model.IsPreviewReady = true; model.Dirty = false;
            model.Status = "布局验证 · 示例照片与拍摄参数 · 所有操作均未修改原文件";
            CheckImportantButtons(root, 1560, 860, Check);
            CheckPreviewPanes(viewer, Check);
            CheckSpeciesRows(window, root, first, Check);
            SaveScreenshot(root, "main-window.png", 1560, 860);

            Layout(root, 1160, 700);
            CheckImportantButtons(root, 1160, 700, Check);
            CheckPreviewPanes(viewer, Check);
            CheckSpeciesRows(window, root, first, Check);
            Check(!model.Dirty, "full-window layout at normal and minimum content sizes preserves clean project state");
            SaveScreenshot(root, "main-window-minimum.png", 1160, 700);

            model.IsBusy = true;
            Check(!viewer.IsEnabled, "busy operations disable detail gestures while a save snapshot is in flight");
            Layout(root, 1560, 860);
            SaveScreenshot(root, "main-window-busy.png", 1560, 860);
            Check(((RadioButton)window.FindName("RatingFavorite")).IsEnabled == false,
                "busy operations disable rating changes");
            Check(!((ListBox)window.FindName("SpeciesResultsList")).IsEnabled,
                "busy operations disable changing the selected detected bird");
            Check(!clearRecognition.IsEnabled, "busy operations disable clearing current-photo recognition results");
            model.IsBusy = false;

            var dialog = new ExportDialog(imported.Added);
            var choices = (ComboBox)dialog.FindName("ActionChoice");
            var removeOriginal = (CheckBox)dialog.FindName("RemoveOriginalBox");
            var exportSurface = CreateDialogSurface(dialog, 644, 400);
            Check(choices.ActualWidth > 350 && choices.ActualHeight >= 40,
                "export range selector has enough width and height for every action label");
            foreach (var button in Descendants(exportSurface).OfType<Button>().Where(button => button.Content is string))
            {
                Rect bounds = button.TransformToAncestor(exportSurface).TransformBounds(new Rect(button.RenderSize));
                Check(bounds.Right <= 645 && bounds.Bottom <= 401 && button.ActualWidth > 0,
                    $"export action '{button.Content}' fits within the actual dialog content area");
            }
            SaveScreenshot(exportSurface, "export-dialog.png", 644, 400);
            Check(!dialog.DeleteOnly && !dialog.RemoveOriginal && removeOriginal.IsChecked == false,
                "export dialog defaults to copying while keeping original files");
            Check(dialog.Includes(first) && dialog.Includes(second) && !dialog.Includes(third),
                "default export includes kept and favorite photos only");
            choices.SelectedIndex = 1;
            Check(dialog.Includes(first) && !dialog.Includes(second) && !dialog.Includes(third),
                "favorite-only export excludes every other rating");
            choices.SelectedIndex = 2;
            Check(!dialog.Includes(first) && !dialog.Includes(second) && dialog.Includes(third),
                "rejected export includes only rejected photos");
            choices.SelectedIndex = 3;
            Check(dialog.DeleteOnly && !dialog.RemoveOriginal &&
                  ((StackPanel)dialog.FindName("DestinationPanel")).Visibility == Visibility.Collapsed &&
                  ((TextBlock)dialog.FindName("DeleteWarning")).Visibility == Visibility.Visible,
                "delete-rejected mode clearly exposes its warning and hides export destination controls");
            foreach (bool deleteOnly in new[] { false, true })
            {
                var reminder = new UnmarkedPhotosDialog(2, deleteOnly);
                var reminderSurface = CreateDialogSurface(reminder, 454, 200);
                var reminderButtons = Descendants(reminderSurface).OfType<Button>().ToList();
                string continueLabel = deleteOnly ? "继续删除" : "继续导出";
                Check(reminderButtons.Any(button => Equals(button.Content, "返回挑选") && button.IsCancel) &&
                      reminderButtons.Any(button => Equals(button.Content, continueLabel)),
                    $"unmarked-photo reminder offers return-to-review and '{continueLabel}' choices");
                string message = ((TextBlock)reminder.FindName("MessageText")).Text;
                Check(message.Contains("2 张未标记", StringComparison.Ordinal) &&
                      message.Contains("返回挑选", StringComparison.Ordinal) &&
                      message.Contains(deleteOnly ? "删除" : "导出", StringComparison.Ordinal),
                    $"the {continueLabel} reminder reports the unmarked count and return-to-review behavior");
                foreach (var button in reminderButtons)
                {
                    Rect bounds = button.TransformToAncestor(reminderSurface).TransformBounds(new Rect(button.RenderSize));
                    Check(bounds.Left >= 0 && bounds.Right <= 455 && bounds.Bottom <= 201 && button.ActualWidth > 0,
                        $"unmarked reminder action '{button.Content}' stays fully visible");
                }
                SaveScreenshot(reminderSurface, deleteOnly ? "unmarked-reminder-delete.png" : "unmarked-reminder.png", 454, 200);
                reminder.Close();
            }
            TestUnmarkedFileOperationConfirmation(window, model, Check);
            TestClearList(window, root, model, viewer, photos, Check);
            model.SetGroups(Array.Empty<PhotoGroup>());
            model.Dirty = false;
            TestCleanWindowClose(window, Check);
            return count;
        }
        finally
        {
            // This increments the production preview version, invalidating any queued async decode.
            model?.SetGroups(Array.Empty<PhotoGroup>());
            SynchronizationContext.SetSynchronizationContext(previousContext);
            string resolved = Path.GetFullPath(fixtureRoot);
            string allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.WindowUiChecks")) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
            {
                // Cache shutdown cancels queued work; WIC must finish any already-entered native decode.
                for (int attempt = 0; ; attempt++)
                {
                    try { Directory.Delete(resolved, recursive: true); break; }
                    catch (IOException) when (attempt < 20) { Thread.Sleep(25); }
                }
            }
        }
    }

    private static void TestRatingAndNavigationModel(Action<bool, string> check)
    {
        var first = new PhotoItem { FilePath = "first.jpg" };
        var second = new PhotoItem { FilePath = "second.jpg", Rating = PhotoRating.Favorite };
        var third = new PhotoItem { FilePath = "third.jpg" };
        var group = new PhotoGroup(); group.Photos.Add(first); group.Photos.Add(second);
        var single = new PhotoGroup(); single.Photos.Add(third);
        var vm = new MainViewModel(); vm.SetGroups(new[] { group, single }); vm.IsPreviewReady = true; vm.Dirty = false;
        check(vm.CurrentRating == PhotoRating.Unmarked && vm.UnmarkedCount == 2 && vm.Groups[0].Rating == PhotoRating.Unmarked,
            "unmarked members take precedence in aggregate group ratings");
        check(System.Text.RegularExpressions.Regex.IsMatch(vm.RatingSummary, @"未标记\s*2"),
            "rating summary includes the current unmarked count");
        vm.SetRating(first, PhotoRating.Keep);
        check(vm.Groups[0].Rating == PhotoRating.Favorite && vm.UnmarkedCount == 1 && vm.Dirty,
            "rating changes update aggregate favorite state, unmarked count and unsaved state");
        vm.SetRating(first, PhotoRating.Reject); vm.SetRating(second, PhotoRating.Keep);
        check(vm.Groups[0].Rating == PhotoRating.Keep, "a group with kept and rejected photos displays kept");
        vm.SetRating(second, PhotoRating.Reject);
        check(vm.Groups[0].Rating == PhotoRating.Reject, "a fully rejected group displays rejected");
        vm.SetRating(second, PhotoRating.Unmarked);
        check(vm.LocateFirstUnmarked() && ReferenceEquals(vm.CurrentPhoto, second),
            "return-to-review locates the first unmarked member within a group");
        vm.SetRating(second, PhotoRating.Keep);
        check(vm.LocateFirstUnmarked() && ReferenceEquals(vm.CurrentGroup, vm.Groups[1]) && ReferenceEquals(vm.CurrentPhoto, third),
            "return-to-review moves into the next group containing an unmarked photo");
        vm.SetRating(third, PhotoRating.Favorite);
        check(!vm.LocateFirstUnmarked() && vm.UnmarkedCount == 0, "fully rated lists report no unmarked photo to locate");
        foreach (Key key in new[] { Key.D, Key.S, Key.Right, Key.Down })
        {
            vm.CurrentGroup = vm.Groups[0]; vm.CurrentPhoto = first;
            check(vm.HandleNavigationKey(key) && ReferenceEquals(vm.CurrentPhoto, second) && ReferenceEquals(vm.CurrentGroup, vm.Groups[0]),
                $"{key} selects the next photo within the current group");
        }
        foreach (Key key in new[] { Key.A, Key.W, Key.Left, Key.Up })
        {
            vm.CurrentGroup = vm.Groups[0]; vm.CurrentPhoto = second;
            check(vm.HandleNavigationKey(key) && ReferenceEquals(vm.CurrentPhoto, first) && ReferenceEquals(vm.CurrentGroup, vm.Groups[0]),
                $"{key} selects the previous photo within the current group");
        }
        vm.HandleNavigationKey(Key.A);
        check(ReferenceEquals(vm.CurrentPhoto, first) && ReferenceEquals(vm.CurrentGroup, vm.Groups[0]),
            "previous-photo navigation stops at the beginning of the whole list");
        foreach (Key key in new[] { Key.D, Key.S, Key.Right, Key.Down })
        {
            vm.CurrentGroup = vm.Groups[0]; vm.CurrentPhoto = second;
            var changed = new List<PhotoItem?>(); void Changed(PhotoItem? photo) => changed.Add(photo);
            vm.PhotoChanged += Changed; vm.HandleNavigationKey(key); vm.PhotoChanged -= Changed;
            check(ReferenceEquals(vm.CurrentPhoto, third) && ReferenceEquals(vm.CurrentGroup, vm.Groups[1]),
                $"{key} crosses a group boundary to the next group's first photo");
            check(changed.Count == 1 && ReferenceEquals(changed[0], third),
                $"{key} crossing emits exactly one target-photo event without an empty intermediate selection");
        }
        foreach (Key key in new[] { Key.A, Key.W, Key.Left, Key.Up })
        {
            vm.CurrentGroup = vm.Groups[1]; vm.CurrentPhoto = third;
            var changed = new List<PhotoItem?>(); void Changed(PhotoItem? photo) => changed.Add(photo);
            vm.PhotoChanged += Changed; vm.HandleNavigationKey(key); vm.PhotoChanged -= Changed;
            check(ReferenceEquals(vm.CurrentPhoto, second) && ReferenceEquals(vm.CurrentGroup, vm.Groups[0]),
                $"{key} crosses backward to the previous group's last photo");
            check(changed.Count == 1 && ReferenceEquals(changed[0], second),
                $"{key} backward crossing loads only the final member, without first loading the group's cover");
        }
        vm.CurrentGroup = vm.Groups[1]; vm.CurrentPhoto = third; vm.HandleNavigationKey(Key.D);
        check(ReferenceEquals(vm.CurrentPhoto, third), "next-photo navigation stops at the end of the whole list");
        vm.CurrentGroup = vm.Groups[0]; vm.CurrentPhoto = first;
        vm.HandleNavigationKey(Key.PageDown);
        check(ReferenceEquals(vm.CurrentGroup, vm.Groups[1]) && ReferenceEquals(vm.CurrentPhoto, third), "Page Down advances to the next group");
        vm.HandleNavigationKey(Key.PageUp);
        check(ReferenceEquals(vm.CurrentGroup, vm.Groups[0]) && ReferenceEquals(vm.CurrentPhoto, first), "Page Up returns to the previous group");
        check(!vm.HandleNavigationKey(Key.F12), "unrelated keys remain available to other commands");
        vm.IsBusy = true; vm.HandleNavigationKey(Key.D); vm.SetRating(first, PhotoRating.Favorite);
        check(ReferenceEquals(vm.CurrentPhoto, first) && first.Rating == PhotoRating.Reject,
            "busy operations prevent navigation and rating mutation");
        vm.IsBusy = false; vm.IsPreviewReady = true;
        foreach (var (key, rating) in new[]
        {
            (Key.D1, PhotoRating.Unmarked), (Key.D2, PhotoRating.Reject),
            (Key.D3, PhotoRating.Keep), (Key.D4, PhotoRating.Favorite),
            (Key.NumPad1, PhotoRating.Unmarked), (Key.NumPad2, PhotoRating.Reject),
            (Key.NumPad3, PhotoRating.Keep), (Key.NumPad4, PhotoRating.Favorite)
        })
            check(vm.HandleRatingKey(key) && first.Rating == rating, $"{key} applies the requested rating through the shared shortcut handler");
        check(!vm.HandleRatingKey(Key.D0) && !vm.HandleRatingKey(Key.NumPad0) && first.Rating == PhotoRating.Favorite,
            "zero is no longer a rating shortcut");
        vm.IsPreviewReady = false; vm.HandleRatingKey(Key.D2);
        check(!vm.CanRate && first.Rating == PhotoRating.Favorite,
            "rating shortcuts cannot mutate a photo whose preview is still loading");
        var target = new PhotoItem { FilePath = "unmarked-target.jpg" };
        var targetGroup = new PhotoGroup();
        targetGroup.Photos.Add(new PhotoItem { FilePath = "already-reviewed-cover.jpg", Rating = PhotoRating.Keep });
        targetGroup.Photos.Add(target);
        vm.SetGroups(new[] { group, targetGroup });
        var locatedChanges = new List<PhotoItem?>();
        void Located(PhotoItem? photo) => locatedChanges.Add(photo);
        vm.PhotoChanged += Located;
        bool located = vm.LocateFirstUnmarked();
        vm.PhotoChanged -= Located;
        check(located && ReferenceEquals(vm.CurrentPhoto, target) && locatedChanges.Count == 1 && ReferenceEquals(locatedChanges[0], target),
            "locating an unmarked non-cover member in another group loads exactly that target without an intermediate cover");
    }

    private static void TestRatingControls(MainWindow window, MainViewModel model, PhotoItem first, PhotoItem second,
        Action<bool, string> check)
    {
        var unmarked = (RadioButton)window.FindName("RatingUnmarked");
        var reject = (RadioButton)window.FindName("RatingReject");
        var keep = (RadioButton)window.FindName("RatingKeep");
        var favorite = (RadioButton)window.FindName("RatingFavorite");
        model.CurrentPhoto = first; model.IsPreviewReady = true;
        check(favorite.IsChecked == true && unmarked.IsChecked != true && reject.IsChecked != true && keep.IsChecked != true,
            "current favorite rating remains visibly selected");
        keep.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
        keep.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
        check(first.Rating == PhotoRating.Keep && keep.IsChecked == true && favorite.IsChecked != true,
            "selecting a rating updates the photo through the real two-way binding");
        keep.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount) { RoutedEvent = Mouse.MouseLeaveEvent });
        check(keep.IsChecked == true, "rating selection stays latched after the pointer leaves");
        foreach (var (button, rating, expectedColor) in new[]
        {
            (unmarked, PhotoRating.Unmarked, Color.FromRgb(170, 183, 195)),
            (reject, PhotoRating.Reject, Color.FromRgb(224, 135, 135)),
            (keep, PhotoRating.Keep, Color.FromRgb(157, 223, 196)),
            (favorite, PhotoRating.Favorite, Color.FromRgb(244, 203, 113))
        })
        {
            button.SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            button.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
            var border = (Border)button.Template.FindName("RatingBorder", button);
            check(first.Rating == rating && border.Background is SolidColorBrush color && color.Color == expectedColor &&
                  new[] { unmarked, reject, keep, favorite }.Count(candidate => candidate.IsChecked == true) == 1,
                $"{rating} has its own latched selection color and remains the only selected rating");
        }
        second.Rating = PhotoRating.Unmarked; model.CurrentPhoto = second; model.IsPreviewReady = true;
        check(unmarked.IsChecked == true && keep.IsChecked != true, "changing photos restores that photo's unmarked selection");
        second.Rating = PhotoRating.Keep; first.Rating = PhotoRating.Favorite; model.CurrentPhoto = first; model.IsPreviewReady = true;
    }

    private static void TestSpeciesResults(MainWindow window, FrameworkElement root, MainViewModel model,
        DualPhotoViewer viewer, PhotoItem photo, BitmapSource bitmap, Action<bool, string> check)
    {
        var originalBox = photo.Bird!.Copy();
        photo.SetBirds(new[]
        {
            new DetectedBird
            {
                Box = originalBox, Species = new SpeciesMatch("大山雀", "Parus major", .412, Confidence: .91),
                RecognitionStatus = "已识别 · 视觉相似度"
            },
            new DetectedBird
            {
                Box = new BirdBox { X = .05, Y = .15, Width = .12, Height = .22, Confidence = .83 },
                Species = new SpeciesMatch("白鹭", "Egretta garzetta", .337, Confidence: .73), RecognitionStatus = "已识别 · 视觉相似度"
            }
        });
        viewer.ShowPhoto(photo, bitmap);
        model.IsPreviewReady = true;
        Layout(root, 1560, 860);
        SettleBindings(root);
        var results = (ListBox)window.FindName("SpeciesResultsList");
        check(ReferenceEquals(results.ItemsSource, photo.Birds) && results.Items.Count == 2 && results.SelectedIndex == 0,
            "the real species result list binds every numbered bird and the default selected detection");
        var firstRow = (ListBoxItem)results.ItemContainerGenerator.ContainerFromIndex(0);
        var rowTexts = Descendants(firstRow).OfType<TextBlock>().Select(text => text.Text).ToList();
        check(rowTexts.Contains("01") && rowTexts.Contains("大山雀") && rowTexts.Contains("Parus major") &&
              rowTexts.Contains(photo.Birds[0].ConfidenceText) && !rowTexts.Contains("0.412") &&
              Descendants(root).OfType<TextBlock>().Any(text => text.Text == "识别置信度"),
            "species rows show number, Chinese name, scientific name and the clearly labeled recognition-confidence percentage");
        check(Descendants(firstRow).OfType<FrameworkElement>()
                .Any(element => element.ToolTip?.ToString()?.Contains("0.412", StringComparison.Ordinal) == true),
            "recognition confidence retains the unmodified raw cosine in the real row tooltip");

        var changes = new List<PhotoItem?>();
        void Changed(PhotoItem? changed) => changes.Add(changed);
        model.PhotoChanged += Changed;
        try
        {
            model.Dirty = false;
            results.SetCurrentValue(Selector.SelectedIndexProperty, 1);
            SettleBindings(root);
            check(photo.SelectedBirdIndex == 1 && ReferenceEquals(results.SelectedItem, photo.Birds[1]) &&
                  ReferenceEquals(model.CurrentPhoto, photo) && ReferenceEquals(viewer.CurrentImage, bitmap) && changes.Count == 0 &&
                  ((TextBlock)viewer.FindName("DetectionText")).Text.Contains("白鹭", StringComparison.Ordinal) && model.Dirty,
                "clicking a bound species row switches that bird's detail view and persists selection without reloading the photo");
            var overlay = (Canvas)viewer.FindName("BirdOverlay");
            ((Grid)overlay.Children[0]).RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
            SettleBindings(root);
            check(photo.SelectedBirdIndex == 0 && results.SelectedIndex == 0 && changes.Count == 0 &&
                  ReferenceEquals(viewer.CurrentImage, bitmap),
                "clicking a full-photo bird box selects its corresponding result row without a new photo event");
            photo.Birds[1].Species = new SpeciesMatch("小白鹭", "Egretta garzetta", .503, Confidence: .96);
            SettleBindings(root);
            var secondRow = (ListBoxItem)results.ItemContainerGenerator.ContainerFromIndex(1);
            var updated = Descendants(secondRow).OfType<TextBlock>().Select(text => text.Text).ToList();
            check(updated.Contains("小白鹭") && updated.Contains(photo.Birds[1].ConfidenceText) && changes.Count == 0,
                "recognition result property notifications update the real result row without rebuilding the photo");
            photo.Birds[1].Species = new SpeciesMatch("小白鹭", "Egretta garzetta", .503);
            SettleBindings(root);
            check(Descendants(secondRow).OfType<TextBlock>().Any(text => text.Text == "—") &&
                  photo.Birds[1].EvidenceTooltip.Contains("0.503", StringComparison.Ordinal),
                "a legacy result without confidence displays a dash while retaining its raw similarity evidence");
        }
        finally { model.PhotoChanged -= Changed; }
        model.IsPreviewReady = false;
        check(!results.IsEnabled, "species result selection remains disabled while the chosen photo preview is not ready");
        model.IsPreviewReady = true;
        model.Dirty = false;
    }

    private static void CheckSpeciesRows(MainWindow window, FrameworkElement root, PhotoItem photo,
        Action<bool, string> check)
    {
        var results = (ListBox)window.FindName("SpeciesResultsList");
        results.UpdateLayout();
        var bounds = results.TransformToAncestor(root).TransformBounds(new Rect(results.RenderSize));
        check(results.ActualWidth >= 500 && results.ActualHeight >= 50 && bounds.Right <= root.ActualWidth &&
              bounds.Bottom <= root.ActualHeight && ReferenceEquals(results.ItemsSource, photo.Birds),
            $"species results retain usable bounds at content width {root.ActualWidth:0}");
        foreach (var item in results.Items.Cast<DetectedBird>())
        {
            if (results.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem row)
                throw new InvalidOperationException("The two test species result rows must remain visible.");
            var texts = Descendants(row).OfType<TextBlock>().ToList();
            check(texts.Count == 4 && texts.All(text => text.ActualWidth > (text.Text == "—" ? 6 : 15) && text.ActualHeight >= 12) &&
                  texts.Any(text => text.Text == item.SpeciesName) && texts.Any(text => text.Text == item.ScientificName) &&
                  texts.Any(text => text.Text == item.ConfidenceText),
                $"bird {item.Number} number, names and confidence remain visibly laid out at content width {root.ActualWidth:0}");
        }
    }

    private static void TestRegionDeletion(MainWindow window, FrameworkElement root, MainViewModel model,
        DualPhotoViewer viewer, PhotoItem photo, BitmapSource bitmap, Action<bool, string> check)
    {
        var original = photo.Birds.ToArray();
        int selected = photo.SelectedBirdIndex, next = photo.NextBirdNumber;
        var view = (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY);
        var rating = photo.Rating;
        var results = (ListBox)window.FindName("SpeciesResultsList");
        ContextMenu MenuFor(DetectedBird bird)
        {
            SettleBindings(root);
            results.ScrollIntoView(bird); results.UpdateLayout();
            var container = results.ItemContainerGenerator.ContainerFromItem(bird) as ListBoxItem
                ?? throw new InvalidOperationException("The region row must be visible before testing its menu.");
            var owner = Descendants(container).OfType<FrameworkElement>().First(element => element.ContextMenu is not null);
            owner.ContextMenu!.PlacementTarget = owner;
            return owner.ContextMenu;
        }
        void Delete(ContextMenu menu) => menu.Items.OfType<MenuItem>().Single(item => item.Header?.ToString() == "删除鸟种")
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        try
        {
            var manual = photo.AddManualRegion(new BirdBox { X = .4, Y = .25, Width = .2, Height = .3 });
            var highest = photo.AddManualRegion(new BirdBox { X = .65, Y = .25, Width = .15, Height = .3 });
            viewer.ShowPhoto(photo, bitmap); model.IsPreviewReady = true;
            viewer.SelectBird(photo.Birds.IndexOf(manual));
            viewer.FitToFrame();
            ((Border)viewer.FindName("DetailViewport")).RaiseEvent(Wheel(120));
            SettleBindings(root);
            var selectedView = (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY);
            model.Dirty = false;
            var firstMenu = MenuFor(photo.Birds[0]);
            var deleted = photo.Birds[0];
            Delete(firstMenu); SettleBindings(root);
            check(!photo.Birds.Contains(deleted) && ReferenceEquals(photo.SelectedBird, manual) &&
                  (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY) == selectedView &&
                  photo.Rating == rating && model.Dirty && ReferenceEquals(viewer.CurrentImage, bitmap),
                "the real row menu removes an unselected automatic region without changing the selected manual subject, view or rating");
            int remaining = photo.Birds.Count;
            Delete(firstMenu); SettleBindings(root);
            check(photo.Birds.Count == remaining && ReferenceEquals(photo.SelectedBird, manual),
                "an already deleted row's old context menu cannot remove a different region");
            Delete(MenuFor(highest)); SettleBindings(root);
            var afterGap = photo.AddManualRegion(new BirdBox { X = .7, Y = .5, Width = .1, Height = .15 });
            check(afterGap.Number > highest.Number && photo.Birds.All(bird => bird.Number != highest.Number),
                "deleting the highest region through its actual row menu leaves its number retired");
            foreach (var bird in photo.Birds.ToArray()) { Delete(MenuFor(bird)); SettleBindings(root); }
            check(photo.Birds.Count == 0 && photo.SelectedBird is null && photo.Bird is null && results.Items.Count == 0 &&
                  ((Canvas)viewer.FindName("BirdOverlay")).Children.Count == 0 &&
                  ReferenceEquals(viewer.CurrentImage, bitmap) && photo.Rating == rating && model.CanRate,
                "deleting every automatic and manual row leaves the photo visible and rateable with no stale boxes or selected bird");
        }
        finally
        {
            photo.SetBirds(original, selected, Math.Max(next, photo.NextBirdNumber));
            (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY) = view;
            photo.Rating = rating;
            viewer.ShowPhoto(photo, bitmap); model.IsPreviewReady = true;
            SettleBindings(root); model.Dirty = false;
        }
    }

    private static void TestNativeManualRecognition(MainWindow window, FrameworkElement root, MainViewModel model,
        DualPhotoViewer viewer, PhotoItem photo, BitmapSource bitmap, Action<bool, string> check)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("BIRD_SPECIES_ASSETS"))) return;
        var originals = photo.Birds.Select(bird => (Bird: bird, bird.Species, bird.RecognitionStatus)).ToArray();
        int selected = photo.SelectedBirdIndex;
        var view = (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY);
        var rating = photo.Rating;
        DetectedBird? added = null;
        try
        {
            viewer.FitToFrame(); SettleBindings(root);
            var displayed = (Image)viewer.FindName("DetailImage");
            var box = originals[0].Bird.Box;
            var start = new Point(Canvas.GetLeft(displayed) + displayed.Width * box.X,
                Canvas.GetTop(displayed) + displayed.Height * box.Y);
            var end = new Point(Canvas.GetLeft(displayed) + displayed.Width * (box.X + box.Width),
                Canvas.GetTop(displayed) + displayed.Height * (box.Y + box.Height));
            check(viewer.BeginManualRegionSelection() && viewer.CompleteManualRegionSelection(start, end),
                "the actual window accepts a manually drawn detail-preview region for native species recognition");
            added = photo.Birds.Last();
            check(photo.Birds.Count == originals.Length + 1 && added.IsManual &&
                  added.Number > originals.Max(entry => entry.Bird.Number) && model.IsBusy,
                "a committed viewer gesture immediately appends and selects a new numbered manual region while recognition runs");
            WaitFor(() => !model.IsBusy, "the native manual-region recognition batch did not finish");
            SettleBindings(root);
            check(added.Species is { IsValid: true, HasValidConfidence: true } &&
                  ReferenceEquals(photo.SelectedBird, added) && ReferenceEquals(viewer.CurrentImage, bitmap) &&
                  photo.Rating == rating && originals.Select((entry, index) => ReferenceEquals(photo.Birds[index], entry.Bird) &&
                      ReferenceEquals(entry.Bird.Species, entry.Species) && entry.Bird.RecognitionStatus == entry.RecognitionStatus).All(value => value),
                "native gesture-to-window recognition identifies only the new ROI and preserves every original region, result and photo rating");
        }
        finally
        {
            if (model.IsBusy)
            {
                (WindowField(window, "_operation") as CancellationTokenSource)?.Cancel();
                WaitFor(() => !model.IsBusy, "native manual-region cancellation did not finish");
            }
            if (added is not null && photo.Birds.Contains(added)) viewer.RequestDeleteRegion(added);
            photo.SelectedBirdIndex = selected;
            (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY) = view;
            viewer.ShowPhoto(photo, bitmap); model.IsPreviewReady = true;
            SettleBindings(root); model.Dirty = false;
        }
    }

    private static void TestUnmarkedFileOperationConfirmation(MainWindow window, MainViewModel model,
        Action<bool, string> check)
    {
        var confirm = typeof(MainWindow).GetMethod("ConfirmUnmarkedBeforeFileOperation",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        check(confirm is not null, "file operations expose one production unmarked-photo confirmation entry point");
        var photos = model.AllPhotos.ToArray();
        check(photos.Length >= 2, "confirmation regression starts with rated and unmarked source-photo fixtures");
        var ratings = photos.Select(photo => photo.Rating).ToArray();
        var originalHashes = photos.Select(photo => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(photo.FilePath))).ToArray();
        var selected = model.CurrentPhoto;
        var selectedGroup = model.CurrentGroup;
        var ratedGroup = model.Groups.First(group => group.Photos.Contains(photos[^1]));
        bool dirty = model.Dirty;
        string status = model.Status;
        try
        {
            foreach (var photo in photos) photo.Rating = PhotoRating.Unmarked;
            photos[^1].Rating = PhotoRating.Reject;
            int expectedCount = photos.Length - 1;
            foreach (bool deleteOnly in new[] { false, true })
            {
                foreach (bool? response in new bool?[] { false, null, true })
                {
                    model.CurrentGroup = ratedGroup;
                    model.CurrentPhoto = photos[^1];
                    int shown = 0;
                    Func<UnmarkedPhotosDialog, bool?> showReminder = reminder =>
                    {
                        shown++;
                        check(Equals(((Button)reminder.FindName("ContinueButton")).Content, deleteOnly ? "继续删除" : "继续导出") &&
                              ((TextBlock)reminder.FindName("MessageText")).Text.Contains($"{expectedCount} 张未标记", StringComparison.Ordinal),
                            $"the production {(deleteOnly ? "delete" : "export")} confirmation receives the current count and matching action");
                        reminder.Close();
                        return response;
                    };
                    bool proceed = (bool)confirm!.Invoke(window, new object?[] { deleteOnly, showReminder })!;
                    check(shown == 1 && proceed == (response == true),
                        $"the {(deleteOnly ? "delete" : "export")} path requires an explicit continue response; response={response?.ToString() ?? "closed"}");
                    check(ReferenceEquals(model.CurrentPhoto, response == true ? photos[^1] : photos[0]),
                        $"the {(deleteOnly ? "delete" : "export")} path returns to the first unmarked photo on cancel or close only");
                }
            }
            foreach (var photo in photos) photo.Rating = PhotoRating.Keep;
            foreach (bool deleteOnly in new[] { false, true })
            {
                int shown = 0;
                Func<UnmarkedPhotosDialog, bool?> showReminder = _ => { shown++; return false; };
                bool proceed = (bool)confirm!.Invoke(window, new object?[] { deleteOnly, showReminder })!;
                check(proceed && shown == 0,
                    $"the {(deleteOnly ? "delete" : "export")} path proceeds without a reminder when every photo is rated");
            }
            check(photos.Select((photo, index) => File.Exists(photo.FilePath) &&
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(photo.FilePath)).SequenceEqual(originalHashes[index])).All(value => value),
                "closing, canceling or accepting either reminder never exports or changes an original photo");
        }
        finally
        {
            for (int index = 0; index < photos.Length; index++) photos[index].Rating = ratings[index];
            model.CurrentGroup = selectedGroup;
            model.CurrentPhoto = selected;
            model.Status = status;
            model.Dirty = dirty;
        }
    }

    private static void TestClearList(MainWindow window, FrameworkElement root, MainViewModel model,
        DualPhotoViewer viewer, string[] paths, Action<bool, string> check)
    {
        var button = (Button)window.FindName("ClearListButton");
        var cache = (PhotoPreviewCache)WindowField(window, "_previewCache")!;
        var saveGate = (SemaphoreSlim)WindowField(window, "_projectSaveGate")!;
        var autosave = (AutoSaveService)WindowField(window, "_autoSave")!;
        var projects = new ProjectService();
        string? originalPath = WindowField(window, "_projectPath") as string;
        string originalName = model.ProjectName;
        string projectPath = Path.Combine(Path.GetDirectoryName(paths[0])!, "clear-current-project.json");
        var originalHashes = paths.Select(path => System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToArray();
        try
        {
            WindowField(window, "_projectPath", projectPath);
            model.ProjectName = "清空列表回归";
            double threshold = model.Threshold;
            long generation = autosave.SessionGeneration, revision = model.Revision;
            string recoveryPath = autosave.RecoveryPath;
            int photoCount = model.PhotoCount;
            check(photoCount > 0 && button.IsEnabled, "the clear-list action is available when the current project has photos");
            Complete(cache.GetAsync(model.CurrentPhoto!));
            check(cache.CachedCount > 0 && viewer.CurrentImage is not null,
                "clear-list regression starts with a displayed frame and a populated production preview cache");
            model.IsBusy = true;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(model.PhotoCount == photoCount, "clear-list cannot mutate a project while another operation is busy");
            model.IsBusy = false;
            check(saveGate.Wait(0), "clear-list regression reserves the production project-save gate");
            try
            {
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                check(model.IsBusy && model.PhotoCount == photoCount,
                    "clear-list waits for an existing project write before removing the current session");
            }
            finally { saveGate.Release(); }
            WaitFor(() => !model.IsBusy, "clear-list did not finish after the project-save gate was released");
            SettleBindings(root);
            WaitFor(() => cache.InFlightCount == 0, "stale preview decode did not finish after clearing");
            check(model.Groups.Count == 0 && model.PhotoCount == 0 && model.CurrentPhoto is null && model.CurrentGroup is null &&
                  viewer.CurrentImage is null && !model.IsPreviewReady && cache.CachedCount == 0 && cache.CachedBytes == 0 &&
                  cache.PendingCount == 0 && ((ListBox)window.FindName("SpeciesResultsList")).Items.Count == 0,
                "clear-list removes all groups, selections, displayed pixels, species rows and cached previews after queued work settles");
            check(model.Dirty && model.Revision > revision && !button.IsEnabled &&
                  WindowField(window, "_projectPath") as string == projectPath && model.ProjectName == "清空列表回归" &&
                  model.Threshold == threshold && autosave.SessionGeneration == generation && autosave.RecoveryPath == recoveryPath,
                "clear-list marks the same project dirty while retaining its path, name, threshold and autosave identity");
            Complete(projects.SaveAsync(projectPath, model.Groups.Select(group => group.Model), model.Threshold));
            var load = projects.LoadAsync(projectPath); Complete(load);
            check(load.Result.Groups.Count == 0 && paths.Select((path, index) => File.Exists(path) &&
                    System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)).SequenceEqual(originalHashes[index])).All(value => value),
                "saving a cleared project persists an empty list and leaves every original JPEG byte unchanged");
        }
        finally { WindowField(window, "_projectPath", originalPath); model.ProjectName = originalName; }
    }

    private static object? WindowField(MainWindow window, string name) => typeof(MainWindow)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(window);

    private static void WindowField(MainWindow window, string name, object? value) => typeof(MainWindow)
        .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(window, value);

    private static void Complete(Task task)
    {
        WaitFor(() => task.IsCompleted, "asynchronous window-test operation did not finish");
        task.GetAwaiter().GetResult();
    }

    private static void WaitFor(Func<bool> condition, string failure)
    {
        if (condition()) return;
        var frame = new DispatcherFrame();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!condition()) throw new InvalidOperationException(failure);
    }

    private static void TestLongSpeciesResults(MainWindow window, FrameworkElement root, MainViewModel model,
        DualPhotoViewer viewer, PhotoItem original, BitmapSource bitmap, Action<bool, string> check)
    {
        var originalGroups = model.Groups.Select(group => group.Model).ToArray();
        var flock = new PhotoItem
        {
            FilePath = original.FilePath, Thumbnail = original.Thumbnail,
            PixelWidth = original.PixelWidth, PixelHeight = original.PixelHeight
        };
        flock.SetBirds(Enumerable.Range(0, 20).Select(index => new DetectedBird
        {
            Box = new BirdBox { X = .3 + index % 4 * .1, Y = .2 + index / 4 * .1, Width = .08, Height = .08, Confidence = .9 },
            Species = new SpeciesMatch("测试鸟" + (index + 1), "Parus major", .4)
        }), selectedIndex: 15);
        var group = new PhotoGroup(); group.Photos.Add(flock);
        model.SetGroups(new[] { group });
        Layout(root, 1560, 860);
        SettleBindings(root);
        var results = (ListBox)window.FindName("SpeciesResultsList");
        var scroll = Descendants(results).OfType<ScrollViewer>().First();
        var selectedRow = results.ItemContainerGenerator.ContainerFromIndex(15) as ListBoxItem;
        check(model.IsPreviewReady && results.Items.Count == 20 && results.SelectedIndex == 15 &&
              selectedRow is { IsSelected: true } && scroll.VerticalOffset <= 15 &&
              15 < scroll.VerticalOffset + Math.Floor(scroll.ViewportHeight) &&
              selectedRow.Template.FindName("ResultBorder", selectedRow) is Border { BorderBrush: SolidColorBrush brush } &&
              brush.Color == Color.FromRgb(125, 175, 159),
            "opening a many-bird photo restores its stored bird and makes the actual selected result row visible");

        int photoEvents = 0;
        void Changed(PhotoItem? _) => photoEvents++;
        model.PhotoChanged += Changed;
        try
        {
            // Selection queues a visibility repair; real wheel intent arrives before that repair.
            double beforeWheel = scroll.VerticalOffset;
            viewer.SelectBird(17);
            var previewWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
            { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = scroll };
            scroll.RaiseEvent(previewWheel);
            if (!previewWheel.Handled)
                scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120)
                { RoutedEvent = Mouse.MouseWheelEvent, Source = scroll });
            root.UpdateLayout();
            double wheelOffset = scroll.VerticalOffset;
            SettleBindings(root);
            check(wheelOffset < beforeWheel && Math.Abs(scroll.VerticalOffset - wheelOffset) < .00001 &&
                  flock.SelectedBirdIndex == 17 && results.SelectedIndex == 17 && photoEvents == 0,
                "manual result-list wheel input wins over pending bird visibility repairs without changing the photo");
        }
        finally { model.PhotoChanged -= Changed; }
        model.SetGroups(originalGroups, original.Id);
        Layout(root, 1560, 860);
        SettleBindings(root);
        viewer.ShowPhoto(original, bitmap);
        model.IsPreviewReady = true;
        model.Dirty = false;
    }

    private static void TestFourVisibleSpeciesResults(MainWindow window, FrameworkElement root, MainViewModel model,
        DualPhotoViewer viewer, PhotoItem photo, BitmapSource bitmap, Action<bool, string> check)
    {
        var original = photo.Birds.ToArray();
        int selected = photo.SelectedBirdIndex, next = photo.NextBirdNumber;
        var originalView = (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY);
        bool dirty = model.Dirty;
        var results = (ListBox)window.FindName("SpeciesResultsList");
        photo.SetBirds(Enumerable.Range(0, 4).Select(index => new DetectedBird
        {
            Box = new BirdBox { X = .1 + index * .2, Y = .25, Width = .12, Height = .2, Confidence = .9 },
            Species = new SpeciesMatch("四行测试鸟" + (index + 1), "Parus major", .7, Confidence: .95),
            RecognitionStatus = "四行布局验证"
        }));
        viewer.ShowPhoto(photo, bitmap); model.IsPreviewReady = true;
        double normalResultsHeight = 0;
        try
        {
            foreach (var (width, height, suffix) in new[] { (1560, 860, "normal"), (1160, 700, "minimum") })
            {
                Layout(root, width, height); SettleBindings(root);
                var scroll = Descendants(results).OfType<ScrollViewer>().First();
                scroll.ScrollToTop(); SettleBindings(root);
                check(results.Items.Count == 4 && scroll.ViewportHeight >= 4,
                    $"the species list has room for four complete logical rows at {width} × {height}");
                CheckVisibleResultRows(results, Enumerable.Range(0, 4), check, $"{width} × {height}");
                CheckPreviewPanes(viewer, check);
                CheckMetadataWrap(window, root, width == 1560, check);
                TestDynamicMetadata(window, root, viewer, photo, results, width, height, check);
                if (width == 1560) normalResultsHeight = results.ActualHeight;
                else check(normalResultsHeight > results.ActualHeight,
                    "single-line metadata gives the normal window more species-result space while narrow metadata wraps without losing four rows");
                var add = (Button)window.FindName("AddSpeciesButton");
                var undo = (Button)window.FindName("UndoSpeciesButton");
                var rating = (RadioButton)window.FindName("RatingKeep");
                Rect listBounds = results.TransformToAncestor(root).TransformBounds(new Rect(results.RenderSize));
                Rect addBounds = add.TransformToAncestor(root).TransformBounds(new Rect(add.RenderSize));
                Rect undoBounds = undo.TransformToAncestor(root).TransformBounds(new Rect(undo.RenderSize));
                Rect ratingBounds = rating.TransformToAncestor(root).TransformBounds(new Rect(rating.RenderSize));
                check(listBounds.Bottom <= Math.Min(addBounds.Top, undoBounds.Top) + 1 &&
                      Math.Max(addBounds.Bottom, undoBounds.Bottom) <= ratingBounds.Top + 1,
                    $"four species rows, manual add/undo and ratings occupy separate visible bands at {width} × {height}");
                SaveScreenshot(root, $"species-four-rows-{suffix}.png", width, height);

                photo.SetBirds(photo.Birds.Concat(Enumerable.Range(4, 4).Select(index => new DetectedBird
                {
                    ManualSpecies = new SpeciesIdentity("更多测试鸟" + (index + 1), "Testus species" + index),
                    RecognitionStatus = "人工指定"
                })));
                viewer.RefreshBirdOverlays(); SettleBindings(root);
                check(results.Items.Count == 8 && scroll.ScrollableHeight > 0,
                    $"more than four species remain scrollable at {width} × {height}");
                viewer.SelectBird(7); SettleBindings(root);
                check(results.SelectedIndex == 7 && photo.SelectedBirdIndex == 7 &&
                      scroll.VerticalOffset > 0 && scroll.ViewportHeight >= 4,
                    $"revealing the last result keeps a four-row viewport at {width} × {height}");
                CheckVisibleResultRows(results, Enumerable.Range(4, 4), check, $"last four at {width} × {height}");
                SaveScreenshot(root, $"species-last-four-rows-{suffix}.png", width, height);
                photo.SetBirds(photo.Birds.Take(4).ToArray()); viewer.RefreshBirdOverlays();
            }
        }
        finally
        {
            photo.SetBirds(original, selected, next);
            (photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY) = originalView;
            viewer.ShowPhoto(photo, bitmap); model.IsPreviewReady = true;
            Layout(root, 1560, 860); SettleBindings(root); model.Dirty = dirty;
        }
    }

    private static void CheckVisibleResultRows(ListBox results, IEnumerable<int> indices,
        Action<bool, string> check, string layout)
    {
        var viewport = Descendants(results).OfType<ScrollContentPresenter>().First();
        foreach (int index in indices)
        {
            var row = results.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            Rect bounds = row is null ? Rect.Empty : row.TransformToAncestor(viewport).TransformBounds(new Rect(row.RenderSize));
            check(row is { ActualHeight: > 0 } && !bounds.IsEmpty && bounds.Top >= -.5 &&
                  bounds.Bottom <= viewport.ActualHeight + .5 && bounds.Left >= -.5 && bounds.Right <= viewport.ActualWidth + .5,
                $"species row {index + 1} is fully inside the actual scroll viewport ({layout})");
        }
    }

    private static void CheckMetadataWrap(MainWindow window, FrameworkElement root, bool singleLine,
        Action<bool, string> check)
    {
        var panel = (BalancedMetadataPanel)window.FindName("MetadataPanel");
        var segments = panel.Children.Cast<FrameworkElement>().Select(child =>
            child.TransformToAncestor(panel).TransformBounds(new Rect(child.RenderSize))).ToArray();
        int lineCount = segments.Select(bounds => Math.Round(bounds.Top, 1)).Distinct().Count();
        check((singleLine ? lineCount == 1 : lineCount > 1) && lineCount <= 2 &&
              segments.All(bounds => bounds.Left >= -.5 && bounds.Top >= -.5 &&
                  bounds.Right <= panel.ActualWidth + .5 && bounds.Bottom <= panel.ActualHeight + .5),
            $"EXIF segments {(singleLine ? "share one readable row" : "wrap into complete rows")} at content width {root.ActualWidth:0}");
        foreach (string name in new[] { "CapturedAt", "Camera", "FocalLength", "Aperture", "Shutter", "Iso", "ExposureBias", "Format" })
        {
            var text = (TextBlock)window.FindName("Metadata" + name);
            Rect bounds = text.TransformToAncestor(panel).TransformBounds(new Rect(text.RenderSize));
            check(text.ActualHeight >= 13 && bounds.Top >= -.5 && bounds.Bottom <= panel.ActualHeight + .5,
                $"metadata {name} remains vertically visible after EXIF reflow at content width {root.ActualWidth:0}");
            CheckCompleteMetadataValue(text, name, root, check);
        }
    }

    private static void CheckCompleteMetadataValue(TextBlock text, string name, FrameworkElement root,
        Action<bool, string> check)
    {
        double naturalWidth = new FormattedText(text.Text, CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, text.Foreground, VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
        IEnumerable<GlyphRun> Runs(Drawing? drawing)
        {
            if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun;
            if (drawing is DrawingGroup group)
                foreach (var child in group.Children)
                    foreach (var run in Runs(child)) yield return run;
        }
        var runs = Runs(VisualTreeHelper.GetDrawing(text)).ToArray();
        bool hasEllipsis = runs.Any(run => run.Characters?.Contains('…') == true ||
            run.GlyphTypeface.CharacterToGlyphMap.TryGetValue('…', out ushort ellipsis) && run.GlyphIndices.Contains(ellipsis));
        check(text.ActualWidth + .01 >= naturalWidth && runs.Length > 0 && !hasEllipsis,
            $"ordinary metadata {name} renders its complete value without ellipsis at content width {root.ActualWidth:0} " +
            $"(value='{text.Text}', available={text.ActualWidth:0.###}, natural={naturalWidth:0.###})");
    }

    private static void TestDynamicMetadata(MainWindow window, FrameworkElement root, DualPhotoViewer viewer,
        PhotoItem photo, ListBox results, int width, int height, Action<bool, string> check)
    {
        var original = photo.Metadata;
        var camera = (TextBlock)window.FindName("MetadataCamera");
        var panel = (BalancedMetadataPanel)window.FindName("MetadataPanel");
        double ValueWidth(TextBlock text) => new FormattedText(text.Text, CultureInfo.CurrentUICulture,
            text.FlowDirection, new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, text.Foreground, 1).WidthIncludingTrailingWhitespace;
        try
        {
            const string longCamera = "Canon EOS R5 Mark II Professional";
            photo.Metadata = new PhotoMetadata
            {
                CapturedAt = original.CapturedAt, Camera = longCamera, FocalLength = original.FocalLength,
                Aperture = original.Aperture, Shutter = original.Shutter, Iso = original.Iso, ExposureBias = original.ExposureBias
            };
            Layout(root, width, height); SettleBindings(root);
            check(camera.Text == longCamera && camera.ActualWidth + 1 >= ValueWidth(camera) && camera.ToolTip?.ToString() == longCamera,
                $"a naturally wide camera name borrows space and remains fully readable at {width} × {height}");
            var segments = panel.Children.Cast<FrameworkElement>().Select(child =>
                child.TransformToAncestor(panel).TransformBounds(new Rect(child.RenderSize))).ToArray();
            check(segments.All(bounds => bounds.Left >= -.5 && bounds.Right <= panel.ActualWidth + .5) &&
                  segments.Select(bounds => Math.Round(bounds.Top, 1)).Distinct().Count() <= 2,
                $"borrowed metadata columns stay within two bounded rows at {width} × {height}");
            foreach (var text in Descendants(panel).OfType<TextBlock>().Where(text => text.Name.StartsWith("Metadata", StringComparison.Ordinal)))
                CheckCompleteMetadataValue(text, text.Name["Metadata".Length..] + " with a long camera", root, check);
            SaveScreenshot(root, $"metadata-balanced-long-{width}.png", width, height);

            photo.Metadata = new PhotoMetadata { Camera = string.Concat(Enumerable.Repeat("极长相机型号", 50)) };
            Layout(root, width, height); SettleBindings(root);
            Rect cameraBounds = camera.TransformToAncestor(panel).TransformBounds(new Rect(camera.RenderSize));
            check(camera.ActualWidth > 0 && camera.ActualWidth < ValueWidth(camera) &&
                  camera.TextTrimming == TextTrimming.CharacterEllipsis && camera.ToolTip?.ToString() == photo.Metadata.Camera &&
                  cameraBounds.Left >= -.5 && cameraBounds.Right <= panel.ActualWidth + .5 && panel.ActualHeight <= 40.5,
                $"an extreme camera field uses bounded ellipsis and retains its full tooltip at {width} × {height}");
            var scroll = Descendants(results).OfType<ScrollViewer>().First(); scroll.ScrollToTop(); SettleBindings(root);
            CheckVisibleResultRows(results, Enumerable.Range(0, 4), check, $"extreme metadata {width} × {height}");
            CheckPreviewPanes(viewer, check);

            photo.Metadata = new PhotoMetadata(); Layout(root, width, height); SettleBindings(root);
            var fields = Descendants(panel).OfType<TextBlock>().Where(text => text.Name.StartsWith("Metadata", StringComparison.Ordinal)).ToArray();
            check(fields.Length == 8 && fields.All(text => text.ActualHeight >= 13 && text.ActualWidth > 0 &&
                      text.TransformToAncestor(panel).TransformBounds(new Rect(text.RenderSize)).Bottom <= panel.ActualHeight + .5),
                $"missing metadata values remain readable without reserving oversized columns at {width} × {height}");
        }
        finally { photo.Metadata = original; Layout(root, width, height); SettleBindings(root); }
    }

    private static void TestContextMenus(ListBox groups, ListBox photos, MainViewModel model, PhotoItem first,
        PhotoItem second, PhotoItem third, Action<bool, string> check)
    {
        ContextMenu MenuFor(ListBox list, object item)
        {
            var container = (DependencyObject)list.ItemContainerGenerator.ContainerFromItem(item);
            var owner = Descendants(container).OfType<FrameworkElement>().First(element => element.ContextMenu is not null);
            owner.ContextMenu!.PlacementTarget = owner;
            return owner.ContextMenu;
        }
        void RateFromMenu(ContextMenu menu, PhotoRating rating)
        {
            var item = menu.Items.OfType<MenuItem>().Single(candidate => candidate.Tag?.ToString() == rating.ToString());
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, item));
        }
        var memberMenu = MenuFor(photos, second);
        check(memberMenu.Items.OfType<MenuItem>().Count() == 4, "member thumbnails offer all four context-menu ratings");
        RateFromMenu(memberMenu, PhotoRating.Reject);
        check(second.Rating == PhotoRating.Reject && first.Rating == PhotoRating.Favorite,
            "member context-menu rating acts on the right-clicked photo");
        var multiMenu = MenuFor(groups, model.Groups[0]);
        RateFromMenu(multiMenu, PhotoRating.Unmarked);
        check(first.Rating == PhotoRating.Favorite && second.Rating == PhotoRating.Reject,
            "multi-photo group context actions cannot bulk-rate group members");
        var singleMenu = MenuFor(groups, model.Groups[1]);
        RateFromMenu(singleMenu, PhotoRating.Favorite);
        check(third.Rating == PhotoRating.Favorite && model.Groups[1].Rating == PhotoRating.Favorite,
            "independent single-photo groups support context-menu rating");
        second.Rating = PhotoRating.Keep; third.Rating = PhotoRating.Reject;
    }

    private static void TestBoundSelection(FrameworkElement root, ListBox groupList, ListBox photoList,
        MainViewModel model, PhotoItem first, PhotoItem second, PhotoItem third, Action<bool, string> check)
    {
        var sequence = new PhotoGroup();
        for (int i = 0; i < 30; i++) sequence.Photos.Add(new PhotoItem
        {
            FilePath = first.FilePath, Thumbnail = first.Thumbnail, PixelWidth = first.PixelWidth, PixelHeight = first.PixelHeight
        });
        var following = new PhotoGroup();
        following.Photos.Add(second); following.Photos.Add(third);
        model.SetGroups(new[] { sequence, following });
        Layout(root, 1560, 860);
        SettleBindings(root);

        bool HasGreenFrame(PhotoItem photo) => photoList.ItemContainerGenerator.ContainerFromItem(photo) is ListBoxItem item &&
            item.IsSelected && item.Template.FindName("Bd", item) is Border border &&
            border.BorderBrush is SolidColorBrush brush && brush.Color == Color.FromRgb(157, 223, 196);
        void Selected(PhotoItem expected, string action)
        {
            SettleBindings(root);
            bool valid = ReferenceEquals(model.CurrentPhoto, expected) && ReferenceEquals(photoList.SelectedItem, expected) &&
                         ReferenceEquals(groupList.SelectedItem, model.CurrentGroup) && HasGreenFrame(expected);
            if (!valid)
            {
                var container = photoList.ItemContainerGenerator.ContainerFromItem(expected) as ListBoxItem;
                var border = container?.Template.FindName("Bd", container) as Border;
                var scroll = Descendants(photoList).OfType<ScrollViewer>().First();
                var owner = root.Parent as MainWindow;
                object? Field(string name) => owner is null ? null : typeof(MainWindow)
                    .GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(owner);
                Console.WriteLine($"  SELECTION DEBUG {action}: expected={expected.Id}, vm={model.CurrentPhoto?.Id}, " +
                    $"selected={(photoList.SelectedItem as PhotoItem)?.Id}, groupMatch={ReferenceEquals(groupList.SelectedItem, model.CurrentGroup)}, " +
                    $"container={container is not null}, isSelected={container?.IsSelected}, brush={border?.BorderBrush}, " +
                    $"offset={scroll.VerticalOffset}, viewport={scroll.ViewportHeight}, extent={scroll.ExtentHeight}, " +
                    $"sync={(Field("_selectionSync") as DispatcherOperation)?.Status}, version={Field("_previewVersion")}, " +
                    $"wheelVersion={Field("_photoListScrollVersion")}, ready={model.IsPreviewReady}, closed={Field("_isClosed")}, " +
                    $"contains={photoList.Items.Contains(expected)}, index={photoList.Items.IndexOf(expected)}, " +
                    $"scrollPart={ReferenceEquals(photoList.Template.FindName("PART_ScrollViewer", photoList), scroll)}");
            }
            check(valid,
                $"{action} retains matching VM selection, actual selected container and green thumbnail frame after queued WPF work");
        }
        void Move(Action move, PhotoItem expected, string action)
        {
            var changes = new List<PhotoItem?>();
            void Changed(PhotoItem? photo) => changes.Add(photo);
            model.PhotoChanged += Changed;
            try
            {
                move();
                SettleBindings(root);
                check(changes.Count == 1 && ReferenceEquals(changes[0], expected),
                    $"{action} emits one final target event with no deferred null/cover load during dispatcher settling");
                Selected(expected, action);
            }
            finally { model.PhotoChanged -= Changed; }
        }

        foreach (Key key in new[] { Key.D, Key.S, Key.Right, Key.Down })
        {
            model.CurrentGroup = model.Groups[0]; model.CurrentPhoto = sequence.Photos.Last();
            SettleBindings(root);
            Move(() => model.HandleNavigationKey(key), second, $"bound forward key {key}");
        }
        foreach (Key key in new[] { Key.A, Key.W, Key.Left, Key.Up })
        {
            model.CurrentGroup = model.Groups[1]; model.CurrentPhoto = second;
            SettleBindings(root);
            Move(() => model.HandleNavigationKey(key), sequence.Photos.Last(), $"bound backward key {key}");
        }
        Move(() => model.HandleNavigationKey(Key.PageDown), second, "Page Down group change");
        Move(() => model.HandleNavigationKey(Key.PageUp), sequence.Photos.First(), "Page Up group change");

        Move(() => groupList.SetCurrentValue(Selector.SelectedItemProperty, model.Groups[1]), second, "actual group-list selection event");
        Move(() => groupList.SetCurrentValue(Selector.SelectedItemProperty, model.Groups[0]), sequence.Photos.First(),
            "actual group-list selection back to previous group");

        foreach (int index in new[] { 25, 0, 18, 0, 29 })
        {
            model.CurrentPhoto = sequence.Photos[index];
            Selected(sequence.Photos[index], $"virtualized/recycled thumbnail {index}");
        }

        // Add a multi-selection before the queued Loaded-priority selection synchronization runs.
        model.CurrentPhoto = sequence.Photos[0];
        photoList.SelectedItems.Add(sequence.Photos[1]);
        photoList.SelectedItems.Add(sequence.Photos[2]);
        SettleBindings(root);
        check(photoList.SelectedItems.Count == 3 && photoList.SelectedItems.Contains(sequence.Photos[0]) &&
              photoList.SelectedItems.Contains(sequence.Photos[1]) && photoList.SelectedItems.Contains(sequence.Photos[2]) &&
              ReferenceEquals(model.CurrentPhoto, sequence.Photos[0]),
            "queued preview-selection synchronization preserves a user's extended photo selection");
        groupList.SelectedItems.Add(model.Groups[1]);
        SettleBindings(root);
        check(groupList.SelectedItems.Count == 2 && ReferenceEquals(model.CurrentGroup, model.Groups[0]),
            "preview selection synchronization preserves extended group selection");
    }

    private static void TestListScrolling(FrameworkElement root, ListBox groupList, ListBox photoList,
        MainViewModel model, PhotoItem source, Action<bool, string> check)
    {
        PhotoItem Fixture() => new() { FilePath = source.FilePath, Thumbnail = source.Thumbnail };
        var sequence = new PhotoGroup();
        for (int i = 0; i < 30; i++) sequence.Photos.Add(Fixture());
        var groups = new List<PhotoGroup> { sequence };
        for (int i = 0; i < 20; i++) { var single = new PhotoGroup(); single.Photos.Add(Fixture()); groups.Add(single); }
        model.SetGroups(groups);
        Layout(root, 1560, 860);
        SettleBindings(root);
        var groupScroll = Descendants(groupList).OfType<ScrollViewer>().First();
        var photoScroll = Descendants(photoList).OfType<ScrollViewer>().First();
        var selectedGroup = model.CurrentGroup; var selectedPhoto = model.CurrentPhoto;
        void Scroll(ListBox list, int delta) { list.RaiseEvent(Wheel(delta)); root.UpdateLayout(); SettleBindings(root); }
        Scroll(groupList, -60); Scroll(photoList, -60);
        check(groupScroll.VerticalOffset == 0 && photoScroll.VerticalOffset == 0,
            "partial wheel notches accumulate independently in each thumbnail list");
        Scroll(groupList, -60); Scroll(photoList, -60);
        check(groupScroll.VerticalOffset == 2 && photoScroll.VerticalOffset == 2,
            "two half-notch deltas scroll exactly two items in each list");
        foreach (var list in new[] { groupList, photoList })
        {
            var scroll = Descendants(list).OfType<ScrollViewer>().First();
            double previous = scroll.VerticalOffset;
            Scroll(list, -120);
            check(scroll.ScrollableHeight > 0 && scroll.VerticalOffset == previous + 2,
                $"{list.Name} wheel notch scrolls exactly two thumbnail items");
            Scroll(list, 120);
            check(scroll.VerticalOffset == previous, $"{list.Name} reverse notch scrolls exactly two items upward");
            check(ReferenceEquals(model.CurrentGroup, selectedGroup) && ReferenceEquals(model.CurrentPhoto, selectedPhoto),
                $"{list.Name} scrolling leaves group and photo selection unchanged");
        }

        // Foreground cache hits can finish synchronously while a Loaded-priority selection repair
        // is pending. Once settled, wheel input must never be pulled back by another repair callback.
        double groupOffset = groupScroll.VerticalOffset, photoOffset = photoScroll.VerticalOffset;
        SettleBindings(root);
        check(groupScroll.VerticalOffset == groupOffset && photoScroll.VerticalOffset == photoOffset,
            "decode completion and queued selection repairs do not scroll thumbnail lists back after wheel input");

        // Wheel intent can arrive before the new photo's queued repair runs. The explicit
        // user scroll must win even though that selected thumbnail is then offscreen.
        model.CurrentPhoto = sequence.Photos[25];
        double beforePendingWheel = photoScroll.VerticalOffset;
        Scroll(photoList, -120);
        check(photoScroll.VerticalOffset == beforePendingWheel + 2 &&
              ReferenceEquals(model.CurrentPhoto, sequence.Photos[25]),
            "wheel input arriving before a pending selection repair keeps its offset and photo selection");
    }

    private static void TestPreviewLoading(MainViewModel model, DualPhotoViewer viewer, PhotoItem source,
        string directory, Action<bool, string> check)
    {
        string path = Path.Combine(directory, "small-final-preview.jpg");
        var reduced = ImageLoader.Load(source.FilePath, maxWidth: 80);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(reduced));
        using (var output = File.Create(path)) encoder.Save(output);
        // Unknown dimensions intentionally avoid background prefetch before this foreground miss.
        var small = new PhotoItem { FilePath = path };
        var group = new PhotoGroup(); group.Photos.Add(source); group.Photos.Add(small);
        model.SetGroups(new[] { group });
        var original = ImageLoader.Load(source.FilePath);
        viewer.ShowPhoto(source, original); model.IsPreviewReady = true;
        model.CurrentPhoto = small;
        bool pending = viewer.IsLoading;
        check(pending
                ? ReferenceEquals(viewer.CurrentImage, original) && !model.IsPreviewReady && !model.CanRate
                : model.IsPreviewReady && viewer.CurrentImage is BitmapSource immediate && immediate.PixelWidth == reduced.PixelWidth,
            "switching photos either retains the old frame while loading or commits an already-ready cached frame");
        if (pending)
        {
            model.HandleRatingKey(Key.D2);
            check(small.Rating == PhotoRating.Unmarked, "a rating key cannot mark the still-loading photo against a stale preview");
        }
        model.CurrentPhoto = source; model.CurrentPhoto = small;
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, viewer.Dispatcher) { Interval = TimeSpan.FromMilliseconds(20) };
        DateTime deadline = DateTime.UtcNow.AddSeconds(3);
        timer.Tick += (_, _) => { if (model.IsPreviewReady || DateTime.UtcNow >= deadline) frame.Continue = false; };
        try { timer.Start(); Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        check(ReferenceEquals(model.CurrentPhoto, small) && model.IsPreviewReady && model.CanRate && !viewer.IsLoading &&
              viewer.CurrentImage is BitmapSource bitmap && bitmap.PixelWidth == reduced.PixelWidth,
            "rapid navigation finishes with the last selected photo's bitmap and enables rating only after it is displayed");
    }

    private static void TestCleanWindowClose(MainWindow window, Action<bool, string> check)
    {
        bool closed = false;
        Exception? dispatcherError = null;
        var frame = new DispatcherFrame();
        var timeout = new DispatcherTimer(DispatcherPriority.Send, window.Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        timeout.Tick += (_, _) => frame.Continue = false;
        void OnClosed(object? sender, EventArgs e) { closed = true; frame.Continue = false; }
        void OnError(object sender, DispatcherUnhandledExceptionEventArgs e)
        { dispatcherError = e.Exception; e.Handled = true; frame.Continue = false; }
        window.Closed += OnClosed;
        window.Dispatcher.UnhandledException += OnError;
        try
        {
            window.Close();
            window.Close();
            check(!closed, "clean-project close is deferred until the Closing callback has returned");
            timeout.Start(); Dispatcher.PushFrame(frame);
            check(closed && dispatcherError is null,
                "rapid clean-project close requests complete without reentrant-close exceptions");
            check(Application.Current.Windows.Cast<Window>().All(candidate => !candidate.IsVisible),
                "close regression checks never open a desktop window");
        }
        finally { timeout.Stop(); window.Closed -= OnClosed; window.Dispatcher.UnhandledException -= OnError; }
    }

    private static MouseWheelEventArgs Wheel(int delta) => new(Mouse.PrimaryDevice, Environment.TickCount, delta)
    {
        RoutedEvent = Mouse.PreviewMouseWheelEvent
    };

    private static void CheckPreviewPanes(DualPhotoViewer viewer, Action<bool, string> check)
    {
        var full = (Border)viewer.FindName("FullViewport");
        var detail = (Border)viewer.FindName("DetailViewport");
        check(full.ActualWidth >= 250 && detail.ActualWidth >= 250 &&
              full.ActualHeight >= 120 && detail.ActualHeight >= 120,
            $"both preview panes retain usable dimensions at content width {viewer.ActualWidth:0} " +
            $"({full.ActualWidth:0.#}×{full.ActualHeight:0.#} and {detail.ActualWidth:0.#}×{detail.ActualHeight:0.#})");
    }

    private static void CheckImportantButtons(FrameworkElement root, double width, double height, Action<bool, string> check)
    {
        string[] required = { "添加照片", "清空列表", "打开工程", "保存工程", "恢复自动保存", "导出 / 清理", "自动分组", "识别鸟种", "清空识别结果", "重新识别本张", "拆分整组", "从列表中移除",
            "将当前图独立", "从列表中移除", "适应画面", "还原鸟类视图", "手动选区", "＋ 手动添加鸟种", "撤销" };
        var buttons = Descendants(root).OfType<ContentControl>().Where(control =>
            control is ButtonBase && required.Contains(control.Content?.ToString()) ||
            control is RadioButton && control.Name.StartsWith("Rating", StringComparison.Ordinal)).ToList();
        check(buttons.Count == required.Length + 4, "all primary review and rating actions are present in the production visual tree");
        foreach (var button in buttons)
        {
            string label = button.Content is string textContent ? textContent : button.Name;
            Rect bounds = button.TransformToAncestor(root).TransformBounds(new Rect(button.RenderSize));
            check(button.ActualWidth > 0 && button.ActualHeight > 0 && bounds.Left >= -1 && bounds.Top >= -1 &&
                  bounds.Right <= width + 1 && bounds.Bottom <= height + 1,
                $"action '{label}' stays within the {width:0} × {height:0} content bounds");
            var text = new FormattedText(label, CultureInfo.CurrentUICulture,
                button.FlowDirection, new Typeface(button.FontFamily, button.FontStyle, button.FontWeight, button.FontStretch),
                button.FontSize, button.Foreground, 1);
            double contentWidth = button.Content is FrameworkElement content ? content.DesiredSize.Width : text.WidthIncludingTrailingWhitespace;
            check(button.ActualWidth + 1 >= contentWidth + button.Padding.Left + button.Padding.Right,
                $"action '{label}' retains enough width for its Chinese label");
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Width = width;
        root.Height = height;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static void SettleBindings(FrameworkElement root)
    {
        root.UpdateLayout();
        // Drain a complete dispatcher turn, including deferred ScrollViewer virtualization work.
        // Do not alter selection here:
        // the production Loaded-priority repair must create the selected container itself.
        root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.Background);
        root.Dispatcher.Invoke(() => root.UpdateLayout(), DispatcherPriority.SystemIdle);
    }

    private static void SaveScreenshot(FrameworkElement root, string fileName, int width, int height)
    {
        string? directory = Environment.GetEnvironmentVariable("BIRD_UI_SCREENSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(directory, fileName));
        encoder.Save(output);
    }

    private static Grid CreateDialogSurface(Window dialog, int width, int height)
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

    private static string[] CreateFixtures(string directory)
    {
        string source = Environment.GetEnvironmentVariable("BIRD_UI_SAMPLE_PATH") ??
            Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "samples", "great-tit.jpg");
        string[] paths = Enumerable.Range(1, 3).Select(index => Path.Combine(directory, $"山雀_{index:000}.JPG")).ToArray();
        if (File.Exists(source))
        {
            foreach (string path in paths) File.Copy(source, path);
        }
        else
        {
            // A fresh checkout can run every layout test without downloading sample photographs.
            byte[] pixels = new byte[480 * 320 * 3];
            for (int y = 0; y < 320; y++)
            for (int x = 0; x < 480; x++)
            {
                int offset = (y * 480 + x) * 3;
                pixels[offset] = (byte)(40 + x / 8);
                pixels[offset + 1] = (byte)(75 + y / 4);
                pixels[offset + 2] = (byte)(50 + x / 12);
            }
            var bitmap = BitmapSource.Create(480, 320, 96, 96, PixelFormats.Rgb24, null, pixels, 480 * 3);
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(paths[0]);
            encoder.Save(output);
            output.Close();
            File.Copy(paths[0], paths[1]); File.Copy(paths[0], paths[2]);
        }
        return paths;
    }
}
