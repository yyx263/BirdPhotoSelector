namespace BirdPhotoSelector;

/// <summary>
/// Gives the strict end of the background score more room on the slider.
/// The midpoint now requires the same similarity as the former 95% setting.
/// </summary>
public static class GroupingThreshold
{
    public const double Default = .5;
    public const int ScaleVersion = 2;
    private static readonly double Exponent = Math.Log(.05) / Math.Log(.5);

    public static double ToActual(double displayed)
    {
        Validate(displayed, nameof(displayed));
        return 1 - Math.Pow(1 - displayed, Exponent);
    }

    public static double FromActual(double actual)
    {
        Validate(actual, nameof(actual));
        return 1 - Math.Pow(1 - actual, 1 / Exponent);
    }

    private static void Validate(double value, string parameter)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(parameter, "相似度阈值应在 0 到 1 之间。");
    }
}
