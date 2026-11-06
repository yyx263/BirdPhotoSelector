using System.Windows.Input;

namespace BirdPhotoSelector;

/// <summary>迁移期的 WPF 键盘适配。照片选择、导航、评级和计数均由无 UI 依赖的会话基类维护。</summary>
public sealed class MainViewModel : PhotoSelectionSession
{
    /// <summary>把 WPF 方向键/WASD/Page Up/Down 映射为平台无关导航操作。</summary>
    public bool HandleNavigationKey(Key key)
    {
        switch (key)
        {
            case Key.A: case Key.W: case Key.Left: case Key.Up: NavigatePhoto(-1); return true;
            case Key.D: case Key.S: case Key.Right: case Key.Down: NavigatePhoto(1); return true;
            case Key.PageUp: NavigateGroup(-1); return true;
            case Key.PageDown: NavigateGroup(1); return true;
            default: return false;
        }
    }

    /// <summary>把主键盘与数字键盘 1..4 映射为既定评级，是否允许修改由会话判断。</summary>
    public bool HandleRatingKey(Key key)
    {
        switch (key)
        {
            case Key.D1: case Key.NumPad1: CurrentRating = PhotoRating.Unmarked; return true;
            case Key.D2: case Key.NumPad2: CurrentRating = PhotoRating.Reject; return true;
            case Key.D3: case Key.NumPad3: CurrentRating = PhotoRating.Keep; return true;
            case Key.D4: case Key.NumPad4: CurrentRating = PhotoRating.Favorite; return true;
            default: return false;
        }
    }

}
