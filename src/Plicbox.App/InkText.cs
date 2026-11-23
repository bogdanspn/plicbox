using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Plicbox.App;

/// <summary>
/// A short label (a count, a chevron) centred by its own shape. A TextBlock centres its line box, which keeps room
/// for descenders, so digits and icon glyphs sit low or high by an amount that depends on the font. This one is as
/// big as its ink, and arranged taller (stretched in a badge), keeps the ink in the middle.
/// </summary>
public sealed class InkText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(InkText),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(typeof(InkText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(InkText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontSize, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty FontWeightProperty = TextElement.FontWeightProperty.AddOwner(typeof(InkText),
        new FrameworkPropertyMetadata(FontWeights.Normal, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(InkText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => (FontWeight)GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private FormattedText? _text;
    private Rect _ink;

    public InkText() => IsHitTestVisible = false;

    protected override Size MeasureOverride(Size available)
    {
        _text = null;
        if (string.IsNullOrEmpty(Text)) return default;
        var text = new FormattedText(Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal), FontSize, Foreground, null,
            TextOptions.GetTextFormattingMode(this), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var ink = text.BuildGeometry(new Point(0, 0)).Bounds;
        if (ink.IsEmpty) return default;
        (_text, _ink) = (text, ink);
        return new Size(Math.Ceiling(ink.Width), Math.Ceiling(ink.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_text is null) return;
        // The ink box to the middle, on whole device pixels so the glyphs stay crisp.
        var scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double Snap(double v) => Math.Round(v * scale) / scale;
        dc.DrawText(_text, new Point(Snap(ActualWidth / 2 - (_ink.X + _ink.Width / 2)), Snap(ActualHeight / 2 - (_ink.Y + _ink.Height / 2))));
    }
}
