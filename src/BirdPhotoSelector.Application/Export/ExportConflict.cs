namespace BirdPhotoSelector;

/// <summary>同名不同内容文件的用户选择；取消结束当前批次。</summary>
public enum ExportConflictAction { Cancel, Overwrite, Rename }

/// <summary>传给宿主确认的纯数据，不包含对话框引用；无法确认安全身份时禁止覆盖。</summary>
public sealed record ExportConflict(string SourcePath, string TargetPath, long SourceLength, long TargetLength,
    bool CanOverwrite = true, string? Explanation = null);

/// <summary>宿主返回的冲突处理策略；记住选项只适用于本次导出批次。</summary>
public sealed record ExportConflictDecision(ExportConflictAction Action, bool ApplyToRemaining = false);
