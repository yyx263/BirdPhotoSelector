using System.Runtime.CompilerServices;

// 仅解码和显示边界可接管刚分配的独立 RGB 数组，避免大图反复复制；公共不可变约定不变。
[assembly: InternalsVisibleTo("BirdPhotoSelector.Imaging.Native")]
[assembly: InternalsVisibleTo("BirdPhotoSelector.Imaging.Portable")]
[assembly: InternalsVisibleTo("BirdPhotoSelector.Imaging.Windows")]
[assembly: InternalsVisibleTo("BirdPhotoSelector.Desktop")]
