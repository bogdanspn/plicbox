using System.Net;

namespace Plicbox.Mail;

/// <summary>
/// A mailto: link (RFC 6068): the addresses, subject and text a new message starts with. Anything else a link may ask
/// for (attaching files, other headers) is ignored.
/// </summary>
public sealed record MailtoLink(string To, string Cc, string Bcc, string Subject, string Body)
{
    private const string Scheme = "mailto:";

    public static bool Is(string? link) => link?.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) == true;

    public static MailtoLink? Parse(string? link)
    {
        if (!Is(link)) return null;
        var rest = link![Scheme.Length..].Split('#')[0];
        var question = rest.IndexOf('?');
        List<string> to = [], cc = [], bcc = [];
        var (subject, body) = ("", "");
        Add(to, question < 0 ? rest : rest[..question]);
        foreach (var field in question < 0 ? [] : rest[(question + 1)..].Split('&'))
        {
            var equals = field.IndexOf('=');
            if (equals <= 0) continue;
            var value = field[(equals + 1)..];
            switch (field[..equals].ToLowerInvariant())
            {
                case "to": Add(to, value); break;
                case "cc": Add(cc, value); break;
                case "bcc": Add(bcc, value); break;
                case "subject": subject = OneLine(value); break;
                case "body": body = body.Length == 0 ? Text(value) : body + "\n" + Text(value); break;
            }
        }
        return new MailtoLink(string.Join(", ", to), string.Join(", ", cc), string.Join(", ", bcc), subject, body);
    }

    /// <summary>The text as the editor shows it: never as HTML.</summary>
    public string BodyHtml => string.Concat(Body.Split('\n').Select(line => $"<div>{(line.Length == 0 ? "<br>" : WebUtility.HtmlEncode(line))}</div>"));

    private static void Add(List<string> list, string value)
    {
        if (OneLine(value).Trim(' ', ',') is { Length: > 0 } addresses) list.Add(addresses);
    }

    // "+" stays "+" (it's only a space in web forms), so ana+news@example.com arrives whole.
    private static string Text(string value) => Clean(Uri.UnescapeDataString(value).ReplaceLineEndings("\n"), keepLines: true);
    private static string OneLine(string value) => Clean(Uri.UnescapeDataString(value).ReplaceLineEndings(" "), keepLines: false);

    private static string Clean(string text, bool keepLines) =>
        new(text.Where(c => !char.IsControl(c) || c == '\t' || keepLines && c == '\n').ToArray());
}
