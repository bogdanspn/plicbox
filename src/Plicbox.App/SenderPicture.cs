using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace Plicbox.App;

/// <summary>
/// A round picture over an avatar's letter: white behind it (for transparent logos), a hairline around it, all drawn
/// together. Without a picture nothing is drawn and the letter shows. (The white and the picture used to be separate
/// parts, each bound by itself, and now and then a row got the one without the other: an empty white circle.)
/// </summary>
public sealed class SenderPicture : FrameworkElement
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(ImageSource), typeof(SenderPicture),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty RingProperty = DependencyProperty.Register(nameof(Ring), typeof(Brush), typeof(SenderPicture),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double RingWidth = 0.5;
    // How far the picture reaches over the letter's circle, so none of its colour shows around the edge.
    private const double Overlap = 0.75;

    public ImageSource? Source { get => (ImageSource?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public Brush? Ring { get => (Brush?)GetValue(RingProperty); set => SetValue(RingProperty, value); }

    public SenderPicture() => IsHitTestVisible = false;

    // One brush per picture, shared by every row that shows it.
    private static readonly ConditionalWeakTable<ImageSource, ImageBrush> Fills = new();

    protected override void OnRender(DrawingContext dc)
    {
        if (Source is not { } source || ActualWidth <= 0 || ActualHeight <= 0) return;
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double rx = ActualWidth / 2, ry = ActualHeight / 2;
        dc.DrawEllipse(Brushes.White, null, centre, Math.Max(0, rx - 0.5), Math.Max(0, ry - 0.5));
        var pen = Ring is { } ring ? new Pen(ring, RingWidth) : null;
        dc.DrawEllipse(FillFor(source), pen, centre, rx + Overlap - RingWidth / 2, ry + Overlap - RingWidth / 2);
    }

    private static ImageBrush FillFor(ImageSource source) => Fills.GetValue(source, s =>
    {
        var brush = new ImageBrush(s) { Stretch = Stretch.UniformToFill };
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    });
}
