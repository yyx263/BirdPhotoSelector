using System.IO;
using System.Text.Json;
using BirdPhotoSelector;

internal static class GroupingThresholdChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK THRESHOLD " + description);
        }

        Check(GroupingThreshold.ToActual(0) == 0 && GroupingThreshold.ToActual(1) == 1,
            "recalibrated slider retains the complete actual score range");
        Check(Math.Abs(GroupingThreshold.ToActual(.5) - .95) < 1e-12 &&
              Math.Abs(GroupingThreshold.FromActual(.95) - .5) < 1e-12,
            "the new 50% midpoint is the former actual 95% threshold");
        Check(new MainViewModel().Threshold == .5 && GroupingThreshold.ToActual(GroupingThreshold.Default) > .82,
            "new sessions start at a stricter threshold than the previous default");
        Check(Enumerable.Range(1, 100).All(step =>
                GroupingThreshold.ToActual(step / 100.0) > GroupingThreshold.ToActual((step - 1) / 100.0)),
            "every increasing slider setting makes grouping strictly more selective");
        Check(new[] { 0d, .01, .3, .5, .73, .82, .95, .99, 1d }.All(actual =>
                Math.Abs(GroupingThreshold.ToActual(GroupingThreshold.FromActual(actual)) - actual) < 1e-12),
            "legacy background scores round-trip through the new scale");

        var model = new MainViewModel { Threshold = -.1 };
        Check(model.Threshold == 0, "the slider can reach the bottom of its full range");
        model.Threshold = 1.1;
        Check(model.Threshold == 1, "the slider clamps values beyond its upper endpoint");

        string root = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.ThresholdChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "project.json");
            var service = new ProjectService();
            await service.SaveAsync(path, Array.Empty<PhotoGroup>(), .5);
            using (var json = JsonDocument.Parse(await File.ReadAllTextAsync(path)))
            {
                Check(json.RootElement.GetProperty("GroupingThresholdScaleVersion").GetInt32() == GroupingThreshold.ScaleVersion &&
                      json.RootElement.GetProperty("GroupingThreshold").GetDouble() == .5,
                    "new projects identify the displayed threshold scale explicitly");
            }
            Check((await service.LoadAsync(path)).GroupingThreshold == .5,
                "new projects preserve the displayed threshold without a second conversion");

            foreach (double actual in new[] { .3, .82, .95, .99 })
            {
                await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
                {
                    Schema = "BirdPhotoSelector.Project", Version = 1, GroupingThreshold = actual,
                    Groups = Array.Empty<object>()
                }));
                var legacy = await service.LoadAsync(path);
                Check(Math.Abs(GroupingThreshold.ToActual(legacy.GroupingThreshold) - actual) < 1e-12,
                    $"a legacy {actual:P0} project retains its original actual strictness");
                await service.SaveAsync(path, legacy.Groups, legacy.GroupingThreshold);
                var migrated = await service.LoadAsync(path);
                Check(migrated.GroupingThreshold == legacy.GroupingThreshold,
                    $"resaving a legacy {actual:P0} project does not convert the threshold again");
            }

            await File.WriteAllTextAsync(path,
                "{\"Schema\":\"BirdPhotoSelector.Project\",\"Version\":1,\"GroupingThreshold\":0.5,\"GroupingThresholdScaleVersion\":3,\"Groups\":[]}");
            bool rejected = false;
            try { await service.LoadAsync(path); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "unknown future threshold scales are rejected rather than silently reinterpreted");
        }
        finally { Directory.Delete(root, recursive: true); }
        return count;
    }
}
