using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

internal static class SpeciesEditingChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); count++; Console.WriteLine("  OK SPECIES EDIT " + message); }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SpeciesEditingChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "photo.jpg");
            var bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Rgb24, null, new byte[64 * 48 * 3], 64 * 3);
            bitmap.Freeze(); var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            var tit = new SpeciesIdentity(" 大山雀 ", " Parus major ", "taxon-tit");
            var sparrow = new SpeciesIdentity("麻雀", "Passer montanus", "taxon-sparrow");
            Check(tit.IsValid && tit.Key == new SpeciesIdentity("别名", "PARUS MAJOR").Key && tit.DisplayName == "大山雀",
                "species keys normalize scientific-name case and whitespace without using Chinese aliases as identity");
            var manual = new DetectedBird { ManualSpecies = sparrow };
            var auto = Auto(7); var last = Auto(11);
            var photo = new PhotoItem { FilePath = path, PixelWidth = 64, PixelHeight = 48 };
            photo.SetBirds(new[] { auto, manual, last }, selectedIndex: 1, nextBirdNumber: 40);
            Check(photo.Birds.Count == 3 && photo.Birds.Select(b => b.DisplayNumber).SequenceEqual(new[] { 1, 0, 2 }) &&
                manual.DisplayNumberText == "手动" && auto.DisplayNumberText == "01" && last.DisplayNumberText == "02",
                "mixed result rows keep collection positions while only real regions receive compact overlay numbers");
            Check(photo.Bird is null && !manual.HasRegion && manual.Box is { IsValid: false } && manual.EffectiveSpecies == sparrow &&
                manual.SimilarityText == "—" && manual.ConfidenceText == "—" && manual.EvidenceTooltip.Contains("人工"),
                "a manual species has no drawable/model box or invented AI score and the compatibility Bird facade is null");
            Check(auto.EffectiveSpecies?.Key == tit.Key && auto.Copy().Species == auto.Species && manual.Copy().ManualSpecies == sparrow,
                "automatic matches and detached clones retain their proper identity and manual origin");

            var history = new SpeciesEditHistory();
            var original = new PhotoItem { FilePath = path, PixelWidth = 64, PixelHeight = 48, ViewZoom = 2.5,
                ViewCenterX = .4, ViewCenterY = .6 };
            original.SetBirds(new[] { Auto(7) }, nextBirdNumber: 40);
            var geometry = original.GeometryReference;
            var noop = history.Capture(original, "无修改"); original.Rating = PhotoRating.Favorite;
            Check(!history.Commit(noop) && !history.CanUndo && !original.HasManualSpeciesEdits,
                "rating-only changes do not manufacture species edits or an undo checkpoint");
            var replacement = history.Replace(original, original.Birds[0], sparrow);
            Check(history.CanUndo && original.HasManualSpeciesEdits && original.SelectedBird == replacement &&
                replacement.ManualSpecies == sparrow && !replacement.HasRegion && replacement.Species is null && replacement.Number >= 40,
                "replace is one undoable edit at the same row with a new score-free, box-free manual identity");
            int highWater = original.NextBirdNumber;
            var raw = new PhotoAsset(Path.ChangeExtension(path, ".NEF"), PhotoAssetKind.Raw);
            original.AddAsset(raw);
            Check(history.Undo() == original && original.Birds.Single().Number == 7 && original.Birds[0].Species?.ScientificName == "Parus major" &&
                original.Rating == PhotoRating.Favorite && original.Assets.Count == 2 && original.NextBirdNumber >= highWater &&
                !original.HasManualSpeciesEdits && original.GeometryReference == geometry && original.ViewZoom == 2.5,
                "undo restores evidence, geometry and view but keeps current rating, file members and allocation high-water mark");
            var added = history.Add(original, sparrow);
            Check(original.Birds.Count == 2 && original.Birds[1] == added && original.SelectedBird == added &&
                added.ManualSpecies == sparrow && history.Description.Contains("新增"),
                "add appends and selects a manual result in one descriptive undo step");
            Check(history.Delete(original, original.Birds[0]) && original.Birds.Count == 1 && original.Birds[0].ManualSpecies == sparrow,
                "deleting an automatic row preserves other manual results");
            history.Undo();
            Check(original.Birds.Count == 2 && original.Birds[0].HasRegion && original.Birds[1].ManualSpecies == sparrow,
                "undo of deletion restores original mixed-row order");
            history.Clear();
            Check(!history.CanUndo && history.Description == "", "clearing session history releases all undo entries");
            var checkpoint = history.Capture(original, "来源变化前编辑");
            history.Add(original, tit); history.Commit(checkpoint);
            original.SetActivePreviewAsset(raw.Id);
            history.Undo();
            Check(original.ActivePreviewAssetId == raw.Id && original.GeometryStatus == PhotoGeometryStatus.NeedsReview &&
                original.ViewZoom is null && original.Assets.Count == 2,
                "undo never restores an old file selection or promotes old boxes after a source change");

            var capped = new SpeciesEditHistory(); var many = new PhotoItem { FilePath = path };
            for (int i = 0; i < 55; i++) capped.Add(many, new("人工鸟种" + i, "Birdus species" + i));
            int maximumNext = many.NextBirdNumber;
            int undone = 0; while (capped.Undo() is not null) undone++;
            Check(undone == 50 && many.Birds.Count == 5 && many.NextBirdNumber == maximumNext,
                "the history cap is fifty lightweight edits and undo never reuses prior internal identities");

            string names = Directory.CreateDirectory(Path.Combine(root, "names")).FullName;
            await File.WriteAllTextAsync(Path.Combine(names, "candidates.json"), JsonSerializer.Serialize(new[] { tit, sparrow }));
            await File.WriteAllTextAsync(Path.Combine(names, "china-unmatched.json"), JsonSerializer.Serialize(new[]
            { new SpeciesIdentity("同学名别名", "PARUS MAJOR"), new SpeciesIdentity("白眉苦恶鸟", "Amaurornis cinerea") }));
            var catalog = SpeciesNameCatalog.Load(names);
            Check(catalog.Records.Count == 3 && catalog.Search("大山雀").Single().Key == tit.Key &&
                catalog.Search("passer").Single().ScientificName == "Passer montanus" && catalog.Search("").Count == 3,
                "name search combines matched/unmatched records, deduplicates scientific names and searches Chinese or Latin without model assets");
            bool missing = false;
            try { SpeciesNameCatalog.Load(Path.Combine(root, "absent")); } catch (IOException ex) { missing = ex.Message.Contains("恢复"); }
            Check(missing, "temporarily missing names produce an actionable recoverable error rather than requiring model loading");
            var recent = new RecentSpeciesHistory();
            for (int i = 0; i < 12; i++) recent.Use(new("鸟种" + i, "Species " + i));
            recent.Use(new("不同中文", " SPECIES 5 "));
            Check(recent.Items.Count == 10 && recent.Items[0].Key == "SPECIES 5" && recent.Items.Count(x => x.Key == "SPECIES 5") == 1,
                "recent choices have a ten-item cap and scientific-name duplicates move to the front");
            recent.Replace(new[] { tit, new SpeciesIdentity("别名", "PARUS MAJOR"), sparrow });
            Check(recent.Items.Select(x => x.Key).SequenceEqual(new[] { tit.Key, sparrow.Key }),
                "project recent-choice restoration preserves order while removing duplicate taxa");
            recent.Clear(); Check(recent.Items.Count == 0, "recent choices can be cleared independently of photo results");

            var protectedPhoto = new PhotoItem { FilePath = Path.Combine(root, "not-readable.jpg") };
            history.Add(protectedPhoto, sparrow);
            int detects = 0, recognizes = 0;
            using (var workflow = new BirdRecognitionWorkflow(detectorOverride: (_, _) =>
                { detects++; return new(null, "不应调用"); }, recognizerOverride: (_, _, _, _) =>
                { recognizes++; return new(null, "不应调用"); }))
            {
                var result = await workflow.RecognizeAsync(new[] { protectedPhoto });
                Check(result.ManuallyEditedSkippedPhotos == 1 && result.SkippedPhotos == 1 && result.Outcomes.Count == 0 &&
                    result.Errors.Count == 0 && !result.MissingModel && detects == 0 && recognizes == 0,
                    "batch localization skips the whole manually edited photo before IO or model work without an error or missing-model warning");
            }
            using (var service = new BirdSpeciesRecognitionService(Path.Combine(root, "no-model")))
            {
                var result = await service.RecognizeAsync(new[] { protectedPhoto });
                Check(result.ManuallyEditedSkippedPhotos == 1 && result.Errors.Count == 0 && !result.MissingModel && result.Outcomes.Count == 0,
                    "stored-box batch species recognition also skips manual edits and never loads a model for label-only rows");
            }
            var rebuildPhoto = new PhotoItem { FilePath = path };
            history.Add(rebuildPhoto, sparrow);
            using (var rebuild = new SinglePhotoRecognitionService(detectorOverride: (_, _) => new(Box(), "定位完成"),
                recognizerOverride: (_, _, _, _) => new(Match(), "识别完成"), loaderOverride: (_, _) => bitmap))
            {
                var beforeRebuild = history.Capture(rebuildPhoto, "重新识别本张");
                var result = await rebuild.RebuildAsync(rebuildPhoto);
                Check(result.Success && rebuildPhoto.HasManualSpeciesEdits && rebuildPhoto.Birds[0].ManualSpecies == sparrow,
                    "an explicit rebuild proposal may replace manual edits but does not mutate them before success is applied");
                result.ApplyTo(rebuildPhoto); history.Commit(beforeRebuild);
                Check(!rebuildPhoto.HasManualSpeciesEdits && rebuildPhoto.Birds.Single().HasRegion && rebuildPhoto.Birds[0].ManualSpecies is null,
                    "successfully applied explicit rebuild replaces manual results and clears batch protection");
                history.Undo();
                Check(rebuildPhoto.HasManualSpeciesEdits && rebuildPhoto.Birds.Single().ManualSpecies == sparrow && !rebuildPhoto.Birds[0].HasRegion,
                    "undoing an explicit rebuild restores the manual species and its batch protection");
            }
            using (var failed = new SinglePhotoRecognitionService(detectorOverride: (_, _) => new(null, "失败", Error: "fixture failure"),
                loaderOverride: (_, _) => bitmap))
            {
                var result = await failed.RebuildAsync(rebuildPhoto);
                Check(!result.Success && rebuildPhoto.HasManualSpeciesEdits && rebuildPhoto.Birds[0].ManualSpecies == sparrow,
                    "a failed explicit rebuild retains both manual results and protection");
            }
            using (var grouping = new AnalysisService())
            {
                var grouped = await grouping.AnalyzeAsync(new[] { rebuildPhoto }, .8);
                Check(grouped.Outcomes.Single().Birds.Count == 0 && rebuildPhoto.Birds.Single().ManualSpecies == sparrow,
                    "background grouping does not turn label-only manual entries into masks or erase the labels");
            }
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe species editing fixture cleanup.");
            Directory.Delete(resolved, true);
        }
        return count;
    }
    private static SpeciesMatch Match() => new("大山雀", "Parus major", .7, Confidence: .98);
    private static BirdBox Box() => new() { X = .1, Y = .2, Width = .4, Height = .5, Confidence = .9 };
    private static DetectedBird Auto(int number) => new() { Number = number, Box = Box(), Species = Match() };
}
