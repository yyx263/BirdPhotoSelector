using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

/// <summary>Project/member persistence only. The opaque RAW fixtures are never decoded.</summary>
internal static class PhotoProjectV2Checks
{
    public static async Task<int> RunAsync()
    {
        int assertions = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            assertions++;
            Console.WriteLine("  OK PROJECT-V2 " + description);
        }

        string root = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.PhotoProjectV2Checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var projects = new ProjectService();
            string pairDirectory = Directory.CreateDirectory(Path.Combine(root, "paired")).FullName;
            string pairJpg = Path.Combine(pairDirectory, "scene.jpg");
            string pairRaw = Path.Combine(pairDirectory, "scene.NEF");
            WriteJpeg(pairJpg);
            WriteOpaqueRaw(pairRaw);
            var jpgAsset = new PhotoAsset(pairJpg, PhotoAssetKind.Jpg, "pair-jpg");
            var rawAsset = new PhotoAsset(pairRaw, PhotoAssetKind.Raw, "pair-raw");
            var pair = Photo(new[] { jpgAsset, rawAsset }, "paired-photo", jpgAsset.Id, 64, 48);

            string rawOnlyPath = Path.Combine(root, "raw-only.CR3");
            WriteOpaqueRaw(rawOnlyPath);
            var rawOnlyAsset = new PhotoAsset(rawOnlyPath, PhotoAssetKind.Raw, "raw-only-member");
            var rawOnly = Photo(new[] { rawOnlyAsset }, "raw-only-photo", rawOnlyAsset.Id, 6000, 4000);
            rawOnly.Metadata = new PhotoMetadata { Camera = "Saved RAW camera", CapturedAt = "2026-10-29 08:12:13", Iso = "ISO 800" };

            string jpgOnlyPath = Path.Combine(root, "jpg-only.jpeg");
            WriteJpeg(jpgOnlyPath);
            var jpgOnlyAsset = new PhotoAsset(jpgOnlyPath, PhotoAssetKind.Jpg, "jpg-only-member");
            var jpgOnly = Photo(new[] { jpgOnlyAsset }, "jpg-only-photo", jpgOnlyAsset.Id, 64, 48);
            var group = Group(pair, rawOnly, jpgOnly);
            string baselinePath = Path.Combine(root, "baseline-v2.json");
            await projects.SaveAsync(baselinePath, new[] { group }, .5);
            string baselineJson = await File.ReadAllTextAsync(baselinePath);
            var baselineNode = JsonNode.Parse(baselineJson)!;
            Check(baselineNode["Version"]!.GetValue<int>() == 2 &&
                  baselineNode["Groups"]![0]!["Photos"]![0]!["Assets"]!.AsArray().Count == 2,
                "new projects explicitly write version 2 and both original file members");

            var loaded = await projects.LoadAsync(baselinePath);
            var photos = loaded.Groups.Single().Photos.ToDictionary(photo => photo.Id);
            var loadedPair = photos[pair.Id];
            var loadedRaw = photos[rawOnly.Id];
            var loadedJpg = photos[jpgOnly.Id];
            Check(photos.Count == 3 && loaded.MissingAssetCount == 0 && loaded.InvalidAssetCount == 0 && loaded.DuplicateAssetCount == 0,
                "JPG+RAW, RAW-only and JPG-only records load as three logical photos without invented invalid members");
            Check(loadedPair.Assets.Count == 2 && loadedPair.Assets.Select(asset => asset.Id).Order()
                      .SequenceEqual(new[] { jpgAsset.Id, rawAsset.Id }.Order()) &&
                  loadedPair.ActivePreviewAssetId == jpgAsset.Id && loadedPair.PreferredPreviewAssetId == jpgAsset.Id,
                "paired members keep stable member IDs and the preferred/active JPG source");
            Check(loadedRaw.Assets.Single().Kind == PhotoAssetKind.Raw && loadedRaw.FilePath == rawOnlyPath &&
                  loadedRaw.Metadata.Camera == "Saved RAW camera" && loadedRaw.Metadata.CapturedAt == "2026-10-29 08:12:13" &&
                  loadedRaw.PixelWidth == 6000 && loadedRaw.PixelHeight == 4000,
                "RAW-only records restore stored EXIF and dimensions without passing opaque RAW bytes through a JPEG decoder");
            Check(loadedJpg.Assets.Single().Kind == PhotoAssetKind.Jpg && loadedJpg.FilePath == jpgOnlyPath &&
                  loadedJpg.Thumbnail is not null,
                "JPG-only v2 records retain the existing real JPEG thumbnail loading path");
            Check(HasSavedBirdState(loadedPair) && HasSavedBirdState(loadedRaw) && loadedPair.GeometryStatus == PhotoGeometryStatus.Current &&
                  loadedPair.GeometryReference?.AssetId == jpgAsset.Id && loadedPair.CanUseBirdGeometry &&
                  loadedPair.ViewZoom == 2.6 && loadedPair.ViewCenterX == .42 && loadedPair.ViewCenterY == .43,
                "paired and RAW-only records retain ratings, species evidence, stable selection, number high-water mark and geometry reference");

