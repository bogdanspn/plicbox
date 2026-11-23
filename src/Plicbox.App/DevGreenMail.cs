#if DEBUG
using System.IO;
using System.Text;
using Plicbox.Core;
using Plicbox.Mail;
using MimeKit;

namespace Plicbox.App;

/// <summary>
/// Debug-only: run the real app against a local GreenMail server (IMAPS 3993 / SMTPS 3465, user alice/secret)
/// with a throwaway database and an in-memory password store. Start with --dev-greenmail.
/// </summary>
internal static class DevGreenMail
{
    public const string Flag = "--dev-greenmail";

    public sealed class MemoryCredentials : ICredentialStore
    {
        private readonly Dictionary<long, string> _secrets = [];
        public void Save(long accountId, string secret) => _secrets[accountId] = secret;
        public string? Get(long accountId) => _secrets.GetValueOrDefault(accountId);
        public void Delete(long accountId) => _secrets.Remove(accountId);
        public void SaveSmtp(long accountId, string secret) => _secrets[-accountId] = secret;
        public string? GetSmtp(long accountId) => _secrets.GetValueOrDefault(-accountId);
        public void DeleteSmtp(long accountId) => _secrets.Remove(-accountId);
    }

    public static async Task SeedAsync(MailEngine engine, IAccountRepository accounts)
    {
        MailConnector.CertificateValidationOverride = (_, _, _, _) => true; // GreenMail's self-signed cert
        await DeliverSamplesAsync();
        var account = new Account
        {
            DisplayName = "Alice Example", Email = "alice@localhost", Username = "alice",
            ImapHost = "127.0.0.1", ImapPort = 3993, SmtpHost = "127.0.0.1", SmtpPort = 3465,
        };
        if ((await accounts.GetAllAsync()).Count == 0) await engine.AddAccountAsync(account, "secret");
    }

    private static async Task DeliverSamplesAsync()
    {
        using var probe = await MailConnector.ConnectImapAsync(new ServerEndpoint("127.0.0.1", 3993), new MailLogin("alice", "secret"), default);
        await probe.Inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);
        if (probe.Inbox.Count > 0) return;

        // A few labels so the sidebar's "More" group has something in it.
        var root = probe.GetFolder(probe.PersonalNamespaces[0]);
        foreach (var name in new[] { "Sent", "Trash", "Drafts", "Junk", "Accounts", "Crypto", "Follow up", "Receipts" })
            await root.CreateAsync(name, true);

        using var smtp = await MailConnector.ConnectSmtpAsync(new ServerEndpoint("127.0.0.1", 3465), null, default);
        foreach (var msg in Samples()) await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);
    }

    private static IEnumerable<MimeMessage> Samples()
    {
        yield return Make("Diego Ruiz", "diego@example.com", "Q4 planning notes",
            "<p>Hi Alice,</p><p>Notes from today's planning are attached. Main themes: <b>search speed</b>, offline mode and calendar sync.</p><p>Diego</p>",
            attachment: ("planning-notes.txt", "Search speed\nOffline mode\nCalendar sync\n"));
        yield return Make("Fabrikam Records", "news@fabrikam.example", "New release: Night Transit",
            "<h2 style=\"color:#1f2937\">Night Transit</h2><p>Eight tracks of late-night synth.</p><img src=\"https://images.example/cover.jpg\" width=\"400\"><img src=\"https://track.example/open.gif\" width=\"1\" height=\"1\">");
        var first = Make("Ana Popescu", "ana@example.org", "Weekend plans", "<p>Are we still on for Saturday?</p>");
        yield return first;
        var reply = Make("Ana Popescu", "ana@example.org", "Re: Weekend plans", "<p>I'll bring coffee ☕</p>");
        reply.InReplyTo = first.MessageId;
        reply.References.Add(first.MessageId!);
        yield return reply;
    }

    private static MimeMessage Make(string name, string from, string subject, string html, (string Name, string Text)? attachment = null)
    {
        var msg = new MimeMessage { Subject = subject, Date = DateTimeOffset.Now };
        msg.From.Add(new MailboxAddress(name, from));
        msg.To.Add(new MailboxAddress("Alice Example", "alice@localhost"));
        var builder = new BodyBuilder { HtmlBody = html };
        if (attachment is { } a) builder.Attachments.Add(a.Name, Encoding.UTF8.GetBytes(a.Text), new ContentType("text", "plain"));
        msg.Body = builder.ToMessageBody();
        return msg;
    }
}
#endif
