using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

internal static class MultiBirdChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            count++;
            Console.WriteLine("  OK MULTIBIRD " + message);
        }
        string root = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.MultiBirdChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string photoPath = Path.Combine(root, "two-birds.jpg");
            var pixels = Enumerable.Repeat((byte)120, 64 * 48 * 3).ToArray();
            var image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Rgb24, null, pixels, 64 * 3);
            image.Freeze();
            var jpeg = new JpegBitmapEncoder(); jpeg.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(photoPath)) jpeg.Save(stream);
            var photo = new PhotoItem { FilePath = photoPath, Rating = PhotoRating.Favorite };
            var large = new BirdBox { X = .05, Y = .1, Width = .45, Height = .7, Confidence = .4 };
            var small = new BirdBox { X = .7, Y = .2, Width = .2, Height = .3, Confidence = .95 };
            photo.SetBirds(new[]
            {
                new DetectedBird { Box = large, Species = new("大山雀", "Parus major", .412), RecognitionStatus = "已识别 · 中国候选" },
                new DetectedBird { Box = small, Species = new("麻雀", "Passer montanus", .387), RecognitionStatus = "已识别 · 中国候选" }
            });
            Check(photo.SelectedBirdIndex == 0 && ReferenceEquals(photo.Bird, large) && photo.Birds.Select(b => b.Number).SequenceEqual(new[] { 1, 2 }),
                "default selection uses the largest-first detection order and every bird receives a stable number");
            Check(photo.Birds[0].SpeciesName == "大山雀" && photo.Birds[0].ScientificName == "Parus major" &&
                  photo.Birds[0].SimilarityText.Contains("0.412") && !photo.Birds[0].SimilarityText.Contains('%'),
                "result rows expose Chinese/scientific names and raw cosine without probability formatting");
            var vm = new MainViewModel(); var group = new PhotoGroup(); group.Photos.Add(photo);
            vm.SetGroups(new[] { group }); vm.Dirty = false;
            photo.SelectedBirdIndex = 1;
            Check(ReferenceEquals(photo.Bird, small) && photo.SelectedBird?.Number == 2 && vm.Dirty,
                "selecting another numbered bird updates the existing detail facade and marks the project dirty");
            photo.ViewZoom = 2.3; photo.ViewCenterX = .74; photo.ViewCenterY = .4;
            string projectPath = Path.Combine(root, "multi.json");
            var projects = new ProjectService();
            await projects.SaveAsync(projectPath, new[] { group }, .83);
            var loaded = (await projects.LoadAsync(projectPath)).Groups.Single().Photos.Single();
            Check(loaded.Birds.Count == 2 && loaded.Birds[0].Box.Width == .45 && loaded.Birds[1].Box.X == .7 &&
                  loaded.SelectedBirdIndex == 1 && loaded.Bird?.Width == .2,
                "project JSON round-trips every bird box and the selected bird instead of retaining only one location");
            Check(loaded.Birds[0].Species is { ChineseName: "大山雀", ScientificName: "Parus major", Similarity: .412, CandidateScope: "China" } &&
                  loaded.Birds[1].Species?.ChineseName == "麻雀" && loaded.Rating == PhotoRating.Favorite && loaded.ViewZoom == 2.3,
                "species scores, names, candidate scope, original rating and manual view survive project reload");
            var autosave = new AutoSaveService(Path.Combine(root, "Recovery"));
            var recovery = await autosave.SaveAsync(new[] { group }, .83, null, 3);
            var recovered = (await projects.LoadAsync(recovery.Path)).Groups.Single().Photos.Single();
            Check(recovered.Birds.Count == 2 && recovered.SelectedBirdIndex == 1 &&
                  recovered.Birds[0].Species?.Similarity == .412 && recovered.Birds[1].Species?.ChineseName == "麻雀",
                "automatic recovery files preserve all bird locations, selected subject and species results");

            var json = JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!;
            var record = json["Groups"]![0]!["Photos"]![0]!;
            record.AsObject().Remove("Birds"); record.AsObject().Remove("SelectedBirdIndex");
            await File.WriteAllTextAsync(projectPath, json.ToJsonString());
            loaded = (await projects.LoadAsync(projectPath)).Groups.Single().Photos.Single();
            Check(loaded.Birds.Count == 1 && loaded.SelectedBirdIndex == 0 && loaded.Bird?.X == .7 && loaded.Birds[0].Species is null,
                "an existing single-bird JSON remains usable as one numbered bird without inventing species results");

            await projects.SaveAsync(projectPath, new[] { group }, .83);
            json = JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!;
            record = json["Groups"]![0]!["Photos"]![0]!;
            record["Birds"]![0]!["Species"]!["Similarity"] = 2;
            record["Birds"]![1]!["Box"]!["Width"] = -1;
            await File.WriteAllTextAsync(projectPath, json.ToJsonString());
            loaded = (await projects.LoadAsync(projectPath)).Groups.Single().Photos.Single();
            Check(loaded.Birds.Count == 1 && loaded.Birds[0].Species is null && loaded.SelectedBirdIndex == 0,
                "invalid stored boxes and non-cosine species scores are rejected without losing the valid photo");

            using (var analysis = new AnalysisService(Path.Combine(root, "missing-detector.onnx")))
            {
                var result = await analysis.AnalyzeAsync(new[] { photo }, .8);
                Check(result.Outcomes.Single().Birds.Count == 2 && result.Outcomes.Single().PreservedExistingBirds &&
                      photo.Birds[0].Species?.ChineseName == "大山雀" && photo.SelectedBirdIndex == 1,
                    "missing detector preserves all prior locations and species without mutating UI models on its worker");
            }
            double score = BackgroundSimilarity.Compare(BackgroundSimilarity.DescribeAll(image, new[] { large, small }),
                BackgroundSimilarity.DescribeAll(image, new[] { large, small }));
            Check(score > .9999, "background grouping can exclude all bird boxes while keeping identical backgrounds comparable");
            await CheckManualRegionLifecycle(photoPath, root, projects, Check);

            string? modelPath = Environment.GetEnvironmentVariable("BIRD_MODEL_PATH");
            string? samplePath = Environment.GetEnvironmentVariable("BIRD_SAMPLE_PATH");
            if (!string.IsNullOrWhiteSpace(modelPath) && !string.IsNullOrWhiteSpace(samplePath))
            {
                var source = ImageLoader.Load(samplePath);
                var rgb = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
                int stride = source.PixelWidth * 2 * 4;
                var pairedPixels = new byte[stride * source.PixelHeight];
                rgb.CopyPixels(new(0, 0, source.PixelWidth, source.PixelHeight), pairedPixels, stride, 0);
                rgb.CopyPixels(new(0, 0, source.PixelWidth, source.PixelHeight), pairedPixels, stride, source.PixelWidth * 4);
                var paired = BitmapSource.Create(source.PixelWidth * 2, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pairedPixels, stride);
                paired.Freeze();
                using var detector = new BirdDetectionService(modelPath);
                var detected = detector.Detect(paired);
                Check(detected.Error is null && detected.Birds.Count >= 2 && detected.Birds.All(box => box.IsValid),
                    "the real ONNX detector returns multiple valid locations from a two-bird photo fixture");
                Check(detected.Birds.Any(box => box.X + box.Width / 2 < .5) && detected.Birds.Any(box => box.X + box.Width / 2 > .5),
                    "the real multi-bird detector retains birds on both sides instead of only its previous best candidate");
                Check(ReferenceEquals(detected.Bird, detected.Birds[0]) && detected.Birds.Zip(detected.Birds.Skip(1))
                    .All(pair => pair.First.Width * pair.First.Height >= pair.Second.Width * pair.Second.Height),
                    "the primary real detection is the largest bird even when confidence order differs");
            }
            return count;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.MultiBirdChecks")) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
        }
    }

    private static async Task CheckManualRegionLifecycle(string photoPath, string root, ProjectService projects,
        Action<bool, string> check)
    {
        BirdBox Box(double x) => new() { X = x, Y = .2, Width = .15, Height = .3, Confidence = .8 };
        var photo = new PhotoItem { FilePath = photoPath, Rating = PhotoRating.Favorite };
        photo.SetBirds(new[] { new DetectedBird { Box = Box(.1) }, new DetectedBird { Box = Box(.3) } });
        var manual = photo.AddManualRegion(Box(.5));
        manual.Species = new SpeciesMatch("大山雀", "Parus major", .63, Confidence: .84,
            AlternativeName: "麻雀", RunnerUpSimilarity: .4, ConfidenceMethod: "candidate-discrimination-v1");
        manual.RecognitionStatus = "手动选区已识别";
        var highest = photo.AddManualRegion(Box(.7));
        check(manual.Number == 3 && highest.Number == 4 && manual.IsManual &&
              ReferenceEquals(photo.SelectedBird, highest),
            "manual regions append after automatic detections and receive successive stable numbers");
        check(photo.RemoveBird(highest) && !photo.RemoveBird(highest),
            "removing a region succeeds once and safely rejects an already removed object");
        var appended = photo.AddManualRegion(Box(.7));
        appended.Species = new SpeciesMatch("麻雀", "Passer montanus", .51, Confidence: .7);
        check(appended.Number == 5 && photo.NextBirdNumber == 6 && photo.Birds.Select(b => b.Number).SequenceEqual(new[] { 1, 2, 3, 5 }),
            "deleting the highest number leaves a gap and the next manual region never reuses it");
        photo.SelectedBirdIndex = photo.Birds.IndexOf(manual);
        photo.ViewZoom = 2.6; photo.ViewCenterX = .57; photo.ViewCenterY = .36;
        photo.ReplaceAutomaticRegions(new[] { Box(.12), Box(.32) });
        check(photo.Birds.Count == 4 && photo.Birds.Take(2).All(b => !b.IsManual && b.Species is null) &&
              photo.Birds.Take(2).Select(b => b.Number).SequenceEqual(new[] { 6, 7 }) &&
              ReferenceEquals(photo.Birds[2], manual) && ReferenceEquals(photo.Birds[3], appended) &&
              ReferenceEquals(photo.SelectedBird, manual) && manual.Species?.Confidence == .84 &&
              manual.RecognitionStatus == "手动选区已识别" && photo.Rating == PhotoRating.Favorite,
            "reanalysis replaces automatic regions while keeping manual references, results, numbers, selection and rating");
        photo.ReplaceAutomaticRegions(Array.Empty<BirdBox>());
        check(photo.Birds.SequenceEqual(new[] { manual, appended }) && ReferenceEquals(photo.SelectedBird, manual) &&
              photo.NextBirdNumber == 8 && photo.ViewZoom == 2.6 && photo.ViewCenterX == .57 && photo.ViewCenterY == .36,
            "zero automatic detections retain every manual region, selected subject, saved view and number high-water mark");
        var group = new PhotoGroup(); group.Photos.Add(photo);
        string project = Path.Combine(root, "manual-regions.json");
        await projects.SaveAsync(project, new[] { group }, .84);
        var loaded = (await projects.LoadAsync(project)).Groups.Single().Photos.Single();
        check(loaded.Birds.Count == 2 && loaded.Birds.All(b => b.IsManual) &&
              loaded.Birds.Select(b => b.Number).SequenceEqual(new[] { 3, 5 }) && loaded.NextBirdNumber == 8 &&
              loaded.SelectedBird?.Number == 3 && loaded.Birds[0].Species is
              { Confidence: .84, AlternativeName: "麻雀", RunnerUpSimilarity: .4, ConfidenceMethod: "candidate-discrimination-v1" } &&
              loaded.Birds[0].RecognitionStatus == "手动选区已识别" && loaded.ViewZoom == 2.6,
            "JSON preserves manual origins, stable numbers, confidence evidence, selected number and view");
        var autosave = new AutoSaveService(Path.Combine(root, "ManualRecovery"));
        var manualRecovery = await autosave.SaveAsync(new[] { group }, .84, null, revision: 16);
        var restoredManual = (await projects.LoadAsync(manualRecovery.Path)).Groups.Single().Photos.Single();
        check(restoredManual.Birds.All(b => b.IsManual) && restoredManual.SelectedBird?.Number == 3 &&
              restoredManual.Birds.Select(b => b.Number).SequenceEqual(new[] { 3, 5 }) &&
              restoredManual.Birds[0].Species?.Confidence == .84 && restoredManual.NextBirdNumber == 8,
            "autosave recovery preserves manual origins, confidence, selected stable number and the next unused number");

        var document = JsonNode.Parse(await File.ReadAllTextAsync(project))!;
        var record = document["Groups"]![0]!["Photos"]![0]!;
        record["Birds"]![0]!["Species"]!["Confidence"] = 1.5;
        record["Birds"]![0]!["Species"]!["RunnerUpSimilarity"] = 9;
        record["Birds"]![1]!["Species"]!["Confidence"] = -.2;
        await File.WriteAllTextAsync(project, document.ToJsonString());
        var sanitized = (await projects.LoadAsync(project)).Groups.Single().Photos.Single();
        check(sanitized.Birds.All(b => b.Species is { IsValid: true, Confidence: null } && b.ConfidenceText == "—") &&
              sanitized.Birds[0].Species?.RunnerUpSimilarity is null && sanitized.Birds[0].Species?.Similarity == .63,
            "invalid optional confidence evidence is sanitized without discarding a valid species name or raw cosine");

        await projects.SaveAsync(project, new[] { group }, .84);
        document = JsonNode.Parse(await File.ReadAllTextAsync(project))!;
        record = document["Groups"]![0]!["Photos"]![0]!;
        record.AsObject().Remove("NextBirdNumber"); record.AsObject().Remove("SelectedBirdNumber");
        record["SelectedBirdIndex"] = 1;
        foreach (var entry in record["Birds"]!.AsArray())
        {
            entry!.AsObject().Remove("Number"); entry.AsObject().Remove("IsManual");
            foreach (string field in new[] { "Confidence", "AlternativeName", "RunnerUpSimilarity", "ConfidenceMethod" })
                entry["Species"]!.AsObject().Remove(field);
        }
        await File.WriteAllTextAsync(project, document.ToJsonString());
        var legacy = (await projects.LoadAsync(project)).Groups.Single().Photos.Single();
        check(legacy.Birds.Count == 2 && legacy.Birds.All(b => !b.IsManual && b.ConfidenceText == "—") &&
              legacy.Birds.Select(b => b.Number).SequenceEqual(new[] { 1, 2 }) && legacy.NextBirdNumber == 3 &&
              legacy.SelectedBirdIndex == 1 && legacy.Birds[0].Species?.Similarity == .63,
            "1.4 JSON without new number, origin or confidence fields migrates in order and retains old species evidence");

        foreach (var bird in photo.Birds.ToArray()) photo.RemoveBird(bird);
        check(photo.SelectedBird is null && photo.Bird is null && photo.NextBirdNumber == 8 && photo.Rating == PhotoRating.Favorite,
            "deleting the last region clears subject selection while preserving the photo rating and number counter");
        await projects.SaveAsync(project, new[] { group }, .84);
        var emptyReload = (await projects.LoadAsync(project)).Groups.Single().Photos.Single();
        check(emptyReload.Birds.Count == 0 && emptyReload.NextBirdNumber == 8 && emptyReload.AddManualRegion(Box(.2)).Number == 8,
            "empty-region JSON does not resurrect a box or reset the next stable region number");
        var recovery = await autosave.SaveAsync(new[] { group }, .84, null, revision: 17);
        var recovered = (await projects.LoadAsync(recovery.Path)).Groups.Single().Photos.Single();
        check(recovered.Birds.Count == 0 && recovered.NextBirdNumber == 8 && recovered.AddManualRegion(Box(.4)).Number == 8 &&
              recovered.Rating == PhotoRating.Favorite,
            "autosave and recovery preserve a deleted-all region counter and the original photo rating");
        var exhausted = new PhotoItem();
        exhausted.SetBirds(new[] { new DetectedBird { Number = int.MaxValue - 1, Box = Box(.1) } });
        bool limitRejected = false;
        try { exhausted.AddManualRegion(Box(.3)); }
        catch (InvalidOperationException) { limitRejected = true; }
        check(limitRejected && exhausted.Birds.Count == 1 && exhausted.NextBirdNumber == int.MaxValue,
            "exhausted stable numbering fails explicitly instead of overflowing or wrapping to a deleted number");
    }
}
