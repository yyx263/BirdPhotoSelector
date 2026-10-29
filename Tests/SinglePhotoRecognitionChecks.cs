using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

internal static class SinglePhotoRecognitionChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK SINGLE RECOGNITION " + description);
        }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SinglePhotoRecognitionChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "generated-single.jpg");
            var image = Image();
            var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(path)) encoder.Save(output);
            var match = new SpeciesMatch("大山雀", "Parus major", .65, Confidence: .86);
            BirdDetectionResult Detected() => new(Box(.1, .12), "已定位两只鸟")
            { Birds = new[] { Box(.1, .12), Box(.5, .30) } };
            SinglePhotoRecognitionService SuccessService() => new(
                detectorOverride: (_, token) => { token.ThrowIfCancellationRequested(); return Detected(); },
                recognizerOverride: (_, _, _, token) => { token.ThrowIfCancellationRequested(); return new(match, "本张新结果"); });
            var photo = Photo(path);
            var group = new PhotoGroup("原分组"); group.Photos.Add(photo);
            var before = State(photo);
            var originalBirds = photo.Birds.ToArray();
            int mutations = 0;
            photo.PropertyChanged += CountMutation;
            void CountMutation(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => mutations++;
            var progress = new List<SpeciesProgress>();
            using (var service = SuccessService())
            {
                var result = await service.RebuildAsync(photo, progress: new InlineProgress(progress.Add));
                Check(result.Success && !result.IsEmpty && result.Errors.Count == 0 && result.Birds.Count == 2 && mutations == 0 &&
                      Unchanged(photo, before, originalBirds),
                    "successful computation is detached and leaves all old automatic/manual evidence and view untouched before explicit application");
                Check(result.Birds[0].Box.Width == .30 && result.Birds.Select(bird => bird.Number).SequenceEqual(new[] { 37, 38 }) &&
                      result.NextBirdNumber == 39 && result.Birds.All(bird => !bird.IsManual && bird.Species == match),
                    "new birds start at the old numbering high-water mark and select the largest newly detected bird");
                Check(progress.Any(update => update.Message.Contains("重新定位", StringComparison.Ordinal) && update.Message.Contains(path, StringComparison.Ordinal)) &&
                      progress.Any(update => update.Message.Contains("选区 2 / 2", StringComparison.Ordinal) && update.Message.Contains(path, StringComparison.Ordinal)) &&
                      progress.Last().Completed == progress.Last().Total,
                    "single-photo progress identifies the exact path, localization stage and current bird through completion");
                // Public result objects cannot be changed into the private transaction to be committed.
                var displayedProposal = result.Birds[0]; displayedProposal.Species = null; displayedProposal.Box.X = .9;
                result.ApplyTo(photo);
                Check(photo.Birds.Count == 2 && photo.Birds.All(bird => !bird.IsManual && bird.Species == match) &&
                      !photo.Birds.Intersect(originalBirds).Any() && photo.SelectedBirdIndex == 0 && photo.SelectedBird?.Number == 37 &&
                      photo.NextBirdNumber == 39,
                    "explicit commit replaces every old automatic and manual region with independently held complete new results");
                Check(group.Photos.Single() == photo && photo.Id == "retained-logical-photo" && photo.Rating == PhotoRating.Favorite &&
                      photo.GeometryStatus == PhotoGeometryStatus.Current && photo.CanUseBirdGeometry &&
                      photo.GeometryReference?.AssetId == photo.ActivePreviewAssetId &&
                      photo.GeometryReference?.DisplayWidth == 64 && photo.GeometryReference?.DisplayHeight == 48 &&
                      photo.PixelWidth == 64 && photo.PixelHeight == 48 && photo.ViewZoom is null &&
                      photo.ViewCenterX is null && photo.ViewCenterY is null,
                    "commit keeps logical identity, rating and group while confirming the current source and resetting its detail view");
                var applied = State(photo); var appliedBirds = photo.Birds.ToArray();
                Check(Rejected(() => result.ApplyTo(photo)) && Unchanged(photo, applied, appliedBirds),
                    "one completed result cannot be applied twice or reuse already committed bird numbers");
            }
            photo.PropertyChanged -= CountMutation;

            async Task Failure(string description, Func<SinglePhotoRecognitionService> create)
            {
                var subject = Photo(path); var state = State(subject); var birds = subject.Birds.ToArray();
                using var service = create();
                var result = await service.RebuildAsync(subject);
                Check(!result.Success && !result.IsEmpty && result.Errors.Count > 0 &&
                      Rejected(() => result.ApplyTo(subject, allowEmpty: true)) && Unchanged(subject, state, birds), description);
            }
            await Failure("a missing detector preserves every old region, species, selected number and saved view",
                () => new(detectorOverride: (_, _) => new(null, "定位模型缺失", true),
                    recognizerOverride: (_, _, _, _) => throw new InvalidOperationException("must not recognize without localization")));
            await Failure("a localization exception cannot be interpreted as a successful empty detection",
                () => new(detectorOverride: (_, _) => throw new IOException("generated localization fault")));
            await Failure("an unreadable current image cannot erase old species or be committed as an empty result",
                () => new(loaderOverride: (_, _) => throw new InvalidDataException("generated decode fault")));
            await Failure("an invalid localization box is a failure instead of a request to clear the previous regions",
                () => new(detectorOverride: (_, _) => new(new BirdBox { X = -.5, Width = .1, Height = .1 }, "invalid")));
            await Failure("a missing species model after successful localization keeps all old manual and automatic results",
                () => new(detectorOverride: (_, _) => Detected(), recognizerOverride: (_, _, _, _) => new(null, "鸟种模型缺失", true)));
            int recognized = 0;
            await Failure("failure on a later new bird never applies an earlier partially recognized result",
                () => new(detectorOverride: (_, _) => Detected(), recognizerOverride: (_, _, _, _) =>
                    ++recognized == 1 ? new(match, "first complete") : new(null, "second failed", false, "generated species failure")));

            foreach (string stage in new[] { "before", "decode", "detect", "recognize" })
            {
                var subject = Photo(path); var state = State(subject); var birds = subject.Birds.ToArray();
                using var cancellation = new CancellationTokenSource();
                if (stage == "before") cancellation.Cancel();
                using var service = new SinglePhotoRecognitionService(
                    loaderOverride: (source, token) =>
                    { if (stage == "decode") cancellation.Cancel(); token.ThrowIfCancellationRequested(); return ImageLoader.Load(source); },
                    detectorOverride: (_, token) =>
                    { if (stage == "detect") cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Detected(); },
                    recognizerOverride: (_, _, _, token) =>
                    { if (stage == "recognize") cancellation.Cancel(); token.ThrowIfCancellationRequested(); return new(match, "complete"); });
                bool canceled = false;
                try { await service.RebuildAsync(subject, cancellationToken: cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled && Unchanged(subject, state, birds),
                    $"cancellation at {stage} preserves the original region objects, recognition evidence, selection, numbering and view");
            }

            var emptyPhoto = Photo(path); var emptyState = State(emptyPhoto); var oldEmptyBirds = emptyPhoto.Birds.ToArray();
            int unexpectedRecognitions = 0;
            using (var emptyService = new SinglePhotoRecognitionService(detectorOverride: (_, _) => new(null, "正常完成但未检测到鸟"),
                recognizerOverride: (_, _, _, _) => { unexpectedRecognitions++; return new(match, "unexpected"); }))
            {
                var empty = await emptyService.RebuildAsync(emptyPhoto);
                Check(empty.Success && empty.IsEmpty && unexpectedRecognitions == 0 &&
                      Rejected(() => empty.ApplyTo(emptyPhoto)) && Unchanged(emptyPhoto, emptyState, oldEmptyBirds),
                    "a genuine zero-detection result requires a separate explicit allowEmpty before replacing any saved region");
                empty.ApplyTo(emptyPhoto, allowEmpty: true);
                Check(emptyPhoto.Birds.Count == 0 && emptyPhoto.NextBirdNumber == 37 && emptyPhoto.Rating == PhotoRating.Favorite &&
                      emptyPhoto.GeometryStatus == PhotoGeometryStatus.Current && emptyPhoto.CanUseBirdGeometry && emptyPhoto.ViewZoom is null,
                    "confirmed empty replacement clears all old regions but keeps the logical photo, rating and numbering high-water mark");
            }
            using (var service = SuccessService())
            {
                var afterClear = await service.RebuildAsync(emptyPhoto); afterClear.ApplyTo(emptyPhoto);
                Check(emptyPhoto.Birds.Select(bird => bird.Number).SequenceEqual(new[] { 37, 38 }),
                    "later detections after an approved clear never reuse a removed old region's identity");
            }

            var rawPath = Path.Combine(root, "generated-single.NEF");
            await File.WriteAllTextAsync(rawPath, "Generated opaque raw bytes; test explicitly injects a compatible decoded image.");
            var sourceSwitch = Photo(path); var oldJpg = sourceSwitch.ActivePreviewAsset!;
            var raw = new PhotoAsset(rawPath, PhotoAssetKind.Raw, "current-raw-source");
            sourceSwitch.AddAsset(raw); sourceSwitch.RemoveAsset(oldJpg.Id);
            Check(sourceSwitch.GeometryStatus == PhotoGeometryStatus.NeedsReview && sourceSwitch.GeometryReference?.AssetId == oldJpg.Id,
                "the rebuild fixture starts with the old JPG frame requiring review on its new RAW source");
            using (var service = new SinglePhotoRecognitionService(loaderOverride: (_, _) => image,
                detectorOverride: (_, _) => Detected(), recognizerOverride: (_, _, china, _) =>
                { Check(china, "single-photo rebuilding retains the selected China candidate scope"); return new(match, "new RAW evidence"); }))
            {
                var rebuilt = await service.RebuildAsync(sourceSwitch); rebuilt.ApplyTo(sourceSwitch);
                Check(sourceSwitch.GeometryStatus == PhotoGeometryStatus.Current && sourceSwitch.CanUseBirdGeometry &&
                      sourceSwitch.GeometryReference?.AssetId == raw.Id && sourceSwitch.GeometryReference.CanonicalPath == raw.CanonicalPath &&
                      sourceSwitch.Birds.All(bird => !bird.IsManual && bird.Species == match) && sourceSwitch.NextBirdNumber == 39,
                    "explicit rebuilding establishes a new current source frame and replaces all unverified old JPG and manual regions");
            }

            foreach (string change in new[] { "species", "box", "view", "rating", "selection", "members", "bird-objects" })
            {
                var subject = Photo(path);
                using var service = SuccessService();
                var result = await service.RebuildAsync(subject);
                switch (change)
                {
                    case "species": subject.Birds[0].Species = new("麻雀", "Passer montanus", .45); break;
                    case "box": subject.Birds[0].Box.X += .01; break;
                    case "view": subject.ViewZoom = 1.1; break;
                    case "rating": subject.Rating = PhotoRating.Keep; break;
                    case "selection": subject.SelectedBirdIndex = 0; break;
                    case "members": subject.AddAsset(new(rawPath, PhotoAssetKind.Raw)); break;
                    case "bird-objects": subject.SetBirds(subject.Birds.Select(bird => new DetectedBird { Number = bird.Number,
                        IsManual = bird.IsManual, Box = bird.Box.Copy(), Species = bird.Species, RecognitionStatus = bird.RecognitionStatus }),
                        subject.SelectedBirdIndex, subject.NextBirdNumber); break;
                }
                var changedState = State(subject); var changedBirds = subject.Birds.ToArray();
                Check(Rejected(() => result.ApplyTo(subject)) && Unchanged(subject, changedState, changedBirds),
                    $"a pending result cannot overwrite a newer {change} change in the same logical photo");
            }

            string changedPath = Path.Combine(root, "changed-after-read.jpg"); File.Copy(path, changedPath);
            var changedPhoto = Photo(changedPath);
            using (var service = SuccessService())
            {
                var result = await service.RebuildAsync(changedPhoto);
                var state = State(changedPhoto); var birds = changedPhoto.Birds.ToArray();
                var oldWrite = File.GetLastWriteTimeUtc(changedPath);
                var changedBytes = await File.ReadAllBytesAsync(changedPath); changedBytes[^1] ^= 1;
                await File.WriteAllBytesAsync(changedPath, changedBytes); File.SetLastWriteTimeUtc(changedPath, oldWrite);
                Check(Rejected(() => result.ApplyTo(changedPhoto)) && Unchanged(changedPhoto, state, birds),
                    "commit detects changed original bytes even when file size and modification time were preserved");
            }
            string duringPath = Path.Combine(root, "changed-during-read.jpg"); File.Copy(path, duringPath);
            var duringPhoto = Photo(duringPath); var duringState = State(duringPhoto); var duringBirds = duringPhoto.Birds.ToArray();
            using (var service = new SinglePhotoRecognitionService(detectorOverride: (_, _) => new(Box(.3, .2), "one bird"),
                recognizerOverride: (_, _, _, _) =>
                { var bytes = File.ReadAllBytes(duringPath); bytes[^1] ^= 1; File.WriteAllBytes(duringPath, bytes); return new(match, "complete"); }))
            {
                var result = await service.RebuildAsync(duringPhoto);
                Check(!result.Success && Unchanged(duringPhoto, duringState, duringBirds),
                    "a file modified during inference cannot be submitted as a successful new coordinate frame");
            }

            var exhausted = Photo(path); exhausted.SetBirds(exhausted.Birds.ToArray(), 1, int.MaxValue);
            var exhaustedState = State(exhausted); var exhaustedBirds = exhausted.Birds.ToArray();
            using (var service = SuccessService())
            {
                var result = await service.RebuildAsync(exhausted);
                Check(!result.Success && Unchanged(exhausted, exhaustedState, exhaustedBirds),
                    "exhausted numbering is rejected before replacing any old region or view");
            }

            string? model = Environment.GetEnvironmentVariable("BIRD_MODEL_PATH");
            string? assets = Environment.GetEnvironmentVariable("BIRD_SPECIES_ASSETS");
            string? sample = Environment.GetEnvironmentVariable("BIRD_SAMPLE_PATH");
            if (!string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(assets) && !string.IsNullOrWhiteSpace(sample))
            {
                var nativePhoto = Photo(Path.GetFullPath(sample));
                using var service = new SinglePhotoRecognitionService(model, assets);
                var result = await service.RebuildAsync(nativePhoto);
                Check(result.Success && result.Birds.Count > 0 && result.Birds.All(bird => bird.Species is { IsValid: true }),
                    "the real detector and BioCLIP model produce a complete detached single-photo replacement");
                result.ApplyTo(nativePhoto);
                Check(nativePhoto.CanUseBirdGeometry && nativePhoto.Birds.All(bird => !bird.IsManual && bird.Number >= 37) &&
                      nativePhoto.Rating == PhotoRating.Favorite && nativePhoto.NextBirdNumber > 37,
                    "real-model single-photo commit replaces every old manual region and confirms current-source geometry without changing rating");
            }
            return count;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
    }

    private static PhotoItem Photo(string path)
    {
        var photo = new PhotoItem { Id = "retained-logical-photo", FilePath = path, Rating = PhotoRating.Favorite,
            PixelWidth = 64, PixelHeight = 48 };
        photo.SetBirds(new[]
        {
            new DetectedBird { Number = 3, Box = Box(.1, .12), Species = new("旧鸟", "Parus major", .41), RecognitionStatus = "old auto" },
            new DetectedBird { Number = 8, IsManual = true, Box = Box(.5, .20), Species = new("旧手动鸟", "Passer montanus", .49), RecognitionStatus = "old manual" }
        }, selectedIndex: 1, nextBirdNumber: 37);
        photo.ViewZoom = 2.7; photo.ViewCenterX = .6; photo.ViewCenterY = .4;
        return photo;
    }
    private static string State(PhotoItem photo) => JsonSerializer.Serialize(new
    {
        photo.Id, Assets = photo.Assets.Select(asset => new { asset.Id, asset.CanonicalPath, asset.Kind }).ToArray(),
        photo.ActivePreviewAssetId, photo.PreferredPreviewAssetId,
        Birds = photo.Birds.Select(bird => new { bird.Number, bird.IsManual, bird.Box, bird.Species, bird.RecognitionStatus }).ToArray(),
        photo.NextBirdNumber, photo.SelectedBirdIndex, photo.Rating, photo.DetectionStatus, photo.GeometryReference,
        photo.GeometryStatus, photo.PixelWidth, photo.PixelHeight, photo.ViewZoom, photo.ViewCenterX, photo.ViewCenterY
    });
    private static bool Unchanged(PhotoItem photo, string state, DetectedBird[] objects) =>
        State(photo) == state && photo.Birds.Count == objects.Length && photo.Birds.Zip(objects).All(pair => ReferenceEquals(pair.First, pair.Second));
    private static bool Rejected(Action action)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is InvalidOperationException or IOException) { return true; }
    }
    private static BirdBox Box(double x, double size) => new() { X = x, Y = .2, Width = size, Height = size, Confidence = .92 };
    private static BitmapSource Image()
    {
        var image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat((byte)110, 64 * 48 * 3).ToArray(), 64 * 3);
        image.Freeze(); return image;
    }
    private sealed class InlineProgress(Action<SpeciesProgress> callback) : IProgress<SpeciesProgress>
    { public void Report(SpeciesProgress value) => callback(value); }
}
