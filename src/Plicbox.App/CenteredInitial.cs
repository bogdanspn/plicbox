using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Plicbox.App;

/// <summary>
/// An avatar's letter, centred by the letter's own shape. A TextBlock centres its line box, which keeps room for
/// descenders, so a capital sits visibly high in a round avatar.
/// </summary>
public sealed class CenteredInitial : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(CenteredInitial),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(CenteredInitial),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    /// <summary>The letter's size as a share of the avatar's height (an em size, so a capital is about 70% of it).</summary>
    public static readonly DependencyProperty ScaleProperty = DependencyProperty.Register(nameof(Scale), typeof(double), typeof(CenteredInitial),
        new FrameworkPropertyMetadata(0.45, FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double Scale { get => (double)GetValue(ScaleProperty); set => SetValue(ScaleProperty, value); }

    public CenteredInitial() => IsHitTestVisible = false;

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Text) || ActualHeight <= 0) return;
        var family = (FontFamily)GetValue(TextElement.FontFamilyProperty);
        var text = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), ActualHeight * Scale, Fill,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var shape = text.BuildGeometry(new Point(0, 0));
        var ink = shape.Bounds;
        if (ink.IsEmpty) return;
        // Move the letter's ink box to the middle (rounded to whole device pixels to stay crisp).
        var dx = Math.Round(ActualWidth / 2 - (ink.X + ink.Width / 2));
        var dy = Math.Round(ActualHeight / 2 - (ink.Y + ink.Height / 2));
        dc.PushTransform(new TranslateTransform(dx, dy));
        dc.DrawGeometry(Fill, null, shape);
        dc.Pop();
    }
}
