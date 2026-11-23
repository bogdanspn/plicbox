using System.Text.RegularExpressions;
using System.Web;
using AngleSharp.Html.Parser;
using MimeKit;

namespace Plicbox.Mail;

/// <summary>
/// How to unsubscribe from a message, as eM Client does it: the unsubscribe link in the message itself, opened in the
/// browser; failing that, the List-Unsubscribe header's web address (RFC 2369), or its mailto address.
/// </summary>
/// <param name="BodyLink">The "Unsubscribe" link in the message (what the sender shows its readers).</param>
/// <param name="Web">The header's https address.</param>
/// <param name="Mailto">The header's address to send an "unsubscribe" email to.</param>
public sealed partial record UnsubscribeOption(Uri? BodyLink, Uri? Web, Uri? Mailto)
{
    /// <summary>The page to open in the browser, if there is one.</summary>
    public Uri? Page => BodyLink ?? Web;

    /// <summary>The message's way out, if it offers one.</summary>
    public static UnsubscribeOption? From(MimeMessage message)
    {
        Uri? web = null, mailto = null;
        if (message.Headers["List-Unsubscribe"] is { Length: > 0 } header)
            foreach (Match m in Bracketed().Matches(header))
            {
                if (!Uri.TryCreate(m.Groups[1].Value.Trim(), UriKind.Absolute, out var uri)) continue;
                if (uri.Scheme == Uri.UriSchemeHttps) web ??= uri;
                else if (uri.Scheme == Uri.UriSchemeMailto) mailto ??= uri;
                // plain http is left out: the request would travel unencrypted
            }
        var body = message.HtmlBody is { } html ? FindInBody(html) : null;
        return body is null && web is null && mailto is null ? null : new UnsubscribeOption(body, web, mailto);
    }

    /// <summary>
    /// The link whose words say unsubscribe (English, German, French, Spanish, Italian, Romanian), else one whose
    /// address does. Only web links.
    /// </summary>
    internal static Uri? FindInBody(string html)
    {
        if (!html.Contains("href", StringComparison.OrdinalIgnoreCase)) return null;
        var links = new HtmlParser().ParseDocument(html).QuerySelectorAll("a[href]")
            .Select(a => (Text: a.TextContent.Trim(), Href: a.GetAttribute("href")?.Trim() ?? ""))
            .Where(l => Uri.TryCreate(l.Href, UriKind.Absolute, out var u) && u.Scheme is "https" or "http")
            .ToList();
        var pick = links.FirstOrDefault(l => Words().IsMatch(l.Text)).Href
                   ?? links.FirstOrDefault(l => l.Href.Contains("unsubscribe", StringComparison.OrdinalIgnoreCase)).Href;
        return pick is null ? null : new Uri(pick);
    }

    /// <summary>The unsubscribe email a mailto: link asks for (to, subject and body from the link).</summary>
    public MimeMessage? BuildMail(MailboxAddress from)
    {
        if (Mailto is null) return null;
        var target = Uri.UnescapeDataString(Mailto.AbsoluteUri["mailto:".Length..].Split('?')[0]);
        if (!MailboxAddress.TryParse(target, out var to)) return null;
        var query = HttpUtility.ParseQueryString(Mailto.Query);
        var message = new MimeMessage();
        message.From.Add(from);
        message.To.Add(to);
        message.Subject = query["subject"] is { Length: > 0 } s ? s : "unsubscribe";
        message.Body = new TextPart("plain") { Text = query["body"] is { Length: > 0 } b ? b : "unsubscribe" };
        return message;
    }

    [GeneratedRegex(@"<([^>]+)>")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"unsubscribe|opt[\s-]?out|abmelden|abbestellen|austragen|d[ée]sabonner|d[ée]sinscri|darse de baja|darte de baja|cancella(?:re)? (?:l'?)?iscrizione|dezabon", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}
