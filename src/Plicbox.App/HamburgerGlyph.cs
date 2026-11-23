using System.Windows;
using System.Windows.Media;

namespace Plicbox.App;

/// <summary>
/// The ☰ menu glyph drawn as three bars that start on whole device pixels, so it stays sharp at any display scaling
/// (the icon font's version blurs at 125 %/150 % because its lines land between pixels).
/// Sized to match the minimize/maximize/close glyphs.
/// </summary>
public sealed class HamburgerGlyph : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(HamburgerGlyph),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    // In device-independent pixels at 100 %; converted to whole device pixels below.
    private const double BarWidth = 10, BarThickness = 1, Gap = 3;

    public HamburgerGlyph()
    {
        UseLayoutRounding = true; // puts the element itself on a pixel boundary
    }

    private (double Scale, int Width, double Thickness, int Step) Metrics()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleY;
        // The stroke keeps its true weight (1.5 device px at 150 %, like the caption glyphs' strokes); only where each bar
        // starts and how far apart they are snap to whole pixels, so the top edges are crisp and the spacing even.
        var thickness = BarThickness * scale;
        var width = (int)Math.Round(BarWidth * scale, MidpointRounding.AwayFromZero);
        var step = (int)Math.Ceiling(thickness) + Math.Max(2, (int)Math.Round(Gap * scale, MidpointRounding.AwayFromZero));
        return (scale, width, thickness, step);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var (scale, width, thickness, step) = Metrics();
        return new Size(width / scale, (2 * step + Math.Ceiling(thickness)) / scale);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi) => InvalidateMeasure();

    protected override void OnRender(DrawingContext dc)
    {
        var (scale, width, thickness, step) = Metrics();
        for (var i = 0; i < 3; i++)
            dc.DrawRectangle(Foreground, null, new Rect(0, i * step / scale, width / scale, thickness / scale));
    }
}
