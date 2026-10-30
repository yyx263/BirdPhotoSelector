using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BirdPhotoSelector.Controls;

/// <summary>
/// Flows fields in source order and shares each row evenly. Fields wider than an equal
/// share keep their natural width while the other columns share the remaining space.
/// At MaxRows, oversized content shares the last row and may use its own text trimming.
/// </summary>
public sealed class BalancedMetadataPanel : Panel
{
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(BalancedMetadataPanel),
        new FrameworkPropertyMetadata(16d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double number && double.IsFinite(number) && number >= 0);

    public static readonly DependencyProperty RowHeightProperty = DependencyProperty.Register(
        nameof(RowHeight), typeof(double), typeof(BalancedMetadataPanel),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is double number && double.IsFinite(number) && number >= 0);

    public static readonly DependencyProperty MaxRowsProperty = DependencyProperty.Register(
        nameof(MaxRows), typeof(int), typeof(BalancedMetadataPanel),
        new FrameworkPropertyMetadata(int.MaxValue, FrameworkPropertyMetadataOptions.AffectsMeasure),
        value => value is int number && number > 0);

    public double ColumnSpacing { get => (double)GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowHeight { get => (double)GetValue(RowHeightProperty); set => SetValue(RowHeightProperty, value); }
    public int MaxRows { get => (int)GetValue(MaxRowsProperty); set => SetValue(MaxRowsProperty, value); }

    private readonly List<Field> _fields = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        _fields.Clear();
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).DpiScaleX;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // A fractional exact-fit width can become smaller during text formatting.
            // Round the required width outwards before wrapping and sharing columns.
            double naturalWidth = child.DesiredSize.Width;
            double requiredWidth = naturalWidth == Math.Truncate(naturalWidth)
                ? naturalWidth : Math.Ceiling(naturalWidth * pixelsPerDip) / pixelsPerDip;
            _fields.Add(new(child, requiredWidth));
        }
        if (_fields.Count == 0) return new Size();
        double width = double.IsPositiveInfinity(availableSize.Width)
            ? _fields.Sum(field => field.Width) + ColumnSpacing * (_fields.Count - 1) : availableSize.Width;
        var rows = MakeRows(width);
        return new Size(width, rows.Sum(row => MeasureRow(row, width).Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double top = 0;
        foreach (var row in MakeRows(finalSize.Width))
        {
            var allocation = MeasureRow(row, finalSize.Width);
            double left = 0;
            for (int index = 0; index < row.Count; index++)
            {
                row[index].Child.Arrange(new Rect(left, top, allocation.Widths[index], allocation.Height));
                left += allocation.Widths[index] + allocation.Spacing;
            }
            top += allocation.Height;
        }
        return finalSize;
    }

    private List<List<Field>> MakeRows(double width)
    {
        var rows = new List<List<Field>>();
        var current = new List<Field>();
        double used = 0;
        foreach (var field in _fields)
        {
            if (current.Count > 0 && used + ColumnSpacing + field.Width > width + .001 && rows.Count < MaxRows - 1)
            {
                rows.Add(current); current = new(); used = 0;
            }
            used += (current.Count == 0 ? 0 : ColumnSpacing) + field.Width;
            current.Add(field);
        }
        if (current.Count > 0) rows.Add(current);
        return rows;
    }

    private (double[] Widths, double Spacing, double Height) MeasureRow(List<Field> row, double width)
    {
        double spacing = row.Count <= 1 ? 0 : Math.Min(ColumnSpacing, width / (row.Count - 1));
        double budget = Math.Max(0, width - spacing * (row.Count - 1));
        var widths = ShareWidths(row, budget);
        double height = RowHeight;
        for (int index = 0; index < row.Count; index++)
        {
            row[index].Child.Measure(new Size(widths[index], double.PositiveInfinity));
            height = Math.Max(height, row[index].Child.DesiredSize.Height);
        }
        return (widths, spacing, height);
    }

    private static double[] ShareWidths(List<Field> row, double budget)
    {
        var widths = new double[row.Count];
        var pending = Enumerable.Range(0, row.Count).ToList();
        bool fitsNaturally = row.Sum(field => field.Width) <= budget;
        while (pending.Count > 0)
        {
            double share = Math.Max(0, budget / pending.Count);
            var fixedColumns = pending.Where(index => fitsNaturally ? row[index].Width > share : row[index].Width < share).ToArray();
            if (fixedColumns.Length == 0)
            {
                foreach (int index in pending) widths[index] = share;
                break;
            }
            foreach (int index in fixedColumns)
            {
                widths[index] = row[index].Width;
                budget -= widths[index]; pending.Remove(index);
            }
        }
        return widths;
    }

    private sealed record Field(UIElement Child, double Width);
}
