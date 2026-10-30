using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

internal static class SpeciesProjectChecks
{
    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            assertions++; Console.WriteLine("  OK SPECIES PROJECT " + message);
        }
        string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.SpeciesProjectChecks"));
        string root = Directory.CreateDirectory(Path.Combine(allowed, Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string firstPath = Path.Combine(root, "first.jpg"), secondPath = Path.Combine(root, "manual.jpg");
            WriteJpeg(firstPath); WriteJpeg(secondPath);
            byte[] original = SHA256.HashData(await File.ReadAllBytesAsync(firstPath));
            var identity = new SpeciesIdentity("人工麻雀", "Passer montanus", "saved-taxon-id");
            var photo = new PhotoItem { FilePath = firstPath, Rating = PhotoRating.Favorite, PixelWidth = 64, PixelHeight = 48 };
            photo.SetBirds(new[]
            {
                new DetectedBird { Number = 2, Box = Box(.1), Species = new("大山雀", "Parus major", .7, Confidence: .95) },
                new DetectedBird { Number = 7, ManualSpecies = identity, RecognitionStatus = "人工指定" },
                new DetectedBird { Number = 12, Box = Box(.6), RecognitionStatus = "未能识别" }
            }, selectedIndex: 1, nextBirdNumber: 40);
            photo.HasManualSpeciesEdits = true;
            var manualOnly = new PhotoItem { FilePath = secondPath, Rating = PhotoRating.Keep };
            manualOnly.SetBirds(new[] { new DetectedBird { ManualSpecies = identity } });
            manualOnly.HasManualSpeciesEdits = true;
            var group = new PhotoGroup(); group.Photos.Add(photo); group.Photos.Add(manualOnly);
            var recent = new RecentSpeciesHistory();
            for (int index = 0; index < 12; index++) recent.Use(new("鸟" + index, "Testus species" + index));
            recent.Use(identity);
            var projects = new ProjectService();
            string path = Path.Combine(root, "project.json");
            await projects.SaveAsync(path, new[] { group }, .5, recentSpecies: recent.Items);
            var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            var records = document["Groups"]![0]!["Photos"]!.AsArray();
            Check(document["Version"]!.GetValue<int>() == 3 && document["RecentSpecies"]!.AsArray().Count == 10,
                "new JSON v3 stores a bounded project-wide recent selection list");
            Check(records[0]!["Birds"]![1]!["Box"] is null && records[0]!["Birds"]![1]!["Species"] is null &&
                  records[0]!["Birds"]![1]!["ManualSpecies"]!["SourceTaxonId"]!.GetValue<string>() == "saved-taxon-id",
                "manual species persists its identity without a fabricated box or inference score");
            var loaded = await projects.LoadAsync(path);
            var restored = loaded.Groups.Single().Photos[0];
            Check(restored.Birds.Count == 3 && restored.SelectedBirdIndex == 1 && restored.NextBirdNumber == 40 &&
                  restored.Birds[1].ManualSpecies == identity && !restored.Birds[1].HasRegion && restored.Birds[1].Species is null &&
                  restored.Birds[0].DisplayNumber == 1 && restored.Birds[2].DisplayNumber == 2,
                "mixed automatic, unknown and manual entries retain order, selection, region numbering and high-water identity");
            Check(restored.HasManualSpeciesEdits && restored.Rating == PhotoRating.Favorite &&
                  restored.Birds[0].Species?.Confidence == .95,
                "manual protection, rating and remaining inference evidence survive reopening");
            Check(loaded.RecentSpecies.Count == 10 && loaded.RecentSpecies[0] == identity &&
                  loaded.RecentSpecies.Select(item => item.Key).Distinct().Count() == 10,
                "recent selections retain names, order and taxon IDs independently from the installed name catalog");
            Check(loaded.Groups.Single().Photos[1].Birds.Single().ManualSpecies == identity &&
                  loaded.Groups.Single().Photos[1].GeometryStatus == PhotoGeometryStatus.Unspecified,
                "manual-only photos reopen without inventing a location or a geometry review warning");
            var autosave = new AutoSaveService(Path.Combine(root, "recoveries"));
            var recovery = await autosave.SaveAsync(loaded.Groups, .5, null, 5, recentSpecies: loaded.RecentSpecies);
            var recovered = await projects.LoadAsync(recovery.Path);
            Check(recovered.RecentSpecies.SequenceEqual(loaded.RecentSpecies) &&
                  recovered.Groups.Single().Photos[0].HasManualSpeciesEdits &&
                  recovered.Groups.Single().Photos[0].Birds[1].ManualSpecies == identity,
                "five-minute recovery uses the same v3 manual-result and recent-selection state");
            var invalid = document.DeepClone();
            invalid["Groups"]![0]!["Photos"]![0]!["Birds"]![1]!["ManualSpecies"]!["ScientificName"] = " ";
            string invalidPath = Path.Combine(root, "invalid-manual.json");
            await File.WriteAllTextAsync(invalidPath, invalid.ToJsonString());
            var filtered = await projects.LoadAsync(invalidPath);
            Check(filtered.Groups.Single().Photos[0].Birds.Count == 2 && filtered.Groups.Single().Photos[0].Rating == PhotoRating.Favorite,
                "an invalid manual label cannot discard valid photo members or automatic results");
            var legacy = document.DeepClone(); legacy["Version"] = 2; legacy.AsObject().Remove("RecentSpecies");
            foreach (var record in legacy["Groups"]![0]!["Photos"]!.AsArray())
            {
                var birds = record!["Birds"]!.AsArray();
                record["Birds"] = new JsonArray(birds.Where(bird => bird!["ManualSpecies"] is null).Select(bird => bird!.DeepClone()).ToArray());
                record.AsObject().Remove("HasManualSpeciesEdits");
            }
            string legacyPath = Path.Combine(root, "actual-v2.json"); await File.WriteAllTextAsync(legacyPath, legacy.ToJsonString());
            var migrated = await projects.LoadAsync(legacyPath);
            Check(migrated.Groups.Single().Photos[0].Birds.Count == 2 && migrated.RecentSpecies.Count == 0,
                "v2 files without new preferences continue to load their explicit original members and regions");
            legacy["Groups"]![0]!["Photos"]![0]!["Birds"]![1]!["IsManual"] = true;
            await File.WriteAllTextAsync(legacyPath, legacy.ToJsonString());
            var editedLegacy = await projects.LoadAsync(legacyPath);
            Check(editedLegacy.Groups.Single().Photos[0].HasManualSpeciesEdits,
                "an explicitly saved hand-drawn region in v2 installs the new manual-edit protection when migrating");
            restored.SetBirds(Array.Empty<DetectedBird>(), nextBirdNumber: restored.NextBirdNumber);
            await projects.SaveAsync(path, loaded.Groups, .5, recentSpecies: loaded.RecentSpecies);
            var empty = (await projects.LoadAsync(path)).Groups.Single().Photos[0];
            Check(empty.Birds.Count == 0 && empty.HasManualSpeciesEdits && empty.NextBirdNumber == 40,
                "deleting every result keeps the manual-edit barrier and identity history through save and reload");
            string emptyPath = Path.Combine(root, "empty-library.json");
            await projects.SaveAsync(emptyPath, Array.Empty<PhotoGroup>(), .5, recentSpecies: recent.Items);
            var emptyLibrary = await projects.LoadAsync(emptyPath);
            Check(emptyLibrary.Groups.Count == 0 && emptyLibrary.RecentSpecies.Count == 10,
                "clearing the photo list still allows its project-wide recent choices to be saved");
            Check(original.SequenceEqual(SHA256.HashData(await File.ReadAllBytesAsync(firstPath))),
                "project edits, migration and recovery leave original photo bytes unchanged");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(allowed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
        return assertions;
    }
    private static BirdBox Box(double x) => new() { X = x, Y = .1, Width = .15, Height = .2, Confidence = .9 };
    private static void WriteJpeg(string path)
    {
        var image = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Rgb24, null, new byte[64 * 48 * 3], 64 * 3);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
