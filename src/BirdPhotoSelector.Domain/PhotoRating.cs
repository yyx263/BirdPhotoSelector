using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Globalization;
using System.Runtime.CompilerServices;
using BirdPhotoSelector.Imaging;

namespace BirdPhotoSelector;

/// <summary>照片四级标记；枚举顺序对应快捷键 1..4，序列化数值沿用旧工程。</summary>
public enum PhotoRating { Unmarked, Reject, Keep, Favorite }
