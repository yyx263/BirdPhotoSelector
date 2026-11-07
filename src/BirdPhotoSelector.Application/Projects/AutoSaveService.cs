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

    /// <summary>使用显式工程服务、恢复目录和可替换时钟创建自动保存会话，不依赖 UI 计时器。</summary>
    public AutoSaveService(ProjectService projects, string? recoveryDirectory = null, Func<DateTimeOffset>? clock = null)
    {
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        RecoveryDirectory = Path.GetFullPath(recoveryDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BirdPhotoSelector", "Autosaves"));
        StartSession();
    }

    /// <summary>开始新的恢复会话，递增代号并创建独立 JSON 恢复路径，旧异步保存不能更新新会话状态。</summary>
    public void StartSession()
    {
        SessionGeneration++;
        LastSavedRevision = -1;
        _nextSaveAt = _clock() + SaveInterval;
        RecoveryPath = Path.Combine(RecoveryDirectory, $"挑选-{_clock().ToLocalTime():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
    }

    /// <summary>人工保存后推迟下一次自动保存并记录已保存修订。</summary>
    public void NoteManualSave(long revision)
    {
        LastSavedRevision = revision;
        _nextSaveAt = _clock() + SaveInterval;
    }

    /// <remarks>The UI serializes this with manual saves and snapshots groups on its own thread.</remarks>
    /// <summary>尝试写原工程，路径不可写时保存独立恢复文件；使用同一写闸门并按会话代号提交状态。</summary>
    public async Task<AutoSaveResult> SaveAsync(IEnumerable<PhotoGroup> groups, double threshold,
        string? projectPath, long revision, CancellationToken cancellationToken = default,
        IReadOnlyList<SpeciesIdentity>? recentSpecies = null)
    {
        await _writes.WaitAsync(cancellationToken);
        try
        {
            long generation = SessionGeneration;
            string recoveryPath = RecoveryPath;
            var snapshotGroups = groups.ToArray();
            var snapshotRecent = recentSpecies?.ToArray();
            string? primaryError = null;
            string savedPath = recoveryPath;
            bool isRecovery = true;
            if (!string.IsNullOrWhiteSpace(projectPath))
            {
                try
                {
                    await _projects.SaveAsync(projectPath, snapshotGroups, threshold, cancellationToken, snapshotRecent);
                    savedPath = Path.GetFullPath(projectPath);
                    isRecovery = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                { primaryError = ex.Message; }
            }
            if (isRecovery)
            {
                Directory.CreateDirectory(RecoveryDirectory);
                await _projects.SaveAsync(recoveryPath, snapshotGroups, threshold, cancellationToken, snapshotRecent);
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

/// <summary>报告真实保存路径、是否恢复副本、原路径错误和所属会话代号。</summary>
public sealed record AutoSaveResult(string Path, bool IsRecovery, string? PrimaryError, long SessionGeneration);
