using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace BirdPhotoSelector;

/// <summary>Maps the current enum rating to a mutually exclusive checked state.</summary>
public sealed class RatingEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is PhotoRating current && TryRating(parameter, out var expected) && current == expected;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true && TryRating(parameter, out var rating) ? rating : Binding.DoNothing;

    private static bool TryRating(object parameter, out PhotoRating rating) =>
        Enum.TryParse(parameter?.ToString(), out rating) && Enum.IsDefined(rating);
}

public sealed class RatingGlyphConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is PhotoRating r ? r switch { PhotoRating.Reject => "×", PhotoRating.Keep => "✓", PhotoRating.Favorite => "★", _ => "·" } : "·";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public sealed class RatingColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => new SolidColorBrush(value is PhotoRating r ? r switch { PhotoRating.Reject => Color.FromRgb(224,135,135), PhotoRating.Keep => Color.FromRgb(157,223,196), PhotoRating.Favorite => Color.FromRgb(244,203,113), _ => Color.FromRgb(140,155,169) } : Colors.Gray);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
