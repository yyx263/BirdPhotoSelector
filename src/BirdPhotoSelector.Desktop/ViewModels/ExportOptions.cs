namespace BirdPhotoSelector.Desktop.ViewModels;

/// <summary>导出窗口的四个用户动作；这是 UI 选择，不改写共享文件操作规则。</summary>
public enum ExportChoice { KeepAndFavorite, Favorite, Rejected, DeleteRejected }
/// <summary>确认后的界面选项。分类和移除原件默认关闭，目标必须为用户选择的现有目录。</summary>
public sealed record ExportOptions(ExportChoice Choice, PhotoFileScope Scope = PhotoFileScope.All,
    string Destination = "", bool RemoveOriginal = false, bool GroupBySpecies = false)
{
    /// <summary>删除模式不使用复制目录或分类选项。</summary>
    public bool DeleteOnly => Choice == ExportChoice.DeleteRejected;
    /// <summary>仅按明确的评级选择逻辑照片，文件格式由共享计划筛选。</summary>
    public bool Includes(PhotoItem photo) => Choice switch
    { ExportChoice.KeepAndFavorite => photo.Rating is PhotoRating.Keep or PhotoRating.Favorite, ExportChoice.Favorite => photo.Rating == PhotoRating.Favorite, _ => photo.Rating == PhotoRating.Reject };
    /// <summary>供用户核对的格式标签。</summary>
    public string ScopeLabel => Scope switch { PhotoFileScope.JpgOnly => "仅 JPG", PhotoFileScope.RawOnly => "仅 RAW", _ => "JPG + RAW" };
}
