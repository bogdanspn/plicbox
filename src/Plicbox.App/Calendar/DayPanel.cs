using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Plicbox.App.Calendar;

/// <summary>
/// One day of the time grid. Draws its own background (hour lines, out-of-hours shading, the "now" line) and
/// places event blocks by minute of the day, side by side when they overlap.
/// </summary>
public sealed class DayPanel : Panel
{
    public static readonly DependencyProperty StartMinuteProperty = DependencyProperty.RegisterAttached(
        "StartMinute", typeof(double), typeof(DayPanel), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty EndMinuteProperty = DependencyProperty.RegisterAttached(
        "EndMinute", typeof(double), typeof(DayPanel), new FrameworkPropertyMetadata(60.0, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached(
        "Column", typeof(int), typeof(DayPanel), new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.RegisterAttached(
        "Columns", typeof(int), typeof(DayPanel), new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    /// <summary>The hover highlight: fills its slot between the lines, edge to edge (events keep a gutter and a gap).</summary>
    public static readonly DependencyProperty FillsSlotProperty = DependencyProperty.RegisterAttached(
        "FillsSlot", typeof(bool), typeof(DayPanel), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static void SetFillsSlot(UIElement e, bool v) => e.SetValue(FillsSlotProperty, v);
    public static bool GetFillsSlot(UIElement e) => (bool)e.GetValue(FillsSlotProperty);

    public static void SetStartMinute(UIElement e, double v) => e.SetValue(StartMinuteProperty, v);
    public static double GetStartMinute(UIElement e) => (double)e.GetValue(StartMinuteProperty);
    public static void SetEndMinute(UIElement e, double v) => e.SetValue(EndMinuteProperty, v);
    public static double GetEndMinute(UIElement e) => (double)e.GetValue(EndMinuteProperty);
    public static void SetColumn(UIElement e, int v) => e.SetValue(ColumnProperty, v);
    public static int GetColumn(UIElement e) => (int)e.GetValue(ColumnProperty);
    public static void SetColumns(UIElement e, int v) => e.SetValue(ColumnsProperty, v);
    public static int GetColumns(UIElement e) => (int)e.GetValue(ColumnsProperty);

    // Light and dark versions of the grid's own colours (Theme.IsDark when drawn).
    private static readonly (Pen Light, Pen Dark) HourPens = (Line(0xE4), Line(0x3A));
    private static readonly (Pen Light, Pen Dark) SlotPens = (Line(0xF0), Line(0x31));
    private static readonly (Pen Light, Pen Dark) DayPens = (Line(0xDD), Line(0x3E));
    private static readonly (Brush Light, Brush Dark) OffHoursFills = (Fill(0xF7, 0xF7, 0xF8), Fill(0x22, 0x22, 0x22));
    private static Pen HourPen => Services.Theme.IsDark ? HourPens.Dark : HourPens.Light;
    private static Pen SlotPen => Services.Theme.IsDark ? SlotPens.Dark : SlotPens.Light;
    private static Pen DayPen => Services.Theme.IsDark ? DayPens.Dark : DayPens.Light;
    private static Brush OffHours => Services.Theme.IsDark ? OffHoursFills.Dark : OffHoursFills.Light;
    private static Pen Line(byte v) => Frozen(new Pen(new SolidColorBrush(Color.FromRgb(v, v, v)), 1));
    private static Brush Fill(byte r, byte g, byte b) => Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
    private static readonly Brush NowBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)));
    private static readonly Pen NowPen = Frozen(new Pen(NowBrush, 2));
    // The picked day in its own light blues (not a see-through tint, which turns the grey shading muddy).
    private static readonly (Brush Light, Brush Dark) SelectedHoursFills = (Fill(0xEE, 0xF4, 0xFD), Fill(0x26, 0x31, 0x40));
    private static readonly (Brush Light, Brush Dark) SelectedOffHoursFills = (Fill(0xE6, 0xEE, 0xF8), Fill(0x22, 0x2B, 0x38));
    private static Brush SelectedHours => Services.Theme.IsDark ? SelectedHoursFills.Dark : SelectedHoursFills.Light;
    private static Brush SelectedOffHours => Services.Theme.IsDark ? SelectedOffHoursFills.Dark : SelectedOffHoursFills.Light;

    public DateOnly Day { get; init; }
    public double HourHeight { get; init; } = 52;
    public int SlotMinutes { get; init; } = 30;
    public int WorkStartHour { get; init; } = 8;
    public int WorkEndHour { get; init; } = 18;
    public bool IsWorkDay { get; init; } = true;
    /// <summary>The day picked (in the small month, or by moving around): tinted across the grid.</summary>
    public bool IsSelected { get; init; }

    public DayPanel()
    {
        Background = Services.Theme.Brush("SurfaceBrush"); // also makes empty space clickable
        ClipToBounds = true;
    }

    public double MinuteToY(double minute) => minute / 60.0 * HourHeight;
    public double YToMinute(double y) => Math.Clamp(y / HourHeight * 60.0, 0, 24 * 60);

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? 200 : available.Width;
        foreach (UIElement child in InternalChildren)
        {
            var columns = Math.Max(1, GetColumns(child));
            var height = Math.Max(18, MinuteToY(GetEndMinute(child) - GetStartMinute(child)) - 2);
            child.Measure(new Size(Math.Max(0, width / columns - 4), height));
        }
        return new Size(width, HourHeight * 24);
    }

    protected override Size ArrangeOverride(Size final)
    {
        foreach (UIElement child in InternalChildren)
        {
            if (GetFillsSlot(child))
            {
                // Inside the lines: the day's line at the left edge (x 0.5) and the slot's top line (y + 0.5).
                var top = Math.Round(MinuteToY(GetStartMinute(child))) + 1;
                var bottom = Math.Round(MinuteToY(GetEndMinute(child)));
                child.Arrange(new Rect(1, top, Math.Max(0, final.Width - 1), Math.Max(0, bottom - top)));
                continue;
            }
            var columns = Math.Max(1, GetColumns(child));
            var columnWidth = (final.Width - 8) / columns; // keep a gutter on the right for clicking to create
            var x = 1 + GetColumn(child) * columnWidth;
            var y = MinuteToY(GetStartMinute(child)) + 1;
            var height = Math.Max(18, MinuteToY(GetEndMinute(child) - GetStartMinute(child)) - 2);
            child.Arrange(new Rect(x, y, Math.Max(0, columnWidth - 2), height));
        }
        return final;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        dc.DrawRectangle(Background, null, new Rect(0, 0, w, ActualHeight));

        // Outside working hours (or a whole non-working day) is shaded, like eM Client and Outlook.
        var off = IsSelected ? SelectedOffHours : OffHours;
        if (IsSelected) dc.DrawRectangle(SelectedHours, null, new Rect(0, 0, w, HourHeight * 24));
        if (!IsWorkDay) dc.DrawRectangle(off, null, new Rect(0, 0, w, HourHeight * 24));
        else
        {
            dc.DrawRectangle(off, null, new Rect(0, 0, w, WorkStartHour * HourHeight));
            dc.DrawRectangle(off, null, new Rect(0, WorkEndHour * HourHeight, w, (24 - WorkEndHour) * HourHeight));
        }

        var slotsPerHour = Math.Max(1, 60 / SlotMinutes);
        for (var h = 0; h < 24; h++)
        {
            var y = Math.Round(h * HourHeight) + 0.5;
            dc.DrawLine(HourPen, new Point(0, y), new Point(w, y));
            for (var s = 1; s < slotsPerHour; s++)
            {
                var sy = Math.Round(y + s * HourHeight / slotsPerHour) + 0.5;
                dc.DrawLine(SlotPen, new Point(0, sy), new Point(w, sy));
            }
        }
        dc.DrawLine(DayPen, new Point(0.5, 0), new Point(0.5, HourHeight * 24));

        if (Day == DateOnly.FromDateTime(DateTime.Now))
        {
            var now = DateTime.Now.TimeOfDay.TotalMinutes;
            var y = MinuteToY(now);
            dc.DrawLine(NowPen, new Point(0, y), new Point(w, y));
            dc.DrawEllipse(NowBrush, null, new Point(3, y), 5, 5);
        }
    }

    private static T Frozen<T>(T f) where T : Freezable
    {
        f.Freeze();
        return f;
    }
}
