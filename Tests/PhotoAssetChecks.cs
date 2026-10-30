using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

internal static class PhotoAssetChecks
{
    public static Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK PHOTO ASSET " + description);
        }

        var legacy = new PhotoItem { FilePath = "relative-fixture.JPG" };
        Check(legacy.FilePath == "relative-fixture.JPG" && legacy.FileName == "relative-fixture.JPG" &&
              legacy.Assets.Count == 1 && legacy.Assets[0].Kind == PhotoAssetKind.Jpg && legacy.FormatLabel == "JPG",
            "legacy relative JPG initializers retain their original path string and single-file facade");
        Check(!string.IsNullOrWhiteSpace(legacy.Assets[0].Id) && Path.IsPathFullyQualified(legacy.Assets[0].CanonicalPath),
            "an original file has a stable member identity and a separate canonical comparison path");
        Check(PhotoAsset.TryGetKind("photo.JPEG", out var jpegKind) && jpegKind == PhotoAssetKind.Jpg &&
              PhotoAsset.TryGetKind("photo.CR3", out var rawKind) && rawKind == PhotoAssetKind.Raw &&
              new[] { ".crw", ".sr2", ".srf" }.All(extension => PhotoAsset.TryGetKind("legacy" + extension, out var kind) && kind == PhotoAssetKind.Raw) &&
              !PhotoAsset.TryGetKind("photo.png", out _),
            "member kind detection accepts JPEG and RAW extensions case-insensitively without accepting unrelated formats");

        string allowed = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.PhotoAssetChecks");
        string root = Path.Combine(allowed, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string pairDirectory = Directory.CreateDirectory(Path.Combine(root, "pair")).FullName;
            string otherDirectory = Directory.CreateDirectory(Path.Combine(root, "other")).FullName;
            string jpgPath = Path.Combine(pairDirectory, "DSC_0001.JPG");
            string rawPath = Path.Combine(pairDirectory, "dsc_0001.NEF");
            File.WriteAllBytes(jpgPath, new byte[] { 1, 2, 3, 4 });
            File.WriteAllBytes(rawPath, new byte[] { 5, 6, 7, 8, 9 });
            var jpg = new PhotoAsset(jpgPath, PhotoAssetKind.Jpg, "jpg-member");
            var raw = new PhotoAsset(rawPath, PhotoAssetKind.Raw, "raw-member");
            Check(jpg.CanPairWith(raw) && !jpg.CanPairWith(new(Path.Combine(otherDirectory, "DSC_0001.NEF"), PhotoAssetKind.Raw)),
                "pairing uses the same directory and stem under Windows case-insensitive comparison");
            Check(!jpg.CanPairWith(new(Path.Combine(pairDirectory, "DSC_0002.NEF"), PhotoAssetKind.Raw)),
                "a different capture stem is not a companion even inside the same directory");
            var reordered = new PhotoItem(); reordered.SetAssets(new[] { raw }); reordered.AddAsset(jpg);
            var forward = new PhotoItem(); forward.SetAssets(new[] { jpg }); forward.AddAsset(raw);
            Check(reordered.ActivePreviewAssetId == jpg.Id && forward.ActivePreviewAssetId == jpg.Id &&
                  reordered.PreferredPreviewAssetId == jpg.Id && forward.PreferredPreviewAssetId == jpg.Id,
                "default JPG preview preference is independent of companion-addition order");

            var photo = new PhotoItem { FilePath = jpgPath, Rating = PhotoRating.Favorite, PixelWidth = 6000, PixelHeight = 4000,
                ViewZoom = 2.4, ViewCenterX = .6, ViewCenterY = .45 };
            var thumbnail = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Rgb24, null, new byte[] { 1, 2, 3 }, 3);
            thumbnail.Freeze(); photo.Thumbnail = thumbnail;
            var bird = new DetectedBird { Number = 7, Box = new() { X = .2, Y = .3, Width = .2, Height = .25, Confidence = .9 },
                Species = new("大山雀", "Parus major", .6, Confidence: .9), RecognitionStatus = "原有识别结果" };
            photo.SetBirds(new[] { bird }, nextBirdNumber: 20);
            string photoId = photo.Id, primaryId = photo.Assets[0].Id;
            var geometry = photo.GeometryReference!;
            var group = new PhotoGroup("逻辑照片"); group.Photos.Add(photo);
            photo.AddAsset(raw);
            Check(photo.Id == photoId && group.Photos.Single() == photo && photo.Rating == PhotoRating.Favorite &&
                  ReferenceEquals(photo.SelectedBird, bird) && photo.NextBirdNumber == 20 && bird.Species?.ScientificName == "Parus major",
                "adding a companion preserves the logical photo, group, rating, selected bird, species and retired-number watermark");
            Check(photo.Assets.Count == 2 && photo.FormatLabel == "JPG + RAW" && photo.ActivePreviewAssetId == primaryId &&
                  ReferenceEquals(photo.Thumbnail, thumbnail) && photo.ViewZoom == 2.4 && photo.PixelWidth == 6000 &&
                  photo.CanUseBirdGeometry && ReferenceEquals(photo.GeometryReference, geometry),
                "adding RAW beside an active JPG preserves that preview and its valid coordinate frame");
            Check(geometry.AssetId == primaryId && geometry.FilePath == jpgPath && geometry.DisplayWidth == 6000 &&
                  geometry.DisplayHeight == 4000 && geometry.FileLength == 4 && geometry.LastWriteTimeUtcTicks is > 0,
                "recognized coordinates retain original member identity, path, display dimensions and available file facts");
            bool readOnly = false;
            try { ((ICollection<PhotoAsset>)photo.Assets).Add(new(Path.Combine(pairDirectory, "DSC_0001.CR3"), PhotoAssetKind.Raw)); }
            catch (NotSupportedException) { readOnly = true; }
            Check(readOnly && photo.Assets.Count == 2, "callers cannot mutate the asset collection around validation and notifications");

            int notifications = 0, collectionChanges = 0;
            photo.PropertyChanged += (_, _) => notifications++;
            ((INotifyCollectionChanged)photo.Assets).CollectionChanged += (_, _) => collectionChanges++;
            void RejectWithoutMutation(Action operation, string description)
            {
                int beforeNotifications = notifications, beforeCollection = collectionChanges;
                var assets = photo.Assets.ToArray();
                bool rejected = false;
                try { operation(); }
                catch (ArgumentException) { rejected = true; }
                Check(rejected && photo.Assets.SequenceEqual(assets) && photo.ActivePreviewAssetId == primaryId &&
                      photo.PreferredPreviewAssetId == primaryId && photo.Id == photoId && photo.Rating == PhotoRating.Favorite &&
                      ReferenceEquals(photo.SelectedBird, bird) && photo.NextBirdNumber == 20 && photo.ViewZoom == 2.4 &&
                      ReferenceEquals(photo.Thumbnail, thumbnail) && ReferenceEquals(photo.GeometryReference, geometry) &&
                      photo.CanUseBirdGeometry && notifications == beforeNotifications && collectionChanges == beforeCollection,
                    description);
            }
            RejectWithoutMutation(() => photo.AddAsset(new(Path.Combine(pairDirectory, "DSC_0001.jpeg"), PhotoAssetKind.Jpg)),
                "an ambiguous second JPG candidate is rejected before any state or binding changes");
            RejectWithoutMutation(() => photo.SetAssets(new[] { photo.Assets[0], new PhotoAsset(rawPath, PhotoAssetKind.Raw, primaryId) }),
                "duplicate asset identities reject the entire replacement atomically");
            RejectWithoutMutation(() => photo.SetAssets(new[] { photo.Assets[0], new PhotoAsset(Path.Combine(otherDirectory, "DSC_0001.NEF"), PhotoAssetKind.Raw) }),
                "cross-directory same-name companions do not replace or mutate the original photo");
            RejectWithoutMutation(() => photo.SetAssets(new[] { photo.Assets[0], new PhotoAsset(Path.Combine(pairDirectory, "DSC_0002.NEF"), PhotoAssetKind.Raw) }),
                "a different-stem candidate rejects the entire replacement without clearing existing evidence");
            RejectWithoutMutation(() => photo.SetAssets(photo.Assets, activePreviewAssetId: "not-a-member"),
                "invalid source selection rejects before touching any member or preview state");
            RejectWithoutMutation(() => photo.AddAsset(new PhotoAsset(jpgPath.ToLowerInvariant(), PhotoAssetKind.Jpg)),
                "case-variant duplicate paths cannot create an additional member or separate rating");

            photo.SetActivePreviewAsset(raw.Id);
            Check(photo.FilePath == rawPath && photo.FileName == Path.GetFileName(rawPath) && photo.PreferredPreviewAssetId == primaryId &&
                  photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && !photo.CanUseBirdGeometry && photo.Bird is null,
                "changing the active source keeps JPG preference but withholds unverified old geometry from the legacy facade");
            Check(ReferenceEquals(photo.GeometryReference, geometry) && ReferenceEquals(photo.SelectedBird, bird) &&
                  bird.Species?.ScientificName == "Parus major" && bird.Number == 7 && photo.NextBirdNumber == 20 &&
                  photo.Rating == PhotoRating.Favorite && group.Photos.Single() == photo,
                "source switching preserves old boxes, species and their original reference independently of logical identity");
            Check(photo.Thumbnail is null && photo.ViewZoom is null && photo.ViewCenterX is null && photo.ViewCenterY is null &&
                  photo.PixelWidth == 0 && photo.PixelHeight == 0,
                "new sources cannot inherit another file's cached thumbnail, saved view or display dimensions");
            photo.SetBirds(photo.Birds.ToArray(), nextBirdNumber: 20);
            Check(photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && ReferenceEquals(photo.GeometryReference, geometry) &&
                  !photo.CanUseBirdGeometry, "reinstalling old bird objects does not silently confirm RAW coordinate geometry");
            photo.SetGeometryReference(geometry, PhotoGeometryStatus.Current);
            Check(photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && !photo.CanUseBirdGeometry,
                "a reference belonging to another member cannot be marked current for the active source");
            photo.SetGeometryReference(geometry, PhotoGeometryStatus.Unspecified);
            Check(photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && photo.Bird is null && !photo.CanUseBirdGeometry &&
                  ReferenceEquals(photo.SelectedBird, bird) && bird.Species is not null,
                "an unspecified historical coordinate frame cannot expose old boxes on another active source");
            var deferredGeometry = new PhotoItem(); deferredGeometry.SetAssets(new[] { raw });
            deferredGeometry.SetGeometryReference(geometry, PhotoGeometryStatus.Unspecified);
            deferredGeometry.SetBirds(new[] { new DetectedBird { Box = bird.Box.Copy(), Species = bird.Species } });
            Check(deferredGeometry.GeometryStatus == PhotoGeometryStatus.NeedsReview && deferredGeometry.Bird is null &&
                  ReferenceEquals(deferredGeometry.GeometryReference, geometry),
                "adding birds after an unspecified foreign frame still requires explicit geometry review");
            var reviewManual = photo.AddManualRegion(new() { X = .1, Y = .1, Width = .2, Height = .2, Confidence = 1 });
            Check(photo.Birds.Count == 2 && reviewManual.IsManual && photo.Birds.Contains(bird) &&
                  photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && !photo.CanUseBirdGeometry,
                "a reviewed source can add a manual region while preserving old results and its source-change notice");
            photo.RemoveBird(reviewManual);
            photo.SetActivePreviewAsset(primaryId);
            photo.SetGeometryReference(geometry, PhotoGeometryStatus.Current);
            Check(photo.CanUseBirdGeometry && ReferenceEquals(photo.Bird, bird.Box),
                "explicit confirmation of the original source restores access to its retained coordinates");

            Check(photo.RemoveAsset(primaryId) && photo.Assets.Count == 1 && photo.ActivePreviewAssetId == raw.Id &&
                  photo.FilePath == rawPath && photo.FormatLabel == "RAW" &&
                  photo.GeometryStatus == PhotoGeometryStatus.NeedsReview && ReferenceEquals(photo.GeometryReference, geometry),
                "removing active JPG selects remaining RAW and retains the deleted-member coordinate snapshot for review");
            Check(File.Exists(jpgPath) && File.Exists(rawPath), "domain member removal never deletes or rewrites an original file");
            string serialized = JsonSerializer.Serialize(geometry);
            var restored = JsonSerializer.Deserialize<PhotoGeometryReference>(serialized)!;
            var restoredAsset = JsonSerializer.Deserialize<PhotoAsset>(JsonSerializer.Serialize(raw))!;
            Check(restored == geometry && restoredAsset.Id == raw.Id && restoredAsset.FilePath == raw.FilePath &&
                  restoredAsset.Kind == PhotoAssetKind.Raw && !serialized.Contains("CanonicalPath", StringComparison.Ordinal),
                "member and historical geometry snapshots serialize with stable identities and optional file facts");
            photo.SetGeometryReference(restored, PhotoGeometryStatus.NeedsReview);
            Check(photo.GeometryReference?.AssetId == primaryId && !photo.Assets.Any(asset => asset.Id == primaryId) &&
                  photo.Birds.Single().Species is not null && !photo.CanUseBirdGeometry,
                "historical references remain valid audit records when their original asset is no longer present");
            Check(photo.RemoveAsset(raw.Id) && photo.Assets.Count == 0 && photo.FilePath == "" && photo.FormatLabel == "" &&
                  photo.Id == photoId && photo.Rating == PhotoRating.Favorite && ReferenceEquals(photo.SelectedBird, bird) &&
                  photo.GeometryReference?.AssetId == primaryId,
                "removing the final member leaves logical-photo ownership and prior recognition evidence to the caller");

            var missingJpg = new PhotoAsset(Path.Combine(pairDirectory, "DSC_0001.jpeg"), PhotoAssetKind.Jpg, "missing-jpg");
            var audit = new PhotoItem();
            audit.SetAssets(new[] { missingJpg, raw }, missingJpg.Id, raw.Id);
            Check(audit.Assets.Count == 2 && audit.PreferredPreviewAssetId == missingJpg.Id && audit.ActivePreviewAssetId == raw.Id,
                "a missing preferred JPG can remain in the asset audit while an available RAW is explicitly selected");
            audit.SetPreferredPreviewAsset(missingJpg.Id);
            Check(audit.PreferredPreviewAssetId == missingJpg.Id && audit.ActivePreviewAssetId == raw.Id,
                "a missing preferred source falls back to a present companion without discarding the missing member");
            var missingRaw = new PhotoAsset(Path.Combine(root, "not-created.NEF"), PhotoAssetKind.Raw);
            var missingPairJpg = new PhotoAsset(Path.Combine(root, "not-created.jpg"), PhotoAssetKind.Jpg);
            var fixtures = new PhotoItem(); fixtures.SetAssets(new[] { missingRaw, missingPairJpg });
            Check(fixtures.ActivePreviewAssetId == missingPairJpg.Id && fixtures.FilePath == missingPairJpg.FilePath,
                "logical fixtures with no physical files retain a deterministic JPG-first source without decoding");
            var alternate = new PhotoItem(); alternate.SetAssets(new[] { raw, jpg }, raw.Id, raw.Id);
            alternate.RemoveAsset(raw.Id);
            Check(alternate.ActivePreviewAssetId == jpg.Id && alternate.FormatLabel == "JPG",
                "removing an active RAW falls back to an available JPG");

            string previousDirectory = Directory.GetCurrentDirectory();
            string creationDirectory = Directory.CreateDirectory(Path.Combine(root, "asset-created-here")).FullName;
            string captureDirectory = Directory.CreateDirectory(Path.Combine(root, "frame-captured-here")).FullName;
            try
            {
                Directory.SetCurrentDirectory(creationDirectory);
                File.WriteAllBytes(Path.Combine(creationDirectory, "relative-source.JPG"), new byte[] { 2, 4, 6 });
                var relative = new PhotoItem { FilePath = "relative-source.JPG", PixelWidth = 2400, PixelHeight = 1600 };
                string originalSource = relative.ActivePreviewAsset!.CanonicalPath;
                var dimensionCompletion = new PhotoItem { FilePath = "relative-source.JPG" };
                var relativeFrame = new PhotoGeometryReference(dimensionCompletion.ActivePreviewAsset!.Id, "relative-source.JPG", null, null);
                dimensionCompletion.SetGeometryReference(relativeFrame, PhotoGeometryStatus.Current);
                string originalFramePath = relativeFrame.CanonicalPath;
                Directory.SetCurrentDirectory(captureDirectory);
                relative.SetBirds(new[] { new DetectedBird { Box = new() { X = .1, Y = .2, Width = .3, Height = .4, Confidence = .9 } } });
                Check(relative.FilePath == "relative-source.JPG" && relative.GeometryReference is { } frame && frame.FilePath == originalSource &&
                      frame.DisplayWidth == 2400 && frame.DisplayHeight == 1600 && frame.FileLength == 3 && relative.CanUseBirdGeometry,
                    "geometry capture remains anchored to the original member after the working directory changes");
                dimensionCompletion.PixelWidth = 1800; dimensionCompletion.PixelHeight = 1200;
                Check(dimensionCompletion.GeometryStatus == PhotoGeometryStatus.Current && dimensionCompletion.CanUseBirdGeometry &&
                      dimensionCompletion.GeometryReference is { } completed && completed.CanonicalPath == originalFramePath &&
                      completed.DisplayWidth == 1800 && completed.DisplayHeight == 1200,
                    "completing unknown frame dimensions cannot re-resolve its original relative path under a different working directory");
            }
            finally { Directory.SetCurrentDirectory(previousDirectory); }
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string allowedPrefix = Path.GetFullPath(allowed) + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(allowedPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(resolved, recursive: true);
        }
        return Task.FromResult(count);
    }
}