            string resavedPath = Path.Combine(root, "resaved-v2.json");
            await projects.SaveAsync(resavedPath, loaded.Groups, loaded.GroupingThreshold);
            var resaved = await projects.LoadAsync(resavedPath);
            await projects.SaveAsync(resavedPath, resaved.Groups, resaved.GroupingThreshold);
            var resavedAgain = (await projects.LoadAsync(resavedPath)).Groups.Single().Photos.ToDictionary(photo => photo.Id);
            Check(resavedAgain[pair.Id].Assets.Select(asset => asset.Id).SequenceEqual(loadedPair.Assets.Select(asset => asset.Id)) &&
                  HasSavedBirdState(resavedAgain[pair.Id]) && HasSavedBirdState(resavedAgain[rawOnly.Id]) &&
                  resavedAgain[pair.Id].GeometryReference == loadedPair.GeometryReference,
                "repeated v2 replacements preserve the full member list, old species evidence and geometry identity");

            var autosave = new AutoSaveService(Path.Combine(root, "Recovery"));
            var recovery = await autosave.SaveAsync(resaved.Groups, .5, null, revision: 42);
            var recovered = (await projects.LoadAsync(recovery.Path)).Groups.Single().Photos.ToDictionary(photo => photo.Id);
            Check(autosave.LastSavedRevision == 42 && recovered[pair.Id].Assets.Count == 2 &&
                  recovered[rawOnly.Id].Assets.Single().Kind == PhotoAssetKind.Raw &&
                  HasSavedBirdState(recovered[pair.Id]) && HasSavedBirdState(recovered[rawOnly.Id]),
                "automatic recovery round-trips multiple members and RAW-only records with old species and numbering intact");

            await CheckLegacyMigration(root, projects, Check);
            await CheckMissingMembers(root, projects, Check);
            await CheckCurrentSourceViews(root, projects, Check);
            await CheckCrossDirectoryIdentity(root, projects, Check);

            // Legacy files have no independent viewport-source proof; keep their unsafe-view safeguards.
            foreach (string? savedStatus in new string?[] { null, "Unspecified", "Current" })
            {
                var document = JsonNode.Parse(baselineJson)!;
                var record = Record(document);
                record.AsObject().Remove("ViewReference");
                if (savedStatus is null) record.AsObject().Remove("GeometryStatus");
                else record["GeometryStatus"] = savedStatus;
                record["GeometryReference"]!["AssetId"] = rawAsset.Id;
                record["GeometryReference"]!["FilePath"] = pairRaw;
                string mismatchPath = Path.Combine(root, $"geometry-mismatch-{savedStatus ?? "missing"}.json");
                await File.WriteAllTextAsync(mismatchPath, document.ToJsonString());
                var outcome = await projects.LoadAsync(mismatchPath);
                var retained = outcome.Groups.Single().Photos.Single(photo => photo.Id == pair.Id);
                Check(retained.GeometryStatus == PhotoGeometryStatus.NeedsReview && !retained.CanUseBirdGeometry &&
                      retained.Bird is null && HasSavedBirdState(retained) && retained.ViewZoom is null &&
                      retained.GeometryReference?.AssetId == rawAsset.Id,
                    $"{savedStatus ?? "missing"} geometry state cannot reuse another member's old boxes or view while preserving species");
            }

