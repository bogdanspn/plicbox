using System.Windows;
using System.Windows.Media;
using Plicbox.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Plicbox.App.Services;

/// <summary>
/// The look the user picked in Settings > Appearance: accent colour, app font and the size of message text.
/// Applied at start and again whenever settings change (also while Settings is open, as a live preview).
/// </summary>
public static class Appearance
{
    public sealed record Swatch(string Hex, string Name);

    /// <summary>The accent setting that follows Windows (Settings > Personalization > Colors).</summary>
    public const string WindowsAccent = "windows";

    /// <summary>Black buttons on the light page, near-white ones on the dark page (black would disappear there).</summary>
    public const string MonoAccent = "mono";

    public static IReadOnlyList<Swatch> Accents { get; } =
    [
        new(WindowsAccent, L.T("Match Windows")), new(MonoAccent, L.T("Black")), new("#F7931E", L.T("Orange")), new("#1A73E8", L.T("Blue")), new("#0E9384", L.T("Teal")), new("#2E9E4F", L.T("Green")),
        new("#7C4DDB", L.T("Purple")), new("#D9488B", L.T("Pink")), new("#DC3D35", L.T("Red")), new("#4B5563", L.T("Graphite")),
    ];

    public const string DefaultFont = "Segoe UI";

    /// <summary>Fonts close enough to Segoe UI in size and spacing to keep every layout intact; only installed ones are offered.</summary>
    public static IReadOnlyList<string> Fonts { get; } =
        new[] { DefaultFont, "Segoe UI Variable Text", "Aptos", "Inter", "Inter UI", "Roboto", "Open Sans", "Lato", "Source Sans Pro", "Noto Sans", "Calibri" }
            .Where(name => name == DefaultFont || System.Windows.Media.Fonts.SystemFontFamilies.Any(f => f.Source == name))
            .ToList();

    public static IReadOnlyList<(double Zoom, string Label)> MessageSizes { get; } =
        [(0.9, L.T("Smaller (90%)")), (1.0, L.T("Normal (100%)")), (1.1, L.T("Larger (110%)")), (1.25, L.T("Large (125%)")), (1.5, L.T("Very large (150%)"))];

    /// <summary>Message text size for viewers created from now on.</summary>
    public static double MessageZoom { get; private set; } = 1.0;

    /// <summary>
    /// Fonts for mail that doesn't choose its own: readable on screen, a few with serifs, and fixed-width ones that
    /// keep plain-text tables and code lined up. Only installed ones are offered.
    /// </summary>
    public static IReadOnlyList<string> MessageFonts { get; } =
        new[] { DefaultFont, "Segoe UI Variable Text", "Aptos", "Calibri", "Arial", "Verdana", "Tahoma", "Georgia", "Cambria", "Consolas", "Cascadia Mono" }
            .Where(name => name == DefaultFont || System.Windows.Media.Fonts.SystemFontFamilies.Any(f => f.Source == name))
            .ToList();

    private static readonly HashSet<string> FixedWidth = ["Consolas", "Cascadia Mono"];

    public static IReadOnlyList<(double Size, string Label)> MessageTextSizes { get; } =
        [(12, "12"), (13, "13"), (14, "14"), (15, L.T("15 (default)")), (16, "16"), (18, "18"), (20, "20")];

    /// <summary>The font or size for message text changed: messages on screen show again with it.</summary>
    public static event Action? MessageTextChanged;

    /// <summary>The settings <see cref="Apply(AppSettings)"/> uses, to tell whether they changed.</summary>
    public static object Part(AppSettings s) =>
        (s.Theme, s.AccentColor, s.UiFont, s.MessageZoom, s.MessageFont, s.MessageTextSize, s.AccentHighlights, s.DarkMessages);

    public static void Apply(AppSettings s) =>
        Apply(s.Theme, s.AccentColor, s.UiFont, s.MessageZoom, s.MessageFont, s.MessageTextSize, s.AccentHighlights, s.DarkMessages);

