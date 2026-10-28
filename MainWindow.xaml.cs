using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using BirdPhotoSelector.Controls;
using Microsoft.Win32;

namespace BirdPhotoSelector;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();
    private readonly PhotoImportService _import = new();
    private readonly ProjectService _projects = new();
    private readonly ExportService _export = new();
    private readonly PhotoPreviewCache _previewCache = new(512L * 1024 * 1024);
    private readonly AutoSaveService _autoSave = new();
    private readonly SemaphoreSlim _projectSaveGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource? _previewRequest;
    private DispatcherOperation? _selectionSync;
    private DispatcherOperation? _speciesSelectionSync;
    private long _photoListScrollVersion;
    private long _speciesListScrollVersion;
    private bool _autoSaving;
    private readonly Dictionary<ListBox, int> _thumbnailWheelRemainders = new();
    private CancellationTokenSource? _operation;
    private string? _projectPath;
    private int _previewVersion;
    private bool _closingAllowed;
    private bool _closeRequestPending;
    private bool _isClosed;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;
        _vm.PhotoChanged += LoadPreview;
        Viewer.ViewChanged += (_, _) => _vm.Dirty = true;
        Viewer.ManualRegionSelected += Viewer_ManualRegionSelected;
        Viewer.DeleteRegionRequested += (_, e) => RemoveBirdRegion(e.Photo, e.Bird);
        Viewer.BirdSelectionChanged += (_, _) =>
        {
            if (_vm.CurrentPhoto is { } photo)
                QueueBirdResultSelection(photo, _previewVersion, _speciesListScrollVersion);
        };
        _autoSaveTimer.Tick += async (_, _) => await TryAutoSave();
        Closed += (_, _) =>
        {
            _isClosed = true; ++_previewVersion;
            _autoSaveTimer.Stop(); _selectionSync?.Abort(); _speciesSelectionSync?.Abort();
            _vm.PhotoChanged -= LoadPreview;
            _lifetime.Cancel(); _previewRequest?.Cancel();
            _previewCache.Dispose();
        };
        Loaded += async (_, _) =>
        {
            _autoSaveTimer.Start();
            var file = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(argument =>
                !string.Equals(argument, App.SoftwareRenderingArgument, StringComparison.OrdinalIgnoreCase));
            if (file != null && File.Exists(file) && ProjectService.IsProjectPath(file)) await OpenProject(file);
        };
    }

    private async void LoadPreview(PhotoItem? photo)
    {
        if (_isClosed) return;
        int version = ++_previewVersion;
        _previewRequest?.Cancel();
        _previewRequest?.Dispose();
        _previewRequest = null;
        _selectionSync?.Abort();
        _speciesSelectionSync?.Abort();
        _vm.IsPreviewReady = false;
        if (photo == null) { Viewer.ShowPhoto(null); return; }
        _previewRequest = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _previewRequest.Token;
        Viewer.ShowLoading();
        if (!_vm.IsBusy) _vm.Status = "正在载入照片：" + photo.FileName;
        GroupList.ScrollIntoView(_vm.CurrentGroup);
        long scrollVersion = _photoListScrollVersion;
        long speciesScrollVersion = _speciesListScrollVersion;
        QueuePhotoSelection(photo, version, scrollVersion);
        try
        {
            if (_isClosed || version != _previewVersion) return;
            var pending = _previewCache.GetAsync(photo, token);
            _previewCache.UpdateNeighborhood(_vm.AllPhotos.ToList(), photo);
            var bitmap = await pending;
            if (_isClosed || version != _previewVersion) return;
            Viewer.ShowPhoto(photo, bitmap);
            _vm.IsPreviewReady = true;
            QueuePhotoSelection(photo, version, scrollVersion);
            QueueBirdResultSelection(photo, version, speciesScrollVersion);
            if (!_vm.IsBusy) _vm.Status = photo.DetectionStatus + " · 右侧滚轮缩放，拖动平移 · R 还原鸟类视图";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_isClosed && version == _previewVersion) { Viewer.ShowLoadError(ex.Message); _vm.Status = "预览失败：" + ex.Message; }
        }
    }

    private void QueuePhotoSelection(PhotoItem photo, int version, long scrollVersion)
    {
        // A cache hit can finish synchronously. Keep its initial queued scroll instead of
        // replacing it with a second refresh for the same photo.
        if (_selectionSync?.Status == DispatcherOperationStatus.Pending) return;
        _selectionSync?.Abort();
        // WPF's deferred ItemsSource/reset-scroll work runs after Loaded. Apply the final
        // selection only after those passes, and retain the original user-scroll version.
        _selectionSync = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
            new Action(() => SyncPhotoSelection(photo, version, scrollVersion, 0)));
    }

    private void SyncPhotoSelection(PhotoItem photo, int version, long scrollVersion, int attempt)
    {
        if (_isClosed || version != _previewVersion || _vm.CurrentPhoto != photo) return;
        // An ItemsSource transfer and its virtualized extent can finish in different layout passes.
        PhotoList.UpdateLayout();
        bool contains = PhotoList.Items.Contains(photo);
        bool canScroll = scrollVersion == _photoListScrollVersion;
        ListBoxItem? item = null;
        if (contains)
        {
            if (!PhotoList.SelectedItems.Contains(photo)) PhotoList.SetCurrentValue(Selector.SelectedItemProperty, photo);
            if (canScroll)
            {
                RevealListItem(PhotoList, photo);
            }
            PhotoList.UpdateLayout();
            item = PhotoList.ItemContainerGenerator.ContainerFromItem(photo) as ListBoxItem;
            if (item is not null && !item.IsSelected) item.SetCurrentValue(ListBoxItem.IsSelectedProperty, true);
        }
        if ((!contains || canScroll && item is null) && attempt < 2)
            _selectionSync = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,
                new Action(() => SyncPhotoSelection(photo, version, scrollVersion, attempt + 1)));
    }

    private void QueueBirdResultSelection(PhotoItem photo, int version, long scrollVersion)
    {
        _speciesSelectionSync?.Abort();
        _speciesSelectionSync = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (_isClosed || version != _previewVersion || _vm.CurrentPhoto != photo ||
                photo.SelectedBird is not { } bird) return;
            SpeciesResultsList.UpdateLayout();
            if (!SpeciesResultsList.Items.Contains(bird)) return;
            if (!ReferenceEquals(SpeciesResultsList.SelectedItem, bird))
                SpeciesResultsList.SetCurrentValue(Selector.SelectedIndexProperty, photo.SelectedBirdIndex);
            if (scrollVersion == _speciesListScrollVersion) RevealListItem(SpeciesResultsList, bird);
        }));
    }

    private static void RevealListItem(ListBox list, object item)
    {
        // A temporarily realized container can still lie outside a recycled logical viewport.
        // Use item indices, keeping as much of the current visible range as possible.
        if (list.Template.FindName("PART_ScrollViewer", list) is ScrollViewer { CanContentScroll: true } scroll)
        {
            int index = list.Items.IndexOf(item);
            if (index < 0) return;
            double visibleItems = Math.Max(1, Math.Floor(scroll.ViewportHeight));
            if (index < scroll.VerticalOffset) scroll.ScrollToVerticalOffset(index);
            else if (index >= scroll.VerticalOffset + visibleItems)
                scroll.ScrollToVerticalOffset(index - visibleItems + 1);
        }
        else list.ScrollIntoView(item);
        list.UpdateLayout();
    }

    private void SpeciesResults_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => _speciesListScrollVersion++;

    private void SetSessionGroups(IEnumerable<PhotoGroup> groups, string? selectedPhotoId = null)
    {
        _vm.SetGroups(groups, selectedPhotoId);
        _previewCache.TrimToPaths(_vm.AllPhotos.Select(p => p.FilePath));
    }

    private async Task RunOperation(Func<CancellationToken, Task> action)
    {
        if (_vm.IsBusy) return;
        _operation = new();
        _vm.IsBusy = true; _vm.Progress = 0;
        try { await action(_operation.Token); }
        catch (OperationCanceledException) { _vm.Status = "操作已取消 · 已完成的文件操作仍然有效"; }
        catch (Exception ex) { _vm.Status = "操作未完成"; ShowMessage(ex.Message, "操作未完成", MessageBoxImage.Warning); }
        finally
        {
            _operation.Dispose(); _operation = null; _vm.IsBusy = false; _vm.Refresh();
            if (!_isClosed && _autoSave.IsDue) _ = TryAutoSave();
        }
    }

    private IProgress<T> OperationProgress<T>(Action<T> update)
    {
        var operation = _operation;
        return new Progress<T>(value =>
        {
            if (!_isClosed && _vm.IsBusy && ReferenceEquals(_operation, operation)) update(value);
        });
    }

    private IProgress<PhotoOperationProgress> FileProgress() => OperationProgress<PhotoOperationProgress>(p =>
    {
        _vm.Progress = p.Total > 0 ? p.Completed * 100.0 / p.Total : 0;
        _vm.Status = p.Message + " · " + p.FilePath;
    });

    private async void AddPhotos_Click(object sender, RoutedEventArgs e) => await ChoosePhotos();
    private async void ClearList_Click(object sender, RoutedEventArgs e) => await ClearSessionAsync();
    private async Task ClearSessionAsync()
    {
        if (_vm.IsBusy || !_vm.HasPhotos) return;
        await RunOperation(async ct =>
        {
            await _projectSaveGate.WaitAsync(ct);
            try
            {
                Viewer.CancelManualRegionSelection();
                _previewCache.Clear();
                SetSessionGroups(Array.Empty<PhotoGroup>());
                _selectionSync = null; _speciesSelectionSync = null;
                _thumbnailWheelRemainders.Clear();
                _photoListScrollVersion++; _speciesListScrollVersion++;
                _vm.Dirty = true; _vm.Progress = 0;
                _vm.Status = "列表已清空 · 原照片文件保留";
            }
            finally { _projectSaveGate.Release(); }
        });
    }
    private async Task ChoosePhotos()
    {
        if (_vm.IsBusy) return;
        var dialog = new OpenFileDialog { Title = "选择要挑选的 JPG 照片（可跨目录继续添加）", Filter = "JPG 照片 (*.jpg;*.jpeg)|*.jpg;*.jpeg|所有文件 (*.*)|*.*", Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await Import(dialog.FileNames);
    }
    private async Task Import(string[] files) => await RunOperation(async ct =>
    {
        _vm.Status = "正在读取照片和 EXIF 信息…";
        var result = await _import.ImportAsync(files, _vm.AllPhotos.Select(p => p.FileName), ct, FileProgress());
        var groups = _vm.Groups.Select(g => g.Model).ToList();
        foreach (var photo in result.Added) { var group = new PhotoGroup(); group.Photos.Add(photo); groups.Add(group); }
        SetSessionGroups(groups, _vm.CurrentPhoto?.Id ?? result.Added.FirstOrDefault()?.Id);
        if (result.Added.Count > 0) _vm.Dirty = true;
        _vm.Progress = 100;
        _vm.Status = $"已添加 {result.Added.Count} 张 · 跳过非 JPG {result.SkippedUnsupported} 张 · 同名去重 {result.SkippedDuplicate} 张";
        if (result.Errors.Count > 0) ShowDetails("部分照片未能读取", result.Errors);
    });
    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_vm.IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] files) await Import(files);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.PhotoCount == 0) { _vm.Status = "请先添加 JPG 照片"; return; }
        await RunOperation(async ct =>
        {
            _vm.Status = "正在按背景相似度分组…";
            var selectedId = _vm.CurrentPhoto?.Id;
            var photos = _vm.AllPhotos.ToList();
            var progress = OperationProgress<AnalyzeProgress>(p => { _vm.Progress = p.Total > 0 ? p.Completed * 100.0 / p.Total : 0; _vm.Status = p.Message; });
            using var analysis = new AnalysisService();
            var result = await analysis.AnalyzeAsync(photos, GroupingThreshold.ToActual(_vm.Threshold), progress, ct);
            ct.ThrowIfCancellationRequested();
            foreach (var outcome in result.Outcomes)
            {
                var photo = photos.First(p => p.Id == outcome.PhotoId);
                photo.PixelWidth = outcome.PixelWidth; photo.PixelHeight = outcome.PixelHeight;
            }
            var groups = result.Groups.Select(list => { var g = new PhotoGroup(); foreach (var p in list) g.Photos.Add(p); return g; }).ToList();
            SetSessionGroups(groups, selectedId); _vm.Dirty = true;
            _vm.Status = $"自动分组完成 · {photos.Count} 张照片 · {groups.Count} 个组 · 已保留鸟框与鸟种结果";
            _vm.Progress = 100;
            if (result.Errors.Count > 0) ShowDetails("分析中的提示", result.Errors);
        });
    }

    private async void RecognizeSpecies_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        if (_vm.PhotoCount == 0)
        { _vm.Status = "请先添加 JPG 照片"; return; }
        await RunOperation(async ct =>
        {
            var photos = _vm.AllPhotos.ToList();
            var byId = photos.ToDictionary(photo => photo.Id);
            bool focusNewBird = _vm.CurrentPhoto is { Birds.Count: 0 };
            var progress = OperationProgress<SpeciesProgress>(p =>
            {
                _vm.Progress = p.Total > 0 ? p.Completed * 100.0 / p.Total : 0;
                _vm.Status = p.Message;
            });
            _vm.Status = "正在准备鸟类定位与鸟种识别 · 中国鸟种候选…";
            // Keep the large inference session only for this batch, rather than retaining it
            // alongside the preview cache throughout the user's entire picking session.
            using var recognition = new BirdRecognitionWorkflow();
            var result = await recognition.RecognizeAsync(photos, chinaOnly: true, progress, ct);
            ct.ThrowIfCancellationRequested();
            foreach (var outcome in result.Outcomes)
            {
                if (byId.TryGetValue(outcome.PhotoId, out var photo)) outcome.ApplyTo(photo);
            }
            if (result.Outcomes.Count > 0) _vm.Dirty = true;
            if (focusNewBird && _vm.CurrentPhoto is { Birds.Count: > 0 } current && _vm.IsPreviewReady)
                Viewer.SelectBird(current.SelectedBirdIndex);
            else Viewer.RefreshBirdOverlays();
            _vm.Progress = 100;
            _vm.Status = result.MissingModel ? "定位与鸟种识别未完整完成 · 模型资源不完整" :
                result.MatchedBirds == 0 && result.Errors.Count > 0 ? "鸟种识别未完成 · 请查看错误提示" :
                $"鸟种识别完成 · {photos.Count} 张照片 · 中国候选 · {result.MatchedBirds} 只鸟有结果 · {result.SkippedPhotos} 张无可用鸟框";
            if (result.MissingModel)
                ShowMessage("鸟类定位或鸟种识别模型资源不完整。\n\n定位模型：运行 scripts/Setup-Model.ps1。\n鸟种模型：运行 scripts/Setup-SpeciesModel.ps1。\n也可以使用包含完整 models 目录的发布包。\n\n已有的分组、鸟框、鸟种结果和评级会保留。", "识别模型未就绪", MessageBoxImage.Information);
            else if (result.Errors.Count > 0) ShowDetails("鸟种识别中的提示", result.Errors);
        });
    }

    private void SpeciesResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_vm.CanRate || _vm.CurrentPhoto is not { } photo || SpeciesResultsList.SelectedItem is not DetectedBird bird) return;
        int index = photo.Birds.IndexOf(bird);
        if (index >= 0 && index != photo.SelectedBirdIndex) Viewer.SelectBird(index);
    }

    private async void Viewer_ManualRegionSelected(object? sender, ManualRegionEventArgs e)
    {
        if (!_vm.CanRate || !ReferenceEquals(_vm.CurrentPhoto, e.Photo) || Viewer.CurrentImage is not BitmapSource image) return;
        var photo = e.Photo;
        await RunOperation(async ct =>
        {
            var bird = photo.AddManualRegion(e.Box);
            _vm.Dirty = true;
            Viewer.SelectBird(photo.SelectedBirdIndex);
            QueueBirdResultSelection(photo, _previewVersion, _speciesListScrollVersion);
            _vm.Status = $"正在识别手动选区 {bird.Number} · 中国鸟种候选…";
            using var recognition = new BirdSpeciesRecognitionService();
            var result = await Task.Run(() => recognition.Recognize(image, bird.Box, chinaOnly: true, ct), ct);
            ct.ThrowIfCancellationRequested();
            if (_isClosed || !photo.Birds.Contains(bird)) return;
            if (result.Match is { IsValid: true } match) bird.Species = match;
            bird.RecognitionStatus = result.Status;
            _vm.Dirty = true;
            Viewer.RefreshBirdOverlays();
            _vm.Progress = 100;
            _vm.Status = result.Match is { IsValid: true }
                ? $"手动选区 {bird.Number} · {bird.SpeciesName} · 置信度 {bird.ConfidenceText}"
                : $"手动选区 {bird.Number} 已保留 · {result.Status}，可稍后点击“识别鸟种”重试";
        });
    }

    private bool RemoveBirdRegion(PhotoItem photo, DetectedBird bird)
    {
        if (!_vm.CanRate || !ReferenceEquals(_vm.CurrentPhoto, photo) || !photo.Birds.Contains(bird)) return false;
        bool wasSelected = ReferenceEquals(photo.SelectedBird, bird);
        Viewer.CancelManualRegionSelection();
        if (!photo.RemoveBird(bird)) return false;
        _vm.Dirty = true;
        if (wasSelected)
        {
            if (photo.Birds.Count > 0) Viewer.SelectBird(photo.SelectedBirdIndex);
            else
            {
                photo.ViewZoom = photo.ViewCenterX = photo.ViewCenterY = null;
                Viewer.ShowPhoto(photo, Viewer.CurrentImage as BitmapSource);
            }
        }
        else Viewer.RefreshBirdOverlays();
        QueueBirdResultSelection(photo, _previewVersion, _speciesListScrollVersion);
        _vm.Status = $"已删除选区 {bird.Number} · 原照片与评级保留";
        return true;
    }

    private void DeleteBirdRegionMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || _vm.CurrentPhoto is not { } photo ||
            ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu) return;
        if ((menu.PlacementTarget as FrameworkElement)?.DataContext is DetectedBird bird) RemoveBirdRegion(photo, bird);
    }

    private void SplitGroup_Click(object sender, RoutedEventArgs e)
    {
        var current = _vm.CurrentGroup;
        if (_vm.IsBusy || current == null || current.Photos.Count < 2) return;
        var selected = _vm.CurrentPhoto?.Id;
        var groups = _vm.Groups.Select(g => g.Model).ToList();
        int index = groups.IndexOf(current.Model); groups.RemoveAt(index);
        foreach (var photo in current.Photos) { var single = new PhotoGroup(); single.Photos.Add(photo); groups.Insert(index++, single); }
        SetSessionGroups(groups, selected); _vm.Dirty = true; _vm.Status = "已将整组拆分为独立照片";
    }
    private void SplitPhoto_Click(object sender, RoutedEventArgs e)
    {
        var current = _vm.CurrentGroup; var photo = _vm.CurrentPhoto;
        if (_vm.IsBusy || current == null || photo == null || current.Photos.Count < 2) return;
        var groups = _vm.Groups.Select(g => g.Model).ToList();
        current.Photos.Remove(photo);
        var single = new PhotoGroup(); single.Photos.Add(photo);
        groups.Insert(groups.IndexOf(current.Model) + 1, single);
        SetSessionGroups(groups, photo.Id); _vm.Dirty = true; _vm.Status = "已将当前照片移为独立组";
    }
    private void RemoveGroups_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        var selected = GroupList.SelectedItems.Cast<GroupCard>().ToHashSet();
        if (selected.Count == 0) return;
        SetSessionGroups(_vm.Groups.Where(g => !selected.Contains(g)).Select(g => g.Model).ToList());
        _vm.Dirty = true; _vm.Status = "已从列表移除所选组 · 原文件保留";
    }
    private void RemovePhotos_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        var selected = PhotoList.SelectedItems.Cast<PhotoItem>().ToHashSet();
        if (selected.Count == 0) return;
        RemoveFromSession(selected);
        _vm.Dirty = true; _vm.Status = "已从列表移除所选照片 · 原文件保留";
    }
    private void RemoveFromSession(IEnumerable<PhotoItem> photos)
    {
        var ids = photos.Select(p => p.Id).ToHashSet();
        var groups = _vm.Groups.Select(g => g.Model).ToList();
        var selected = _vm.CurrentPhoto?.Id;
        foreach (var group in groups) foreach (var photo in group.Photos.Where(p => ids.Contains(p.Id)).ToList()) group.Photos.Remove(photo);
        SetSessionGroups(groups, selected);
    }
    private void PhotoRatingMenu_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetRatingMenu(sender, out var data, out var rating) && data is PhotoItem photo) _vm.SetRating(photo, rating);
    }
    private void GroupRatingMenu_Click(object sender, RoutedEventArgs e)
    {
        if (TryGetRatingMenu(sender, out var data, out var rating) && data is GroupCard group && group.Photos.Count == 1)
            _vm.SetRating(group.Photos[0], rating);
    }
    private void GroupRatingMenu_Opening(object sender, ContextMenuEventArgs e)
    {
        if (_vm.IsBusy || sender is not FrameworkElement { DataContext: GroupCard group } || group.Photos.Count != 1) e.Handled = true;
    }
    private static bool TryGetRatingMenu(object sender, out object? data, out PhotoRating rating)
    {
        data = null; rating = PhotoRating.Unmarked;
        if (sender is not MenuItem item || !Enum.TryParse(item.Tag?.ToString(), out rating)) return false;
        if (ItemsControl.ItemsControlFromItemContainer(item) is not ContextMenu menu) return false;
        data = (menu.PlacementTarget as FrameworkElement)?.DataContext ?? menu.Tag ?? menu.DataContext;
        return true;
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy || !await ConfirmReplace()) return;
        var dialog = new OpenFileDialog { Title = "打开挑选工程", Filter = "JSON 工程 (*.json)|*.json" };
        if (dialog.ShowDialog(this) == true) await OpenProject(dialog.FileName);
    }
    private async Task OpenProject(string path, bool recovering = false) => await RunOperation(async ct =>
    {
        _vm.Status = "正在打开工程并检查照片路径…";
        var result = await _projects.LoadAsync(path, ct);
        _vm.Threshold = result.GroupingThreshold;
        _previewCache.Clear();
        SetSessionGroups(result.Groups);
        _autoSave.StartSession();
        _projectPath = recovering ? null : path;
        _vm.ProjectName = recovering ? "恢复的挑选" : Path.GetFileNameWithoutExtension(path);
        _vm.Dirty = recovering; _vm.Refresh();
        _vm.AutoSaveSummary = "自动保存 · 5分钟";
        _vm.AutoSavePath = recovering ? path : "自动保存将更新当前 JSON 工程。";
        _vm.Status = $"工程已打开 · {_vm.PhotoCount} 张照片"; _vm.Progress = 100;
        var summary = $"已载入 {_vm.PhotoCount} 张照片，{_vm.Groups.Count} 个组。\n\n跳过路径不存在：{result.SkippedMissing}\n跳过无效文件：{result.SkippedInvalid}\n跳过同名照片：{result.SkippedDuplicate}";
        if (result.Errors.Count > 0) summary += "\n\n" + string.Join("\n", result.Errors.Take(8));
        ShowMessage(summary, "工程导入完成", MessageBoxImage.Information);
    });
    private async void SaveProject_Click(object sender, RoutedEventArgs e) => await SaveProject();
    private async Task<bool> SaveProject()
    {
        if (_vm.IsBusy) return false;
        var dialog = new SaveFileDialog { Title = "保存挑选工程", Filter = "JSON 工程 (*.json)|*.json", DefaultExt = ".json", FileName = _projectPath == null ? "本次挑选.json" : Path.ChangeExtension(_projectPath, ".json"), AddExtension = true };
        if (dialog.ShowDialog(this) != true) return false;
        bool saved = false;
        await RunOperation(async ct =>
        {
            await _projectSaveGate.WaitAsync(ct);
            try
            {
                long revision = _vm.Revision;
                await _projects.SaveAsync(dialog.FileName, _vm.Groups.Select(g => g.Model), _vm.Threshold, ct);
                _projectPath = dialog.FileName; _vm.ProjectName = Path.GetFileNameWithoutExtension(dialog.FileName);
                if (revision == _vm.Revision) _vm.Dirty = false;
                _autoSave.NoteManualSave(revision);
                _vm.AutoSavePath = dialog.FileName;
                _vm.Status = "工程已保存 · 包含分组、鸟类定位、缩放视图和评级"; saved = true;
            }
            finally { _projectSaveGate.Release(); }
        });
        return saved;
    }

    private async Task TryAutoSave()
    {
        if (_isClosed || _autoSaving || _vm.IsBusy || !_vm.Dirty ||
            !_autoSave.IsDue || _autoSave.LastSavedRevision == _vm.Revision) return;
        _autoSaving = true;
        try
        {
            await _projectSaveGate.WaitAsync(_lifetime.Token);
            try
            {
                if (_isClosed || _vm.IsBusy || !_vm.Dirty || !_autoSave.IsDue) return;
                long revision = _vm.Revision;
                var result = await _autoSave.SaveAsync(_vm.Groups.Select(g => g.Model), _vm.Threshold,
                    _projectPath, revision, _lifetime.Token);
                if (_isClosed || result.SessionGeneration != _autoSave.SessionGeneration) return;
                if (!result.IsRecovery && revision == _vm.Revision) _vm.Dirty = false;
                _vm.AutoSaveSummary = $"自动保存 {DateTime.Now:HH:mm}";
                _vm.AutoSavePath = result.Path;
                _vm.Status = result.IsRecovery
                    ? "已自动保存恢复文件 · 可通过“恢复自动保存”打开"
                    : "工程已自动保存";
                if (result.PrimaryError != null) _vm.Status = "原工程路径暂不可写，已自动保存至本机恢复文件";
            }
            finally { _projectSaveGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_isClosed)
            {
                _vm.AutoSaveSummary = "自动保存失败";
                _vm.AutoSavePath = ex.Message;
                _vm.Status = "自动保存未完成：" + ex.Message;
            }
        }
        finally { _autoSaving = false; }
    }

    private async void RecoverAutoSave_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy || !await ConfirmReplace()) return;
        if (!Directory.Exists(_autoSave.RecoveryDirectory))
        { ShowMessage("尚无自动保存的恢复文件。未命名工程会在首次自动保存时创建恢复文件。", "恢复自动保存"); return; }
        var dialog = new OpenFileDialog
        {
            Title = "恢复自动保存的挑选", Filter = "JSON 工程 (*.json)|*.json",
            InitialDirectory = _autoSave.RecoveryDirectory, CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) await OpenProject(dialog.FileName, recovering: true);
    }
    private async Task<bool> ConfirmReplace()
    {
        if (!_vm.Dirty) return true;
        var result = MessageBox.Show(this, "当前挑选尚未保存，是否先保存工程？", "保存本次挑选", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return result == MessageBoxResult.No || (result == MessageBoxResult.Yes && await SaveProject());
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy || _vm.PhotoCount == 0) { _vm.Status = "请先添加照片并评级"; return; }
        var dialog = new ExportDialog(_vm.AllPhotos.ToList()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (!ConfirmUnmarkedBeforeFileOperation(dialog.DeleteOnly)) return;
        var photos = _vm.AllPhotos.Where(dialog.Includes).ToList();
        if (photos.Count == 0) { ShowMessage("没有符合所选评级的照片。", "没有待处理照片"); return; }
        if (dialog.DeleteOnly || dialog.RemoveOriginal)
        {
            var action = dialog.DeleteOnly ? "删除这些舍弃的原文件" : "复制成功并校验后移除对应原文件";
            if (MessageBox.Show(this, $"即将{action}，共 {photos.Count} 张。\n\n原文件会放入回收站；所在磁盘不支持回收站时将永久删除。\n取消、冲突或复制失败的文件不会移除。\n\n是否继续？", "确认原文件操作", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        }
        await RunOperation(async ct =>
        {
            _vm.Status = dialog.DeleteOnly ? "正在移除舍弃的原文件…" : "正在复制照片并校验…";
            var result = dialog.DeleteOnly
                ? await _export.DeleteAsync(photos, ct, FileProgress())
                : await _export.ExportAsync(photos, dialog.Destination, dialog.RemoveOriginal, ct, FileProgress());
            if (result.SourceRemoved.Count > 0) { RemoveFromSession(result.SourceRemoved); _vm.Dirty = true; }
            _vm.Status = $"操作完成 · 成功 {result.Succeeded.Count} 张 · 跳过 {result.Skipped.Count} 张 · 失败 {result.Errors.Count} 张"; _vm.Progress = 100;
            ShowMessage($"成功：{result.Succeeded.Count} 张\n已移除原文件：{result.SourceRemoved.Count} 张\n跳过：{result.Skipped.Count} 张\n失败：{result.Errors.Count} 张" +
                (result.Skipped.Count + result.Errors.Count > 0 ? "\n\n" + string.Join("\n", result.Skipped.Concat(result.Errors).Take(12)) : ""), "处理完成", result.Errors.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
        });
    }

    private bool ConfirmUnmarkedBeforeFileOperation(bool deleteOnly,
        Func<UnmarkedPhotosDialog, bool?>? showReminder = null)
    {
        if (_vm.UnmarkedCount == 0) return true;
        var reminder = new UnmarkedPhotosDialog(_vm.UnmarkedCount, deleteOnly);
        bool? response;
        if (showReminder is null)
        {
            reminder.Owner = this;
            response = reminder.ShowDialog();
        }
        else response = showReminder(reminder);
        bool proceed = response == true;
        if (!proceed) _vm.LocateFirstUnmarked();
        return proceed;
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void Help_Click(object sender, RoutedEventArgs e) => ShowMessage("1. 添加照片：直接多选 JPG，可从不同目录继续添加。同名文件自动跳过。“清空列表”仅移除当前列表，不删除原文件。\n2. 自动分组：只按当前列表顺序合并连续相似背景，保留已有鸟框、鸟种、当前选区与缩放视图。新版默认 50% 相当于旧版 95%，阈值越高分组越严格。\n3. 识别鸟种：逐张定位鸟并识别所有自动框及手动选区，无需先分组，默认中国鸟种候选。下方每行对应左图同序号虚线框，可点击编号或结果切换主体。置信度根据全部候选的分数差距计算，并非实际准确率；鼠标悬停可查看原始相似度与相近候选。旧工程需重新识别后才有新置信度。\n4. 左侧全图固定；右侧滚轮缩放、拖动平移，白框指示当前区域，拖动和缩放均不会超出照片边界。缩放范围为适应画面至 200%；极小图片最高显示 200%。R 还原鸟类约占框 70% 的视图，也可点击适应画面。右上“手动选区”开启后拖框，松开即识别并追加编号，Esc 取消；在编号框或结果行右键可删除选区，剩余编号保持不变。重新分组保留全部已有鸟框与识别结果。\n5. 两栏缩略图滚轮只滚动列表。方向键 / WASD 切换组内照片；Page Up / Page Down 切换照片组。在组内首尾继续切图，会衔接上一组最后一张或下一组第一张。\n6. 1 未标记，2 舍弃，3 保留，4 精选。评级保持选中状态；缩略图右键可快速评级，照片组右键仅用于独立照片。Ctrl / Shift 支持列表多选。\n7. 导出到自行选择的现有目录，不会自动创建子目录；同名目标跳过。导出或删除舍弃前若有未标记照片，可返回定位继续挑选；处理时显示当前照片序号和文件名。\n8. JSON 工程记录原始路径、分组、视图和评级，不打包照片。工程仅使用 .json 格式。\n9. 前后各预加载两张照片，缓存上限 512 MB（不含当前显示和解码缓冲）。每 5 分钟自动保存修改；未命名工程或原路径不可写时保存本机恢复文件，可点击“恢复自动保存”打开。\n\nCtrl+O 添加照片 · Ctrl+S 保存工程\n所有分析均在本机进行。", "羽选 · 使用帮助", MessageBoxImage.Information);

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Viewer.HandleSelectionKey(e.Key)) { e.Handled = true; return; }
        if (_vm.IsBusy || e.OriginalSource is TextBox) return;
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            if (e.Key == Key.O) { e.Handled = true; await ChoosePhotos(); }
            else if (e.Key == Key.S) { e.Handled = true; await SaveProject(); }
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (_vm.HandleNavigationKey(e.Key)) { e.Handled = true; return; }
        if (_vm.HandleRatingKey(e.Key)) { e.Handled = true; return; }
        switch (e.Key)
        {
            case Key.R: Viewer.RestoreBirdView(); break;
            default: return;
        }
        e.Handled = true;
    }

    private void ThumbnailList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox list || _vm.IsBusy) return;
        if (ReferenceEquals(list, PhotoList)) _photoListScrollVersion++;
        e.Handled = true;
        int accumulated = _thumbnailWheelRemainders.GetValueOrDefault(list) + e.Delta;
        int notches = accumulated / Mouse.MouseWheelDeltaForOneLine;
        _thumbnailWheelRemainders[list] = accumulated % Mouse.MouseWheelDeltaForOneLine;
        if (notches == 0) return;
        if (list.Template.FindName("PART_ScrollViewer", list) is not ScrollViewer scroll) return;
        // Virtualized lists scroll in item units. Ignore the system's usual three-line setting
        // and accumulate high-resolution wheel deltas separately for each thumbnail column.
        int steps = Math.Min(Math.Abs(notches), 100) * 2;
        for (int i = 0; i < steps; i++)
            if (notches > 0) scroll.LineUp(); else scroll.LineDown();
    }
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closingAllowed) return;
        e.Cancel = true;
        if (_vm.IsBusy) { _vm.Status = "请先取消或等待当前操作完成，再关闭程序"; return; }
        if (_closeRequestPending) return;
        _closeRequestPending = true;
        // Close must run after this Closing callback returns, including the clean-project path
        // where ConfirmReplace completes synchronously. Calling Close here reenters WPF closing.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (!_isClosed && await ConfirmReplace()) { _closingAllowed = true; Close(); }
            }
            catch (Exception ex) { ShowMessage(ex.Message, "无法关闭", MessageBoxImage.Warning); }
            finally { _closeRequestPending = false; }
        }));
    }
    private void ShowMessage(string message, string title, MessageBoxImage image = MessageBoxImage.Information) => MessageBox.Show(this, message, "羽选 · " + title, MessageBoxButton.OK, image);
    private void ShowDetails(string title, IEnumerable<string> messages) => ShowMessage(string.Join("\n", messages.Take(14)), title, MessageBoxImage.Warning);
}