            var unconfirmedDocument = JsonNode.Parse(baselineJson)!;
            Record(unconfirmedDocument)["GeometryStatus"] = "Unspecified";
            Record(unconfirmedDocument).AsObject().Remove("ViewReference");
            string unconfirmedPath = Path.Combine(root, "unconfirmed-geometry.json");
            await File.WriteAllTextAsync(unconfirmedPath, unconfirmedDocument.ToJsonString());
            var unconfirmed = (await projects.LoadAsync(unconfirmedPath)).Groups.Single().Photos.Single(photo => photo.Id == pair.Id);
            Check(unconfirmed.GeometryStatus == PhotoGeometryStatus.NeedsReview && unconfirmed.Bird is null &&
                  !unconfirmed.CanUseBirdGeometry && HasSavedBirdState(unconfirmed) && unconfirmed.ViewZoom is null,
                "an unspecified geometry state never implicitly confirms saved coordinates even when member identity matches");

            foreach (string fault in new[] { "unknown-status", "missing-reference-id", "invalid-reference-dimensions" })
            {
                var document = JsonNode.Parse(baselineJson)!;
                var record = Record(document);
                record.AsObject().Remove("ViewReference");
                if (fault == "unknown-status") record["GeometryStatus"] = "FutureState";
                else if (fault == "missing-reference-id") record["GeometryReference"]!.AsObject().Remove("AssetId");
                else record["GeometryReference"]!["DisplayWidth"] = 0;
                string faultPath = Path.Combine(root, fault + ".json");
                await File.WriteAllTextAsync(faultPath, document.ToJsonString());
                var outcome = await projects.LoadAsync(faultPath);
                var retained = outcome.Groups.Single().Photos.Single(photo => photo.Id == pair.Id);
                Check(outcome.SkippedInvalid == 0 && outcome.Errors.Count > 0 && retained.Assets.Count == 2 &&
                      HasSavedBirdState(retained) && retained.GeometryStatus == PhotoGeometryStatus.NeedsReview &&
                      !retained.CanUseBirdGeometry && retained.Bird is null && retained.ViewZoom is null,
                    $"{fault} reports an unsafe frame without discarding valid members, ratings or saved species");
            }

            async Task CheckInvalidMember(string name, Action<JsonNode> mutate, Func<ProjectLoadResult, bool> condition)
            {
                var document = JsonNode.Parse(baselineJson)!;
                mutate(document);
                string path = Path.Combine(root, name + ".json");
                await File.WriteAllTextAsync(path, document.ToJsonString());
                var outcome = await projects.LoadAsync(path);
                Check(condition(outcome) && outcome.Groups.SelectMany(item => item.Photos)
                    .Any(photo => photo.Id == pair.Id && photo.Assets.Any(asset => asset.Id == jpgAsset.Id)), name);
            }

            await CheckInvalidMember("duplicate member IDs are explicitly reported instead of merging JPG and RAW", document =>
            {
                var record = Record(document);
                record["Assets"]![1]!["Id"] = record["Assets"]![0]!["Id"]!.GetValue<string>();
            }, outcome => outcome.DuplicateAssetCount > 0 && AllMemberIdsUnique(outcome));

            await CheckInvalidMember("duplicate canonical member paths are reported and retained at most once", document =>
            {
                var assets = Record(document)["Assets"]!.AsArray();
                var duplicate = assets[0]!.DeepClone();
                duplicate["Id"] = "same-file-new-id";
                assets.Add(duplicate);
            }, outcome => outcome.DuplicateAssetCount > 0 && AllMemberPathsUnique(outcome));

            await CheckInvalidMember("member kind and file extension mismatches are invalid without losing a surviving JPG", document =>
            {
                Record(document)["Assets"]![1]!["Kind"] = "Jpg";
            }, outcome => outcome.InvalidAssetCount > 0 && outcome.Groups.SelectMany(item => item.Photos)
                .Any(photo => photo.Id == pair.Id && photo.Assets.Any(asset => asset.Id == jpgAsset.Id)));

            string wrongStem = Path.Combine(pairDirectory, "different-scene.NEF");
            WriteOpaqueRaw(wrongStem);
            await CheckInvalidMember("different filename stems cannot be silently paired into one logical photo", document =>
            {
                Record(document)["Assets"]![1]!["FilePath"] = wrongStem;
            }, outcome => outcome.InvalidAssetCount > 0 && outcome.Groups.SelectMany(item => item.Photos)
                .SelectMany(photo => photo.Assets).All(asset => asset.FilePath != wrongStem));

