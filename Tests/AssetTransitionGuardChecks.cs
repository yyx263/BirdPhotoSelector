using System.IO;
using System.Security.Cryptography;
using BirdPhotoSelector;

internal static class AssetTransitionGuardChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool value, string description)
        {
            if (!value) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK ASSET GUARD " + description);
        }

        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.AssetTransitionChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string jpg = Path.Combine(root, "paired.jpg"), raw = Path.Combine(root, "paired.NEF");
            string singlePath = Path.Combine(root, "single.jpg");
            await File.WriteAllTextAsync(jpg, "Generated JPG-kind original; no image decoding in these checks.");
            await File.WriteAllTextAsync(raw, "Generated opaque RAW original.");
            await File.WriteAllTextAsync(singlePath, "Generated standalone JPG-kind original.");
            var jpgAsset = new PhotoAsset(jpg, PhotoAssetKind.Jpg);
            var rawAsset = new PhotoAsset(raw, PhotoAssetKind.Raw);
            var pair = new PhotoItem(); pair.SetAssets(new[] { jpgAsset, rawAsset });
            var rawOnly = new PhotoItem { FilePath = raw };
            var single = new PhotoItem { FilePath = singlePath };
            string destination = Directory.CreateDirectory(Path.Combine(root, "destination")).FullName;
            var before = new[] { jpg, raw, singlePath }.ToDictionary(path => path,
                path => SHA256.HashData(File.ReadAllBytes(path)));
            var service = new ExportService();

            var exported = await service.ExportAsync(new[] { single, pair }, destination, removeOriginal: false);
            Check(exported.CopiedFileCount == 3 && exported.Succeeded.Count == 2 && exported.RemovedFileCount == 0,
                "a mixed export now verifies all three managed files without losing either logical photo");
            Check(ExportService.CreatePlan(new[] { single, pair }, PhotoFileScope.JpgOnly).FileCount == 2 &&
                  ExportService.CreatePlan(new[] { single, pair }, PhotoFileScope.RawOnly).FileCount == 1,
                "file scopes select managed members independently from logical photo counts");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            var canceledDelete = await service.DeleteAsync(new[] { rawOnly }, canceled.Token);
            Check(canceledDelete.RemovedFileCount == 0 && canceledDelete.CanceledFileCount == 1,
                "canceling RAW-only deletion preserves the member and explicitly records cancellation");
            Check(before.All(entry => File.Exists(entry.Key) &&
                      SHA256.HashData(File.ReadAllBytes(entry.Key)).SequenceEqual(entry.Value)) &&
                  Directory.EnumerateFiles(destination).Count() == 3,
                "copy-only export and canceled deletion preserve every original byte");

            var photo = new PhotoItem { FilePath = jpg, PixelWidth = 4000, PixelHeight = 3000,
                Rating = PhotoRating.Favorite };
            photo.SetBirds(new[] { new DetectedBird
            {
                Number = 4, IsManual = true,
                Box = new() { X = .1, Y = .2, Width = .3, Height = .4, Confidence = .9 },
                Species = new("大山雀", "Parus major", .42)
            } }, nextBirdNumber: 18);
            var bird = photo.SelectedBird;
            var group = new PhotoGroup(); group.Photos.Add(photo);
            var model = new MainViewModel(); model.SetGroups(new[] { group }); model.Dirty = false;
            string id = photo.Id;
            photo.AddAsset(new(raw, PhotoAssetKind.Raw, "added-raw"));
            Check(model.Dirty && model.PhotoCount == 1 && photo.Id == id && ReferenceEquals(photo.SelectedBird, bird) &&
                  photo.Rating == PhotoRating.Favorite && photo.NextBirdNumber == 18,
                "adding a companion marks the session dirty without duplicating a photo or losing selection and evidence");
            model.Dirty = false;
            var originalJpg = photo.Assets.Single(asset => asset.Kind == PhotoAssetKind.Jpg);
            photo.RemoveAsset(originalJpg.Id);
            Check(model.Dirty && model.PhotoCount == 1 && photo.GeometryStatus == PhotoGeometryStatus.NeedsReview &&
                  !photo.CanUseBirdGeometry && photo.Bird is null && ReferenceEquals(photo.SelectedBird, bird) &&
                  photo.SelectedBird?.Species?.ScientificName == "Parus major" && photo.GeometryReference?.AssetId == originalJpg.Id,
                "source removal persists a dirty review state while retaining the logical photo and its old species evidence");
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.GetFullPath(allowed) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
        return count;
    }

}
