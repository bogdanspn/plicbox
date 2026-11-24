using System.Net;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Ganss.Xss;
using MimeKit;

namespace Plicbox.Mail;

/// <param name="BlockedTracking">e.g. "SendGrid email tracking" when tracking pixels were removed; null when none.</param>
public sealed record RenderedMessage(string Html, bool HasRemoteContent, string? BlockedTracking = null);

/// <summary>
/// Turns a MIME message into the document the reading pane shows. An HTML message is shown as its sender made it (its
/// doctype, head styles, body attributes, every CSS rule), so it looks as it does in other mail apps; what keeps it
/// safe is the view around it rather than rewriting it: scripts are off in the WebView, a Content-Security-Policy
/// blocks everything remote (pictures, styles, fonts) until the user allows pictures, and links only ever open
/// outside, after the phishing check. On top of that, <see cref="Defuse"/> takes out whatever could act (scripts,
/// frames, plugins, event handlers, script URLs, form submission, meta refresh), and tracking pixels are removed.
/// </summary>
public sealed partial class MessageRenderer
{
    /// <summary>
    /// Settings > Appearance > Message text: the font (a CSS font-family list) and size in CSS pixels for mail that
    /// doesn't choose its own (plain text, and HTML without a font). A sender's own fonts and sizes still win.
    /// </summary>
    public static string TextFont { get; set; } = "'Segoe UI', sans-serif";
    public static double TextSize { get; set; } = DefaultTextSize;
    public const double DefaultTextSize = 15;

    private static string TextCss => $"font-family: {TextFont}; font-size: {TextSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}px";

    // Quoting into a reply goes through a real sanitizer: that HTML ends up in the editor and in mail you send.
    private readonly HtmlSanitizer _sanitizer;

    public MessageRenderer()
    {
        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.Add("style");
        _sanitizer.AllowedTags.Add("center");
        _sanitizer.AllowedTags.Add("font");
        _sanitizer.AllowedSchemes.Add("data");
        _sanitizer.AllowedSchemes.Add("mailto");
        _sanitizer.AllowedAttributes.Add("class");
        _sanitizer.AllowedAttributes.Add("bgcolor");
        _sanitizer.AllowedAttributes.Add("color");
        _sanitizer.AllowedAttributes.Add("face");
        _sanitizer.AllowedAttributes.Add("background");
        _sanitizer.AllowedAtRules.Add(AngleSharp.Css.Dom.CssRuleType.Media);
        _sanitizer.AllowDataAttributes = false;
    }

    /// <param name="allowRemoteContent">Load external pictures, styles and fonts.</param>
    /// <param name="allowTracking">Keep tracking pixels (they're removed otherwise, even when pictures are allowed).</param>
    public RenderedMessage Render(MimeMessage message, bool allowRemoteContent, bool allowTracking = false)
    {
        if (message.HtmlBody is not { } html)
        {
            var text = TextToHtml(message.TextBody ?? "");
            return new RenderedMessage(WrapDocument(text, "default-src 'none'; style-src 'unsafe-inline'"), false);
        }

        var doc = new HtmlParser().ParseDocument(InlineCidImages(message, html));
        Defuse(doc);
        var (trackers, trackerOrigins) = HandleTrackers(doc, allowTracking);
        var hasRemote = RemoteReference().IsMatch(doc.DocumentElement.OuterHtml);

        var remote = allowRemoteContent ? " https: http:" : "";
        // Allowing tracking (but not pictures) lets exactly the tracker hosts through, nothing else.
        var trackerSources = allowTracking && !allowRemoteContent ? string.Concat(trackerOrigins.Select(o => " " + o)) : "";
        var csp = $"default-src 'none'; img-src data:{remote}{trackerSources}; style-src 'unsafe-inline'{remote}; font-src data:{remote}";
        var dark = Dark is { } colours && LooksPlain(doc) ? colours : null; // checked before our own styles go in
        InstallHead(doc, csp);
        if (dark is not null) AddDarkStyle(doc, dark);
        return new RenderedMessage(doc.ToHtml(), hasRemote, trackers.Count > 0 && !allowTracking ? Trackers.Describe(trackers) : null);
    }

