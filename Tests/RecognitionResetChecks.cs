using System.IO;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

internal static class RecognitionResetChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); count++; Console.WriteLine("  OK RECOGNITION RESET " + message); }
        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.RecognitionResetChecks");
        string root = Directory.CreateDirectory(Path.Combine(allowed, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string path = Path.Combine(root, "generated.jpg");
            var image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Rgb24, null, new byte[64 * 48 * 3], 64 * 3); image.Freeze();
            var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path)) encoder.Save(stream);
            byte[] bytes = await File.ReadAllBytesAsync(path);
            BirdBox Box(double x = .1) => new() { X = x, Y = .2, Width = .2, Height = .4, Confidence = .9 };
            DetectedBird Region(int number, double x = .1) => new() { Number = number, Box = Box(x), Species = new("大山雀", "Parus major", .7, Confidence: .95) };
            DetectedBird Label(int number, string name, string latin) => new() { Number = number, ManualSpecies = new(name, latin) };
            var regionFirst = Region(20); var regionSecond = Region(10, .6);
            var white = Label(101, "白鹭", "Egretta garzetta");
            var great = Label(102, "大山雀", "Parus major");
            var green = Label(103, "绿背山雀", "Parus monticolus");
            var sparrow = Label(104, "麻雀", "Passer montanus");
            var mandarin = Label(105, "鸳鸯", "Aix galericulata");
            var photo = new PhotoItem { FilePath = path, Rating = PhotoRating.Favorite, PixelWidth = 64, PixelHeight = 48,
                ViewZoom = 2.4, ViewCenterX = .6, ViewCenterY = .4 };
            photo.SetBirds(new[] { mandarin, regionFirst, green, sparrow, white, great, regionSecond }, 2, 300);
            Check(photo.Birds.SequenceEqual(new[] { regionFirst, regionSecond, white, great, green, sparrow, mandarin }) &&
                  ReferenceEquals(photo.SelectedBird, green) && photo.SelectedBirdIndex == 4 &&
                  photo.Birds.Select(bird => bird.Number).SequenceEqual(new[] { 20, 10, 101, 102, 103, 104, 105 }),
                "set and load-style updates put unchanged regions first and manual labels in zh-CN name order while preserving selected identity");
            Check(regionFirst.DisplayNumber == 1 && regionSecond.DisplayNumber == 2 && photo.Birds.Skip(2).All(bird => bird.DisplayNumber == 0) &&
                  photo.ViewZoom == 2.4 && photo.NextBirdNumber == 300,
                "sorting does not allocate new stable IDs, reset the view or give label-only rows region numbers");
            int collectionEvents = 0;
            photo.Birds.CollectionChanged += (_, _) => collectionEvents++; // Simulate an additional WPF subscriber.
            var inserted = Label(250, "红嘴蓝鹊", "Urocissa erythroryncha");
            photo.Birds.Insert(0, inserted);
            Check(collectionEvents > 0 && ReferenceEquals(photo.SelectedBird, green) && photo.Birds[0] == regionFirst &&
                  photo.Birds.Skip(2).Select(bird => bird.SpeciesName).SequenceEqual(new[] { "白鹭", "大山雀", "红嘴蓝鹊", "绿背山雀", "麻雀", "鸳鸯" }),
                "direct collection insertion sorts outside notification reentrancy and retains the selected object with multiple subscribers");
            photo.Birds.Move(photo.Birds.IndexOf(mandarin), 0);
            Check(photo.Birds[0] == regionFirst && photo.Birds[^1] == mandarin && ReferenceEquals(photo.SelectedBird, green),
                "external row moves cannot put a manual label ahead of regions or switch the current subject");
            green.ManualSpecies = new("阿尔法鸟", "Testus alpha");
            Check(photo.Birds[2] == green && photo.SelectedBirdIndex == 2 && green.Number == 103 &&
                  regionFirst.DisplayNumber == 1 && regionSecond.DisplayNumber == 2 && photo.HasManualSpeciesEdits,
                "changing a manual name re-sorts its same object and preserves stable IDs and selection");
            var edits = new SpeciesEditHistory();
            int beforeReplace = photo.NextBirdNumber;
            var replacement = edits.Replace(photo, regionFirst, new("白鹭", "Egretta garzetta"));
            Check(photo.Birds[0] == regionSecond && ReferenceEquals(photo.SelectedBird, replacement) && !replacement.HasRegion &&
                  replacement.Number >= beforeReplace && photo.Birds.Count(bird => bird.HasRegion) == 1,
                "replacing a region moves only its new manual label behind surviving numbered regions");
            Check(edits.Undo() == photo && photo.Birds.Take(2).Select(bird => bird.Number).SequenceEqual(new[] { 20, 10 }) &&
                  photo.SelectedBird?.Number == 103 && photo.SelectedBird.ManualSpecies?.ChineseName == "阿尔法鸟",
                "undo re-applies canonical ordering and restores the selected stable identity");

            var group = new PhotoGroup(); group.Photos.Add(photo);
            var recent = new RecentSpeciesHistory(); recent.Use(new("白鹭", "Egretta garzetta"));
            var assets = photo.Assets.ToArray(); int next = photo.NextBirdNumber;
            photo.ViewZoom = 2.7; photo.ViewCenterX = .51; photo.ViewCenterY = .42; photo.DetectionStatus = "已识别";
            photo.HasManualSpeciesEdits = true;
            var selectedNumber = photo.SelectedBird!.Number;
            Check(edits.Clear(photo) && edits.CanUndo && edits.Description.Contains("清空") &&
                  photo.Birds.Count == 0 && !photo.HasManualSpeciesEdits && photo.SelectedBird is null &&
                  photo.DetectionStatus == "待识别" && photo.GeometryStatus == PhotoGeometryStatus.Unspecified &&
                  photo.ViewZoom is null && photo.ViewCenterX is null && photo.ViewCenterY is null,
                "clear is one undoable reset of results, recognition status, manual protection, geometry and viewport");
            Check(photo.NextBirdNumber == next && photo.Rating == PhotoRating.Favorite && group.Photos.Single() == photo &&
                  photo.Assets.SequenceEqual(assets) && recent.Items.Count == 1,
                "clear keeps file members, group membership, rating, recent choices and identity high-water mark");
            Check(!edits.Clear(photo), "clearing an already reset photo does not add an artificial undo step");
            Check(edits.Undo() == photo && photo.Birds.Count == 8 && photo.SelectedBird?.Number == selectedNumber &&
                  photo.HasManualSpeciesEdits && photo.ViewZoom == 2.7 && photo.ViewCenterX == .51 && photo.ViewCenterY == .42,
                "undo of clear restores sorted results, their selection, protection and view without modifying originals");
            foreach (var bird in photo.Birds.ToArray()) edits.Delete(photo, bird);
            Check(photo.Birds.Count == 0 && !photo.HasManualSpeciesEdits && photo.GeometryStatus == PhotoGeometryStatus.Unspecified &&
                  photo.DetectionStatus == "待识别" && photo.ViewZoom is null && photo.NextBirdNumber == next,
                "deleting the final individual result releases the same protection and stale geometry as explicit clear");
            Check(edits.Undo() == photo && photo.Birds.Count == 1 && photo.HasManualSpeciesEdits,
                "undo of the last deletion restores one protected result rather than leaving the photo accidentally re-runnable");

            var fresh = new PhotoItem { FilePath = path };
            var legacy = new PhotoItem { FilePath = path };
            var legacyGroup = new PhotoGroup(); legacyGroup.Photos.Add(legacy);
            var projects = new ProjectService(); string projectPath = Path.Combine(root, "legacy-empty.json");
            await projects.SaveAsync(projectPath, new[] { legacyGroup }, .5);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(projectPath))!;
            var record = document["Groups"]![0]!["Photos"]![0]!;
            record["HasManualSpeciesEdits"] = true; record["GeometryStatus"] = "NeedsReview";
            record["DetectionStatus"] = "旧人工状态"; record["NextBirdNumber"] = 70;
            await File.WriteAllTextAsync(projectPath, document.ToJsonString());
            legacy = (await projects.LoadAsync(projectPath)).Groups.Single().Photos.Single();
            Check(legacy.Birds.Count == 0 && !legacy.HasManualSpeciesEdits && legacy.GeometryStatus == PhotoGeometryStatus.NeedsReview,
                "an old empty project cannot retain manual protection even when its historical geometry still needs review");
            var blocked = new PhotoItem { FilePath = path };
            blocked.SetBirds(new[] { Region(3) }); blocked.HasManualSpeciesEdits = true;
            var reviewed = new PhotoItem { FilePath = path };
            reviewed.SetBirds(new[] { Region(4) }); reviewed.SetGeometryReference(reviewed.GeometryReference, PhotoGeometryStatus.NeedsReview);
            var manualOnly = new PhotoItem { FilePath = path };
            manualOnly.SetBirds(new[] { Label(5, "白鹭", "Egretta garzetta") }); manualOnly.HasManualSpeciesEdits = false;
            int detections = 0, recognitions = 0;
            using (var workflow = new BirdRecognitionWorkflow(detectorOverride: (_, _) =>
                { detections++; return new(Box(), "定位成功"); }, recognizerOverride: (_, _, _, _) =>
                { recognitions++; return new(new("大山雀", "Parus major", .7, Confidence: .95), "识别成功"); }))
            {
                var result = await workflow.RecognizeAsync(new[] { fresh, legacy, blocked, reviewed, manualOnly });
                Check(detections == 2 && recognitions == 2 && result.ManuallyEditedSkippedPhotos == 2 &&
                      result.SkippedPhotos == 3 && result.Errors.Count == 1 && result.Outcomes.Count == 2,
                    "empty photos rerun while nonempty manual edits, manual-only labels and reviewed real geometry remain protected");
                foreach (var outcome in result.Outcomes)
                    outcome.ApplyTo(outcome.PhotoId == fresh.Id ? fresh : legacy);
                Check(legacy.Birds.Single().HasRegion && legacy.Birds.Single().Number >= 70 && legacy.CanUseBirdGeometry &&
                      fresh.Birds.Single().HasRegion && !legacy.HasManualSpeciesEdits && !fresh.HasManualSpeciesEdits,
                    "new localization on an empty legacy review state binds current-source geometry and retains the number history");
                bool protectedApply = false;
                try { new PhotoRecognitionOutcome { PhotoId = blocked.Id }.ApplyTo(blocked); }
                catch (InvalidOperationException) { protectedApply = true; }
                Check(protectedApply && blocked.Birds.Count == 1 && manualOnly.Birds.Single().ManualSpecies is not null,
                    "applying a detached result still refuses to overwrite a nonempty manually protected photo");
            }
            edits.Clear(legacy);
            using (var missing = new BirdRecognitionWorkflow(detectorOverride: (_, _) => new(null, "定位模型缺失", true)))
            {
                var result = await missing.RecognizeAsync(new[] { legacy });
                result.Outcomes.Single().ApplyTo(legacy);
                Check(result.MissingDetectionModel && legacy.Birds.Count == 0 && !legacy.HasManualSpeciesEdits && legacy.NextBirdNumber >= 71,
                    "a missing model preserves an already cleared list and its ability to retry later");
            }
            Check((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes),
                "sorting, clearing, undo, project migration and recognition never change original JPEG bytes");
            return count;
        }
        finally
        {
            if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe recognition reset fixture cleanup.");
            Directory.Delete(root, true);
        }
    }
}
