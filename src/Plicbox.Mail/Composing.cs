using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Plicbox.Core;
using MimeKit;
using MimeKit.Utils;

namespace Plicbox.Mail;

public enum ComposeMode
{
    New, Reply, ReplyAll, Forward,
    /// <summary>The original goes along as an attached .eml, untouched.</summary>
    ForwardAsAttachment,
    /// <summary>The original's text and attachments, without the "Forwarded message" header or quoting.</summary>
    ForwardAsOriginal,
    /// <summary>A sent message again, to the same people, as a new message you can still edit.</summary>
    Resend,
}

/// <summary>Everything the compose window starts with.</summary>
public sealed record ComposeDraft
{
    public long AccountId { get; init; }
    /// <summary>Which of the account's identities to send from (null = the account's own address).</summary>
    public string? FromAddress { get; init; }
    public string To { get; init; } = "";
    public string Cc { get; init; } = "";
    public string Bcc { get; init; } = "";
    public string ReplyTo { get; init; } = "";
    public string Subject { get; init; } = "";
    /// <summary>Initial editor HTML: signature and/or quoted original. Already sanitized.</summary>
    public string BodyHtml { get; init; } = "";
    public string? InReplyTo { get; init; }
    public IReadOnlyList<string> References { get; init; } = [];
    /// <summary>Attachments carried over when forwarding or continuing a saved draft.</summary>
    public IReadOnlyList<MimeEntity> ForwardedAttachments { get; init; } = [];
    public MessageImportance Importance { get; init; } = MessageImportance.Normal;
    /// <summary>Ask the recipient's mail app to confirm when the message is read.</summary>
    public bool ReadReceipt { get; init; }
    /// <summary>Plain text instead of HTML; the body passed to <see cref="Composing.ToMimeMessage"/> is then plain text.</summary>
    public bool PlainText { get; init; }
    /// <summary>Set once the message has been saved as a draft: later saves replace the copy with this Message-ID.</summary>
    public string? MessageId { get; init; }
    /// <summary>
    /// Show the quoted original's web pictures while writing (you allowed them for that message). Otherwise they show
    /// as placeholders; either way they're sent as they were, as links.
    /// </summary>
    public bool ShowRemotePictures { get; init; }
}

public static partial class Composing
{
    public static ComposeDraft New(Account account) => new()
    {
        AccountId = account.Id,
        BodyHtml = "<div><br></div>" + SignatureHtml(account),
    };