            string otherDirectory = Directory.CreateDirectory(Path.Combine(root, "other-directory")).FullName;
            string wrongDirectoryRaw = Path.Combine(otherDirectory, "scene.NEF");
            WriteOpaqueRaw(wrongDirectoryRaw);
            await CheckInvalidMember("matching stems in different directories cannot be silently paired", document =>
            {
                Record(document)["Assets"]![1]!["FilePath"] = wrongDirectoryRaw;
            }, outcome => outcome.InvalidAssetCount > 0 && outcome.Groups.SelectMany(item => item.Photos)
                .SelectMany(photo => photo.Assets).All(asset => asset.FilePath != wrongDirectoryRaw));

            await CheckInvalidMember("member IDs are unique across logical photos rather than only within each record", document =>
            {
                document["Groups"]![0]!["Photos"]![1]!["Assets"]![0]!["Id"] = jpgAsset.Id;
            }, outcome => outcome.DuplicateAssetCount > 0 && AllMemberIdsUnique(outcome));

            await CheckInvalidMember("a canonical member path cannot be owned by two logical photo IDs", document =>
            {
                document["Groups"]![0]!["Photos"]![1]!["Assets"]![0]!["FilePath"] = pairRaw;
            }, outcome => outcome.DuplicateAssetCount > 0 && AllMemberPathsUnique(outcome));

            var future = JsonNode.Parse(baselineJson)!;
            future["Version"] = 999;
            string futurePath = Path.Combine(root, "future.json");
            await File.WriteAllTextAsync(futurePath, future.ToJsonString());
            bool rejectedFuture = false;
            try { await projects.LoadAsync(futurePath); }
            catch (InvalidDataException) { rejectedFuture = true; }
            Check(rejectedFuture, "future project versions are rejected instead of being interpreted as v2 members");

            byte[] originalProject = await File.ReadAllBytesAsync(baselinePath);
            var collidingAsset = new PhotoAsset(jpgOnlyPath, PhotoAssetKind.Jpg, jpgAsset.Id);
            var collidingPhoto = Photo(new[] { collidingAsset }, "colliding-member-photo", collidingAsset.Id, 64, 48);
            bool rejectedInvalidSave = false;
            try { await projects.SaveAsync(baselinePath, new[] { Group(pair, collidingPhoto) }, .5); }
            catch (InvalidDataException) { rejectedInvalidSave = true; }
            Check(rejectedInvalidSave && (await File.ReadAllBytesAsync(baselinePath)).SequenceEqual(originalProject),
                "saving duplicate global member IDs rejects the invalid model without replacing an existing project");

