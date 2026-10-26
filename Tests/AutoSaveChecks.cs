using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BirdPhotoSelector;

/// <summary>Uses a fake clock and an isolated recovery directory; never waits five minutes or writes AppData.</summary>
internal static class AutoSaveChecks
{
    public static async Task<int> RunAsync()
    {
        int count = 0;
        void Check(bool condition, string description)
        {
            if (!condition) throw new InvalidOperationException(description);
            count++;
            Console.WriteLine("  OK AUTOSAVE " + description);
        }

        string testRoot = Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.AutoSaveChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        try
        {
            DateTimeOffset now = new(2026, 10, 18, 9, 0, 0, TimeSpan.FromHours(8));
            string recoveryDirectory = Path.Combine(testRoot, "Recovery");
            var autosave = new AutoSaveService(recoveryDirectory, () => now);
            Check(autosave.RecoveryDirectory == Path.GetFullPath(recoveryDirectory) && !Directory.Exists(recoveryDirectory),
                "constructing an unnamed session does not create a folder or write outside its injected recovery location");
            Check(!autosave.IsDue && autosave.LastSavedRevision == -1, "new sessions start with a fresh five-minute interval");
            now += TimeSpan.FromMinutes(5) - TimeSpan.FromMilliseconds(1);
            Check(!autosave.IsDue, "autosave is not due before five minutes");
            now += TimeSpan.FromMilliseconds(1);
            Check(autosave.IsDue, "autosave becomes due exactly at five minutes without a real-time wait");

            string sourcePath = Path.Combine(testRoot, "fixture.jpg");
            WriteJpeg(sourcePath);
            var photo = new PhotoItem
            {
                FilePath = sourcePath, Rating = PhotoRating.Favorite,
                Bird = new BirdBox { X = .2, Y = .2, Width = .3, Height = .4, Confidence = .9 },
                ViewZoom = 2.5, ViewCenterX = .4, ViewCenterY = .45
            };
            var group = new PhotoGroup("自动保存测试");
            group.Photos.Add(photo);
            var projects = new ProjectService();
            string firstRecoveryPath = autosave.RecoveryPath;
            long firstGeneration = autosave.SessionGeneration;
            var unnamed = await autosave.SaveAsync(new[] { group }, .81, null, revision: 7);
            Check(unnamed.IsRecovery && unnamed.PrimaryError is null && unnamed.Path == firstRecoveryPath && File.Exists(unnamed.Path),
                "an unnamed project produces a standalone JSON recovery file");
            Check(unnamed.SessionGeneration == firstGeneration && autosave.LastSavedRevision == 7 && !autosave.IsDue,
                "a successful autosave records its revision and starts the next five-minute interval");
            var restored = await projects.LoadAsync(unnamed.Path);
            var recoveredPhoto = restored.Groups.Single().Photos.Single();
            Check(restored.GroupingThreshold == .81 && recoveredPhoto.FilePath == sourcePath &&
                  recoveredPhoto.Rating == PhotoRating.Favorite && recoveredPhoto.Bird?.Width == .3 &&
                  recoveredPhoto.ViewZoom == 2.5 && recoveredPhoto.ViewCenterY == .45,
                "recovery JSON opens through the production loader with paths, rating, bird box, view and threshold intact");

            photo.Rating = PhotoRating.Reject;
            now += TimeSpan.FromMinutes(5);
            var replacement = await autosave.SaveAsync(new[] { group }, .77, null, revision: 8);
            var replaced = await projects.LoadAsync(replacement.Path);
            Check(replacement.Path == firstRecoveryPath && replaced.Groups.Single().Photos.Single().Rating == PhotoRating.Reject &&
                  replaced.GroupingThreshold == .77 && autosave.LastSavedRevision == 8,
                "repeated autosaves atomically replace the same session file with the latest revision");
            Check(!Directory.EnumerateFiles(recoveryDirectory, "*.tmp", SearchOption.AllDirectories).Any(),
                "successful atomic autosaves leave no temporary files");

            string namedPath = Path.Combine(testRoot, "named.json");
            string namedRecovery = Path.Combine(testRoot, "NamedRecovery");
            var named = new AutoSaveService(namedRecovery, () => now);
            photo.Rating = PhotoRating.Keep;
            var namedResult = await named.SaveAsync(new[] { group }, .85, namedPath, revision: 11);
            Check(!namedResult.IsRecovery && namedResult.PrimaryError is null && namedResult.Path == Path.GetFullPath(namedPath) &&
                  File.Exists(namedPath) && !Directory.Exists(namedRecovery),
                "named projects save to their selected JSON without creating unnecessary recovery files");
            photo.Rating = PhotoRating.Favorite;
            await named.SaveAsync(new[] { group }, .86, namedPath, revision: 12);
            Check((await projects.LoadAsync(namedPath)).Groups.Single().Photos.Single().Rating == PhotoRating.Favorite &&
                  named.LastSavedRevision == 12,
                "an existing named JSON is atomically updated to its latest revision");

            string invalidDirectory = Path.Combine(testRoot, "MissingPrimaryDirectory");
            string invalidPath = Path.Combine(invalidDirectory, "unwritable.json");
            var fallback = await named.SaveAsync(new[] { group }, .88, invalidPath, revision: 13);
            Check(fallback.IsRecovery && !string.IsNullOrWhiteSpace(fallback.PrimaryError) &&
                  fallback.Path == named.RecoveryPath && File.Exists(fallback.Path) && !Directory.Exists(invalidDirectory),
                "an unavailable primary directory falls back to recovery without creating or changing the requested primary location");
            Check((await projects.LoadAsync(fallback.Path)).GroupingThreshold == .88 && named.LastSavedRevision == 13,
                "fallback recovery remains valid project JSON and records the completed revision");

            now += TimeSpan.FromMinutes(5);
            Check(named.IsDue, "the injected clock can advance the named session to its next due time");
            named.NoteManualSave(20);
            Check(!named.IsDue && named.LastSavedRevision == 20, "manual saves record their revision and reset the autosave clock");
            now += TimeSpan.FromMinutes(4);
            Check(!named.IsDue, "manual-save reset suppresses the old autosave deadline");
            now += TimeSpan.FromMinutes(1);
            Check(named.IsDue, "autosave resumes five minutes after the manual save");

            long oldGeneration = named.SessionGeneration;
            string oldRecoveryPath = named.RecoveryPath;
            var pendingOldSave = named.SaveAsync(new[] { group }, .89, null, revision: 21);
            named.StartSession();
            long newGeneration = named.SessionGeneration;
            string newRecoveryPath = named.RecoveryPath;
            var oldResult = await pendingOldSave;
            Check(newGeneration == oldGeneration + 1 && newRecoveryPath != oldRecoveryPath &&
                  oldResult.SessionGeneration == oldGeneration && oldResult.Path == oldRecoveryPath,
                "a new session gets a unique recovery identity even while the previous session's save completes");
            Check(named.LastSavedRevision == -1 && named.RecoveryPath == newRecoveryPath && !named.IsDue,
                "completion from an older session cannot overwrite the new session's revision or deadline");
            var newResult = await named.SaveAsync(new[] { group }, .9, null, revision: 1);
            Check(newResult.Path == newRecoveryPath && newResult.SessionGeneration == newGeneration &&
                  named.LastSavedRevision == 1 && File.Exists(oldRecoveryPath) && File.Exists(newRecoveryPath),
                "new and old session recovery files remain independently recoverable");
            var emptyResult = await named.SaveAsync(Array.Empty<PhotoGroup>(), .9, null, revision: 2);
            Check((await projects.LoadAsync(emptyResult.Path)).Groups.Count == 0 && named.LastSavedRevision == 2,
                "removing every photo persists an empty recovery session instead of resurrecting an older list");

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            string canceledRecovery = Path.Combine(testRoot, "CanceledRecovery");
            var canceledService = new AutoSaveService(canceledRecovery, () => now);
            bool cancellationObserved = false;
            try { await canceledService.SaveAsync(new[] { group }, .8, null, 99, canceled.Token); }
            catch (OperationCanceledException) { cancellationObserved = true; }
            Check(cancellationObserved && !Directory.Exists(canceledRecovery) && canceledService.LastSavedRevision == -1,
                "a canceled autosave creates no recovery file and does not claim a saved revision");
            return count;
        }
        finally
        {
            string resolved = Path.GetFullPath(testRoot);
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "BirdPhotoSelector.AutoSaveChecks")) + Path.DirectorySeparatorChar;
            if (resolved.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, recursive: true);
        }
    }

    private static void WriteJpeg(string path)
    {
        var bitmap = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Rgb24, null,
            Enumerable.Repeat((byte)100, 8 * 6 * 3).ToArray(), 8 * 3);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }
}
