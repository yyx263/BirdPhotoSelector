using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BirdPhotoSelector.Desktop.Dialogs;
using BirdPhotoSelector.Desktop.Imaging;
using BirdPhotoSelector.Desktop.ViewModels;

namespace BirdPhotoSelector.Desktop.Views;

/// <summary>
/// Avalonia 挑片窗口，只处理控件、文件选择和提示；所有领域状态与异步用例交给协调器。
/// 主动关闭先取消事件并异步确认，完成后再次 Close，避免在系统关闭过程中打开子窗口。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private AvaloniaImageFrame? _shownFrame;
    private PhotoItem? _shownPhoto;
    private bool _syncing, _closed, _closePending, _closeAllowed;
    private readonly Dictionary<ListBox, double> _wheelRemainders = new();
    /// <summary>命令行的本地 JSON 工程或照片，在窗口打开且文件选择服务可用后处理。</summary>
    public string[] StartupFiles { get; init; } = [];
    /// <summary>本窗口独立会话，检查入口可注入可控制的图像后端。</summary>
    public DesktopController Controller { get; }
    /// <summary>正常桌面入口，模型仍延迟加载。</summary>
    public MainWindow() : this(new DesktopController()) { }
    /// <summary>构造控件后统一绑定生命周期，不在控件事件中另外创建业务服务。</summary>
    public MainWindow(DesktopController controller)
    {
        Controller = controller ?? throw new ArgumentNullException(nameof(controller));
        InitializeComponent(); DataContext = controller.State;
        Controller.State.PropertyChanged += StateChanged; Controller.PreviewChanged += UpdatePreview;
        FullPane.BirdSelected += SelectBird;
        Controller.ResultsChanged += ResultsChanged;
        Controller.State.SpeciesEdits.PropertyChanged += HistoryChanged;
        FullPane.DeleteRegionRequested += (photo, bird) => Controller.DeleteResult(photo, bird);
        DetailPane.ManualRegionSelected += async (photo, box) => await GuardUi(() => Execute(() => Controller.RecognizeRegionAsync(photo, box)));
        DetailPane.ManualModeChanged += () => { ManualRegionButton.IsChecked = DetailPane.IsManualMode; ManualRegionButton.Content = DetailPane.IsManualMode ? "取消选区" : "手动选区"; };
        DetailPane.ViewChanged += user =>
        {
            FullPane.VisibleRegion = DetailPane.VisibleRegion; FullPane.RefreshOverlays(); ZoomText.Text = DetailPane.ScaleText;
            if (user && !_closed) Controller.State.Dirty = true;
        };
        AddHandler(KeyDownEvent, HandleKeys, RoutingStrategies.Tunnel);
        GroupList.AddHandler(PointerWheelChangedEvent, ScrollThumbnails, RoutingStrategies.Tunnel);
        PhotoList.AddHandler(PointerWheelChangedEvent, ScrollThumbnails, RoutingStrategies.Tunnel);
        // 先于行容器的旧菜单处理，确保撤销/换图后重新捕获当前鸟，而非使用回收行中的旧引用。
        BirdList.AddHandler(ContextRequestedEvent, ResultContextRequested, RoutingStrategies.Tunnel);
        _autoSaveTimer.Tick += async (_, _) => await Controller.AutoSaveIfDueAsync();
        Opened += async (_, _) =>
        {
            _autoSaveTimer.Start();
            var paths = StartupFiles.Where(f => !f.StartsWith("--", StringComparison.Ordinal)).ToArray();
            if (paths.Length == 1 && ProjectService.IsProjectPath(paths[0])) await Execute(() => Controller.OpenAsync(paths[0]));
            else if (paths.Length > 0) await Execute(() => Controller.ImportAsync(paths));
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _closed = true; _autoSaveTimer.Stop(); Controller.State.PropertyChanged -= StateChanged;
            Controller.PreviewChanged -= UpdatePreview; Controller.ResultsChanged -= ResultsChanged; Controller.State.SpeciesEdits.PropertyChanged -= HistoryChanged; Controller.Dispose();
        };
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets/App.ico");
        if (File.Exists(icon)) Icon = new WindowIcon(icon);
        UpdateRatings(); UpdateSpeciesControls();
    }
    /// <summary>同步组/照片选择先于滚动；空的 SelectionChanged 不得把会话已选照片清空。</summary>
    private void StateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_closed) return;
        if (e.PropertyName is nameof(PhotoSelectionSession.CurrentGroup) or nameof(PhotoSelectionSession.CurrentPhoto)) SyncSelection();
        if (e.PropertyName is nameof(PhotoSelectionSession.CurrentRating) or nameof(PhotoSelectionSession.CanRate)) UpdateRatings();
        if (e.PropertyName is nameof(PhotoSelectionSession.CanRate) or nameof(PhotoSelectionSession.IsBusy)) { UpdateInteraction(); UpdateSpeciesControls(); }
    }
    /// <summary>保留 Ctrl/Shift 多选；只有新的当前项未在选择中时才替换选择。</summary>
    private void SyncSelection()
    {
        if (_syncing) return; _syncing = true;
        try
        {
            var state = Controller.State;
            if (state.CurrentGroup is { } group && GroupList.SelectedItems?.Contains(group) != true) GroupList.SelectedItem = group;
            if (!ReferenceEquals(PhotoList.ItemsSource, state.CurrentGroup?.Photos)) PhotoList.ItemsSource = state.CurrentGroup?.Photos;
            if (state.CurrentPhoto is { } photo && PhotoList.SelectedItems?.Contains(photo) != true) PhotoList.SelectedItem = photo;
            BirdList.ItemsSource = state.CurrentPhoto?.Birds; BirdList.SelectedItem = state.CurrentPhoto?.SelectedBird;
            var current = state.CurrentPhoto;
            Dispatcher.UIThread.Post(() =>
            {
                if (_closed || !ReferenceEquals(current, state.CurrentPhoto)) return;
                if (state.CurrentGroup is not null) GroupList.ScrollIntoView(state.CurrentGroup);
                if (current is not null) PhotoList.ScrollIntoView(current);
            }, DispatcherPriority.Loaded);
        }
        finally { _syncing = false; }
    }
    /// <summary>忙碌与加载状态分别控制列表和画布，旧帧只能查看不能编辑。</summary>
    private void UpdateInteraction() { FullPane.IsInteractive = DetailPane.IsInteractive = Controller.State.CanRate; if (!Controller.State.CanRate) DetailPane.CancelManualRegion(); }
    /// <summary>只有实际帧/照片变更才重置画布，加载通知本身保留原图和位置。</summary>
    private void UpdatePreview()
    {
        if (_closed) return;
        if (!ReferenceEquals(_shownFrame, Controller.Preview) || !ReferenceEquals(_shownPhoto, Controller.PreviewPhoto))
        {
            _shownFrame = Controller.Preview; _shownPhoto = Controller.PreviewPhoto;
            FullPane.ShowPhoto(_shownPhoto, _shownFrame); DetailPane.ShowPhoto(_shownPhoto, _shownFrame);
        }
        UpdateInteraction(); UpdateRatings();
    }
    /// <summary>评级按钮互斥置位，点击已选按钮也维持置位；颜色由主题按四类分别定义。</summary>
    private void UpdateRatings()
    {
        var rating = Controller.State.CurrentRating;
        UnmarkedRating.IsChecked = rating == PhotoRating.Unmarked; RejectRating.IsChecked = rating == PhotoRating.Reject;
        KeepRating.IsChecked = rating == PhotoRating.Keep; FavoriteRating.IsChecked = rating == PhotoRating.Favorite;
    }
    /// <summary>组列表只将新增选择同步到会话，移除/重建引起的空事件忽略。</summary>
    private void GroupSelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (!_syncing && !Controller.State.IsBusy && e.AddedItems.OfType<GroupCard>().LastOrDefault() is { } group) Controller.State.CurrentGroup = group; }
    /// <summary>组内列表不会因为滚动更改照片，只有显式选择才修改会话。</summary>
    private void PhotoSelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (!_syncing && !Controller.State.IsBusy && e.AddedItems.OfType<PhotoItem>().LastOrDefault() is { } photo) Controller.State.CurrentPhoto = photo; }
    /// <summary>结果行选择保留无框人工结果视图，有框结果聚焦到对应鸟。</summary>
    private void BirdSelectionChanged(object? sender, SelectionChangedEventArgs e)
    { if (!_syncing && e.AddedItems.OfType<DetectedBird>().FirstOrDefault() is { } bird && Controller.State.CurrentPhoto is { } photo) SelectBird(photo.Birds.IndexOf(bird)); }
    /// <summary>双向统一全图框和结果行选中态，不复制鸟种记录。</summary>
    private void SelectBird(int index)
    {
        if (!Controller.State.CanRate || Controller.State.CurrentPhoto is not { } photo || index < 0 || index >= photo.Birds.Count) return;
        photo.SelectedBirdIndex = index; _syncing = true;
        try { BirdList.SelectedItem = photo.SelectedBird; }
        finally { _syncing = false; }
        if (photo.SelectedBird?.HasRegion == true) DetailPane.FocusBird();
        FullPane.RefreshOverlays();
    }
    /// <summary>选择本地 JPG/RAW 文件，保留跨目录多次追加和格式跳过规则。</summary>
    private async void AddPhotos(object? sender, RoutedEventArgs e) => await GuardUi(async () =>
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "选择 JPG / RAW 照片", AllowMultiple = true,
            FileTypeFilter = [PhotoFilePickerTypes.Photos, FilePickerFileTypes.All] });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (!_closed && paths.Length > 0) await Execute(() => Controller.ImportAsync(paths));
    });
    /// <summary>JSON 文件选择规则与业务读写校验一致。</summary>
    private static FilePickerFileType ProjectFilter => new("JSON 工程") { Patterns = ["*.json"] };
    /// <summary>打开前先处理未保存修改，取消文件选择不修改会话。</summary>
    private async void OpenProject(object? sender, RoutedEventArgs e) => await GuardUi(async () =>
    {
        if (!await ConfirmReplace()) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "打开挑选工程", FileTypeFilter = [ProjectFilter] });
        if (!_closed && files.FirstOrDefault()?.TryGetLocalPath() is { } path) await Execute(() => Controller.OpenAsync(path));
    });
    /// <summary>恢复工程打开为未命名会话，不自动覆盖原恢复副本。</summary>
    private async void RecoverProject(object? sender, RoutedEventArgs e) => await GuardUi(async () =>
    {
        if (!await ConfirmReplace()) return;
        if (!Directory.Exists(Controller.RecoveryDirectory)) { await Message("恢复自动保存", "尚无自动保存的恢复文件。"); return; }
        var folder = await StorageProvider.TryGetFolderFromPathAsync(Controller.RecoveryDirectory);
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "恢复自动保存", FileTypeFilter = [ProjectFilter], SuggestedStartLocation = folder });
        if (!_closed && files.FirstOrDefault()?.TryGetLocalPath() is { } path) await Execute(() => Controller.OpenAsync(path, recovery: true));
    });
    /// <summary>按钮与快捷键共用同一保存路径选择。</summary>
    private async void SaveProject(object? sender, RoutedEventArgs e) => await GuardUi(async () => { await SaveProjectAsync(); });
    /// <summary>写入成功才向关闭/替换流程返回 true；文件选择取消不是同意放弃修改。</summary>
    private async Task<bool> SaveProjectAsync()
    {
        if (_closed || Controller.State.IsBusy) return false;
        var folder = Controller.ProjectPath is { } saved ? await StorageProvider.TryGetFolderFromPathAsync(Path.GetDirectoryName(saved)!) : null;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "保存挑选工程", DefaultExtension = "json", SuggestedFileName = Path.GetFileName(Controller.ProjectPath ?? "本次挑选.json"), FileTypeChoices = [ProjectFilter], ShowOverwritePrompt = true, SuggestedStartLocation = folder });
        return !_closed && file?.TryGetLocalPath() is { } path && await Execute(() => Controller.SaveAsync(path));
    }
    /// <summary>保存/继续/返回三个明确选择，不因关闭提示框推断同意。</summary>
    private async Task<bool> ConfirmReplace()
    {
        if (_closed || Controller.State.IsBusy) return false;
        if (!Controller.State.Dirty) return true;
        var result = await PromptDialog.AskAsync(this, "保存本次挑选", "当前挑选尚未保存，是否先保存工程？", ("返回", PromptResult.Cancel), ("不保存，继续", PromptResult.Continue), ("保存工程", PromptResult.Save));
        return result == PromptResult.Continue || result == PromptResult.Save && await SaveProjectAsync();
    }
    /// <summary>有照片且空闲时先确认；取消、关闭提示或关闭主窗口均保留列表，不删除原照片。</summary>
    private async void ClearList(object? sender, RoutedEventArgs e) => await GuardUi(async () =>
    {
        if (_closed || Controller.State.IsBusy || !Controller.State.HasPhotos) return;
        var answer = await PromptDialog.AskAsync(this, "确认清空列表",
            $"确定清空列表中的全部 {Controller.State.PhotoCount} 张照片吗？\n\n这会移除当前列表及其挑选记录，磁盘上的原照片不会删除。",
            ("取消", PromptResult.Cancel), ("确认清空", PromptResult.Continue));
        if (answer == PromptResult.Continue && !_closed && !Controller.State.IsBusy && Controller.State.HasPhotos)
            await Execute(Controller.ClearAsync);
    });
    /// <summary>开始背景分组，期间显示现有画面并可取消。</summary>
    private async void GroupPhotos(object? sender, RoutedEventArgs e) => await Execute(Controller.GroupAsync);
    /// <summary>只取消后台操作，不关闭窗口。</summary>
    private void CancelOperation(object? sender, RoutedEventArgs e) => Controller.Cancel();
    /// <summary>拆分整组但保留同一批照片对象。</summary>
    private void SplitGroup(object? sender, RoutedEventArgs e) => Controller.Split(true);
    /// <summary>当前照片独立成组，原件和评级保持不变。</summary>
    private void SplitPhoto(object? sender, RoutedEventArgs e) => Controller.Split(false);
    /// <summary>支持一次移除多个选中组。</summary>
    private void RemoveGroups(object? sender, RoutedEventArgs e) => Controller.Remove(GroupList.SelectedItems?.OfType<GroupCard>().SelectMany(g => g.Photos).ToArray() ?? []);
    /// <summary>支持一次移除多个组内选中项。</summary>
    private void RemovePhotos(object? sender, RoutedEventArgs e) => Controller.Remove(PhotoList.SelectedItems?.OfType<PhotoItem>().ToArray() ?? []);
    /// <summary>评级动作由会话检查当前加载状态。</summary>
    private void RatePhoto(object? sender, RoutedEventArgs e)
    { if (sender is ToggleButton { Tag: string text } && Enum.TryParse<PhotoRating>(text, out var rating)) Controller.State.CurrentRating = rating; UpdateRatings(); }
    /// <summary>适应画面与恢复鸟视图只改变人工视图。</summary>
    private void FitPhoto(object? sender, RoutedEventArgs e) => DetailPane.Fit();
    /// <summary>显式恢复鸟视图保留当前结果选择。</summary>
    private void FocusBird(object? sender, RoutedEventArgs e) => DetailPane.FocusBird();
    /// <summary>组内缩略图允许不切换当前照片的右键快捷评级。</summary>
    private void PhotoContextRequested(object? sender, ContextRequestedEventArgs e)
    { if (sender is Control { DataContext: PhotoItem photo } control) OpenRatingMenu(control, photo, e); }
    /// <summary>独立照片组可评级，多照片组仍使用组内列表逐张评级。</summary>
    private void GroupContextRequested(object? sender, ContextRequestedEventArgs e)
    { if (sender is Control { DataContext: GroupCard { Photos.Count: 1 } group } control) OpenRatingMenu(control, group.Photos[0], e); }
    /// <summary>菜单仅捕获明确照片对象，关闭或忙碌后不能继续修改。</summary>
    private void OpenRatingMenu(Control control, PhotoItem photo, ContextRequestedEventArgs e)
    {
        if (!Controller.State.CanEdit) return;
        var menu = new ContextMenu();
        foreach (var (label, rating) in new[] { ("● 未标记", PhotoRating.Unmarked), ("× 舍弃", PhotoRating.Reject), ("✓ 保留", PhotoRating.Keep), ("★ 精选", PhotoRating.Favorite) })
        { var item = new MenuItem { Header = label }; item.Click += (_, _) => { if (!_closed) Controller.State.SetRating(photo, rating); }; menu.Items.Add(item); }
        menu.Open(control); e.Handled = true;
    }
    /// <summary>两个列表都按每格约两张滚动，消费滚轮后不触发选择或照片导航。</summary>
    private void ScrollThumbnails(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ListBox list) return;
        e.Handled = true; double remainder = _wheelRemainders.GetValueOrDefault(list) + e.Delta.Y;
        int steps = (int)Math.Truncate(remainder); _wheelRemainders[list] = remainder - steps;
        if (steps == 0 || list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll) return;
        double height = (list.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault()?.Bounds.Height ?? 132) + 8;
        scroll.Offset = new Vector(scroll.Offset.X, Math.Clamp(scroll.Offset.Y - steps * 2 * height, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
    }
    /// <summary>导航统一进入共享会话；文本输入和组合键保留给原控件与系统。</summary>
    private async void HandleKeys(object? sender, KeyEventArgs e) => await GuardUi(async () =>
    {
        if (FocusManager?.GetFocusedElement() is TextBox || _closed) return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
        {
            if (e.Key == Key.Z) { e.Handled = true; Controller.UndoResults(); }
            else if (e.Key == Key.S) { e.Handled = true; await SaveProjectAsync(); }
            else if (e.Key == Key.O) { e.Handled = true; OpenProject(this, new RoutedEventArgs()); }
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Left: case Key.Up: case Key.A: case Key.W: Controller.State.NavigatePhoto(-1); break;
            case Key.Right: case Key.Down: case Key.D: case Key.S: Controller.State.NavigatePhoto(1); break;
            case Key.PageUp: Controller.State.NavigateGroup(-1); break;
            case Key.PageDown: Controller.State.NavigateGroup(1); break;
            case Key.D1: case Key.NumPad1: Controller.State.CurrentRating = PhotoRating.Unmarked; break;
            case Key.D2: case Key.NumPad2: Controller.State.CurrentRating = PhotoRating.Reject; break;
            case Key.D3: case Key.NumPad3: Controller.State.CurrentRating = PhotoRating.Keep; break;
            case Key.D4: case Key.NumPad4: Controller.State.CurrentRating = PhotoRating.Favorite; break;
            case Key.R: DetailPane.FocusBird(); break;
            case Key.Escape: if (!DetailPane.CancelManualRegion()) Controller.Cancel(); break;
            default: return;
        }
        e.Handled = true;
    });
    /// <summary>后台异常只在窗口仍然打开时提示，关闭后绝不 ShowDialog。</summary>
    private async Task<bool> Execute(Func<Task<bool>> action)
    {
        try
        {
            bool result = await action();
            if (!_closed && !_closePending && Controller.Notice is { } notice) await Message("操作提示", notice);
            return result;
        }
        catch (Exception ex) { if (!_closed) { Controller.State.Status = "操作未完成：" + ex.Message; if (!_closePending) await Message("操作未完成", ex.Message); } return false; }
    }
    /// <summary>文件选择器和帮助等宿主异步入口的错误边界，避免 async void 异常使应用退出。</summary>
    private async Task GuardUi(Func<Task> action)
    {
        if (_closed) return;
        try { await action(); }
        catch (Exception ex)
        {
            if (_closed) return;
            Controller.State.Status = "操作未完成：" + ex.Message;
            try { await Message("操作未完成", ex.Message); }
            catch (Exception) { /* 系统对话框不可用时保留可见状态，不递归打开更多窗口。 */ }
        }
    }
    /// <summary>单按钮提示统一检查窗口可见性，避免生命周期回调打开已关闭的 owner。</summary>
    private Task Message(string title, string text) => _closed || _closeAllowed || !IsVisible ? Task.CompletedTask : PromptDialog.AskAsync(this, title, text, ("确定", PromptResult.Continue));
    /// <summary>帮助包含真实快捷键及可访问的项目来源。</summary>
    private async void ShowHelp(object? sender, RoutedEventArgs e) => await GuardUi(async () =>
    {
        var result = await PromptDialog.AskAsync(this, "羽选 v2.0.0-alpha.11", "添加本地 JPG / RAW 照片，或打开已有 JSON 工程。\n\n方向键 / WASD：连续切图（到组首尾自动跨组）\nPage Up / Down：切换组\n1 / 2 / 3 / 4：未标记 / 舍弃 / 保留 / 精选\n右侧滚轮：缩放；左键拖动：平移\nR：还原鸟类视图\nCtrl / Command + Z：撤销鸟种/选区编辑\nCtrl / Command + S：保存工程\n\n每五分钟自动保存有变更的工程。未命名工程保存在本机恢复目录。\n\nhttps://github.com/yyx263/BirdPhotoSelector", ("关闭", PromptResult.Cancel), ("打开 GitHub", PromptResult.Continue));
        if (result == PromptResult.Continue && !_closed) await Launcher.LaunchUriAsync(new Uri("https://github.com/yyx263/BirdPhotoSelector"));
    });
    /// <summary>先结束当前 Closing 回调，再异步确认；禁止在关闭过程直接打开模态窗口。</summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeAllowed) return;
        e.Cancel = true;
        if (_closePending) return;
        _closePending = true; Dispatcher.UIThread.Post(CompleteClose);
    }
    /// <summary>等待取消回滚并处理未保存状态，确认成功后执行唯一一次最终关闭。</summary>
    private async void CompleteClose()
    {
        try
        {
            if (Controller.State.IsBusy)
            {
                if (await PromptDialog.AskAsync(this, "退出羽选", "有任务正在处理，是否取消任务并退出？", ("返回", PromptResult.Cancel), ("取消任务并退出", PromptResult.Continue)) != PromptResult.Continue) return;
                Controller.Cancel(); await Controller.WhenIdleAsync();
            }
            if (!await ConfirmReplace()) return;
            _closeAllowed = true; Close();
        }
        catch (Exception ex) { if (!_closed) Controller.State.Status = "无法完成关闭：" + ex.Message; }
        finally { _closePending = false; }
    }
}