            pair.Rating = PhotoRating.Reject;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            bool canceled = false;
            try { await projects.SaveAsync(baselinePath, new[] { group }, .75, cancellation.Token); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled && (await File.ReadAllBytesAsync(baselinePath)).SequenceEqual(originalProject) &&
                  !Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories).Any(),
                "a canceled v2 save preserves the existing project bytes and leaves no partial replacement");
            Check((await File.ReadAllBytesAsync(pairRaw)).SequenceEqual(OpaqueRawBytes) &&
                  (await File.ReadAllBytesAsync(rawOnlyPath)).SequenceEqual(OpaqueRawBytes),
                "member persistence, invalid-record handling and cancellation never rewrite original RAW fixture bytes");
            return assertions;
        }
        finally
        {
            string resolved = Path.GetFullPath(root);
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.PhotoProjectV2Checks")) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static async Task CheckCurrentSourceViews(string root, ProjectService projects, Action<bool, string> check)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "current-source-views")).FullName;
        string jpg = Path.Combine(directory, "view.jpg"), raw = Path.Combine(directory, "view.NEF");
        WriteJpeg(jpg); WriteOpaqueRaw(raw);
        var jpgAsset = new PhotoAsset(jpg, PhotoAssetKind.Jpg, "view-jpg");
        var rawAsset = new PhotoAsset(raw, PhotoAssetKind.Raw, "view-raw");
        var photo = Photo(new[] { jpgAsset, rawAsset }, "source-view-photo", jpgAsset.Id, 64, 48);
        string beforeFallback = Path.Combine(directory, "before-fallback.json");
        await projects.SaveAsync(beforeFallback, new[] { Group(photo) }, .5);

        File.Delete(jpg);
        var fallback = (await projects.LoadAsync(beforeFallback)).Groups.Single().Photos.Single();
        check(fallback.ActivePreviewAssetId == rawAsset.Id && fallback.GeometryStatus == PhotoGeometryStatus.NeedsReview &&
              fallback.ViewZoom is null && fallback.ViewCenterX is null && fallback.ViewCenterY is null,
            "the first JPG-to-RAW project fallback never restores the old JPG viewport");

        // Simulate the view saved after the current RAW pixels have been displayed and manually examined.
        fallback.PixelWidth = 1200; fallback.PixelHeight = 800;
        fallback.ViewZoom = 3; fallback.ViewCenterX = .65; fallback.ViewCenterY = .42;
        string currentPath = Path.Combine(directory, "current-raw-view.json");
        await projects.SaveAsync(currentPath, new[] { Group(fallback) }, .5);
        string currentJson = await File.ReadAllTextAsync(currentPath);
        var record = Record(JsonNode.Parse(currentJson)!);
        check(record["ViewReference"]!["AssetId"]!.GetValue<string>() == rawAsset.Id &&
              record["GeometryReference"]!["AssetId"]!.GetValue<string>() == jpgAsset.Id &&
              record["ViewReference"]!["FileLength"]!.GetValue<long>() == new FileInfo(raw).Length,
            "a new viewport stores the current RAW source independently from the historical JPG bird reference");

        static bool HasCurrentView(PhotoItem loaded) => loaded.ViewZoom == 3 && loaded.ViewCenterX == .65 && loaded.ViewCenterY == .42;
        var reopened = (await projects.LoadAsync(currentPath)).Groups.Single().Photos.Single();
        check(HasCurrentView(reopened) && reopened.GeometryStatus == PhotoGeometryStatus.NeedsReview &&
              !reopened.CanUseBirdGeometry && reopened.GeometryReference?.AssetId == jpgAsset.Id && HasSavedBirdState(reopened),
            "reopening restores the verified current-source manual view while keeping old bird geometry unverified");
        await projects.SaveAsync(currentPath, new[] { Group(reopened) }, .5);
        var recovery = await new AutoSaveService(Path.Combine(directory, "Recovery"))
            .SaveAsync(new[] { Group(reopened) }, .5, null, revision: 1);
        check(HasCurrentView((await projects.LoadAsync(currentPath)).Groups.Single().Photos.Single()) &&
              HasCurrentView((await projects.LoadAsync(recovery.Path)).Groups.Single().Photos.Single()),
            "repeated saves and automatic recovery preserve the new manual view without clearing the review notice");

        var legacy = JsonNode.Parse(currentJson)!;
        Record(legacy).AsObject().Remove("ViewReference");
        string legacyPath = Path.Combine(directory, "legacy-unverified-view.json");
        await File.WriteAllTextAsync(legacyPath, legacy.ToJsonString());
        check((await projects.LoadAsync(legacyPath)).Groups.Single().Photos.Single().ViewZoom is null,
            "older unverified-source projects without viewport provenance still discard ambiguous historical views");

        var invalid = JsonNode.Parse(currentJson)!;
        Record(invalid)["ViewReference"]!["DisplayWidth"] = 0;
        string invalidPath = Path.Combine(directory, "invalid-view-reference.json");
        await File.WriteAllTextAsync(invalidPath, invalid.ToJsonString());
        var invalidResult = await projects.LoadAsync(invalidPath);
        check(invalidResult.Errors.Count > 0 && invalidResult.SkippedInvalid == 0 &&
              invalidResult.Groups.Single().Photos.Single() is { ViewZoom: null } retained && HasSavedBirdState(retained),
            "an invalid viewport reference falls back to fit without discarding the photo or historical species");

        File.AppendAllText(raw, "Changed temporary RAW fixture bytes.");
        check((await projects.LoadAsync(currentPath)).Groups.Single().Photos.Single().ViewZoom is null,
            "a changed current-source file length invalidates its saved viewport");
        WriteOpaqueRaw(raw);
        long savedTicks = record["ViewReference"]!["LastWriteTimeUtcTicks"]!.GetValue<long>();
        File.SetLastWriteTimeUtc(raw, new DateTime(savedTicks, DateTimeKind.Utc).AddSeconds(10));
        check((await projects.LoadAsync(currentPath)).Groups.Single().Photos.Single().ViewZoom is null,
            "a changed current-source timestamp also invalidates a same-length saved viewport");
    }

    private static async Task CheckLegacyMigration(string root, ProjectService projects, Action<bool, string> check)
    {
        string jpg = Path.Combine(root, "legacy.jpg");
        string raw = Path.Combine(root, "legacy.NEF");
        WriteJpeg(jpg);
        WriteOpaqueRaw(raw);
        // This is a hand-built v1 document, deliberately not produced by the new serializer.
        var legacy = new
        {
            Schema = "BirdPhotoSelector.Project", Version = 1, GroupingThreshold = .95,
            Groups = new[]
            {
                new
                {
                    Id = "legacy-group", Label = "Legacy", Photos = new[]
                    {
                        new
                        {
                            Id = "legacy-photo", FilePath = jpg, Rating = (int)PhotoRating.Favorite,
                            SelectedBirdIndex = 0, SelectedBirdNumber = 4, NextBirdNumber = 91,
                            ViewZoom = 2.6, ViewCenterX = .42, ViewCenterY = .43,
                            Birds = new[]
                            {
                                new
                                {
                                    Number = 4, IsManual = true, Box = Box(.2), RecognitionStatus = "旧版本结果",
                                    Species = new { ChineseName = "大山雀", ScientificName = "Parus major", Similarity = .412 }
                                }
                            }
                        }
                    }
                }
            }
        };
        string path = Path.Combine(root, "legacy-v1.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(legacy));
        var result = await projects.LoadAsync(path);
        var photo = result.Groups.Single().Photos.Single();
        check(photo.Id == "legacy-photo" && photo.Assets.Count == 1 && photo.Assets[0].Kind == PhotoAssetKind.Jpg &&
              photo.FilePath == jpg && File.Exists(raw),
            "hand-written v1 JSON migrates only its explicit JPG and never associates an adjacent same-stem RAW");
        check(photo.Rating == PhotoRating.Favorite && photo.SelectedBird?.Number == 4 && photo.NextBirdNumber == 91 &&
              photo.SelectedBird?.Species is { Similarity: .412, Confidence: null } &&
              Math.Abs(result.GroupingThreshold - .5) < 1e-12,
            "v1 migration retains legacy species without inventing confidence, high-water numbering and actual grouping strength");
        string upgradedPath = Path.Combine(root, "legacy-upgraded.json");
        await projects.SaveAsync(upgradedPath, result.Groups, result.GroupingThreshold);
        var upgraded = (await projects.LoadAsync(upgradedPath)).Groups.Single().Photos.Single();
        check(upgraded.Assets.Count == 1 && upgraded.Assets[0].Kind == PhotoAssetKind.Jpg &&
              upgraded.SelectedBird?.Species?.Similarity == .412 && upgraded.NextBirdNumber == 91 &&
              (await File.ReadAllBytesAsync(raw)).SequenceEqual(OpaqueRawBytes),
            "saving a migrated v1 record as v2 still leaves its unassociated neighboring RAW untouched");
    }

    private static async Task CheckMissingMembers(string root, ProjectService projects, Action<bool, string> check)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "missing-members")).FullName;
        string jpg = Path.Combine(directory, "missing-jpg.jpg");
        string raw = Path.Combine(directory, "missing-jpg.NEF");
        WriteJpeg(jpg); WriteOpaqueRaw(raw);
        var jpegAsset = new PhotoAsset(jpg, PhotoAssetKind.Jpg, "missing-jpeg-member");
        var rawAsset = new PhotoAsset(raw, PhotoAssetKind.Raw, "surviving-raw-member");
        var photo = Photo(new[] { jpegAsset, rawAsset }, "missing-jpg-photo", jpegAsset.Id, 64, 48);
        string path = Path.Combine(root, "missing-jpg.json");
        await projects.SaveAsync(path, new[] { Group(photo) }, .5);
        File.Delete(jpg); // Only the generated JPEG owned by this test.
        var loaded = await projects.LoadAsync(path);
        var retained = loaded.Groups.Single().Photos.Single();
        check(loaded.MissingAssetCount == 1 && loaded.SkippedMissing == 0 && retained.Id == photo.Id &&
              retained.ActivePreviewAsset?.Id == rawAsset.Id && retained.PreferredPreviewAssetId == jpegAsset.Id &&
              retained.Assets.Count == 2,
            "a missing JPG retains the logical photo, RAW fallback and missing member audit without treating it as a missing photo");
        check(HasSavedBirdState(retained) && retained.GeometryStatus == PhotoGeometryStatus.NeedsReview &&
              !retained.CanUseBirdGeometry && retained.GeometryReference?.AssetId == jpegAsset.Id &&
              retained.GeometryReference?.FilePath == jpg,
            "RAW fallback retains species and old JPG geometry provenance but explicitly makes old boxes require review");
        check(retained.Thumbnail is null && retained.PixelWidth == 0 && retained.PixelHeight == 0 &&
              retained.ViewZoom is null && retained.ViewCenterX is null && retained.ViewCenterY is null,
            "switching from a missing JPG to RAW does not reuse the old JPG pixels, thumbnail, zoom or pan");
        await projects.SaveAsync(path, loaded.Groups, loaded.GroupingThreshold);
        var restoredAgain = (await projects.LoadAsync(path)).Groups.Single().Photos.Single();
        var autosave = new AutoSaveService(Path.Combine(root, "MissingRecovery"));
        var recovery = await autosave.SaveAsync(new[] { Group(restoredAgain) }, .5, null, revision: 73);
        var recovered = (await projects.LoadAsync(recovery.Path)).Groups.Single().Photos.Single();
        check(restoredAgain.GeometryStatus == PhotoGeometryStatus.NeedsReview && recovered.GeometryStatus == PhotoGeometryStatus.NeedsReview &&
              recovered.Assets.Count == 2 && recovered.ActivePreviewAsset?.Id == rawAsset.Id &&
              recovered.GeometryReference?.AssetId == jpegAsset.Id && HasSavedBirdState(recovered),
            "resave and automatic recovery never promote unverified fallback geometry or lose its species/numbering history");

        string survivingJpg = Path.Combine(directory, "missing-raw.jpg");
        string missingRaw = Path.Combine(directory, "missing-raw.ARW");
        WriteJpeg(survivingJpg); WriteOpaqueRaw(missingRaw);
        var survivingAsset = new PhotoAsset(survivingJpg, PhotoAssetKind.Jpg, "surviving-jpeg-member");
        var removedAsset = new PhotoAsset(missingRaw, PhotoAssetKind.Raw, "missing-raw-member");
        var jpegPhoto = Photo(new[] { survivingAsset, removedAsset }, "missing-raw-photo", survivingAsset.Id, 64, 48);
        string missingRawPath = Path.Combine(root, "missing-raw.json");
        await projects.SaveAsync(missingRawPath, new[] { Group(jpegPhoto) }, .5);
        File.Delete(missingRaw);
        var jpegResult = await projects.LoadAsync(missingRawPath);
        var jpegRetained = jpegResult.Groups.Single().Photos.Single();
        check(jpegResult.MissingAssetCount == 1 && jpegResult.SkippedMissing == 0 &&
              jpegRetained.ActivePreviewAsset?.Id == survivingAsset.Id && jpegRetained.GeometryStatus == PhotoGeometryStatus.Current &&
              jpegRetained.CanUseBirdGeometry && HasSavedBirdState(jpegRetained),
            "a missing RAW leaves the existing JPG preview and its verified geometry/species intact");
        File.Delete(survivingJpg);
        var allMissing = await projects.LoadAsync(missingRawPath);
        check(allMissing.Groups.Count == 0 && allMissing.SkippedMissing == 1 && allMissing.MissingAssetCount == 2,
            "only a logical photo with every member missing is counted as a skipped missing photo");
    }

    private static async Task CheckCrossDirectoryIdentity(string root, ProjectService projects, Action<bool, string> check)
    {
        var photos = new List<PhotoItem>();
        foreach (string folder in new[] { "A", "B" })
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, "same-names", folder)).FullName;
            string jpg = Path.Combine(directory, "same.jpg");
            string raw = Path.Combine(directory, "same.NEF");
            WriteJpeg(jpg); WriteOpaqueRaw(raw);
            var jpegAsset = new PhotoAsset(jpg, PhotoAssetKind.Jpg, folder + "-jpg");
            var rawAsset = new PhotoAsset(raw, PhotoAssetKind.Raw, folder + "-raw");
            photos.Add(Photo(new[] { jpegAsset, rawAsset }, "same-name-photo-" + folder, jpegAsset.Id, 64, 48));
        }
        string path = Path.Combine(root, "same-names.json");
        await projects.SaveAsync(path, new[] { Group(photos.ToArray()) }, .5);
        var loaded = await projects.LoadAsync(path);
        var restored = loaded.Groups.Single().Photos;
        check(restored.Count == 2 && restored.SelectMany(photo => photo.Assets).Count() == 4 && loaded.SkippedDuplicate == 0 &&
              loaded.DuplicateAssetCount == 0 && restored.Select(photo => photo.Id).Distinct().Count() == 2,
            "v2 retains both same-named logical photos and all four distinct full paths across directories");
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        document["Groups"]![0]!["Photos"]![1]!["Id"] = photos[0].Id;
        await File.WriteAllTextAsync(path, document.ToJsonString());
        var duplicate = await projects.LoadAsync(path);
        check(duplicate.Groups.SelectMany(group => group.Photos).Count() == 1 && duplicate.SkippedDuplicate > 0,
            "a duplicate logical photo ID is reported rather than silently merging its distinct original files");
    }

    private static JsonNode Record(JsonNode document) => document["Groups"]![0]!["Photos"]![0]!;
    private static bool AllMemberIdsUnique(ProjectLoadResult result)
    {
        var ids = result.Groups.SelectMany(group => group.Photos).SelectMany(photo => photo.Assets).Select(asset => asset.Id).ToArray();
        return ids.Distinct(StringComparer.Ordinal).Count() == ids.Length;
    }
    private static bool AllMemberPathsUnique(ProjectLoadResult result)
    {
        var paths = result.Groups.SelectMany(group => group.Photos).SelectMany(photo => photo.Assets).Select(asset => asset.CanonicalPath).ToArray();
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == paths.Length;
    }
    private static PhotoItem Photo(PhotoAsset[] assets, string id, string preferredId, int width, int height)
    {
        var photo = new PhotoItem { Id = id, Rating = PhotoRating.Favorite };
        photo.SetAssets(assets, preferredId, preferredId);
        photo.SetBirds(new[]
        {
            new DetectedBird { Number = 5, Box = Box(.1), Species = new("大山雀", "Parus major", .412), RecognitionStatus = "旧结果" },
            new DetectedBird { Number = 9, IsManual = true, Box = Box(.55),
                Species = new("麻雀", "Passer montanus", .63, Confidence: .84), RecognitionStatus = "手动旧结果" }
        }, selectedIndex: 1, nextBirdNumber: 77);
        photo.PixelWidth = width; photo.PixelHeight = height;
        photo.Metadata = new PhotoMetadata { Camera = "Saved camera" };
        photo.ViewZoom = 2.6; photo.ViewCenterX = .42; photo.ViewCenterY = .43;
        var reference = assets.Single(asset => asset.Id == preferredId);
        photo.SetGeometryReference(new PhotoGeometryReference(reference.Id, reference.FilePath, width, height), PhotoGeometryStatus.Current);
        return photo;
    }
    private static bool HasSavedBirdState(PhotoItem photo) => photo.Rating == PhotoRating.Favorite &&
        photo.NextBirdNumber == 77 && photo.SelectedBird?.Number == 9 && photo.Birds.Count == 2 &&
        photo.Birds[0].Number == 5 && photo.Birds[0].Species is { ScientificName: "Parus major", Similarity: .412, Confidence: null } &&
        photo.Birds[1] is { Number: 9, IsManual: true, Species: { ScientificName: "Passer montanus", Similarity: .63, Confidence: .84 } };
    private static PhotoGroup Group(params PhotoItem[] photos)
    {
        var group = new PhotoGroup("Project v2 fixtures");
        foreach (var photo in photos) group.Photos.Add(photo);
        return group;
    }
    private static BirdBox Box(double x) => new() { X = x, Y = .2, Width = .2, Height = .3, Confidence = .9 };
    private static readonly byte[] OpaqueRawBytes = { 0x52, 0x41, 0x57, 0x2d, 0x70, 0x65, 0x72, 0x73, 0x69, 0x73, 0x74 };
    private static void WriteOpaqueRaw(string path) => File.WriteAllBytes(path, OpaqueRawBytes);
    private static void WriteJpeg(string path)
    {
        const int width = 64, height = 48;
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat((byte)100, width * height * 3).ToArray(), width * 3);
        image.Freeze();
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
