using System.IO;

namespace BirdPhotoSelector;

/// <summary>Periodic project persistence with an independently recoverable unnamed session.</summary>
public sealed class AutoSaveService
{
    public static readonly TimeSpan SaveInterval = TimeSpan.FromMinutes(5);
    private readonly ProjectService _projects;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _writes = new(1, 1);
    private DateTimeOffset _nextSaveAt;
    public string RecoveryDirectory { get; }
    public string RecoveryPath { get; private set; } = "";
    public long SessionGeneration { get; private set; }
    public long LastSavedRevision { get; private set; } = -1;
    public bool IsDue => _clock() >= _nextSaveAt;

    public AutoSaveService(string? recoveryDirectory = null, Func<DateTimeOffset>? clock = null,
        ProjectService? projects = null)
    {
        _projects = projects ?? new ProjectService();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        RecoveryDirectory = Path.GetFullPath(recoveryDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirdPhotoSelector", "Autosaves"));
        StartSession();
    }

    public void StartSession()
    {
        SessionGeneration++;
        LastSavedRevision = -1;
        _nextSaveAt = _clock() + SaveInterval;
        RecoveryPath = Path.Combine(RecoveryDirectory, $"挑选-{_clock().ToLocalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
    }

    public void NoteManualSave(long revision)
    {
        LastSavedRevision = revision;
        _nextSaveAt = _clock() + SaveInterval;
    }

    /// <summary>The UI serializes this with manual saves and snapshots groups on its own thread.</summary>
    public async Task<AutoSaveResult> SaveAsync(IEnumerable<PhotoGroup> groups, double threshold,
        string? projectPath, long revision, CancellationToken cancellationToken = default)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            long generation = SessionGeneration;
            string recoveryPath = RecoveryPath;
            var snapshotGroups = groups.ToArray();
            string? primaryError = null;
            string savedPath = recoveryPath;
            bool isRecovery = true;
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                try
                {
                    await _projects.SaveAsync(projectPath, snapshotGroups, threshold, cancellationToken);
                    savedPath = Path.GetFullPath(projectPath);
                    isRecovery = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { primaryError = ex.Message; }
            }
            if (isRecovery)
            {
                Directory.CreateDirectory(RecoveryDirectory);
                await _projects.SaveAsync(recoveryPath, snapshotGroups, threshold, cancellationToken);
            }
            if (generation == SessionGeneration)
            {
                LastSavedRevision = revision;
                _nextSaveAt = _clock() + SaveInterval;
            }
            return new(savedPath, isRecovery, primaryError, generation);
        }
        finally { _writes.Release(); }
    }
}

public sealed record AutoSaveResult(string Path, bool IsRecovery, string? PrimaryError, long SessionGeneration);
