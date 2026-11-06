namespace BirdPhotoSelector;

/// <summary>操作系统提供的文件身份。设备和节点共同区分实体文件，硬链接应返回相同身份。</summary>
/// <param name="Device">设备或卷标识。</param>
/// <param name="Node">设备中的文件标识。</param>
public readonly record struct FileIdentity(ulong Device, ulong Node);

/// <summary>导出所需的最小系统能力，业务层保留复制、哈希校验和来源保护规则。</summary>
public interface IExportFilePlatform
{
    /// <summary>从已经打开的文件获取实体身份；无法确认时返回 null，业务必须保守禁止覆盖，不能用路径冒充身份。</summary>
    FileIdentity? TryGetIdentity(FileStream stream);
    /// <summary>将指定原件送入回收站，无可用回收站时按平台规则删除。成功返回时原路径必须已不存在。</summary>
    void RecycleOrDelete(string path);
}