    public static ComposeDraft Build(ComposeMode mode, MimeMessage original, Account account, MessageRenderer renderer)
    {
        var mine = account.Identities.Select(i => i.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Answer from whichever of my addresses the message was sent to (alias-aware, like eM Client).
        var recipients = original.To.Mailboxes.Concat(original.Cc.Mailboxes).Select(m => m.Address);
        var fromAddress = recipients.FirstOrDefault(mine.Contains) ?? account.Email;
        var quoted = renderer.SanitizeForQuote(original);
        var date = (original.Date == default ? DateTimeOffset.Now : original.Date).LocalDateTime;

        switch (mode)
        {
            case ComposeMode.ForwardAsAttachment:
                return new ComposeDraft
                {
                    AccountId = account.Id,
                    FromAddress = fromAddress,
                    Subject = Prefixed("Fwd: ", original.Subject),
                    BodyHtml = "<div><br></div>" + SignatureHtml(account),
                    ForwardedAttachments = [AsAttachment(original)],
                };
            case ComposeMode.ForwardAsOriginal:
                return new ComposeDraft
                {
                    AccountId = account.Id,
                    FromAddress = fromAddress,
                    Subject = Prefixed("Fwd: ", original.Subject),
                    BodyHtml = quoted,
                    ForwardedAttachments = original.Attachments.ToList(),
                };
            case ComposeMode.Resend:
                return new ComposeDraft
                {
                    AccountId = account.Id,
                    // Sent from one of my addresses: send it from that one again.
                    FromAddress = original.From.Mailboxes.FirstOrDefault(m => mine.Contains(m.Address))?.Address ?? fromAddress,
                    To = original.To.ToString(),
                    Cc = original.Cc.ToString(),
                    Bcc = original.Bcc.ToString(),
                    ReplyTo = original.ReplyTo.ToString(),
                    Subject = original.Subject ?? "",
                    BodyHtml = quoted,
                    InReplyTo = original.InReplyTo,
                    References = original.References.ToList(),
                    ForwardedAttachments = original.Attachments.ToList(),
                    Importance = original.Importance,
                };
        }

        if (mode == ComposeMode.Forward)
        {
            var lines = new List<string>
            {
                L.T("---------- Forwarded message ----------"),
                L.F("From: {0}", original.From),
                L.F("Date: {0}", date.ToString("f", CultureInfo.CurrentCulture)),
                L.F("Subject: {0}", original.Subject ?? ""),
                L.F("To: {0}", original.To),
            };
            if (original.Cc.Count > 0) lines.Add(L.F("Cc: {0}", original.Cc));
            var header = string.Concat(lines.Select(l => Enc(l) + "<br>\n"));
            return new ComposeDraft
            {
                AccountId = account.Id,
                FromAddress = fromAddress,
                Subject = Prefixed("Fwd: ", original.Subject),
                BodyHtml = $"<div><br></div>{SignatureHtml(account)}<div class=\"mr-quote-header\">{header}</div><br>{quoted}",
                ForwardedAttachments = original.Attachments.ToList(),
            };
        }

        var replyTo = original.ReplyTo.Mailboxes.Any() ? original.ReplyTo.Mailboxes : original.From.Mailboxes;
        var to = replyTo.ToList();
        var cc = new List<MailboxAddress>();
        if (mode == ComposeMode.ReplyAll)
        {
            to.AddRange(original.To.Mailboxes);
            cc.AddRange(original.Cc.Mailboxes);
        }
        to = Distinct(to.Where(m => !mine.Contains(m.Address)));
        cc = Distinct(cc.Where(m => !mine.Contains(m.Address) && !to.Any(t => SameAddress(t, m))));
        if (to.Count == 0) to = replyTo.ToList(); // replying to my own message

        var references = original.References.ToList();
        if (!string.IsNullOrEmpty(original.MessageId)) references.Add(original.MessageId);

        var who = original.From.Mailboxes.FirstOrDefault() is { } f ? (string.IsNullOrWhiteSpace(f.Name) ? f.Address : f.Name) : L.T("someone");
        return new ComposeDraft
        {
            AccountId = account.Id,
            FromAddress = fromAddress,
            To = string.Join(", ", to.Select(m => m.ToString())),
            Cc = string.Join(", ", cc.Select(m => m.ToString())),
            Subject = Prefixed("Re: ", original.Subject),
            BodyHtml = $"<div><br></div>{SignatureHtml(account)}" +
                       $"<div class=\"mr-quote-header\">{Enc(L.F("On {0}, {1} wrote:", date.ToString("f", CultureInfo.CurrentCulture), who))}</div>" +
                       $"<blockquote style=\"margin:0 0 0 .8ex;border-left:2px solid #ccc;padding-left:1ex\">{quoted}</blockquote>",
            InReplyTo = original.MessageId,
            References = references,
        };
    }

    /// <summary>Continues a message saved in Drafts: same recipients, options and Message-ID (so saving replaces it).</summary>
    public static ComposeDraft FromDraft(MimeMessage saved, Account account, MessageRenderer renderer)
    {
        var plain = saved.HtmlBody is null;
        var html = saved.HtmlBody ?? "";
        // Pictures embedded in the text come back inline; only real attachments are listed as attachments.
        var attachments = saved.Attachments
            .Where(a => string.IsNullOrEmpty(a.ContentId) || !html.Contains("cid:" + a.ContentId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new ComposeDraft
        {
            AccountId = account.Id,
            FromAddress = saved.From.Mailboxes.FirstOrDefault()?.Address,
            To = saved.To.ToString(),
            Cc = saved.Cc.ToString(),
            Bcc = saved.Bcc.ToString(),
            ReplyTo = saved.ReplyTo.ToString(),
            Subject = saved.Subject ?? "",
            BodyHtml = plain ? Enc(saved.TextBody ?? "") : renderer.SanitizeForQuote(saved),
            InReplyTo = saved.InReplyTo,
            References = saved.References.ToList(),
            ForwardedAttachments = attachments,
            Importance = saved.Importance,
            ReadReceipt = saved.Headers.Contains(HeaderId.DispositionNotificationTo),
            PlainText = plain,
            MessageId = saved.MessageId,
        };
    }

    /// <summary>Builds the outgoing message. Throws <see cref="FormatException"/> with a readable message for bad addresses.</summary>
    /// <param name="body">The editor's HTML, or plain text when <see cref="ComposeDraft.PlainText"/> is set.</param>
    /// <param name="asDraft">Saving to Drafts: recipients may be missing or half-typed, and the automatic Bcc isn't added.</param>
    public static MimeMessage ToMimeMessage(Account account, ComposeDraft draft, string to, string cc, string bcc,
        string subject, string body, IEnumerable<string> attachmentPaths, MailAlias? from = null, bool asDraft = false)
    {
        var message = new MimeMessage();
        if (!string.IsNullOrEmpty(draft.MessageId)) message.MessageId = draft.MessageId;
        from ??= account.Identities.FirstOrDefault(i => i.Address.Equals(draft.FromAddress, StringComparison.OrdinalIgnoreCase))
                 ?? account.Identities[0];
        var sender = new MailboxAddress(string.IsNullOrWhiteSpace(from.Name) ? account.DisplayName : from.Name, from.Address);
        message.From.Add(sender);
        message.To.AddRange(ParseAddresses(to, L.T("To"), asDraft));
        message.Cc.AddRange(ParseAddresses(cc, L.T("Cc"), asDraft));
        message.Bcc.AddRange(ParseAddresses(bcc, L.T("Bcc"), asDraft));
        message.ReplyTo.AddRange(ParseAddresses(draft.ReplyTo, L.T("Reply to"), asDraft));
        if (!asDraft)
        {
            if (!string.IsNullOrWhiteSpace(account.BccAddress) && MailboxAddress.TryParse(account.BccAddress, out var copy)
                && !message.Bcc.Mailboxes.Any(m => string.Equals(m.Address, copy.Address, StringComparison.OrdinalIgnoreCase)))
                message.Bcc.Add(copy);
            if (message.To.Count + message.Cc.Count + message.Bcc.Count == 0)
                throw new FormatException(L.T("Add at least one recipient."));
        }

        message.Subject = subject;
        message.Date = DateTimeOffset.Now;
        if (draft.InReplyTo is not null) message.InReplyTo = draft.InReplyTo;
        foreach (var r in draft.References) message.References.Add(r);
        if (draft.Importance != MessageImportance.Normal)
        {
            message.Importance = draft.Importance;
            // Outlook and friends read X-Priority rather than Importance.
            message.XPriority = draft.Importance == MessageImportance.High ? XMessagePriority.Highest : XMessagePriority.Lowest;
        }
        if (draft.ReadReceipt) message.Headers[HeaderId.DispositionNotificationTo] = sender.ToString();

        var builder = new BodyBuilder();
        if (draft.PlainText)
        {
            builder.TextBody = body;
        }
        else
        {
            var (html, images) = PrepareHtml(body);
            // At the message text size set in Settings, the size it was written at.
            var size = MessageRenderer.TextSize.ToString(System.Globalization.CultureInfo.InvariantCulture);
            builder.HtmlBody = $"<!doctype html><html><head><meta charset=\"utf-8\"></head><body style=\"font-family:'Segoe UI',sans-serif;font-size:{size}px\">{html}</body></html>";
            builder.TextBody = MessageRenderer.HtmlToText(html);
            foreach (var image in images)
                builder.LinkedResources.Add(image.FileName, image.Data, ContentType.Parse(image.MimeType)).ContentId = image.ContentId;
        }
        foreach (var entity in draft.ForwardedAttachments) builder.Attachments.Add(entity);
        foreach (var path in attachmentPaths) builder.Attachments.Add(path);
        message.Body = builder.ToMessageBody();
        return message;
    }

    /// <summary>A picture pasted or inserted into the editor, sent as an embedded part referenced by cid:.</summary>
    public sealed record InlineImage(string ContentId, string FileName, string MimeType, byte[] Data);

    /// <summary>
    /// Final pass over the editor HTML before sending: web addresses still typed as plain text become links, and
    /// pictures held as data: URIs become embedded parts (many mail apps don't show data: images).
    /// </summary>
    public static (string Html, IReadOnlyList<InlineImage> Images) PrepareHtml(string html)
    {
        var doc = new HtmlParser().ParseDocument($"<!doctype html><html><body>{html}</body></html>");
        var body = doc.Body!;

        // Web pictures shown as placeholders in the editor go out with their own address again.
        var held = body.QuerySelectorAll($"img[{HeldSource}]").ToList();
        foreach (var img in held)
        {
            img.SetAttribute("src", img.GetAttribute(HeldSource));
            img.RemoveAttribute(HeldSource);
            if (img.GetAttribute("style") == PlaceholderStyle) img.RemoveAttribute("style");
            else img.SetAttribute("style", img.GetAttribute("style")?.Replace(PlaceholderStyle, "") ?? "");
        }

        var texts = body.Descendants<IText>()
            .Where(t => t.ParentElement?.Closest("a, style, script, textarea, title") is null && LinkCandidate().IsMatch(t.Data))
            .ToList();
        foreach (var text in texts) LinkifyText(doc, text);

        var images = new List<InlineImage>();
        foreach (var img in body.QuerySelectorAll("img[src^='data:' i]"))
        {
            var match = DataUri().Match(img.GetAttribute("src")!);
            if (!match.Success) continue;
            byte[] data;
            try { data = Convert.FromBase64String(match.Groups["data"].Value); }
            catch (FormatException) { continue; }
            var type = match.Groups["type"].Value.ToLowerInvariant();
            var cid = MimeUtils.GenerateMessageId();
            images.Add(new InlineImage(cid, $"image{images.Count + 1}.{ImageExtension(type)}", type, data));
            img.SetAttribute("src", "cid:" + cid);
        }
        return (texts.Count == 0 && images.Count == 0 && held.Count == 0 ? html : body.InnerHtml, images);
    }

    // The web picture's address while the editor shows a placeholder instead (put back by PrepareHtml).
    private const string HeldSource = "data-mr-src";
    private const string PlaceholderStyle = "background:#eeeeee;";
    // A quiet grey picture outline, the same size as the picture where the message gives one.
    private const string PlaceholderImage = "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPjxyZWN0IHdpZHRoPSIxMDAlIiBoZWlnaHQ9IjEwMCUiIGZpbGw9IiNlZWVlZWUiLz48c3ZnIHg9IjUwJSIgeT0iNTAlIiBvdmVyZmxvdz0idmlzaWJsZSI+PGcgdHJhbnNmb3JtPSJ0cmFuc2xhdGUoLTE2IC0xMikiIGZpbGw9Im5vbmUiIHN0cm9rZT0iI2FhYWFhYSIgc3Ryb2tlLXdpZHRoPSIyIiBzdHJva2UtbGluZWpvaW49InJvdW5kIj48cmVjdCB4PSIwIiB5PSIwIiB3aWR0aD0iMzIiIGhlaWdodD0iMjQiIHJ4PSIzIi8+PHBhdGggZD0iTTIgMjJsOS05IDcgNyA0LTQgOCA4Ii8+PGNpcmNsZSBjeD0iMjQiIGN5PSI3IiByPSIyLjUiLz48L2c+PC9zdmc+PC9zdmc+";

    /// <summary>
    /// For the editor: web pictures (a quoted newsletter's, say) become placeholders, so writing a reply doesn't contact
    /// the sender's servers. The address is kept and put back when the message is saved or sent.
    /// </summary>
    public static string HoldRemotePictures(string html)
    {
        if (!html.Contains("<img", StringComparison.OrdinalIgnoreCase)) return html;
        var doc = new HtmlParser().ParseDocument($"<!doctype html><html><body>{html}</body></html>");
        var remote = doc.Body!.QuerySelectorAll("img[src]")
            .Where(img => img.GetAttribute("src")!.TrimStart().StartsWith("http", StringComparison.OrdinalIgnoreCase)).ToList();
        if (remote.Count == 0) return html;
        foreach (var img in remote)
        {
            img.SetAttribute(HeldSource, img.GetAttribute("src"));
            img.SetAttribute("src", PlaceholderImage);
            img.SetAttribute("style", PlaceholderStyle + (img.GetAttribute("style") ?? ""));
        }
        return doc.Body.InnerHtml;
    }

    /// <summary>A web address with trailing sentence punctuation (and unbalanced closing brackets) removed.</summary>
    public static string TrimUrl(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (".,;:!?'\"»”’…".Contains(last)
                || (last == ')' && url.Count(c => c == ')') > url.Count(c => c == '('))
                || (last == ']' && url.Count(c => c == ']') > url.Count(c => c == '[')))
                url = url[..^1];
            else
                break;
        }
        return url;
    }

    private static void LinkifyText(IDocument doc, IText text)
    {
        var data = text.Data;
        var parent = text.Parent!;
        var position = 0;
        var changed = false;
        foreach (Match m in LinkCandidate().Matches(data))
        {
            var url = TrimUrl(m.Value);
            if (!IsLinkable(url)) continue;
            if (m.Index > position) parent.InsertBefore(doc.CreateTextNode(data[position..m.Index]), text);
            var a = doc.CreateElement("a");
            a.SetAttribute("href", url.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? "http://" + url : url);
            a.TextContent = url;
            parent.InsertBefore(a, text);
            position = m.Index + url.Length;
            changed = true;
        }
        if (changed) text.Data = data[position..];
    }

    // "https://" or "www." alone isn't an address; www. needs a dot after the host name ("www.example.com").
    private static bool IsLinkable(string url) =>
        url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? url.IndexOf('.', 4) > 4
            : url.Length > url.IndexOf("//", StringComparison.Ordinal) + 2;

    private static string ImageExtension(string mimeType) => mimeType switch
    {
        "image/jpeg" => "jpg",
        "image/svg+xml" => "svg",
        "image/x-icon" or "image/vnd.microsoft.icon" => "ico",
        _ => mimeType.StartsWith("image/", StringComparison.Ordinal) ? mimeType[6..] : "bin",
    };

    [GeneratedRegex(@"(?<![\w./@-])(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkCandidate();

    [GeneratedRegex(@"^data:(?<type>[\w.+-]+/[\w.+-]+)(?:;[^,;]*)*;base64,(?<data>[A-Za-z0-9+/=\s]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex DataUri();

    private static InternetAddressList ParseAddresses(string text, string field, bool lenient = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        // Accept ; as a separator too, as Outlook users type it.
        text = text.Replace(';', ',');
        if (InternetAddressList.TryParse(text, out var list) && list.Mailboxes.All(m => m.Address.Contains('@'))) return list;
        if (!lenient) throw new FormatException(L.F("Check the addresses in \"{0}\".", field));
        // A draft keeps whatever parts of a half-typed list are already valid.
        var kept = new InternetAddressList();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (MailboxAddress.TryParse(part, out var mailbox) && mailbox.Address.Contains('@')) kept.Add(mailbox);
        return kept;
    }

    /// <summary>The account's signature as an editor block (empty when it has none).</summary>
    public static string SignatureBlock(Account account)
    {
        if (string.IsNullOrWhiteSpace(account.Signature)) return "";
        // Plain-text signatures (as typed in Accounts) are escaped; anything with markup is used as HTML.
        var html = account.Signature.Contains('<')
            ? account.Signature
            : Enc(account.Signature).ReplaceLineEndings("<br>");
        return $"<div class=\"mr-signature\">-- <br>{html}</div>";
    }

    private static string SignatureHtml(Account account) => SignatureBlock(account) is { Length: > 0 } block ? block + "<br>" : "";

    /// <summary>The whole original message as an attachment (message/rfc822), named after its subject.</summary>
    private static MessagePart AsAttachment(MimeMessage original)
    {
        var name = string.IsNullOrWhiteSpace(original.Subject) ? L.T("message") : original.Subject.Trim();
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name.Length > 80) name = name[..80].TrimEnd();
        return new MessagePart
        {
            Message = original,
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = name + ".eml" },
        };
    }

    private static string Prefixed(string prefix, string? subject)
    {
        subject ??= "";
        return subject.StartsWith(prefix.Trim(), StringComparison.OrdinalIgnoreCase) ? subject : prefix + subject;
    }

    private static bool SameAddress(MailboxAddress a, MailboxAddress b) => string.Equals(a.Address, b.Address, StringComparison.OrdinalIgnoreCase);
    private static List<MailboxAddress> Distinct(IEnumerable<MailboxAddress> list) => list.DistinctBy(m => m.Address.ToLowerInvariant()).ToList();
    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
