using Avalonia;
using Avalonia.Controls;

namespace BirdPhotoSelector.Desktop.Controls;

/// <summary>参数按自然宽度换行并均分剩余空间，长字段借用短字段余量；不截断或缩小字号。</summary>
public sealed class BalancedMetadataPanel : Panel
{
    private const double ColumnSpacing = 16;
    private const double RowHeight = 24;
    private const int MaxRows = int.MaxValue;
    private readonly List<Field> _fields = new();

    /// <summary>先自然测量，再按有效宽度换行，向外取整避免高 DPI 字形被截断。</summary>
    protected override Size MeasureOverride(Size availableSize)
    {
        _fields.Clear();
        double pixelsPerDip = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        foreach (Control child in Children)
        {
            if (!child.IsVisible) continue;
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

    /// <summary>逐行均分与借宽，按确定尺寸排列参数。</summary>
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

    /// <summary>保留字段顺序，只有总自然宽度不足时才换行。</summary>
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

    /// <summary>使用实际列宽重新测量行高，参数内容保持完整。</summary>
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

    /// <summary>通过固定较长或较短列，迭代分配剩余空间。</summary>
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

    private sealed record Field(Control Child, double Width);
}
