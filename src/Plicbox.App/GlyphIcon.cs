using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Plicbox.App;

/// <summary>
/// An icon from the icon font, sized and coloured like text (font size and foreground follow the parent). Also draws
/// the icons the font lacks, such as a plain Reply All arrow (<see cref="ReplyAll"/>).
/// </summary>
public sealed class GlyphIcon : FrameworkElement
{
    /// <summary>Reply All as a plain double arrow: the font's own is an envelope, busier than Reply and Forward.</summary>
    public const string ReplyAll = "\uE8C2";
    /// <summary>The plain curved arrows the toolbar and menus use for replying and forwarding.</summary>
    public const string Reply = "\uE97A", Forward = "\uEE35";

    /// <summary>
    /// Icons the font lacks, drawn from Fluent UI System Icons outlines (20 px regular, MIT licence): an inbox tray, and
    /// a trashcan with a small ✕ for Empty Trash (the font's plain can is Delete, often in the same menu).
    /// A name rather than a character, so it never passes for text.
    /// </summary>
    public const string MailInbox = "fluent:mail-inbox", EmptyTrash = "fluent:delete-dismiss";

    /// <summary>Each outline with the part of its 20-unit grid that fills the font's icon box: From, From + Size.</summary>
    private static readonly Dictionary<string, (Geometry Geometry, double From, double Size)> Outlines = new()
    {
        // delete_dismiss_20_regular: drawn edge to edge of Fluent's 2–18 grid, like the font's own trashcan.
        [EmptyTrash] = (Outline("M11.5 4C11.5 3.17157 10.8284 2.5 10 2.5C9.17157 2.5 8.5 3.17157 8.5 4H11.5ZM7.5 4C7.5 2.61929 8.61929 1.5 10 1.5C11.3807 1.5 12.5 2.61929 12.5 4H17.5C17.7761 4 18 4.22386 18 4.5C18 4.77614 17.7761 5 17.5 5H16.4456L15.9614 9.19628C15.6433 9.10881 15.3136 9.0492 14.9751 9.02024L15.439 5H4.56101L5.74131 15.2292C5.85775 16.2384 6.71225 17 7.72813 17H9.59971C9.78261 17.3578 10.0035 17.6929 10.2572 18H7.72813C6.20431 18 4.92256 16.8576 4.7479 15.3439L3.55437 5H2.5C2.22386 5 2 4.77614 2 4.5C2 4.22386 2.22386 4 2.5 4H7.5ZM19 14.5C19 16.9853 16.9853 19 14.5 19C12.0147 19 10 16.9853 10 14.5C10 12.0147 12.0147 10 14.5 10C16.9853 10 19 12.0147 19 14.5ZM16.3536 13.3536C16.5488 13.1583 16.5488 12.8417 16.3536 12.6464C16.1583 12.4512 15.8417 12.4512 15.6464 12.6464L14.5 13.7929L13.3536 12.6464C13.1583 12.4512 12.8417 12.4512 12.6464 12.6464C12.4512 12.8417 12.4512 13.1583 12.6464 13.3536L13.7929 14.5L12.6464 15.6464C12.4512 15.8417 12.4512 16.1583 12.6464 16.3536C12.8417 16.5488 13.1583 16.5488 13.3536 16.3536L14.5 15.2071L15.6464 16.3536C15.8417 16.5488 16.1583 16.5488 16.3536 16.3536C16.5488 16.1583 16.5488 15.8417 16.3536 15.6464L15.2071 14.5L16.3536 13.3536Z"), 2, 16),
        // mail_inbox_20_regular: its ink runs 3–17, scaled up a little to match the font's envelope.
        [MailInbox] = (Outline("M6 3C4.34315 3 3 4.34315 3 6V14C3 15.6569 4.34315 17 6 17H14C15.6569 17 17 15.6569 17 14V6C17 4.34315 15.6569 3 14 3H6ZM16 10H12.5C12.2239 10 12 10.223 12 10.4991L11.9997 10.5114C11.9993 10.5238 11.9983 10.5443 11.9964 10.5718C11.9925 10.6269 11.9843 10.7088 11.9677 10.8084C11.9341 11.0101 11.8679 11.2713 11.7403 11.5264C11.6137 11.7796 11.4317 12.0176 11.168 12.1933C10.9074 12.3671 10.5375 12.5 10 12.5C9.46249 12.5 9.0926 12.3671 8.83204 12.1933C8.56834 12.0176 8.38631 11.7796 8.25971 11.5264C8.13214 11.2713 8.06586 11.0101 8.03226 10.8084C8.01565 10.7088 8.00755 10.6269 8.00361 10.5718C8.00165 10.5443 8.00075 10.5238 8.00033 10.5114L8 10.4994C7.99966 10.2235 7.77594 10 7.5 10H4V6C4 4.89543 4.89543 4 6 4H14C15.1046 4 16 4.89543 16 6V10ZM4 11H7.0505C7.09652 11.2643 7.18655 11.6161 7.36529 11.9736C7.55119 12.3454 7.83791 12.7324 8.27734 13.0254C8.7199 13.3204 9.28751 13.5 10 13.5C10.7125 13.5 11.2801 13.3204 11.7227 13.0254C12.1621 12.7324 12.4488 12.3454 12.6347 11.9736C12.8134 11.6161 12.9035 11.2643 12.9495 11H16V14C16 15.1046 15.1046 16 14 16H6C4.89543 16 4 15.1046 4 14V11Z"), 3, 14),
    };

