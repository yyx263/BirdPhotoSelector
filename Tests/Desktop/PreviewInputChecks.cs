using System.Buffers.Binary;
using System.IO.Enumeration;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BirdPhotoSelector;
using BirdPhotoSelector.Desktop.Controls;
using BirdPhotoSelector.Desktop.Dialogs;
using BirdPhotoSelector.Desktop.Imaging;
using BirdPhotoSelector.Desktop.ViewModels;
using BirdPhotoSelector.Desktop.Views;
using BirdPhotoSelector.Imaging;
using BirdPhotoSelector.Imaging.Portable;
using SkiaSharp;

/// <summary>实际输入和像素回归：结果行空白/边距右键，以及四个方向拖框在松手前均可见。</summary>
internal static class PreviewInputChecks
{
    private static int _checks;
    public static async Task<int> Run(string root)
    {
        _checks = 0;
        PickerAndCandidates();
        await ManualOutline(root);
        await ResultMenus(root);
        return _checks;
    }
    /// <summary>验证简洁小写选择器及实际导入规则；以两种真实文本向量查询实际候选评分，排除行错位。</summary>
    private static void PickerAndCandidates()
    {
        var patterns = PhotoFilePickerTypes.Photos.Patterns!;
        Check(patterns.Count == 24 && patterns.All(p => p == p.ToLowerInvariant()) && patterns.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 24,
            "picker displays each supported extension exactly once in lowercase");
        foreach (string name in new[] { "bird.jpg", "bird.jpeg", "bird.cr2", "bird.nef", "bird.arw", "bird.dng" })
            Check(patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: false)), "compact picker includes " + name);
        Check(!patterns.Any(p => FileSystemName.MatchesSimpleExpression(p, "bird.png", ignoreCase: false)), "compact picker preserves supported format scope");
        Check(PhotoImageFormats.IsJpeg("bird.JPG") && PhotoImageFormats.IsJpeg("bird.JpEg") && PhotoImageFormats.IsRaw("bird.NEF"),
            "actual import format validation remains case insensitive");
        string directory = Path.Combine(Directory.GetCurrentDirectory(), "models/bioclip2");
        var catalog = SpeciesCandidateCatalog.Load(directory);
        using var names = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "candidates.json")));
        var entries = names.RootElement.EnumerateArray().ToArray();
        using var vectors = File.OpenRead(Path.Combine(directory, "embeddings.f32"));
        foreach (string name in new[] { "Phasianus colchicus", "Syrmaticus reevesii" })
        {
            int row = Array.FindIndex(entries, e => e.GetProperty("ScientificName").GetString() == name);
            if (row < 0) throw new InvalidOperationException("Missing candidate: " + name);
            var bytes = new byte[768 * 4]; vectors.Position = row * bytes.Length; vectors.ReadExactly(bytes);
            var query = Enumerable.Range(0, 768).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4))).ToArray();
            Check(catalog.FindBest(query).ScientificName == name, "actual catalog scoring preserves species/vector mapping for " + name);
        }
    }
    /// <summary>从真实 ListBoxItem 的文字、空白和边距发出右键，不能直接调用事件处理器代替命中测试。</summary>
    private static async Task ResultMenus(string root)
    {
        string dir = Path.Combine(root, "preview-input"); Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "bird.jpg"); File.Copy(Path.Combine(Directory.GetCurrentDirectory(), "Tests/Assets/great-tit.jpg"), path);
        using var controller = new DesktopController(new(new PortablePhotoImageDecoder(), new SystemFileOperations(), Path.Combine(dir, "recovery")));
        var window = new MainWindow(controller); window.Show();
        try
        {
            await controller.ImportAsync([path]); await controller.PreviewTask;
            var photo = controller.State.CurrentPhoto!;
            var automatic = new DetectedBird { Number = 1, Box = new BirdBox { X = .2, Y = .2, Width = .3, Height = .3 }, Species = new("环颈雉", "Phasianus colchicus", .6, Confidence: .2) };
            photo.SetBirds([automatic]); controller.ApplySpecies(photo, new("白鹭", "Egretta garzetta")); await Flush();
            var list = window.FindControl<ListBox>("BirdList")!;
            var rows = list.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
            foreach (var row in rows)
                foreach (double x in new[] { 2d, row.Bounds.Width * .3, row.Bounds.Width * .65, row.Bounds.Width - 3 })
                {
                    var menu = await OpenMenu(window, row, x);
                    Check(menu.Items.OfType<MenuItem>().Select(i => i.Header?.ToString()).SequenceEqual(new[] { "删除鸟种", "更换鸟种" }), "right click opens both actions across automatic/manual row at x=" + x);
                    menu.Close(); await Flush();
                }
            var autoRow = rows.Single(r => ReferenceEquals(r.DataContext, automatic));
            var deleteMenu = await OpenMenu(window, autoRow, autoRow.Bounds.Width - 3);
            deleteMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "删除鸟种").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); deleteMenu.Close(); await Flush();
            Check(!photo.Birds.Contains(automatic) && controller.UndoResults() && photo.Birds.Any(b => b.HasRegion), "result menu deletes its region and undo restores it");
            await Flush();
            var replacementRow = list.GetVisualDescendants().OfType<ListBoxItem>().First(r => r.DataContext is DetectedBird { HasRegion: true });
            var replaced = (DetectedBird)replacementRow.DataContext!;
            var replaceMenu = await OpenMenu(window, replacementRow, 2);
            replaceMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "更换鸟种").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)); replaceMenu.Close();
            SpeciesPickerDialog? picker = null;
            for (int i = 0; i < 200 && picker is null; i++) { await Flush(); picker = window.OwnedWindows.OfType<SpeciesPickerDialog>().FirstOrDefault(w => w.IsVisible); }
            if (picker is null) throw new TimeoutException("Species replacement picker missing.");
            picker.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "SpeciesQuery").Text = "大山雀"; await Flush();
            var choices = picker.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "SpeciesChoices");
            choices.SelectedItem = choices.Items.OfType<SpeciesIdentity>().First(s => s.ScientificName == "Parus major");
            var confirm = picker.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ConfirmSpecies");
            if (!confirm.IsEnabled) throw new InvalidOperationException("Species replacement confirmation disabled.");
            confirm.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 200 && photo.Birds.Contains(replaced); i++) await Flush();
            Check(!photo.Birds.Contains(replaced) && photo.Birds.Any(b => b.ManualSpecies?.ScientificName == "Parus major") && !photo.Birds.Any(b => b.HasRegion), "replacement menu removes old region and applies chosen manual species");
        }
        finally { controller.State.Dirty = false; window.Close(); }
    }
    private static async Task<ContextMenu> OpenMenu(Window window, ListBoxItem row, double x)
    {
        var point = row.TranslatePoint(new Point(x, row.Bounds.Height / 2), window) ?? throw new InvalidOperationException("Missing result row position.");
        window.MouseDown(point, MouseButton.Right); window.MouseUp(point, MouseButton.Right); await Flush();
        if (row.ContextMenu is not { IsOpen: true } menu) throw new InvalidOperationException("Result menu missing at row x=" + x);
        return menu;
    }
    /// <summary>用统一灰色底图直接数绿色描边像素，避免仅验证最终鸟框而漏掉拖动期间不可见的问题。</summary>
    private static async Task ManualOutline(string root)
    {
        var pane = new PhotoPane { IsDetail = true, IsInteractive = true };
        var window = new Window { Width = 640, Height = 480, Content = pane }; window.Show();
        try
        {
            var pixels = Enumerable.Repeat((byte)95, 640 * 480 * 3).ToArray();
            pane.ShowPhoto(new PhotoItem { FilePath = Path.Combine(root, "outline.jpg") }, AvaloniaImageFrame.Create(new Rgb24ImageFrame(640, 480, pixels))); await Flush();
            int committed = 0; pane.ManualRegionSelected += (_, _) => committed++;
            var low = new Point(180, 150); var high = new Point(440, 330);
            var drags = new[] { (low, high), (high, low), (new Point(high.X, low.Y), new Point(low.X, high.Y)), (new Point(low.X, high.Y), new Point(high.X, low.Y)) };
            foreach (var (start, end) in drags)
            {
                pane.BeginManualRegion(); window.MouseDown(start, MouseButton.Left); window.MouseMove(end, RawInputModifiers.LeftMouseButton); await Flush();
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No manual selection frame.");
                using var buffer = new MemoryStream(); frame.Save(buffer, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default); buffer.Position = 0;
                using var bitmap = SKBitmap.Decode(buffer);
                int green = bitmap.Pixels.Count(p => p.Green > 150 && p.Green > p.Red + 15 && p.Green > p.Blue + 5);
                Check(green > 100, "manual outline is visible before release for drag " + start + " -> " + end);
                window.MouseUp(end, MouseButton.Left); await Flush();
            }
            Check(committed == 4 && !pane.IsManualMode, "all four drag directions submit exactly one region and reset tool state");
        }
        finally { window.Close(); }
    }
    private static async Task Flush() { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); _checks++; Console.WriteLine("  OK PREVIEW INPUT " + message); }
}
