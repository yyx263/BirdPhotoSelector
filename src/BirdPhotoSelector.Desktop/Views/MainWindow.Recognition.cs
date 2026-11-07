using System.ComponentModel;
using Avalonia;
using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using BirdPhotoSelector.Desktop.Dialogs;
using BirdPhotoSelector.Desktop.ViewModels;

namespace BirdPhotoSelector.Desktop.Views;

/// <summary>识别按钮、结果上下文菜单和视图刷新；所有编辑由协调器交给共享会话。</summary>
public sealed partial class MainWindow
{
    private SpeciesNameCatalog? _speciesCatalog;
    /// <summary>历史变化只更新按钮，不触发照片列表滚动。</summary>
    private void HistoryChanged(object? sender, PropertyChangedEventArgs e) => UpdateSpeciesControls();
    /// <summary>忙碌时不能撤销，标签说明本次可撤销动作。</summary>
    private void UpdateSpeciesControls()
    {
        UndoResultsButton.IsEnabled = !_closed && Controller.State.CanEdit && Controller.State.SpeciesEdits.CanUndo;
        ToolTip.SetTip(UndoResultsButton, Controller.State.SpeciesEdits.CanUndo ? Controller.State.SpeciesEdits.Description + " · Ctrl / Command + Z" : "撤销最近一次鸟种或选区编辑");
    }
    /// <summary>结果变化同步编号和选中，不重建缩略图列表，不打断用户滚动位置。</summary>
    private void ResultsChanged(PhotoItem photo, ResultsViewAction action)
    {
        if (_closed || !ReferenceEquals(Controller.State.CurrentPhoto, photo)) return;
        _syncing = true;
        try { BirdList.ItemsSource = photo.Birds; BirdList.SelectedItem = photo.SelectedBird; }
        finally { _syncing = false; }
        if (Controller.State.IsPreviewReady && ReferenceEquals(Controller.PreviewPhoto, photo))
        {
            if (action == ResultsViewAction.RestoreSaved) { FullPane.ShowPhoto(photo, Controller.Preview); DetailPane.ShowPhoto(photo, Controller.Preview); }
            else { FullPane.RefreshOverlays(); if (action == ResultsViewAction.FocusBird && photo.SelectedBird?.HasRegion == true) DetailPane.FocusBird(); }
        }
        if (photo.SelectedBird is { } bird) BirdList.ScrollIntoView(bird);
        UpdateSpeciesControls();
    }
    /// <summary>批量入口默认中国候选，不改变分组和评级。</summary>
    private async void RecognizeAll(object? sender, RoutedEventArgs e) => await GuardUi(async () =>
    { if (!Controller.State.HasPhotos) { Controller.State.Status = "请先添加照片"; return; } await Execute(Controller.RecognizeAsync); });
    /// <summary>重新识别成功才替换旧结果，可撤销，因此不再重复确认。</summary>
    private async void ReidentifyPhoto(object? sender, RoutedEventArgs e) => await GuardUi(() => Execute(Controller.ReidentifyAsync));
    /// <summary>清空与其他人工结果操作共用撤销栈。</summary>
    private void ClearBirdResults(object? sender, RoutedEventArgs e) { DetailPane.CancelManualRegion(); Controller.ClearResults(); }
    /// <summary>工具按钮与 Esc 的手动模式状态一致。</summary>
    private void ToggleManualRegion(object? sender, RoutedEventArgs e)
    { if (DetailPane.IsManualMode) DetailPane.CancelManualRegion(); else DetailPane.BeginManualRegion(); }
    /// <summary>按钮与快捷键执行同一个撤销入口。</summary>
    private void UndoBirdResults(object? sender, RoutedEventArgs e) { DetailPane.CancelManualRegion(); Controller.UndoResults(); }
    /// <summary>名录延迟加载，仅确认后将人工鸟种应用到原照片，等待期间不预先改动。</summary>
    private async Task PickSpecies(PhotoItem photo, DetectedBird? replaced = null)
    {
        if (!Controller.State.CanRate || !ReferenceEquals(Controller.State.CurrentPhoto, photo) || replaced is not null && !photo.Birds.Contains(replaced)) return;
        _speciesCatalog ??= SpeciesNameCatalog.Load();
        var picker = new SpeciesPickerDialog(_speciesCatalog.Records, Controller.State.RecentSpecies.Items, replaced?.EffectiveSpecies);
        var selected = await picker.ShowDialog<SpeciesIdentity?>(this);
        if (!_closed && selected is not null) Controller.ApplySpecies(photo, selected, replaced);
    }
    /// <summary>允许重复点击添加多个鸟种，每次搜索仍为单选并更新工程最近选择。</summary>
    private async void AddSpecies(object? sender, RoutedEventArgs e) => await GuardUi(async () =>
    { if (Controller.State.CurrentPhoto is { } photo) await PickSpecies(photo); });
    /// <summary>自动和手动结果提供相同的删除/更换菜单，删除入口同时删除对应选区。</summary>
    private void ResultContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!Controller.State.CanEditBirdRegions || Controller.State.CurrentPhoto is not { } photo) return;
        // 在列表层接收事件，覆盖整行的文字、空白和外边距；空列表区域不借用上次选中的鸟。
        var row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
            .FirstOrDefault(item => item.GetVisualAncestors().Contains(BirdList));
        if (row?.DataContext is not DetectedBird bird || !photo.Birds.Contains(bird)) return;
        var menu = new ContextMenu(); var delete = new MenuItem { Header = "删除鸟种" }; var replace = new MenuItem { Header = "更换鸟种" };
        delete.Click += (_, _) => { if (!_closed) Controller.DeleteResult(photo, bird); };
        replace.Click += async (_, _) => await GuardUi(() => PickSpecies(photo, bird)); menu.Items.Add(delete); menu.Items.Add(replace);
        row.ContextMenu = menu; e.Handled = true; menu.Open(row);
    }
}
