using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

/// <summary>Presentation numbers compact independently of persisted result identities.</summary>
internal static class ContinuousBirdNumberChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
            Console.WriteLine("  OK CONTINUOUS NUMBERS " + message);
        }

        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.ContinuousBirdNumberChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "numbering.JPG");
            WriteJpeg(path);
            var species = new SpeciesMatch("大山雀", "Parus major", .61, Confidence: .88);
            var first = Bird(37, .05, species);
            var second = Bird(55, .35, species);
            var third = Bird(92, .65, species, manual: true);
            var photo = new PhotoItem { FilePath = path, Rating = PhotoRating.Favorite };
            photo.SetBirds(new[] { first, second, third }, selectedIndex: 1, nextBirdNumber: 200);
            Check(photo.Birds.Select(bird => bird.DisplayNumber).SequenceEqual(new[] { 1, 2, 3 }) &&
                  photo.Birds.Select(bird => bird.Number).SequenceEqual(new[] { 37, 55, 92 }) && photo.NextBirdNumber == 200,
                "loaded sparse identities display 1 to N without rewriting identity or its next allocation");

            var changed = new List<string?>();
            second.PropertyChanged += Track;
            void Track(object? sender, PropertyChangedEventArgs e) => changed.Add(e.PropertyName);
            photo.RemoveBird(first);
            Check(photo.Birds.Select(bird => bird.DisplayNumber).SequenceEqual(new[] { 1, 2 }) &&
                  ReferenceEquals(photo.SelectedBird, second) && second.Number == 55 && third.Number == 92 &&
                  second.Species == species && third.Species == species && photo.NextBirdNumber == 200,
                "removing a leading row compacts visible numbers and keeps the selected bird, species and identities");
            Check(changed.Contains(nameof(DetectedBird.DisplayNumber)) && !changed.Contains(nameof(DetectedBird.Number)),
                "renumbering publishes DisplayNumber notifications without pretending the result identity changed");
            second.PropertyChanged -= Track;

            var manual = photo.AddManualRegion(Box(.15)); manual.Species = species;
            Check(manual.Number == 200 && manual.DisplayNumber == 3 && photo.NextBirdNumber == 201 &&
                  ReferenceEquals(photo.SelectedBird, manual) && third.DisplayNumber == 2,
                "a new manual selection receives the next compact row number while asynchronous identity stays unique");

            photo.SelectedBirdIndex = 0;
            photo.Birds.Move(1, 0);
            Check(photo.Birds.Select(bird => bird.DisplayNumber).SequenceEqual(new[] { 1, 2, 3 }) &&
                  ReferenceEquals(photo.SelectedBird, second) && photo.SelectedBirdIndex == 1,
                "moving a result row reindexes the presentation without selecting a different bird");
            var inserted = Bird(401, .5, species);
            photo.Birds.Insert(0, inserted);
            Check(photo.Birds.Select(bird => bird.DisplayNumber).SequenceEqual(new[] { 1, 2, 3, 4 }) &&
                  ReferenceEquals(photo.SelectedBird, second) && photo.SelectedBirdIndex == 2,
                "inserting a result before the current row retains its selected object and compacts all labels");
            var replacement = Bird(402, .45, species);
            photo.Birds[0] = replacement;
            Check(replacement.DisplayNumber == 1 && second.DisplayNumber == 3 &&
                  ReferenceEquals(photo.SelectedBird, second),
                "replacing a different row preserves selection and assigns its current display position");
            photo.Birds.Remove(replacement);
            Check(second.DisplayNumber == 2 && ReferenceEquals(photo.SelectedBird, second) &&
                  photo.Birds.Select(bird => bird.DisplayNumber).SequenceEqual(new[] { 1, 2, 3 }),
                "direct collection removal reindexes labels while retaining a surviving selected object");

            photo.SetBirds(new[] { manual, second }, selectedIndex: 1, nextBirdNumber: 500);
            Check(manual.DisplayNumber == 1 && second.DisplayNumber == 2 &&
                  ReferenceEquals(photo.SelectedBird, second) && second.Species == species,
                "replacing the displayed result order also starts at one and keeps the requested subject");
            var group = new PhotoGroup("编号检查"); group.Photos.Add(photo);
            string project = Path.Combine(root, "compact.json");
            var projects = new ProjectService();
            await projects.SaveAsync(project, new[] { group }, .5);
            var restored = (await projects.LoadAsync(project)).Groups.Single().Photos.Single();
            Check(restored.Birds.Select(bird => bird.DisplayNumber).SequenceEqual(new[] { 1, 2 }) &&
                  restored.SelectedBird?.Number == second.Number && restored.Birds[0].Number == manual.Number &&
                  restored.Birds[1].Species == species,
                "project reopen reconstructs compact presentation while preserving the selected stored result");

            photo.SetBirds(Array.Empty<DetectedBird>(), nextBirdNumber: 500);
            var restarted = photo.AddManualRegion(Box(.2));
            Check(restarted.DisplayNumber == 1 && restarted.Number == 500 && photo.NextBirdNumber == 501,
                "clearing every selection then adding one restarts the visible list at one");

            var progress = new List<SpeciesProgress>();
            using (var rebuild = new SinglePhotoRecognitionService(
                detectorOverride: (_, _) => new(Box(.1), "定位完成") { Birds = new[] { Box(.1), Box(.55) } },
                recognizerOverride: (_, _, _, _) => new(species, "成功")))
            {
                var result = await rebuild.RebuildAsync(photo, progress: new InlineProgress(progress.Add));
                Check(result.Success && progress.Any(item => item.Message.Contains("（鸟 1）")) &&
                      progress.Any(item => item.Message.Contains("（鸟 2）")) &&
                      progress.All(item => !item.Message.Contains("（鸟 501）")),
                    "single-photo rebuild progress uses the visible sequence instead of old allocation values");
                result.ApplyTo(photo);
                Check(photo.Birds.Select(bird => bird.DisplayNumber).SequenceEqual(new[] { 1, 2 }) &&
                      photo.Birds.Select(bird => bird.Number).SequenceEqual(new[] { 501, 502 }),
                    "successful complete rebuild displays a fresh 1 to N list without reusing old result identities");
            }
            progress.Clear();
            var detectedBoxes = photo.Birds.Select(bird => bird.Box.Copy()).ToArray();
            using (var workflow = new BirdRecognitionWorkflow(
                detectorOverride: (_, _) => new(Box(.1), "定位完成") { Birds = detectedBoxes },
                recognizerOverride: (_, _, _, _) => new(species, "成功")))
            {
                var result = await workflow.RecognizeAsync(new[] { photo }, progress: new InlineProgress(progress.Add));
                Check(result.MatchedBirds == 2 && progress.Any(item => item.Message.Contains("（鸟 1）")) &&
                      progress.Any(item => item.Message.Contains("（鸟 2）")) &&
                      progress.All(item => !item.Message.Contains("（鸟 501）")),
                    "batch localization and recognition progress also reports the current visual sequence");
            }

            Check(photo.CompactFormatLabel == "JPG", "a single JPG has a compact JPG badge");
            var labels = new List<string?>();
            photo.PropertyChanged += (_, e) => labels.Add(e.PropertyName);
            var raw = new PhotoAsset(Path.ChangeExtension(path, ".NEF"), PhotoAssetKind.Raw);
            photo.AddAsset(raw);
            Check(photo.CompactFormatLabel == "J+R" && labels.Contains(nameof(PhotoItem.CompactFormatLabel)),
                "adding a companion publishes the compact J+R badge without decoding RAW");
            photo.RemoveAsset(photo.Assets.Single(asset => asset.Kind == PhotoAssetKind.Jpg).Id);
            Check(photo.CompactFormatLabel == "RAW", "a remaining RAW source displays the compact RAW badge");
            return count;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe continuous-numbering fixture cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static DetectedBird Bird(int identity, double x, SpeciesMatch species, bool manual = false) =>
        new() { Number = identity, IsManual = manual, Box = Box(x), Species = species, RecognitionStatus = "已有结果" };
    private static BirdBox Box(double x) => new() { X = x, Y = .2, Width = .15, Height = .3, Confidence = .9 };
    private sealed class InlineProgress(Action<SpeciesProgress> action) : IProgress<SpeciesProgress>
    { public void Report(SpeciesProgress value) => action(value); }
    private static void WriteJpeg(string path)
    {
        var image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat((byte)100, 64 * 48 * 3).ToArray(), 64 * 3);
        image.Freeze();
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path); encoder.Save(output);
    }
}