    public static void Apply(string? mode, string accentHex, string? font, double messageZoom, string? messageFont = null,
        double messageTextSize = Plicbox.Mail.MessageRenderer.DefaultTextSize, bool accentHighlights = true, bool darkMessages = true)
    {
        _mode = mode;
        Theme.Apply(Theme.WantsDark(mode));
        var resources = Application.Current.Resources;
        var accent = Resolve(accentHex);
        ApplyHighlights(resources, accent, accentHighlights);
        var dark = Theme.IsDark && darkMessages
            ? new Plicbox.Mail.DarkPageColours(Hex(Theme.Color("SurfaceBrush")), "#E3E3E3", Hex(Theme.Color("LinkBrush")))
            : null;
        // Other messages are on a white page whatever the mode, so their links get the light mode's shade.
        var messageLink = accentHighlights ? Hex(Readable(accent, Colors.White, lighten: false)) : "#1a73e8";
        ApplyMessageText(messageFont, messageTextSize, messageLink, dark);
        _followsWindows = accentHex == WindowsAccent;
        SetColor(resources, "AccentBrush", accent);
        var light = Luminance(accent) > 0.5; // only near-white accents (Black in dark mode, a pale custom colour)
        // Hover a shade further from the page; text on the button dark when the accent is light.
        SetColor(resources, "AccentHoverBrush", Mix(accent, light ? Colors.White : Colors.Black, light ? 0.3 : 0.08));
        SetColor(resources, "OnAccentBrush", light ? Color.FromRgb(0x1F, 0x1F, 0x1F) : Colors.White);
        SetColor(resources, "FreshGlowBrush", Mix(accent, Theme.Color("SurfaceBrush"), 0.75)); // new mail's brief highlight in the list
        resources["UiFont"] = new FontFamily(font is { Length: > 0 } f && Fonts.Contains(f) ? f : DefaultFont);

        MessageZoom = Math.Clamp(messageZoom, 0.5, 3);
        foreach (Window window in Application.Current.Windows)
            if (window.FindName("Body") is WebView2 view && view.CoreWebView2 is not null)
                view.ZoomFactor = MessageZoom;
    }

    /// <summary>
    /// The colours that are blue by default (links, the picked row, counts, the unread dot) in the accent colour, when
    /// that's chosen: a shade of it that reads on this mode's backgrounds (darker in light mode, lighter in dark mode),
    /// and the picked row's tint about as light (or dark) as the blue one.
    /// </summary>
    private static void ApplyHighlights(ResourceDictionary resources, Color accent, bool on)
    {
        string[] keys = ["LinkBrush", "CountBrush", "UnreadDotBrush", "SelectionBrush", "HoverBrush"];
        if (!on)
        {
            foreach (var key in keys) SetColor(resources, key, Theme.PaletteColor(key));
            return;
        }
        var dark = Theme.IsDark;
        // Links sit on the page, the sidebars and the menus: readable on the lightest of them in dark mode, the darkest in light.
        var link = Readable(accent, dark ? Color.FromRgb(0x33, 0x33, 0x33) : Theme.Color("ChromeBrush"), lighten: dark, target: 4.0);
        SetColor(resources, "LinkBrush", link);
        SetColor(resources, "CountBrush", link);
        SetColor(resources, "UnreadDotBrush", link);
        var page = Theme.Color("SurfaceBrush");
        SetColor(resources, "SelectionBrush", Tint(accent, page, dark, dark ? 0.06 : 0.78));
        SetColor(resources, "HoverBrush", Tint(accent, page, dark, dark ? 0.035 : 0.87));
    }

    /// <summary>The accent mixed into the page until it's as light (light mode) or as dark (dark mode) as the given luminance.</summary>
    private static Color Tint(Color accent, Color page, bool dark, double luminance)
    {
        // More and more of the accent, as long as it stays on the page's side of that luminance.
        var tint = page;
        for (var share = 0.04; share <= 0.6; share += 0.01)
        {
            var c = Mix(page, accent, share);
            if (dark ? Luminance(c) > luminance : Luminance(c) < luminance) break;
            tint = c;
        }
        return tint;
    }