    // ───────── keeping a message's HTML harmless without changing how it looks ─────────

    // Elements that run something, load another document, or steer the page: gone with everything in them.
    private static readonly HashSet<string> ActiveElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "iframe", "frame", "frameset", "object", "embed", "applet", "param", "base", "meta", "portal", "noembed",
    };

    // Attributes that act (submit, ping on click, a document of their own, a cache manifest, focus taken).
    private static readonly HashSet<string> ActiveAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "action", "formaction", "ping", "srcdoc", "manifest", "autofocus", "http-equiv",
    };

    // Attributes holding an address, checked for its scheme; those of links can't be data: or cid:.
    private static readonly HashSet<string> UrlAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "href", "src", "background", "poster", "cite", "longdesc", "lowsrc", "dynsrc", "xlink:href", "data", "icon",
    };

    private static readonly HashSet<string> LinkAttributes = new(StringComparer.OrdinalIgnoreCase) { "href", "xlink:href", "cite", "longdesc" };

    /// <summary>
    /// Takes out what could act, and nothing that only shows: scripts and frames, plugins, &lt;base&gt;/&lt;meta&gt;
    /// (refresh, policies of their own), &lt;link&gt;s other than stylesheets (prefetching reaches out even past the
    /// CSP), on… handlers, script and other unknown URL schemes, and form submission (the fields still show). Styles
    /// are left exactly as written: under the CSP they can't load anything the user didn't allow.
    /// </summary>
    internal static void Defuse(IDocument doc)
    {
        foreach (var el in doc.All.ToList())
        {
            var name = el.LocalName;
            if (ActiveElements.Contains(name)
                || (name.Equals("link", StringComparison.OrdinalIgnoreCase) && !IsStylesheet(el))
                // SVG animation can rewrite a link's address after the fact.
                || (name is "set" or "animate" && (el.GetAttribute("attributeName") ?? "").Contains("href", StringComparison.OrdinalIgnoreCase)))
            {
                el.Remove();
                continue;
            }
            if (name.Equals("form", StringComparison.OrdinalIgnoreCase))
            {
                // A form in a mail is a survey or phishing: its fields stay visible, but there's nothing to send them.
                while (el.FirstChild is { } child) el.Parent?.InsertBefore(child, el);
                el.Remove();
                continue;
            }
            foreach (var attr in el.Attributes.ToList())
            {
                var a = attr.Name;
                if (a.StartsWith("on", StringComparison.OrdinalIgnoreCase) || ActiveAttributes.Contains(a)
                    || (UrlAttributes.Contains(a) && !SafeUrl(attr.Value, picture: !LinkAttributes.Contains(a)))
                    || (a.Equals("srcset", StringComparison.OrdinalIgnoreCase)
                        && !attr.Value.Split(',').All(c => SafeUrl(c.Trim().Split(' ')[0], picture: true))))
                    el.RemoveAttribute(attr.NamespaceUri, attr.LocalName);
            }
        }
    }

    private static bool IsStylesheet(IElement link) =>
        (link.GetAttribute("rel") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("stylesheet", StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Web, mail and phone addresses, in-page and relative ones (they lead nowhere here); for pictures also cid: and
    /// data: pictures (not SVG ones, which are documents). Script URLs and every other scheme are refused; control
    /// characters and spaces, which browsers skip when reading a scheme, are skipped here too.
    /// </summary>
    private static bool SafeUrl(string value, bool picture)
    {
        var url = new string(value.Where(c => !char.IsControl(c) && !char.IsWhiteSpace(c)).ToArray());
        var colon = url.IndexOf(':');
        var stop = url.IndexOfAny(['/', '?', '#']);
        if (colon < 0 || (stop >= 0 && stop < colon)) return true; // relative, or #fragment
        var scheme = url[..colon].ToLowerInvariant();
        return scheme is "http" or "https" or "mailto" or "tel" or "sms"
            || (picture && (scheme == "cid" || (scheme == "data" && url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase))));
    }

    /// <summary>
    /// The pane's own head in front of the sender's: the charset, the Content-Security-Policy (first, so it covers
    /// everything after it), and a few defaults at zero specificity (<c>:where</c>), so any rule of the sender's wins:
    /// a font for mail that names none, some room at the edges, the quiet placeholder for pictures not loaded, and the
    /// app's slim scrollbars.
    /// </summary>
    private static void InstallHead(IDocument doc, string csp)
    {
        var head = doc.Head ?? doc.DocumentElement.InsertBefore(doc.CreateElement("head"), doc.Body);
        var charset = doc.CreateElement("meta");
        charset.SetAttribute("charset", "utf-8");
        var policy = doc.CreateElement("meta");
        policy.SetAttribute("http-equiv", "Content-Security-Policy");
        policy.SetAttribute("content", csp);
        var style = doc.CreateElement("style");
        style.TextContent = $$"""
            :where(body) { margin: 0; padding: 16px 20px; {{TextCss}}; color: #1f1f1f; overflow-wrap: break-word; }
            :where(a) { color: {{LinkColor}}; }
            {{MissingPictureCss}}
            {{ScrollbarCss}}
            """;
        head.InsertBefore(style, head.FirstChild);
        head.InsertBefore(policy, style);
        head.InsertBefore(charset, policy);
    }

    /// <summary>Tracking pixels are found (always) and removed (unless kept); kept ones' hosts are returned for the CSP.</summary>
    private static (List<string> Trackers, HashSet<string> Origins) HandleTrackers(IDocument doc, bool keep)
    {
        var trackers = new List<string>();
        var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var images = doc.QuerySelectorAll("img").ToList();
        var uses = images.GroupBy(i => i.GetAttribute("src") ?? "").ToDictionary(g => g.Key, g => g.Count());
        foreach (var img in images)
        {
            var src = img.GetAttribute("src");
            var name = Trackers.Identify(src, img.GetAttribute("width"), img.GetAttribute("height"), img.GetAttribute("style"), uses[src ?? ""]);
            if (name is null) continue;
            trackers.Add(name);
            if (keep && Uri.TryCreate(src, UriKind.Absolute, out var uri)) origins.Add(uri.GetLeftPart(UriPartial.Authority));
            if (!keep) img.Remove();
        }
        return (trackers, origins);
    }

    /// <summary>
    /// The original message body as safe HTML for quoting in a reply or forward. Tracking pixels are left out: passed
    /// on, they would report every opening by the new recipient as you reading it again.
    /// </summary>
    public string SanitizeForQuote(MimeMessage message)
    {
        if (message.HtmlBody is not { } html) return TextToHtml(message.TextBody ?? "");
        var clean = _sanitizer.Sanitize(InlineCidImages(message, html));
        if (!clean.Contains("<img", StringComparison.OrdinalIgnoreCase)) return clean;
        var doc = new HtmlParser().ParseDocument($"<!doctype html><html><body>{clean}</body></html>");
        return HandleTrackers(doc, keep: false).Trackers.Count == 0 ? clean : doc.Body!.InnerHtml;
    }

    /// <summary>Format › Plain Text: the message's text part (or its HTML reduced to text), with no styling, pictures or remote content.</summary>
    public static string RenderPlainText(MimeMessage message)
    {
        var text = message.TextBody is { Length: > 0 } plain ? plain
            : message.HtmlBody is { } html ? HtmlToText(html)
            : "";
        return WrapDocument(TextToHtml(text), "default-src 'none'; style-src 'unsafe-inline'");
    }

    /// <summary>Readable plain text from HTML, keeping paragraph and line breaks (for the text/plain alternative).</summary>
    public static string HtmlToText(string html)
    {
        var text = HeadAndScripts().Replace(html, "");
        text = ParagraphEnd().Replace(text, "\n\n");
        text = BlockBreak().Replace(text, "\n");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        text = HorizontalSpace().Replace(text, " ");
        text = ExtraBlankLines().Replace(text, "\n\n");
        return string.Join('\n', text.Split('\n').Select(l => l.Trim())).Trim();
    }

    /// <param name="busy">Something is under way (a download): a small turning ring above the text.</param>
    public static string Placeholder(string text, bool busy = false) =>
        WrapDocument((busy ? BusyRing : "") + $"<p style=\"color:#888;text-align:center;margin-top:{(busy ? "12px" : "3em")}\">{WebUtility.HtmlEncode(text)}</p>",
            "default-src 'none'; style-src 'unsafe-inline'");

    private const string BusyRing =
        "<style>@keyframes turn{to{transform:rotate(360deg)}}</style>"
        + "<div style=\"width:18px;height:18px;margin:3em auto 0;box-sizing:border-box;border:2px solid rgba(136,136,136,.25);"
        + "border-top-color:#888;border-radius:50%;animation:turn .9s linear infinite\"></div>";

    /// <summary>Plain-text snippet for the message list and the search index.</summary>
    public static string ExtractText(MimeMessage message, int maxLength = int.MaxValue)
    {
        var text = message.TextBody;
        if (string.IsNullOrWhiteSpace(text) && message.HtmlBody is { } html)
            text = WebUtility.HtmlDecode(Tags().Replace(HeadAndScripts().Replace(html, " "), " "));
        text = Whitespace().Replace(text ?? "", " ").Trim();
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    private static string InlineCidImages(MimeMessage message, string html)
    {
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (string.IsNullOrEmpty(part.ContentId) || part.Content is null) continue;
            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            var dataUri = $"data:{part.ContentType.MimeType};base64,{Convert.ToBase64String(ms.ToArray())}";
            html = html.Replace($"cid:{part.ContentId}", dataUri, StringComparison.OrdinalIgnoreCase);
        }
        return html;
    }

    private static string TextToHtml(string text)
    {
        var encoded = WebUtility.HtmlEncode(text);
        var linked = Url().Replace(encoded, m => $"<a href=\"{m.Value}\">{m.Value}</a>");
        // The font is the page's (Settings > Appearance), or the editor's when quoted into a reply.
        return $"<div style=\"white-space:pre-wrap\">{linked}</div>";
    }

    /// <summary>
    /// Slim scrollbars matching the app's own: hidden until the pointer is over the page, fading in and out
    /// (a registered colour property can transition; the scrollbar pseudo-elements themselves can't).
    /// </summary>
    public const string ScrollbarCss = """
        @property --mr-thumb { syntax: '<color>'; inherits: true; initial-value: transparent; }
        :root { --mr-thumb: transparent; transition: --mr-thumb .4s .3s; }
        :root:hover { --mr-thumb: rgba(0,0,0,.36); transition: --mr-thumb .15s; }
        ::-webkit-scrollbar { width: 12px; height: 12px; background: transparent; }
        ::-webkit-scrollbar-thumb { background-color: var(--mr-thumb); border-radius: 6px; border: 3px solid transparent; background-clip: padding-box; min-height: 32px; }
        ::-webkit-scrollbar-thumb:hover { background-color: rgba(0,0,0,.45); }
        ::-webkit-scrollbar-thumb:active { background-color: rgba(0,0,0,.55); }
        ::-webkit-scrollbar-button, ::-webkit-scrollbar-corner { display: none; background: transparent; }
        """;

    /// <summary>
    /// A picture that didn't load (not downloaded yet, or gone) shows as a soft grey box with its description, or a
    /// small picture outline when it has none, instead of the browser's broken-image icon. Chromium draws ::before and
    /// ::after on an image only when it failed, so pictures that load are untouched. inline-block keeps a failed
    /// picture's width and height (otherwise one with a description shrinks to its text); the box reaches 1px past the
    /// picture to cover the browser's own grey border.
    /// </summary>
    public const string MissingPictureCss = """
        :where(img) { position: relative; display: inline-block; }
        img::before {
          content: ""; position: absolute; inset: -1px; border-radius: 3px; background: #f1f2f4 no-repeat center / 18px
            url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='none' stroke='%23a3a9b2' stroke-width='1.5' stroke-linejoin='round'%3E%3Crect x='3' y='5' width='18' height='14' rx='2'/%3E%3Cpath d='M3 16l5-5 4 4 3-3 6 6'/%3E%3Ccircle cx='16' cy='9' r='1.5'/%3E%3C/svg%3E");
        }
        img[alt]:not([alt=""])::before { background-image: none; }
        img::after {
          content: attr(alt); position: absolute; inset: 0; display: flex; align-items: center; justify-content: center;
          padding: 0 6px; overflow: hidden; white-space: nowrap; text-overflow: ellipsis;
          font: 12px/1.3 'Segoe UI', sans-serif; color: #8b919a;
        }
        """;

    /// <summary>
    /// A rendered message (from <see cref="Render"/>) made ready to print: the subject as the page's title (printed
    /// headers show it, not "about:blank"), and who/when above the message, as mail apps print it.
    /// </summary>
    public static string ForPrint(string renderedHtml, string title, IEnumerable<(string Label, string Value)> header)
    {
        renderedHtml = WithoutDarkStyle(renderedHtml);
        var rows = string.Concat(header.Where(h => !string.IsNullOrWhiteSpace(h.Value)).Select(h =>
            $"<tr><th>{WebUtility.HtmlEncode(h.Label)}</th><td>{WebUtility.HtmlEncode(h.Value)}</td></tr>"));
        var head = $$"""
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
              /* The margins come from the print window; the page number sits in the bottom one. */
              @page { @bottom-right { content: counter(page) " / " counter(pages); font: 8pt 'Segoe UI', sans-serif; color: #8a8a8a; } }
              /* Colours as on screen: newsletters put white text on coloured bands, which would vanish without them. */
              * { -webkit-print-color-adjust: exact !important; print-color-adjust: exact !important; }
              .mr-print-head { font: 13px/1.45 'Segoe UI', sans-serif; color: #1f1f1f; margin: 0 0 14px; padding-bottom: 10px; border-bottom: 1px solid #ccc; }
              .mr-print-head h1 { font-size: 17px; font-weight: 600; margin: 0 0 8px; }
              .mr-print-head table { border-collapse: collapse; }
              .mr-print-head th { color: #666; font-weight: normal; text-align: left; vertical-align: top; padding: 1px 14px 1px 0; white-space: nowrap; }
              .mr-print-head td { padding: 1px 0; word-break: break-word; }
            </style>
            """;
        var block = $"<div class=\"mr-print-head\"><h1>{WebUtility.HtmlEncode(title)}</h1><table>{rows}</table></div>";
        var charset = renderedHtml.IndexOf("<meta charset=\"utf-8\">", StringComparison.Ordinal);
        var html = charset < 0 ? renderedHtml : renderedHtml.Insert(charset + "<meta charset=\"utf-8\">".Length, head);
        // The message's own <body> keeps its attributes (a colour, a class), so the block goes after whatever tag it is.
        var body = BodyTag().Match(html);
        return body.Success ? html.Insert(body.Index + body.Length, block) : html;
    }

    /// <summary>The pane's page around plain text (and its notices): our font and spacing, as nothing of the sender's is there; dark in dark mode.</summary>
    private static string WrapDocument(string body, string csp) => $$"""
        <!doctype html>
        <html><head>
        <meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="{{csp}}">
        <style>
          html, body { margin: 0; padding: 0; }
          body { {{TextCss}}; line-height: 1.5; color: #1f1f1f; padding: 16px 20px; word-wrap: break-word; }
          a { color: {{LinkColor}}; }
        {{ScrollbarCss}}
        </style>
        {{(Dark is { } dark ? DarkStyle(dark) : "")}}
        </head><body>{{body}}</body></html>
        """;

    // Anything the message would fetch from the web: pictures (src, srcset, poster, background), CSS url()s and
    // @imports, stylesheet links. It decides whether "Download pictures" is offered.
    [GeneratedRegex(@"(?:\b(?:src|srcset|poster|background)\s*=\s*[""']?\s*[^""'>]*?https?:|url\(\s*[""']?\s*https?:|@import\s+[""']\s*https?:|<link\b[^>]*\bhref\s*=\s*[""']?\s*https?:)", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteReference();

    [GeneratedRegex(@"https?://[^\s<>""']+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"<body\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BodyTag();

    [GeneratedRegex(@"<(head|script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HeadAndScripts();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"</(p|h[1-6]|blockquote|table)>", RegexOptions.IgnoreCase)]
    private static partial Regex ParagraphEnd();

    [GeneratedRegex(@"<br\s*/?>|</(div|li|tr)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex HorizontalSpace();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex ExtraBlankLines();
}
