namespace BirdPhotoSelector;

/// <summary>操作系统提供的文件身份。设备和节点共同区分实体文件，硬链接应返回相同身份。</summary>
/// <param name="Device">设备或卷标识。</param>
/// <param name="Node">设备中的文件标识。</param>
public readonly record struct FileIdentity(ulong Device, ulong Node);

/// <summary>只读文件实体身份能力，供导入去重及导出来源保护；不授予文件移除能力。</summary>
public interface IFileIdentityProvider
{
    /// <summary>只借用已打开的句柄；未知时返回 null，不以路径文本冒充实体身份。</summary>
    FileIdentity? TryGetIdentity(FileStream stream);
}

/// <summary>导出在只读实体身份能力之外，额外获得明确授权后的系统移除能力。</summary>
public interface IExportFilePlatform : IFileIdentityProvider
{
    /// <summary>将指定原件送入回收站，无可用回收站时按平台规则删除。成功返回时原路径必须已不存在。</summary>
    void RecycleOrDelete(string path);
}
