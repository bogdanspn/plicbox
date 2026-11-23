using Plicbox.Core;
using Plicbox.Mail;
using MimeKit;

namespace Plicbox.Tests;

public sealed class ComposingTests
{
    private static readonly Account Me = new()
    {
        Id = 1, DisplayName = "Me Myself", Email = "me@example.com", Username = "me@example.com", ImapHost = "i", SmtpHost = "s",
    };
    private readonly MessageRenderer _renderer = new();

    private static MimeMessage Original()
    {
        var m = new MimeMessage { Subject = "Plans", MessageId = "orig@x" };
        m.From.Add(new MailboxAddress("Ana", "ana@example.org"));
        m.To.Add(new MailboxAddress("Me", "me@example.com"));
        m.To.Add(new MailboxAddress("Bo", "bo@example.org"));
        m.Cc.Add(new MailboxAddress("Cy", "cy@example.org"));
        m.Cc.Add(new MailboxAddress("Me again", "ME@example.com"));
        m.References.Add("root@x");
        m.Body = new TextPart("html") { Text = "<p>Hi <script>x()</script>there</p>" };
        return m;
    }

    [Fact]
    public void Reply_goes_to_sender_only_with_threading_headers()
    {
        var d = Composing.Build(ComposeMode.Reply, Original(), Me, _renderer);
        Assert.Equal("\"Ana\" <ana@example.org>", d.To);
        Assert.Equal("", d.Cc);
        Assert.Equal("Re: Plans", d.Subject);
        Assert.Equal("orig@x", d.InReplyTo);
        Assert.Equal(["root@x", "orig@x"], d.References);
        Assert.Contains("<blockquote", d.BodyHtml);
        Assert.DoesNotContain("<script", d.BodyHtml);
    }