    private static Geometry Outline(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(GlyphIcon),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FontSizeProperty = TextElement.FontSizeProperty.AddOwner(typeof(GlyphIcon),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(GlyphIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    private static readonly Typeface IconFace = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public GlyphIcon() => VerticalAlignment = VerticalAlignment.Center;

    private FormattedText Text(string glyph, double size) => new(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        IconFace, size, Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // Reply All takes the same room as Reply, so menus and toolbars stay in line.
    protected override Size MeasureOverride(Size available)
    {
        if (string.IsNullOrEmpty(Glyph)) return default;
        // Drawn icons take the room of the font's envelope, so they line up with the font's icons beside them.
        var text = Text(Glyph == ReplyAll ? Reply : Outlines.ContainsKey(Glyph) ? "" : Glyph, FontSize);
        return new Size(text.WidthIncludingTrailingWhitespace, text.Height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrEmpty(Glyph)) return;
        if (Outlines.TryGetValue(Glyph, out var outline))
        {
            // The outline's From…From+Size units fill the font's 16-unit box the way the font's own icons do, in the
            // same place: the box is found from the font's envelope, whose ink runs 0–16 across and 2–15 down.
            var scale = FontSize / outline.Size;
            var envelope = Text("", FontSize).BuildGeometry(new Point(0, 0)).Bounds;
            var em = envelope.IsEmpty ? new Point(0, 0) : new Point(envelope.X, envelope.Y - 2 * FontSize / 16);
            dc.PushTransform(new TranslateTransform(Math.Round(em.X - outline.From * scale), Math.Round(em.Y - outline.From * scale)));
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawGeometry(Foreground, null, outline.Geometry);
            dc.Pop();
            dc.Pop();
            return;
        }
        if (Glyph != ReplyAll)
        {
            dc.DrawText(Text(Glyph, FontSize), new Point(0, 0));
            return;
        }

        // Reply All: a slightly smaller Reply arrow with a second arrowhead in front of it, in Reply's box.
        var box = Text(Reply, FontSize).BuildGeometry(new Point(0, 0)).Bounds;
        var arrow = Text(Reply, FontSize * 0.9).BuildGeometry(new Point(0, 0));
        var ink = arrow.Bounds;
        if (box.IsEmpty || ink.IsEmpty) return;
        var gap = ink.Width * 0.28;
        var head = new CombinedGeometry(GeometryCombineMode.Intersect, arrow, new RectangleGeometry(new Rect(ink.X, ink.Y, ink.Width * 0.4, ink.Height)));
        var dx = Math.Round(box.X + box.Width / 2 - (ink.X + (ink.Width + gap) / 2));
        var dy = Math.Round(box.Y + box.Height / 2 - (ink.Y + ink.Height / 2));
        dc.PushTransform(new TranslateTransform(dx, dy));
        dc.DrawGeometry(Foreground, null, head);
        dc.PushTransform(new TranslateTransform(gap, 0));
        dc.DrawGeometry(Foreground, null, arrow);
        dc.Pop();
        dc.Pop();
    }
}
