using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Plicbox.App;

/// <summary>
/// Turns a toolbar button's own icon while something is busy (<c>local:Spin.IsSpinning="{Binding IsSyncing}"</c>):
/// the Refresh button's arrows go round instead of a separate spinner. When it stops, the icon finishes the turn
/// it's on rather than snapping back, so even a quick refresh shows one smooth turn.
/// </summary>
public static class Spin
{
    private static readonly TimeSpan Turn = TimeSpan.FromSeconds(1.1);

    public static readonly DependencyProperty IsSpinningProperty = DependencyProperty.RegisterAttached("IsSpinning", typeof(bool), typeof(Spin),
        new PropertyMetadata(false, OnChanged));

    public static bool GetIsSpinning(DependencyObject d) => (bool)d.GetValue(IsSpinningProperty);
    public static void SetIsSpinning(DependencyObject d, bool value) => d.SetValue(IsSpinningProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Control control) return;
        if (control.IsLoaded) Apply(control, (bool)e.NewValue);
        else control.Loaded += Later;

        void Later(object sender, RoutedEventArgs args)
        {
            control.Loaded -= Later;
            Apply(control, GetIsSpinning(control));
        }
    }

    private static void Apply(Control control, bool spin)
    {
        control.ApplyTemplate();
        if (control.Template?.FindName("Glyph", control) is not FrameworkElement glyph) return;
        if (glyph.RenderTransform is not RotateTransform rotate || rotate.IsFrozen)
        {
            glyph.RenderTransform = rotate = new RotateTransform();
            glyph.RenderTransformOrigin = new Point(0.5, 0.5);
        }

        var angle = rotate.Angle % 360; // the animated value while it's turning
        if (spin)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(angle, angle + 360, Turn) { RepeatBehavior = RepeatBehavior.Forever });
            return;
        }
        if (angle < 0.5)
        {
            rotate.BeginAnimation(RotateTransform.AngleProperty, null);
            return;
        }
        // Finish the turn, then rest upright.
        var finish = new DoubleAnimation(angle, 360, TimeSpan.FromTicks((long)(Turn.Ticks * (360 - angle) / 360)))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        rotate.BeginAnimation(RotateTransform.AngleProperty, finish);
    }
}