    /// <summary>The accent, darkened (or lightened) just enough to read against a background (WCAG contrast).</summary>
    private static Color Readable(Color accent, Color background, bool lighten, double target = 4.5)
    {
        var c = accent;
        for (var i = 0; i < 40 && Contrast(c, background) < target; i++)
            c = Mix(c, lighten ? Colors.White : Colors.Black, 0.05);
        return c;
    }

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>
    /// How messages are drawn (font, size, link colour, dark page or not), for messages rendered from now on; ones on
    /// screen show again if it changed.
    /// </summary>
    private static void ApplyMessageText(string? font, double size, string link, Plicbox.Mail.DarkPageColours? dark)
    {
        var name = font is { Length: > 0 } f && MessageFonts.Contains(f) ? f : DefaultFont;
        var css = $"'{name}', " + (FixedWidth.Contains(name) ? "monospace" : "'Segoe UI', sans-serif");
        var px = Math.Clamp(size, 8, 40);
        if (css == Plicbox.Mail.MessageRenderer.TextFont && px == Plicbox.Mail.MessageRenderer.TextSize
            && link == Plicbox.Mail.MessageRenderer.LinkColor && dark == Plicbox.Mail.MessageRenderer.Dark) return;
        Plicbox.Mail.MessageRenderer.TextFont = css;
        Plicbox.Mail.MessageRenderer.TextSize = px;
        Plicbox.Mail.MessageRenderer.LinkColor = link;
        Plicbox.Mail.MessageRenderer.Dark = dark;
        RecentRenders.Clear(); // rendered with the old look
        MessageTextChanged?.Invoke();
    }

    /// <summary>
    /// How bright a colour looks (relative luminance, 0–1, from linear light as in WCAG). Plain RGB averages call
    /// orange #F7931E light (0.63) and would put dark text on it; here it's 0.41, and keeps its white text.
    /// </summary>
    internal static double Luminance(Color c)
    {
        static double Linear(byte v) => v / 255.0 is var s && s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    /// <summary>The colour an accent setting stands for (the Windows one is read now; it can change any time).</summary>
    public static Color Resolve(string? accent) =>
        accent == WindowsAccent ? WindowsColor()
        : accent == MonoAccent ? (Theme.IsDark ? Color.FromRgb(0xE8, 0xE8, 0xE8) : Color.FromRgb(0x1F, 0x1F, 0x1F))
        : Parse(accent) ?? Parse("#F7931E")!.Value;

    /// <summary>
    /// Windows' accent colour, as a shade that carries white text: a pale accent (yellow, light green) is swapped for
    /// Windows' own darker variant of it.
    /// </summary>
    public static Color WindowsColor()
    {
        var c = SystemColors.AccentColor;
        var luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
        return luminance > 0.62 ? SystemColors.AccentColorDark1 : c;
    }

    private static bool _followsWindows;
    private static string? _mode;
    private static bool _watching;

    /// <summary>Re-applies when Windows' accent colour or light/dark mode changes, for the settings that follow Windows.</summary>
    public static void WatchWindows(Func<AppSettings> current)
    {
        if (_watching) return;
        _watching = true;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            var followsMode = _mode is null or Theme.System;
            if (!(_followsWindows || followsMode) || e.Category is not (Microsoft.Win32.UserPreferenceCategory.General or Microsoft.Win32.UserPreferenceCategory.Color)) return;
            // SystemColors pick the new accent up a moment after the notification.
            Application.Current?.Dispatcher.BeginInvoke(() => Apply(current()), System.Windows.Threading.DispatcherPriority.Background);
        };
    }

    private static void SetColor(ResourceDictionary resources, string key, Color color)
    {
        // The brushes are shared by every StaticResource use, so changing their colour recolours everything at once.
        if (resources[key] is SolidColorBrush { IsFrozen: false } brush) brush.Color = color;
        else resources[key] = new SolidColorBrush(color);
    }

    private static Color? Parse(string? hex)
    {
        try { return hex is null ? null : (Color)ColorConverter.ConvertFromString(hex); }
        catch (FormatException) { return null; }
    }

    private static Color Mix(Color c, Color with, double amount) => Color.FromRgb(
        (byte)(c.R + (with.R - c.R) * amount), (byte)(c.G + (with.G - c.G) * amount), (byte)(c.B + (with.B - c.B) * amount));
}
