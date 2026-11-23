using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace Plicbox.Mail;

/// <summary>The colours of a message shown on a dark page (CSS colours).</summary>
public sealed record DarkPageColours(string Background, string Text, string Link);

/// <summary>
/// Dark mode for messages that don't style themselves, as Apple Mail does it: plain text, and HTML with no colours or
/// pictures of its own (or only black text and white backgrounds, as Outlook writes them) is shown light on dark.
/// Everything else (newsletters, a message with a logo, coloured text) stays on its white page, as it was designed for
/// one: turning its colours around would break it.
/// </summary>
public sealed partial class MessageRenderer
{
    /// <summary>Set while the app is dark and the setting is on; null shows every message on a white page.</summary>
    public static DarkPageColours? Dark { get; set; }

    /// <summary>The link colour on white pages (the app's link colour, which can follow the accent).</summary>
    public static string LinkColor { get; set; } = "#1a73e8";

    /// <summary>The marker <see cref="IsDarkPage"/> looks for, and printing takes out (paper is white).</summary>
    private const string DarkStyleId = "mr-dark";

    public static bool IsDarkPage(string html) => html.Contains($"<style id=\"{DarkStyleId}\">", StringComparison.Ordinal);

    /// <summary>The style that turns a plain page dark. Forcing every colour is safe: the page had none of its own worth keeping.</summary>
    private static string DarkStyle(DarkPageColours c) => $$"""
        <style id="{{DarkStyleId}}">
          :root { color-scheme: dark; background: {{c.Background}} !important; }
          :root:hover { --mr-thumb: rgba(255,255,255,.3); }
          ::-webkit-scrollbar-thumb:hover { background-color: rgba(255,255,255,.42); }
          ::-webkit-scrollbar-thumb:active { background-color: rgba(255,255,255,.52); }
          body { background: transparent !important; }
          body, body * { color: {{c.Text}} !important; background-color: transparent !important; }
          a, a * { color: {{c.Link}} !important; }
        </style>
        """;

    private static void AddDarkStyle(IDocument doc, DarkPageColours colours)
    {
        var head = doc.Head!;
        var holder = doc.CreateElement("div");
        holder.InnerHtml = DarkStyle(colours);
        head.AppendChild(holder.FirstElementChild!); // last in the head: after the sender's own rules
    }

    /// <summary>Printing: the white page, whatever the screen shows.</summary>
    private static string WithoutDarkStyle(string html) => DarkStyleBlock().Replace(html, "");

    /// <summary>
    /// Whether an HTML message leaves its look to the reader: no pictures, no background pictures, and colours only
    /// in black-ish text, white-ish backgrounds and links (which get the dark page's link colour).
    /// </summary>
    internal static bool LooksPlain(IDocument doc)
    {
        if (doc.QuerySelector("img, svg, picture, video, canvas, input, button, select, textarea, [background]") is not null) return false;
        if (doc.QuerySelectorAll("link").Any(IsStylesheet)) return false; // its rules aren't here to check
        foreach (var el in doc.All)
        {
            var inLink = el.Closest("a") is not null;
            if (el.GetAttribute("bgcolor") is { } bg && !IsLightBackground(bg)) return false;
            if (el.GetAttribute("color") is { } color && !inLink && !IsDarkText(color)) return false;
            if (el.LocalName == "body" && el.GetAttribute("text") is { } text && !IsDarkText(text)) return false;
            if (el.GetAttribute("style") is { } style && !NeutralDeclarations(style, inLink)) return false;
            if (el.LocalName == "style")
                foreach (Match rule in CssRule().Matches(el.TextContent))
                    if (!NeutralDeclarations(rule.Groups[2].Value, IsLinkSelector(rule.Groups[1].Value))) return false;
        }
        return true;
    }

