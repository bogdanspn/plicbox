using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Plicbox.App;

/// <summary>
/// "Working on it": a faint ring with a quarter of it drawn solid, turning. The same ring the reading pane shows while
/// a message downloads (MessageRenderer.Placeholder). It turns only while it's visible, so a hidden one costs no frames.
/// </summary>
public sealed class BusyRing : FrameworkElement
{
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(nameof(Foreground), typeof(Brush), typeof(BusyRing),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(BusyRing),
        new FrameworkPropertyMetadata(2.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private readonly RotateTransform _turn = new();

    public BusyRing()
    {
        RenderTransform = _turn;
        RenderTransformOrigin = new Point(0.5, 0.5);
        IsVisibleChanged += (_, _) => _turn.BeginAnimation(RotateTransform.AngleProperty, IsVisible
            ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever }
            : null);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var radius = (Math.Min(ActualWidth, ActualHeight) - Thickness) / 2;
        if (radius <= 0) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var pen = new Pen(Foreground, Thickness);
        dc.PushOpacity(0.25);
        dc.DrawEllipse(null, pen, center, radius, radius);
        dc.Pop();

        // The top quarter, as CSS draws border-top on a round box.
        Point At(double degrees) => new(center.X + radius * Math.Cos(degrees * Math.PI / 180), center.Y + radius * Math.Sin(degrees * Math.PI / 180));
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            g.BeginFigure(At(-135), isFilled: false, isClosed: false);
            g.ArcTo(At(-45), new Size(radius, radius), 0, isLargeArc: false, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }
}
