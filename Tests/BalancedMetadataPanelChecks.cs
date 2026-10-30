using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BirdPhotoSelector.Controls;

/// <summary>Uses actual WPF layout slots; no copied production allocation algorithm.</summary>
internal static class BalancedMetadataPanelChecks
{
    public static int Run()
    {
        int count = 0;
        void Check(bool condition, string description)
        { if (!condition) throw new InvalidOperationException(description); count++; Console.WriteLine("  OK BALANCED METADATA " + description); }

        var equal = Make(12, 40, 40, 40, 40); Layout(equal, 400);
        Check(Slots(equal).All(rect => Near(rect.Width, 91)) && Near(Slots(equal)[1].Left, 103),
            "short fields receive equal columns and one consistent gap across the full row");
        var borrowed = Make(12, 160, 20, 20, 20); Layout(borrowed, 400);
        Check(Near(Slots(borrowed)[0].Width, 160) && Slots(borrowed).Skip(1).All(rect => Near(rect.Width, 68)),
            "a long field borrows the same amount from each short neighbor without clipping any natural width");
        var multiple = Make(12, 135, 140, 20, 20); Layout(multiple, 400);
        Check(Near(Slots(multiple)[0].Width, 135) && Near(Slots(multiple)[1].Width, 140) &&
              Slots(multiple).Skip(2).All(rect => Near(rect.Width, 44.5)),
            "multiple long fields keep their needed widths while short fields share the remainder fairly");

        var wrapped = Make(10, 90, 90, 90, 90); Layout(wrapped, 210);
        Check(Near(wrapped.DesiredSize.Height, 40) && Near(Slots(wrapped)[0].Top, Slots(wrapped)[1].Top) &&
              Slots(wrapped)[2].Top > Slots(wrapped)[1].Top && Slots(wrapped).All(rect => Near(rect.Width, 100)),
            "insufficient total width wraps in source order and balances each row separately");
        Check(Inside(wrapped, 210), "wrapped columns never overlap or extend outside either row");
        Layout(wrapped, 410);
        Check(Near(wrapped.DesiredSize.Height, 20) && Slots(wrapped).All(rect => Near(rect.Width, 95)),
            "expanding the panel joins its rows and restores uniform columns");
        ((Probe)wrapped.Children[1]).NaturalWidth = 170; Layout(wrapped, 410);
        Check(Near(wrapped.DesiredSize.Height, 40) && Near(Slots(wrapped)[1].Width, 170) && Inside(wrapped, 410),
            "changing a field remeasures its natural width and recalculates wrapping");

        var extreme = Make(10, 90, 2000, 60, 60, 60); Layout(extreme, 210);
        Check(Near(extreme.DesiredSize.Height, 40) && Inside(extreme, 210) &&
              Slots(extreme).All(rect => double.IsFinite(rect.Width) && rect.Width >= 0),
            "an extreme field converges within two rows and cannot push later fields offscreen");
        Layout(extreme, 0);
        Check(Near(extreme.DesiredSize.Height, 40) && Slots(extreme).All(rect => Near(rect.Width, 0) && Near(rect.Left, 0)),
            "zero available width contracts both columns and gaps without invalid layout sizes");
        Layout(extreme, 210);
        Check(Inside(extreme, 210) && Slots(extreme).Any(rect => rect.Width > 0),
            "columns recover finite widths after a zero-width layout without retaining collapsed allocations");
        bool stable = true;
        foreach (double width in new double[] { 0, 1, 210, 410, 210, 0, 410 })
        { Layout(extreme, width); stable &= Inside(extreme, width) && extreme.ActualHeight <= 40; }
        Check(stable, "rapid narrow/wide layout changes retain finite nonoverlapping columns within the two-row limit");
        var tight = Make(12, 100, 100, 100); tight.MaxRows = 1; Layout(tight, 200);
        Check(Slots(tight).All(rect => Near(rect.Width, 176d / 3)) && Inside(tight, 200),
            "a bounded final row divides unavoidable overflow evenly instead of overlapping fields");

        var hidden = Make(12, 60, 60, 60); hidden.Children[1].Visibility = Visibility.Collapsed; Layout(hidden, 300);
        Check(Near(LayoutInformation.GetLayoutSlot((FrameworkElement)hidden.Children[0]).Width, 144) &&
              Near(LayoutInformation.GetLayoutSlot((FrameworkElement)hidden.Children[2]).Left, 156),
            "collapsed fields consume neither a column nor a gap");
        var unbounded = Make(10, 80, 20); unbounded.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Check(Near(unbounded.DesiredSize.Width, 110) && Near(unbounded.DesiredSize.Height, 20),
            "unconstrained measuring reports a finite natural size");
        Layout(unbounded, 60);
        Check(Near(unbounded.DesiredSize.Height, 40) && Inside(unbounded, 60),
            "finite layout after unconstrained measuring refreshes the row allocations");
        var empty = Make(16); empty.Measure(new Size(400, double.PositiveInfinity));
        Check(empty.DesiredSize == new Size(), "an empty metadata row reserves no height");
        return count;
    }

    private static BalancedMetadataPanel Make(double spacing, params double[] naturalWidths)
    {
        var panel = new BalancedMetadataPanel { ColumnSpacing = spacing, RowHeight = 20, MaxRows = 2 };
        foreach (double width in naturalWidths) panel.Children.Add(new Probe(width));
        return panel;
    }
    private static void Layout(FrameworkElement panel, double width)
    {
        panel.Measure(new Size(width, double.PositiveInfinity));
        panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height)); panel.UpdateLayout();
    }
    private static Rect[] Slots(Panel panel) => panel.Children.Cast<FrameworkElement>()
        .Where(child => child.Visibility != Visibility.Collapsed).Select(LayoutInformation.GetLayoutSlot).ToArray();
    private static bool Inside(Panel panel, double width)
    {
        var slots = Slots(panel);
        return slots.All(rect => rect.Left >= -.001 && rect.Right <= width + .001 && rect.Top >= 0 && rect.Bottom <= panel.ActualHeight + .001) &&
            !slots.SelectMany((first, index) => slots.Skip(index + 1).Select(second => (first, second)))
                .Any(pair => Math.Min(pair.first.Right, pair.second.Right) - Math.Max(pair.first.Left, pair.second.Left) > .001 &&
                             Math.Min(pair.first.Bottom, pair.second.Bottom) - Math.Max(pair.first.Top, pair.second.Top) > .001);
    }
    private static bool Near(double first, double second) => Math.Abs(first - second) < .001;
    private sealed class Probe(double width) : FrameworkElement
    {
        private double _naturalWidth = width;
        public double NaturalWidth { get => _naturalWidth; set { _naturalWidth = value; InvalidateMeasure(); } }
        protected override Size MeasureOverride(Size availableSize) => new(Math.Min(_naturalWidth, availableSize.Width), 18);
    }
}
