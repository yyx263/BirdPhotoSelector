using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

internal static class RecognitionWorkflowChecks
{
    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            assertions++;
            Console.WriteLine("  OK RECOGNITION FLOW " + message);
        }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.RecognitionWorkflowChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "first-photo.jpg");
            string secondPath = Path.Combine(root, "second-photo.jpg");
            var image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Rgb24, null,
                Enumerable.Repeat((byte)110, 64 * 48 * 3).ToArray(), 64 * 3);
            image.Freeze();
            var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path)) encoder.Save(stream);
            File.Copy(path, secondPath);
            BirdBox Box(double x) => new() { X = x, Y = .15, Width = .2, Height = .5, Confidence = .9 };
            var species = new SpeciesMatch("大山雀", "Parus major", .61, Confidence: .88);
            var empty = new PhotoItem { FilePath = path, ViewZoom = 1.3, ViewCenterX = .4, ViewCenterY = .6 };
            var second = new PhotoItem { FilePath = secondPath };
            var updates = new List<SpeciesProgress>();
            int detections = 0, recognitions = 0, mutations = 0;
            empty.PropertyChanged += (_, _) => mutations++;
            using (var workflow = new BirdRecognitionWorkflow(
                detectorOverride: (_, ct) => { ct.ThrowIfCancellationRequested(); detections++; return new(Box(.1), "已定位 1 只鸟"); },
                recognizerOverride: (_, _, china, ct) =>
                { ct.ThrowIfCancellationRequested(); Check(china, "the combined command retains the China candidate scope"); recognitions++; return new(species, "已识别"); }))
            {
                var result = await workflow.RecognizeAsync(new[] { empty, second }, progress: new InlineProgress(updates.Add));
                Check(detections == 2 && recognitions == 2 && result.MatchedBirds == 2 && result.SkippedPhotos == 0,
                    "photos can be localized and identified directly without running automatic grouping first");
                Check(mutations == 0 && empty.Birds.Count == 0 && result.Outcomes[0].Birds.Single().Number == 1 &&
                      empty.ViewZoom == 1.3 && empty.ViewCenterX == .4 && empty.ViewCenterY == .6,
                    "the combined worker returns detached first-detection results without editing UI-owned photos or their browsed view");
                Check(updates.Any(update => update.Message.Contains("正在定位鸟类 1 / 2") && update.Message.Contains(path)) &&
                      updates.Any(update => update.Message.Contains("正在识别鸟种 2 / 2") && update.Message.Contains(secondPath)) &&
                      updates.Last().Completed == 2 && updates.Last().Total == 2 && updates.Last().Message.Contains(secondPath),
                    "progress identifies the photo index, full path and localization/recognition stage through completion");
                result.Outcomes[0].ApplyTo(empty);
                Check(empty.Birds.Count == 1 && empty.SelectedBird?.Number == 1 && empty.Birds[0].Species == species &&
                      empty.PixelWidth == 64 && empty.PixelHeight == 48,
                    "explicit UI application installs localization, species and oriented image dimensions");
                Check(empty.ViewZoom is null && empty.ViewCenterX is null && empty.ViewCenterY is null,
                    "first localization clears a previously browsed fit view so later photo selection initializes the bird detail framing");
            }

            var original = empty.Birds[0];
            var manual = empty.AddManualRegion(Box(.65));
            manual.Species = species;
            manual.RecognitionStatus = "原有手动识别";
            empty.ViewZoom = 2.4; empty.ViewCenterX = .7; empty.ViewCenterY = .4;
            int originalNumber = original.Number, manualNumber = manual.Number;
            int highWaterMark = empty.NextBirdNumber;
            using (var workflow = new BirdRecognitionWorkflow(
                detectorOverride: (_, _) => new(Box(.11), "已定位 1 只鸟"),
                recognizerOverride: (_, _, _, _) => new(null, "模型读取失败", true, "fixture species failure")))
            {
                var result = await workflow.RecognizeAsync(new[] { empty });
                Check(result.MissingSpeciesModel && result.Outcomes.Single().Birds[0].Number == originalNumber &&
                      result.Outcomes.Single().Birds[0].Species == species && result.Outcomes.Single().NextBirdNumber == highWaterMark,
                    "overlapping refreshed detections reuse stable numbers and failed species recognition retains prior evidence");
                result.Outcomes.Single().ApplyTo(empty);
                Check(ReferenceEquals(empty.SelectedBird, manual) && manual.Number == manualNumber &&
                      empty.Birds.Last() == manual && manual.Species == species && manual.RecognitionStatus.Contains("保留上次结果") &&
                      empty.ViewZoom == 2.4 && empty.ViewCenterX == .7 && empty.ViewCenterY == .4,
                    "recognition keeps manual region identity, selection, saved pan/zoom and previous species on failure");
            }
            var beforeFailure = empty.Birds.ToArray();
            using (var workflow = new BirdRecognitionWorkflow(
                detectorOverride: (_, _) => new(null, "定位模型缺失", true),
                recognizerOverride: (_, _, _, _) => new(null, "鸟种模型缺失", true)))
            {
                var result = await workflow.RecognizeAsync(new[] { empty });
                result.Outcomes.Single().ApplyTo(empty);
                Check(result.MissingDetectionModel && result.MissingSpeciesModel &&
                      empty.Birds.SequenceEqual(beforeFailure) && empty.Birds.All(bird => bird.Species == species) &&
                      ReferenceEquals(empty.SelectedBird, manual),
                    "missing inference assets retain all existing region objects, evidence and the selected bird");
            }
            using (var workflow = new BirdRecognitionWorkflow(
                detectorOverride: (_, _) => new(null, "本次未检测到鸟"),
                recognizerOverride: (_, _, _, _) => new(species, "已识别")))
            {
                var result = await workflow.RecognizeAsync(new[] { empty });
                Check(result.Outcomes.Single().Birds.Count == 2 && result.MatchedBirds == 2,
                    "a detector false negative does not erase known regions and those regions can still be identified");
            }
            var shiftedPhoto = new PhotoItem { FilePath = path };
            shiftedPhoto.SetBirds(new[] { new DetectedBird { Number = 4, Box = Box(.1), Species = species } });
            var previouslyKnown = shiftedPhoto.Birds.Single();
            using (var workflow = new BirdRecognitionWorkflow(
                detectorOverride: (_, _) => new(Box(.65), "本次定位发生变化"),
                recognizerOverride: (_, _, _, _) => new(null, "鸟种模型失败", false, "fixture failure")))
            {
                var result = await workflow.RecognizeAsync(new[] { shiftedPhoto });
                result.Outcomes.Single().ApplyTo(shiftedPhoto);
                Check(shiftedPhoto.Birds.Contains(previouslyKnown) && ReferenceEquals(shiftedPhoto.SelectedBird, previouslyKnown) &&
                      previouslyKnown.Species == species && shiftedPhoto.Birds.Any(bird => bird.Number == 5),
                    "an unmatched refreshed detection plus a failed species stage preserves previous evidence and selection");
            }
            using (var cancellation = new CancellationTokenSource())
            using (var workflow = new BirdRecognitionWorkflow(
                detectorOverride: (_, _) => new(Box(.1), "已定位"),
                recognizerOverride: (_, _, _, ct) => { cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return new(species, "不可达"); }))
            {
                bool canceled = false;
                try { await workflow.RecognizeAsync(new[] { empty }, cancellationToken: cancellation.Token); }
                catch (OperationCanceledException) { canceled = true; }
                Check(canceled && empty.Birds.SequenceEqual(beforeFailure) && ReferenceEquals(empty.SelectedBird, manual) &&
                      empty.ViewZoom == 2.4 && empty.NextBirdNumber == highWaterMark,
                    "cancellation during recognition returns no partial edits to birds, view or stable numbering");
            }
            var absent = new PhotoItem { FilePath = Path.Combine(root, "absent.jpg"), DetectionStatus = "原定位状态" };
            absent.SetBirds(new[] { new DetectedBird { Number = 7, Box = Box(.1), Species = species, RecognitionStatus = "原识别状态" } });
            using (var workflow = new BirdRecognitionWorkflow(
                detectorOverride: (_, _) => throw new InvalidOperationException("unreadable photos must not reach the detector")))
            {
                var result = await workflow.RecognizeAsync(new[] { absent });
                var bird = absent.Birds.Single();
                result.Outcomes.Single().ApplyTo(absent);
                Check(result.Errors.Count == 1 && ReferenceEquals(absent.Birds.Single(), bird) &&
                      absent.DetectionStatus == "原定位状态" && bird.RecognitionStatus == "原识别状态" && bird.Species == species,
                    "an unreadable photo reports its path while retaining its original localization and species state");
            }
            string invalidModel = Path.Combine(root, "not-a-model.onnx");
            await File.WriteAllTextAsync(invalidModel, "invalid model must never be read by grouping");
            var beforeGrouping = empty.Birds.ToArray();
            string detectionStatus = empty.DetectionStatus;
            using (var grouping = new AnalysisService(invalidModel))
            {
                var result = await grouping.AnalyzeAsync(new[] { empty, second, absent }, .95);
                Check(!result.MissingModel && result.Errors.Count == 1 &&
                      result.Outcomes.All(outcome => outcome.PreservedExistingBirds) &&
                      result.Outcomes[0].DetectionStatus == detectionStatus && result.Outcomes[2].DetectionStatus == "原定位状态",
                    "grouping does not load inference models and preserves localization status even for unreadable photos");
                Check(empty.Birds.SequenceEqual(beforeGrouping) && ReferenceEquals(empty.SelectedBird, manual) &&
                      empty.Birds.All(bird => bird.Species == species) && empty.ViewZoom == 2.4 && empty.ViewCenterX == .7,
                    "regrouping leaves all automatic/manual species, selected region and saved view untouched");
            }

            string? nativeAssets = Environment.GetEnvironmentVariable("BIRD_SPECIES_ASSETS");
            string? nativeModel = Environment.GetEnvironmentVariable("BIRD_MODEL_PATH");
            string? nativeSample = Environment.GetEnvironmentVariable("BIRD_SAMPLE_PATH");
            if (!string.IsNullOrWhiteSpace(nativeAssets) && !string.IsNullOrWhiteSpace(nativeModel) &&
                !string.IsNullOrWhiteSpace(nativeSample))
            {
                var nativePhoto = new PhotoItem { FilePath = Path.GetFullPath(nativeSample), ViewZoom = 1.25 };
                var nativeUpdates = new List<SpeciesProgress>();
                using var nativeWorkflow = new BirdRecognitionWorkflow(Path.GetFullPath(nativeModel), Path.GetFullPath(nativeAssets));
                var nativeResult = await nativeWorkflow.RecognizeAsync(new[] { nativePhoto },
                    progress: new InlineProgress(nativeUpdates.Add));
                Check(!nativeResult.MissingModel && nativeResult.Errors.Count == 0 && nativeResult.MatchedBirds > 0 &&
                      nativeResult.Outcomes.Single().Birds.Any(bird => bird.Box.IsValid && bird.Species is { IsValid: true }),
                    "the ungrouped public sample completes real YOLO localization and real BioCLIP species recognition in one command");
                Check(nativePhoto.Birds.Count == 0 && nativePhoto.ViewZoom == 1.25 &&
                      nativeUpdates.Any(update => update.Message.Contains("正在定位鸟类") && update.Message.Contains(nativePhoto.FilePath)) &&
                      nativeUpdates.Any(update => update.Message.Contains("正在识别鸟种") && update.Message.Contains(nativePhoto.FileName)) &&
                      nativeUpdates.Last().Completed == 1 && nativeUpdates.Last().Total == 1,
                    "native inference keeps UI state detached and reports photo-specific localization and species progress");
                nativeResult.Outcomes.Single().ApplyTo(nativePhoto);
                Check(nativePhoto.Bird is { IsValid: true } && nativePhoto.SelectedBird?.Species is { IsValid: true } &&
                      nativePhoto.ViewZoom is null,
                    "applying the two-model native result installs valid evidence and resets only the first-location detail view");
                nativePhoto.ViewZoom = 2.2;
                nativePhoto.ViewCenterX = .42;
                nativePhoto.ViewCenterY = .58;
                var nativeBirds = nativePhoto.Birds.ToArray();
                var nativeSpecies = nativeBirds.Select(bird => bird.Species).ToArray();
                var nativeSelected = nativePhoto.SelectedBird;
                string nativeDetectionStatus = nativePhoto.DetectionStatus;
                int nativeNextNumber = nativePhoto.NextBirdNumber;
                using var nativeGrouping = new AnalysisService(invalidModel);
                var regrouped = await nativeGrouping.AnalyzeAsync(new[] { nativePhoto }, .95);
                Check(regrouped.Groups.Single().Single() == nativePhoto && regrouped.Errors.Count == 0 &&
                      regrouped.Outcomes.Single().PreservedExistingBirds && nativePhoto.Birds.SequenceEqual(nativeBirds) &&
                      nativePhoto.Birds.Select(bird => bird.Species).SequenceEqual(nativeSpecies) &&
                      ReferenceEquals(nativePhoto.SelectedBird, nativeSelected) && nativePhoto.NextBirdNumber == nativeNextNumber &&
                      nativePhoto.DetectionStatus == nativeDetectionStatus && nativePhoto.ViewZoom == 2.2 &&
                      nativePhoto.ViewCenterX == .42 && nativePhoto.ViewCenterY == .58,
                    "background regrouping after both real models preserves native species, region objects, selection, numbering and saved view");
            }
            return assertions;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(resolved, true);
        }
    }

    private sealed class InlineProgress(Action<SpeciesProgress> report) : IProgress<SpeciesProgress>
    { public void Report(SpeciesProgress value) => report(value); }
}