    [Fact]
    public void Reply_all_includes_everyone_except_me()
    {
        var d = Composing.Build(ComposeMode.ReplyAll, Original(), Me, _renderer);
        Assert.Contains("ana@example.org", d.To);
        Assert.Contains("bo@example.org", d.To);
        Assert.DoesNotContain("me@example.com", d.To, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("\"Cy\" <cy@example.org>", d.Cc);
    }

    [Fact]
    public void Reply_honours_reply_to_and_does_not_double_prefix()
    {
        var o = Original();
        o.Subject = "RE: Plans";
        o.ReplyTo.Add(new MailboxAddress("List", "list@example.org"));
        var d = Composing.Build(ComposeMode.Reply, o, Me, _renderer);
        Assert.Equal("\"List\" <list@example.org>", d.To);
        Assert.Equal("RE: Plans", d.Subject);
    }

    [Fact]
    public void Forward_keeps_attachments_and_has_no_recipients()
    {
        var o = Original();
        var builder = new BodyBuilder { HtmlBody = "<p>see file</p>" };
        builder.Attachments.Add("a.pdf", new byte[] { 1 }, new ContentType("application", "pdf"));
        o.Body = builder.ToMessageBody();

        var d = Composing.Build(ComposeMode.Forward, o, Me, _renderer);
        Assert.Equal("", d.To);
        Assert.Equal("Fwd: Plans", d.Subject);
        Assert.Single(d.ForwardedAttachments);
        Assert.Contains("Forwarded message", d.BodyHtml);
    }

    [Fact]
    public void Outgoing_message_has_html_and_text_parts_and_accepts_semicolons()
    {
        var msg = Composing.ToMimeMessage(Me, new ComposeDraft { InReplyTo = "orig@x", References = ["orig@x"] },
            "a@example.org; B <b@example.org>", "", "", "Hi", "<p>Hello<br>world</p>", []);
        Assert.Equal(2, msg.To.Count);
        Assert.Equal("Me Myself", ((MailboxAddress)msg.From[0]).Name);
        Assert.Equal("orig@x", msg.InReplyTo);
        Assert.Equal("Hello\nworld", msg.TextBody!.Trim().ReplaceLineEndings("\n"));
        Assert.Contains("<p>Hello", msg.HtmlBody);
    }

    [Fact]
    public void Forward_as_attachment_attaches_the_untouched_original()
    {
        var d = Composing.Build(ComposeMode.ForwardAsAttachment, Original(), Me, _renderer);
        Assert.Equal("Fwd: Plans", d.Subject);
        var part = Assert.IsType<MessagePart>(Assert.Single(d.ForwardedAttachments));
        Assert.Equal("Plans.eml", part.ContentDisposition!.FileName);
        Assert.Equal("orig@x", part.Message!.MessageId);
        Assert.DoesNotContain("blockquote", d.BodyHtml);

        var sent = Composing.ToMimeMessage(Me, d, "a@example.org", "", "", d.Subject, d.BodyHtml, []);
        Assert.Contains(sent.BodyParts, p => p is MessagePart);
    }

    [Fact]
    public void Forward_as_original_has_the_text_without_a_forward_header()
    {
        var d = Composing.Build(ComposeMode.ForwardAsOriginal, Original(), Me, _renderer);
        Assert.Equal("Fwd: Plans", d.Subject);
        Assert.Equal("", d.To);
        Assert.Contains("Hi", d.BodyHtml);
        Assert.DoesNotContain("Forwarded message", d.BodyHtml);
        Assert.DoesNotContain("<blockquote", d.BodyHtml);
    }

    [Fact]
    public void Resend_keeps_recipients_subject_and_my_from_address()
    {
        var sent = new MimeMessage { Subject = "Invoice", MessageId = "sent@x" };
        sent.From.Add(new MailboxAddress("Me", "me@example.com"));
        sent.To.Add(new MailboxAddress("Ana", "ana@example.org"));
        sent.Cc.Add(new MailboxAddress("Bo", "bo@example.org"));
        sent.Body = new TextPart("html") { Text = "<p>Attached</p>" };

        var d = Composing.Build(ComposeMode.Resend, sent, Me, _renderer);
        Assert.Equal("me@example.com", d.FromAddress);
        Assert.Equal("\"Ana\" <ana@example.org>", d.To);
        Assert.Equal("\"Bo\" <bo@example.org>", d.Cc);
        Assert.Equal("Invoice", d.Subject);
        Assert.Contains("Attached", d.BodyHtml);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("not an address", "")]
    public void Outgoing_message_rejects_missing_or_bad_recipients(string to, string cc)
    {
        Assert.Throws<FormatException>(() => Composing.ToMimeMessage(Me, new ComposeDraft(), to, cc, "", "s", "<p>x</p>", []));
    }

    [Fact]
    public void Addresses_left_as_plain_text_become_links_when_sending()
    {
        var html = "<p>Videos: https://drive.google.com/drive/folders/1md?usp=sharing</p>"
                   + "<p>(see www.example.com/a_(b)), or <a href=\"https://kept.example\">https://kept.example</a>.</p>"
                   + "<p>Not these: https:// and www. and mail@www.example.com</p>";
        var msg = Composing.ToMimeMessage(Me, new ComposeDraft(), "a@example.org", "", "", "Links", html, []);

        var body = msg.HtmlBody!;
        Assert.Contains("<a href=\"https://drive.google.com/drive/folders/1md?usp=sharing\">https://drive.google.com/drive/folders/1md?usp=sharing</a></p>", body);
        Assert.Contains("(see <a href=\"http://www.example.com/a_(b)\">www.example.com/a_(b)</a>),", body);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "kept\\.example\">"));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Matches(body, "<a ").Count);
    }

    [Theory]
    [InlineData("https://example.com/page.", "https://example.com/page")]
    [InlineData("https://en.wikipedia.org/wiki/Mail_(app)", "https://en.wikipedia.org/wiki/Mail_(app)")]
    [InlineData("https://example.com/x)", "https://example.com/x")]
    [InlineData("https://example.com/?q=1\",", "https://example.com/?q=1")]
    public void Trailing_punctuation_is_not_part_of_a_link(string typed, string expected) =>
        Assert.Equal(expected, Composing.TrimUrl(typed));

    [Fact]
    public void Pasted_pictures_are_sent_as_embedded_parts()
    {
        var png = Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var html = $"<p>Screenshot:</p><p><img src=\"data:image/png;base64,{png}\"></p>";
        var msg = Composing.ToMimeMessage(Me, new ComposeDraft(), "a@example.org", "", "", "Pic", html, []);

        Assert.DoesNotContain("data:", msg.HtmlBody);
        var related = Assert.IsType<MultipartRelated>(msg.Body is MultipartAlternative alt ? alt.OfType<MultipartRelated>().Single() : msg.Body);
        var image = related.OfType<MimePart>().Single(p => p.ContentType.MimeType == "image/png");
        Assert.Contains($"src=\"cid:{image.ContentId}\"", msg.HtmlBody);
        using var ms = new MemoryStream();
        image.Content!.DecodeTo(ms);
        Assert.Equal(Convert.FromBase64String(png), ms.ToArray());
        Assert.Empty(msg.Attachments);
    }

    [Fact]
    public void Importance_read_receipt_and_reply_to_are_set()
    {
        var draft = new ComposeDraft { Importance = MessageImportance.High, ReadReceipt = true, ReplyTo = "team@example.org" };
        var msg = Composing.ToMimeMessage(Me, draft, "a@example.org", "", "", "s", "<p>x</p>", []);
        Assert.Equal(MessageImportance.High, msg.Importance);
        Assert.Equal(XMessagePriority.Highest, msg.XPriority);
        Assert.Contains("me@example.com", msg.Headers[HeaderId.DispositionNotificationTo]);
        Assert.Equal("team@example.org", msg.ReplyTo.Mailboxes.Single().Address);
    }

    [Fact]
    public void Plain_text_messages_have_no_html_part()
    {
        var msg = Composing.ToMimeMessage(Me, new ComposeDraft { PlainText = true }, "a@example.org", "", "", "s", "line one\nline <two>", []);
        Assert.Null(msg.HtmlBody);
        Assert.Equal("line one\nline <two>", msg.TextBody!.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Drafts_keep_half_typed_recipients_and_open_again_as_they_were()
    {
        var png = Convert.ToBase64String([1, 2, 3]);
        var draft = new ComposeDraft
        {
            Importance = MessageImportance.Low, ReadReceipt = true, ReplyTo = "team@example.org",
            InReplyTo = "orig@x", References = ["orig@x"],
            ForwardedAttachments = [new MimePart("application", "pdf") { FileName = "a.pdf", Content = new MimeContent(new MemoryStream([1])) }],
        };
        var saved = Composing.ToMimeMessage(Me, draft, "ana@example.org, bo@", "", "cy@example.org", "Half done",
            $"<p>Hi</p><p><img src=\"data:image/png;base64,{png}\"></p>", [], asDraft: true);

        var reopened = Composing.FromDraft(saved, Me, _renderer);
        Assert.Equal("ana@example.org", reopened.To);
        Assert.Equal("cy@example.org", reopened.Bcc);
        Assert.Equal("team@example.org", reopened.ReplyTo);
        Assert.Equal("Half done", reopened.Subject);
        Assert.Equal(saved.MessageId, reopened.MessageId);
        Assert.Equal("orig@x", reopened.InReplyTo);
        Assert.Equal(MessageImportance.Low, reopened.Importance);
        Assert.True(reopened.ReadReceipt);
        Assert.False(reopened.PlainText);
        Assert.Contains("data:image/png;base64,", reopened.BodyHtml); // the picture is back in the text...
        Assert.Equal("a.pdf", Assert.Single(reopened.ForwardedAttachments) is MimePart p ? p.FileName : null); // ...not an attachment
    }

    [Fact]
    public void Draft_message_id_is_kept_so_saving_again_replaces_it()
    {
        var archived = Me with { BccAddress = "archive@example.org" };
        var first = Composing.ToMimeMessage(archived, new ComposeDraft(), "", "", "", "s", "<p>x</p>", [], asDraft: true);
        var again = Composing.ToMimeMessage(archived, new ComposeDraft { MessageId = first.MessageId }, "", "", "", "s", "<p>y</p>", [], asDraft: true);
        Assert.Equal(first.MessageId, again.MessageId);
        Assert.Empty(again.Bcc); // the automatic Bcc is only added when sending
    }

    [Fact]
    public void Plain_text_drafts_reopen_in_plain_text()
    {
        var saved = Composing.ToMimeMessage(Me, new ComposeDraft { PlainText = true }, "", "", "", "s", "a < b", [], asDraft: true);
        var reopened = Composing.FromDraft(saved, Me, _renderer);
        Assert.True(reopened.PlainText);
        Assert.Equal("a &lt; b", reopened.BodyHtml.Trim());
    }
}

public sealed class MailHelpersTests
{
    [Theory]
    [InlineData("<a@x>", null, null, "a@x")]
    [InlineData("<b@x>", "<A@X>", null, "a@x")]
    [InlineData("<c@x>", null, "<Parent@x>", "parent@x")]
    [InlineData(null, null, null, null)]
    public void Thread_key_prefers_root_reference(string? id, string? firstRef, string? inReplyTo, string? expected)
    {
        Assert.Equal(expected, ThreadKeys.For(id, firstRef is null ? null : [firstRef, "<later@x>"], inReplyTo));
    }

    [Theory]
    [InlineData("INBOX.Clients.Acme", '.', "Clients / Acme")]
    [InlineData("Projects/2026", '/', "Projects / 2026")]
    [InlineData("INBOX", '.', "INBOX")]
    public void Friendly_path_strips_inbox_namespace(string full, char sep, string expected) =>
        Assert.Equal(expected, FolderRoles.FriendlyPath(full, sep));

    [Fact]
    public void Autoconfig_prefers_ssl_and_rejects_plaintext_only()
    {
        const string xml = """
            <clientConfig version="1.1"><emailProvider id="example.com">
              <incomingServer type="pop3"><hostname>pop.example.com</hostname><port>995</port><socketType>SSL</socketType></incomingServer>
              <incomingServer type="imap"><hostname>imap.example.com</hostname><port>143</port><socketType>STARTTLS</socketType><username>%EMAILADDRESS%</username></incomingServer>
              <incomingServer type="imap"><hostname>imap.example.com</hostname><port>993</port><socketType>SSL</socketType><username>%EMAILADDRESS%</username></incomingServer>
              <outgoingServer type="smtp"><hostname>smtp.example.com</hostname><port>587</port><socketType>STARTTLS</socketType></outgoingServer>
            </emailProvider></clientConfig>
            """;
        var s = AccountDiscovery.ParseAutoconfig(xml)!;
        Assert.Equal(("imap.example.com", 993, "smtp.example.com", 587), (s.ImapHost, s.ImapPort, s.SmtpHost, s.SmtpPort));
        Assert.Equal((TlsMode.Tls, TlsMode.StartTls), (s.ImapSecurity, s.SmtpSecurity));

        var plain = xml.Replace("SSL", "plain").Replace("STARTTLS", "plain");
        Assert.Null(AccountDiscovery.ParseAutoconfig(plain));
    }

    [Theory]
    [InlineData("imap.gmail.com", ProviderKind.Gmail)]
    [InlineData("imap.mail.me.com", ProviderKind.ICloud)]
    [InlineData("imap.mail.yahoo.com", ProviderKind.Yahoo)]
    [InlineData("mail.bogus.example", ProviderKind.Imap)]
    public void Provider_detected_from_imap_host(string host, ProviderKind expected) =>
        Assert.Equal(expected, AccountDiscovery.ProviderFor(host));

    [Theory]
    [InlineData(new[] { "aspmx.l.google.com", "alt1.aspmx.l.google.com" }, ProviderKind.Gmail)]
    [InlineData(new[] { "smtp.google.com" }, ProviderKind.Gmail)]
    [InlineData(new[] { "mx01.mail.icloud.com" }, ProviderKind.ICloud)]
    [InlineData(new[] { "mta5.am0.yahoodns.net" }, ProviderKind.Yahoo)]
    public void Hosted_domains_recognised_from_mx(string[] mx, ProviderKind expected) =>
        Assert.Equal(expected, AccountDiscovery.MxProvider(mx));

    [Theory]
    [InlineData("mx.zoho.eu", "imap.zoho.eu", "smtp.zoho.eu")]
    [InlineData("mx2.zoho.com", "imap.zoho.com", "smtp.zoho.com")]
    [InlineData("in1-smtp.messagingengine.com", "imap.fastmail.com", "smtp.fastmail.com")]
    [InlineData("contoso-com.mail.protection.outlook.com", "outlook.office365.com", "smtp.office365.com")]
    public void More_hosts_recognised_from_mx(string mx, string imap, string smtp)
    {
        var s = AccountDiscovery.FromMx([mx])!;
        Assert.Equal((imap, smtp), (s.ImapHost, s.SmtpHost));
    }

    [Theory]
    [InlineData("imap.gmail.com", "Google", true)]
    [InlineData("smtp.gmail.com", "Google", true)]
    [InlineData("IMAP.MAIL.ME.COM", "Apple", true)]
    [InlineData("imap.mail.yahoo.com", "Yahoo", true)]
    [InlineData("imap.aol.com", "AOL", true)]
    [InlineData("imap.fastmail.com", "Fastmail", true)]
    [InlineData("imap.web.de", "WEB.DE", false)]
    [InlineData("mail.gmx.net", "GMX", false)]
    [InlineData("imappro.zoho.eu", "Zoho", false)]
    public void Providers_say_what_they_need_first(string host, string name, bool appPassword)
    {
        var help = AccountDiscovery.HelpFor(host)!;
        Assert.Equal((name, appPassword), (help.Name, help.AppPassword));
        if (appPassword) Assert.NotNull(help.Url); // every app-password provider links to where it's made
    }

    [Theory]
    [InlineData("imappro.zoho.eu", "https://accounts.zoho.eu/")]
    [InlineData("imap.zoho.com", "https://accounts.zoho.com/")]
    [InlineData("imap.zoho.com.au", "https://accounts.zoho.com.au/")]
    public void Zoho_links_to_app_passwords_in_its_own_region(string host, string site)
    {
        // App-specific passwords are only needed with two-factor sign-in, so Zoho doesn't count as an app-password provider.
        var help = AccountDiscovery.HelpFor(host)!;
        Assert.StartsWith(site, help.Url);
        Assert.Contains("two-factor", help.Steps);
    }

    [Theory]
    [InlineData("mail.myhost.example")]
    [InlineData("notgmail.com")]
    [InlineData("imap.zoho.example")]
    public void Other_hosts_need_nothing_first(string host) => Assert.Null(AccountDiscovery.HelpFor(host));

    [Fact]
    public void A_rejected_password_names_the_app_password_provider()
    {
        var rejected = new MailKit.Security.AuthenticationException();
        Assert.Contains("Yahoo", AccountDiscovery.Explain(rejected, "imap.mail.yahoo.com"));
        // A Google sign-in account never had a password to replace with an app password.
        Assert.DoesNotContain("app password", AccountDiscovery.Explain(rejected, "imap.gmail.com", AuthKind.GoogleOAuth));
        Assert.Equal("The server rejected the user name or password.", AccountDiscovery.Explain(rejected, "mail.myhost.example"));
    }

    [Fact]
    public void Microsoft_hosted_mail_goes_to_microsoft_sign_in()
    {
        Assert.Equal(ProviderKind.Microsoft, AccountDiscovery.FromMx(["contoso-com.mail.protection.outlook.com"])!.Provider);
        Assert.Equal(ProviderKind.Microsoft, AccountDiscovery.ProviderFor("outlook.office365.com"));
        Assert.Equal(ProviderKind.Microsoft, AccountDiscovery.ProviderFor("imap-mail.outlook.com"));
        Assert.NotEqual(ProviderKind.Microsoft, AccountDiscovery.FromMx(["mx.zoho.eu"])!.Provider);
        Assert.Null(AccountDiscovery.FromMx(["mail.myhost.example", "mx.notzoho.example"]));
    }

    [Theory]
    [InlineData("mx00.ionos.de", "ionos.de")]
    [InlineData("mx1.mailhost.co.uk", "mailhost.co.uk")]
    [InlineData("mail.example.com", "example.com")]
    [InlineData("localhost", null)]
    public void Mail_host_domain_for_the_shared_database(string host, string? expected) =>
        Assert.Equal(expected, AccountDiscovery.BaseDomain(host));

    [Fact]
    public void Certificate_names_come_from_its_alternative_names()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=server123.host.example", key, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        var names = new System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder();
        names.AddDnsName("server123.host.example");
        names.AddDnsName("*.host.example");
        request.CertificateExtensions.Add(names.Build());
        using var cert = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(1));
        Assert.Equal(["server123.host.example", "*.host.example"], AccountDiscovery.CertificateNames(cert));
    }

    [Fact]
    public void Unknown_mx_is_not_guessed() =>
        Assert.Null(AccountDiscovery.MxProvider(["mail.myhost.example", "evilgoogle.com.example"]));

    [Fact]
    public void Html_to_text_keeps_paragraphs()
    {
        Assert.Equal("Hi\n\nSecond & last", MessageRenderer.HtmlToText("<p>Hi</p><p>Second &amp; <b>last</b></p>").ReplaceLineEndings("\n").Replace("\n\n\n", "\n\n"));
    }

    [Theory]
    [InlineData(993, MailKit.Security.SecureSocketOptions.SslOnConnect)]
    [InlineData(465, MailKit.Security.SecureSocketOptions.SslOnConnect)]
    [InlineData(587, MailKit.Security.SecureSocketOptions.StartTls)]
    [InlineData(143, MailKit.Security.SecureSocketOptions.StartTls)]
    public void Never_plaintext(int port, MailKit.Security.SecureSocketOptions expected) =>
        Assert.Equal(expected, MailConnector.SecurityFor(port));
}
