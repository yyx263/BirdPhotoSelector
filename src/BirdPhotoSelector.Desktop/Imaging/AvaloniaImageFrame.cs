using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace BirdPhotoSelector.Desktop.Imaging;

/// <summary>
/// 只在桌面边界持有一份 BGRA 位图；入缓存前转换，避免长期同时保留托管 RGB 和显示像素。
/// 共享帧不能主动 Dispose，否则会破坏仍在显示的引用；Avalonia 原生所有者随最后引用最终释放。
/// </summary>
public sealed class AvaloniaImageFrame : ImageFrame
{
    private readonly Bitmap _bitmap;
    /// <summary>只供显示，不向调用方公开可写锁或原生像素指针。</summary>
    public IImage Image => _bitmap;
    /// <inheritdoc/>
    public override int PixelWidth => _bitmap.PixelSize.Width;
    /// <inheritdoc/>
    public override int PixelHeight => _bitmap.PixelSize.Height;
    /// <inheritdoc/>
    public override long RetainedBytes => checked((long)PixelWidth * PixelHeight * 4);
    /// <summary>只接收本类已完成复制的不可变位图，所有权跟随共享帧。</summary>
    private AvaloniaImageFrame(Bitmap bitmap) => _bitmap = bitmap;
    /// <summary>一次转换并复制到显示后端；返回前释放临时缓冲，后续缓存命中直接使用 Image。</summary>
    public static AvaloniaImageFrame Create(ImageFrame frame, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(frame); token.ThrowIfCancellationRequested();
        if (frame is AvaloniaImageFrame display) return display;
        byte[] bgra;
        if (frame is Bgra32ImageFrame decoded) bgra = decoded.ReadOnlyPixels;
        else
        {
            byte[] rgb = frame is Rgb24ImageFrame managed ? managed.ReadOnlyPixels : frame.CopyRgb24(token);
            bgra = GC.AllocateUninitializedArray<byte>(checked(frame.PixelWidth * frame.PixelHeight * 4));
            ExpandRgb(rgb, bgra, token);
        }
        var pinned = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            return new(new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, pinned.AddrOfPinnedObject(),
                new PixelSize(frame.PixelWidth, frame.PixelHeight), new Vector(96, 96), checked(frame.PixelWidth * 4)));
        }
        finally { pinned.Free(); }
    }
    /// <summary>RAW/托管 RGB 路径每次并行重排四个像素，JIT 在 x64/ARM64 选择本机 SIMD；末尾始终检查边界。</summary>
    private static void ExpandRgb(byte[] rgb, byte[] bgra, CancellationToken token)
    {
        if ((long)rgb.Length * 4 != (long)bgra.Length * 3) throw new InvalidDataException("RGB 显示数据长度不符。");
        int read = 0, write = 0;
        if (Vector128.IsHardwareAccelerated)
        {
            var indices = Vector128.Create((byte)2, 1, 0, 255, 5, 4, 3, 255, 8, 7, 6, 255, 11, 10, 9, 255);
            var alpha = Vector128.Create(0xff000000u).AsByte();
            ref byte input = ref MemoryMarshal.GetArrayDataReference(rgb); ref byte output = ref MemoryMarshal.GetArrayDataReference(bgra);
            while (read <= rgb.Length - 16)
            {
                if ((write & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
                var values = Vector128.Shuffle(Vector128.LoadUnsafe(ref input, (nuint)read), indices) | alpha;
                values.StoreUnsafe(ref output, (nuint)write); read += 12; write += 16;
            }
        }
        for (; read < rgb.Length; read += 3, write += 4)
        {
            if ((write & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
            bgra[write] = rgb[read + 2]; bgra[write + 1] = rgb[read + 1]; bgra[write + 2] = rgb[read]; bgra[write + 3] = 255;
        }
        token.ThrowIfCancellationRequested();
    }
    /// <summary>按区域读取紧密 RGB24，临时 BGRA 副本仅覆盖请求区域，不复制整幅照片。</summary>
    public override byte[] CopyRgb24(BirdPhotoSelector.Imaging.PixelRect region, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); region.Validate(PixelWidth, PixelHeight);
        byte[] bgra = new byte[checked(region.Width * region.Height * 4)];
        var pinned = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try { _bitmap.CopyPixels(new Avalonia.PixelRect(region.X, region.Y, region.Width, region.Height), pinned.AddrOfPinnedObject(), bgra.Length, checked(region.Width * 4)); }
        finally { pinned.Free(); }
        var rgb = new byte[checked(region.Width * region.Height * 3)];
        for (int i = 0; i < region.Width * region.Height; i++)
        { cancellationToken.ThrowIfCancellationRequested(); rgb[i * 3] = bgra[i * 4 + 2]; rgb[i * 3 + 1] = bgra[i * 4 + 1]; rgb[i * 3 + 2] = bgra[i * 4]; }
        return rgb;
    }
    /// <inheritdoc/>
    public override ImageFrame Resize(int width, int height, CancellationToken cancellationToken = default) => ImageResampling.Resize(this, width, height, cancellationToken);
}

/// <summary>仅为小缩略图创建弱键显示缓存；整图由 PreviewImageCache 的单份显示帧管理。</summary>
public sealed class ThumbnailConverter : IValueConverter
{
    private static readonly ConditionalWeakTable<ImageFrame, AvaloniaImageFrame> Frames = new();
    /// <summary>弱键不会让已经移出列表的缩略图继续存活。</summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is ImageFrame frame ? Frames.GetValue(frame, static f => AvaloniaImageFrame.Create(f)).Image : null;
    /// <summary>显示转换为单向，不允许修改领域缩略图。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>评级图标和颜色只在 UI 选择；组优先级继续复用 GroupCard。</summary>
public sealed class RatingConverter : IValueConverter
{
    /// <summary>参数 color 返回颜色，其余返回角标字符。</summary>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rating = value is PhotoRating r ? r : PhotoRating.Unmarked;
        if (parameter?.ToString() == "size") return rating == PhotoRating.Unmarked ? 11d : 17d;
        if (parameter?.ToString() == "color") return Brush.Parse(rating switch { PhotoRating.Reject => "#E08787", PhotoRating.Keep => "#9DDFC4", PhotoRating.Favorite => "#F4CB71", _ => "#B5C2CB" });
        return rating switch { PhotoRating.Reject => "×", PhotoRating.Keep => "✓", PhotoRating.Favorite => "★", _ => "●" };
    }
    /// <summary>评级数据只通过会话命令修改。</summary>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