    private static bool NeutralDeclarations(string css, bool inLink)
    {
        foreach (Match d in ColourDeclaration().Matches(css))
        {
            var property = d.Groups[1].Value.ToLowerInvariant();
            var value = d.Groups[2].Value;
            if (property == "background-image")
            {
                if (!value.Trim().StartsWith("none", StringComparison.OrdinalIgnoreCase)) return false;
                continue;
            }
            if (property.StartsWith("background", StringComparison.Ordinal))
            {
                if (value.Contains("url(", StringComparison.OrdinalIgnoreCase) || value.Contains("gradient", StringComparison.OrdinalIgnoreCase)) return false;
                if (!Colours(value).All(c => c is null || IsLight(c.Value))) return false;
            }
            else if (!inLink && !Colours(value).All(c => c is null || IsDark(c.Value)))
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsLinkSelector(string selector) =>
        selector.Split(',').Any(s => LinkSelector().IsMatch(s.Trim()) || s.Contains("MsoHyperlink", StringComparison.OrdinalIgnoreCase));

    private static bool IsLightBackground(string value) => Colours(value).All(c => c is null || IsLight(c.Value));
    private static bool IsDarkText(string value) => Colours(value).All(c => c is null || IsDark(c.Value));

    // Near-black, greyish text: a colour that only says "text".
    private static bool IsDark((int R, int G, int B) c) => Luminance(c) <= 0.2 && Spread(c) <= 60;
    // Near-white, greyish backgrounds.
    private static bool IsLight((int R, int G, int B) c) => Luminance(c) >= 0.8 && Spread(c) <= 10;

    private static int Spread((int R, int G, int B) c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));

    private static double Luminance((int R, int G, int B) c)
    {
        static double Linear(int v) => v / 255.0 is var s && s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }

    // Words in a colour or background value that aren't colours; any other word is taken for a named colour.
    private static readonly HashSet<string> NotColours = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "transparent", "inherit", "initial", "unset", "revert", "currentcolor", "important", "repeat", "no-repeat",
        "repeat-x", "repeat-y", "space", "round", "center", "top", "bottom", "left", "right", "scroll", "fixed", "local",
        "cover", "contain", "auto", "padding-box", "border-box", "content-box", "text",
    };

    private static readonly Dictionary<string, (int, int, int)> NamedGreys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = (0, 0, 0), ["windowtext"] = (0, 0, 0), ["dimgray"] = (105, 105, 105), ["dimgrey"] = (105, 105, 105),
        ["gray"] = (128, 128, 128), ["grey"] = (128, 128, 128), ["white"] = (255, 255, 255), ["window"] = (255, 255, 255),
        ["whitesmoke"] = (245, 245, 245), ["snow"] = (255, 250, 250), ["ghostwhite"] = (248, 248, 255),
    };

    /// <summary>The colours in a CSS value: null for words that aren't colours, (1000, 0, 0) for one that can't be read (never neutral).</summary>
    private static IEnumerable<(int R, int G, int B)?> Colours(string value)
    {
        foreach (Match m in ColourToken().Matches(value))
        {
            var token = m.Value;
            if (token.StartsWith('#'))
            {
                var hex = token[1..];
                if (hex.Length is 3 or 4) hex = string.Concat(hex[..3].Select(ch => $"{ch}{ch}"));
                if (hex.Length >= 6 && int.TryParse(hex[..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
                    yield return ((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
                else
                    yield return (1000, 0, 0);
            }
            else if (token.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                var parts = Number().Matches(token).Select(n => n.Value).ToList();
                if (parts.Count >= 3 && parts.Take(3).All(p => double.TryParse(p.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                {
                    int Channel(string p) => (int)Math.Clamp(p.EndsWith('%')
                        ? double.Parse(p.TrimEnd('%'), CultureInfo.InvariantCulture) * 2.55
                        : double.Parse(p, CultureInfo.InvariantCulture), 0, 255);
                    yield return (Channel(parts[0]), Channel(parts[1]), Channel(parts[2]));
                }
                else
                {
                    yield return (1000, 0, 0);
                }
            }
            else if (NamedGreys.TryGetValue(token, out var grey))
            {
                yield return grey;
            }
            else if (token.Contains('('))
            {
                yield return (1000, 0, 0); // hsl(), lab()…: not read here, so not taken for neutral
            }
            else if (NotColours.Contains(token) || token.Any(char.IsDigit))
            {
                yield return null;
            }
            else
            {
                yield return (1000, 0, 0); // red, navy, hsl(…): a colour of its own
            }
        }
    }

    [GeneratedRegex(@"<style id=""mr-dark"">.*?</style>\s*", RegexOptions.Singleline)]
    private static partial Regex DarkStyleBlock();

    // A rule in a <style>: its selector and declarations (rules nested in @media are found on their own).
    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}")]
    private static partial Regex CssRule();

    // color / background / background-color / background-image, not border-color and the like.
    [GeneratedRegex(@"(?<![\w-])(background-color|background-image|background|color)\s*:\s*([^;}""]*)", RegexOptions.IgnoreCase)]
    private static partial Regex ColourDeclaration();

    // Units after a number (10px, 1.5em) aren't words of their own.
    [GeneratedRegex(@"#[0-9a-fA-F]+|(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color)\([^)]*\)|(?<![\w.-])[a-zA-Z][\w-]*", RegexOptions.IgnoreCase)]
    private static partial Regex ColourToken();

    [GeneratedRegex(@"-?[\d.]+%?")]
    private static partial Regex Number();

    // "a", "a:link", "a:visited", "a span", "div a"…
    [GeneratedRegex(@"(^|[\s>+~])a(?=$|[\s:.\[#>+~])", RegexOptions.IgnoreCase)]
    private static partial Regex LinkSelector();
}
