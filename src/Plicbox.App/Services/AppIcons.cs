using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Plicbox.App.Services;

/// <summary>The app icon (Assets/plicbox, made with the icon-creator set) for windows and the tray.</summary>
public static partial class AppIcon
{
    private static readonly Uri IcoUri = new("pack://application:,,,/Assets/plicbox/windows/plicbox.ico");
    private static ImageSource? _window;

    public static ImageSource Window => _window ??= BitmapFrame.Create(IcoUri);

    /// <summary>Win32 icon at the notification area's size for the current DPI (the tray library needs a GDI icon).</summary>
    public static System.Drawing.Icon CreateTrayIcon()
    {
        var size = Math.Max(16, GetSystemMetrics(SM_CXSMICON));
        using var stream = Application.GetResourceStream(IcoUri)!.Stream;
        return new System.Drawing.Icon(stream, size, size);
    }

    private const int SM_CXSMICON = 49;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);
}

/// <summary>
/// "New mail" marks on the app's icon: a count over the taskbar button (Windows draws it small in the corner) and a dot
/// on the tray icon. In the blue of the sidebar's unread counts (white on the app's orange would be too faint to read).
/// </summary>
public static partial class NewMailBadge
{
    private static readonly Color Fill = Color.FromRgb(0x1A, 0x73, 0xE8);

    /// <summary>The taskbar overlay: the number in a plain round badge, "99+" past that.</summary>
    public static ImageSource Overlay(int count)
    {
        const int size = 32;
        var text = count > 99 ? "99+" : count.ToString(CultureInfo.CurrentCulture);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(Fill), null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
            var label = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                text.Length switch { 1 => 20, 2 => 16, _ => 11.5 }, Brushes.White, 1.0);
            // Centred on the digits' own shape: the text's line box has room above for accents, so it would sit high.
            var digits = label.BuildGeometry(new Point(0, 0));
            var ink = digits.Bounds;
            dc.PushTransform(new TranslateTransform(size / 2.0 - (ink.X + ink.Width / 2), size / 2.0 - (ink.Y + ink.Height / 2)));
            dc.DrawGeometry(Brushes.White, null, digits);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>The tray icon with a dot in its lower right corner (a number wouldn't be readable at that size).</summary>
    public static System.Drawing.Icon TrayIcon(System.Drawing.Icon plain)
    {
        using var bitmap = plain.ToBitmap();
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var d = bitmap.Width * 0.5f;
            var x = bitmap.Width - d;
            var y = bitmap.Height - d;
            using var dot = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(Fill.R, Fill.G, Fill.B));
            g.FillEllipse(dot, x, y, d, d);
        }
        var handle = bitmap.GetHicon();
        try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}

/// <summary>
/// Title-bar icons for secondary windows: a small rounded tile in the app's style with the window's own glyph,
/// so Settings shows a gear, Accounts shows people, and so on.
/// </summary>
public static class WindowIcons
{
    public static ImageSource Settings => Glyph("");
    public static ImageSource Accounts => Glyph("");
    public static ImageSource AddAccount => Glyph("\uE77B"); // a single person, like the contact windows
    public static ImageSource Message => Glyph(""); // reading a message: a plain envelope
    public static ImageSource SignIn => Glyph("");

    public static ImageSource Folder => Glyph("");
    public static ImageSource Source => Glyph("");

    public static ImageSource Contact => Glyph("\uE77B");
    public static ImageSource Rules => Glyph("\uE8B1");
    public static ImageSource Operations => Glyph("\uE8CB"); // traffic to and from the servers, the same as its menu item
    public static ImageSource About => Glyph("\uE946"); // an "i" in a circle, like its menu item
    public static ImageSource SendLater => Glyph("\uE823"); // a clock, like the Send later button

    public static ImageSource Tasks => Glyph("");
    public static ImageSource Calendar => Glyph("");
    public static ImageSource Print => Glyph("");

    /// <summary>The window's icon: the glyph alone in dark grey, drawn with a hairline outline so it isn't too thin at 16px.</summary>
    private static ImageSource Glyph(string glyph)
    {
        const int size = 32;
        var ink = Theme.IsDark ? new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)) : new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe Fluent Icons, Segoe MDL2 Assets"), 26, ink, 1.0);
            var shape = text.BuildGeometry(new Point((size - text.Width) / 2, (size - text.Height) / 2));
            dc.DrawGeometry(ink, new Pen(ink, 0.9), shape);
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
