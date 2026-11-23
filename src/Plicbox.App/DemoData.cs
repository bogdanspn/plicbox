using System.IO;
using System.Text;
using Plicbox.Core;
using Plicbox.Mail;
using MimeKit;

namespace Plicbox.App;

/// <summary>Seeds a throwaway database with fictional mail so the UI can be previewed without real accounts (--demo).</summary>
internal sealed class DemoData(IAccountRepository accounts, IFolderRepository folders, IMessageRepository messages, IBodyStore bodies)
{
    private long _uid;

    public async Task SeedAsync()
    {
        var now = DateTimeOffset.Now;
        var personal = await AddAccountAsync("Personal", "you@example.com", "#F7931E");
        var work = await AddAccountAsync("Work", "you@work.example", "#3B82F6");
        var p = await AddFoldersAsync(personal);
        var w = await AddFoldersAsync(work);
        const string me = "You";

        await AddAsync(p[FolderRole.Inbox], "Fabrikam Records", "news@fabrikam.example", "New release from Lumen Atlas: Night Transit", now.AddHours(-1.2),
            Newsletter("#1f2937", "Night Transit", "Lumen Atlas returns with eight tracks of late-night synth. Stream it now or grab the vinyl before it's gone.", remoteImage: true));
        await AddAsync(p[FolderRole.Inbox], "Northwind Pay", "service@northwind.example", "You received a payment", now.AddHours(-3),
            Simple("Hi,", "You received <b>€45.00</b> from Ana Popescu.", "The money is available in your balance now."), flagged: true);
        // A phishing sample: forged sender (failed DMARC), replies to a free mailbox, and attachments dressed up as documents.
        await AddAsync(p[FolderRole.Inbox], "Northwind Pay", "service@northwind.example", "Your account has been limited",
            now.AddHours(-2),
            Simple("Dear customer,", "We noticed unusual activity. Open the attached receipt and verify your account within 24 hours.", "Northwind Pay Security"),
            headers: [("Authentication-Results", "mx.demo.example; dkim=none; spf=fail smtp.mailfrom=northwind.example; dmarc=fail (p=REJECT sp=REJECT dis=REJECT) header.from=northwind.example")],
            replyTo: "northwind.security.team@gmail.com",
            attachments:
            [
                ("Payment_Receipt_2026-10-15_Ana_Popescu_Transfer_Confirmation.pdf.exe", "application/octet-stream"),
                ("Verify account.html", "text/html"),
                ("Quarterly-Statement-Northwind-Pay-Personal-Account-October-2026-final-version.pdf", "application/pdf"),
            ]);
        await AddAsync(p[FolderRole.Inbox], "Contoso Bank", "no-reply@contoso.example", "An update to our bank transfer terms", now.AddHours(-5),
            Simple("Dear customer,", "We're updating our terms for bank transfers from 1 December.", "The updated terms are attached as a PDF."),
            attachment: ("Transfer-Terms-2026.pdf", "application/pdf"));
        await AddAsync(p[FolderRole.Inbox], "Tailspin Travel", "trips@tailspin.example", "Your boarding pass for Friday", now.AddHours(-7),
            Simple("Ready for take-off,", "Your boarding pass for <b>OTP → MUC</b> on Friday 08:40 is ready.", "Gate opens 40 minutes before departure."),
            flagged: true, read: true, important: true);

        await AddAsync(p[FolderRole.Inbox], "Alpine Ski House", "hello@alpineski.example", "Finish setting up your account to unlock member benefits", now.AddDays(-1).AddHours(-1),
            Banner("Hello there,", "Thanks for joining Alpine Ski House.", "Please create a password to complete your account setup.", "Create password"));
        await AddAsync(p[FolderRole.Inbox], "Ana Popescu", "ana@example.org", "Weekend plans", now.AddDays(-1).AddHours(-6),
            Simple("Hey!", "Are we still on for the hike on Saturday?", "Ana"), read: true, thread: "weekend");
        await AddAsync(p[FolderRole.Sent], me, "you@example.com", "Re: Weekend plans", now.AddDays(-1).AddHours(-5),
            Simple("Yes!", "Let's meet at 9 at the trailhead.", ""), read: true, thread: "weekend", to: "ana@example.org");
        await AddAsync(p[FolderRole.Drafts], me, "you@example.com", "Notes for Monday's call", now.AddHours(-3),
            Simple("Hi team,", "A few things I'd like to cover on Monday…", ""), read: true, to: "team@work.example");
        // The website's main screenshot opens this one, so it reads like a real letter rather than a one-liner.
        await AddAsync(p[FolderRole.Inbox], "Ana Popescu", "ana@example.org", "Re: Weekend plans", now.AddDays(-1).AddHours(-2),
            """
            <p>Perfect, 9 it is!</p>
            <p>I checked the forecast: sunny until about three, so we should be back at the car before the clouds roll in. Here's what I'm thinking:</p>
            <ul>
              <li><b>Route:</b> the ridge loop, about 14 km and 600 m up. I've attached the map.</li>
              <li><b>Lunch:</b> the hut at the top does soup and blueberry pie, so we can pack light.</li>
              <li><b>Bring:</b> a warm layer and proper shoes, it's still muddy down by the lake.</li>
            </ul>
            <p>I'll bring coffee ☕ and the good sandwiches. If it rains, plan B is the museum and a long lunch.</p>
            <p>See you Saturday,<br>Ana</p>
            <blockquote style="margin:20px 0 0;padding-left:12px;border-left:2px solid #d4d4d8;color:#71717a">
              On Monday you wrote:<br>Yes! Let's meet at 9 at the trailhead.
            </blockquote>
            """, thread: "weekend", attachment: ("Ridge-loop-map.pdf", "application/pdf"));
        await AddAsync(p[FolderRole.Inbox], "Margie's Travel", "billing@margies.example", "Invoice #2041", now.AddDays(-3),
            Simple("Hello,", "Please find your invoice #2041 attached.", "Thank you for travelling with us."), read: true,
            attachment: ("Invoice-2041.pdf", "application/pdf"));
        await AddAsync(p[FolderRole.Inbox], "Litware Weekly", "digest@litware.example", "October roundup: 12 tools worth trying", now.AddDays(-9),
            Newsletter("#0f766e", "October roundup", "Twelve small tools that quietly make your day better.", remoteImage: true), read: true);
        await AddAsync(p[FolderRole.Inbox], "Plicbox", "hello@plicbox.local", "Welcome to Plicbox", now.AddDays(-40),
            Simple("Welcome!", "This is demo data. Nothing here touches a real mail server.", "Run without --demo to use your own accounts."), read: true);
        await AddAsync(p[FolderRole.Junk], "Prize Center", "win@prize.example", "You have WON!!!", now.AddDays(-2),
            Simple("Congratulations", "Click here to claim.", ""));
        await AddAsync(p[FolderRole.Trash], "Old Newsletter", "old@news.example", "Last chance", now.AddDays(-12),
            Simple("Hi", "This is in the trash.", ""), read: true);

        await AddAsync(w[FolderRole.Inbox], "Diego Ruiz", "diego@work.example", "Q4 planning notes", now.AddHours(-0.5),
            Simple("Hi team,", "Notes from today's planning session are below. Main themes: search speed, offline mode, and calendar sync.", "Diego"));
        await AddAsync(w[FolderRole.Inbox], "GitHub", "notifications@github.example", "[plicbox/core] Add FTS5 search (PR #12)", now.AddDays(-1).AddHours(-4),
            Simple("@diego requested your review", "Adds full-text search over subject, sender and body.", ""), read: true, thread: "pr12");
        await AddAsync(w[FolderRole.Inbox], "GitHub", "notifications@github.example", "Re: [plicbox/core] Add FTS5 search (PR #12)", now.AddDays(-1).AddHours(-3),
            Simple("Diego Ruiz commented", "Rebased on main, tests are green.", ""), thread: "pr12");
        await AddAsync(w[FolderRole.Inbox], "Mia Chen", "mia@work.example", "Lunch on Thursday?", now.AddDays(-2),
            Simple("Hey,", "Want to grab lunch on Thursday? The new ramen place opened.", "Mia"), read: true);

        // People history for the contacts module: Mia (not saved yet, with a signature), Diego, and Laura (gone quiet).
        const string miaSignature = "<p>Best regards,<br>Mia Chen<br>Senior Product Designer | Work Example<br>M: +49 151 2345 6789<br>www.work.example</p>";
        await AddAsync(w[FolderRole.Inbox], "Mia Chen", "mia@work.example", "Design specs v2", now.AddDays(-5),
            "<p>Hi,</p><p>Attached are the updated specs for the calendar screens.</p>" + miaSignature, read: true,
            attachment: ("Calendar-specs-v2.pdf", "application/pdf"));
        await AddAsync(w[FolderRole.Sent], me, "you@work.example", "Re: Design specs v2", now.AddDays(-4),
            Simple("Thanks Mia,", "Looks great. Two small notes inline.", ""), read: true, to: "Mia Chen <mia@work.example>");
        await AddAsync(w[FolderRole.Sent], me, "you@work.example", "Re: Lunch on Thursday?", now.AddDays(-2).AddHours(1),
            Simple("Yes!", "Ramen it is. 12:30?", ""), read: true, to: "Mia Chen <mia@work.example>");
        for (var i = 0; i < 4; i++)
            await AddAsync(w[FolderRole.Sent], me, "you@work.example", $"Re: Sprint {40 + i} review", now.AddDays(-7 * (i + 1)),
                Simple("Hi Diego,", "Notes from the review attached.", ""), read: true, to: "Diego Ruiz <diego@work.example>");
        for (var i = 0; i < 6; i++)
        {
            var day = -200 + i * 16;
            await AddAsync(w[FolderRole.Inbox], "Laura Brandt", "laura@work.example", $"Roadmap check-in #{i + 1}", now.AddDays(day),
                Simple("Hi,", "Quick check-in on the roadmap. Anything blocking you?", "Laura"), read: true);
            await AddAsync(w[FolderRole.Sent], me, "you@work.example", $"Re: Roadmap check-in #{i + 1}", now.AddDays(day + 1),
                Simple("Hi Laura,", "All good, thanks! Update below.", ""), read: true, to: "Laura Brandt <laura@work.example>");
        }
    }

    private async Task<Account> AddAccountAsync(string name, string email, string color)
    {
        var account = new Account
        {
            DisplayName = name, Email = email, Username = email, Color = color,
            ImapHost = "imap.invalid", SmtpHost = "smtp.invalid",
        };
        return account with { Id = await accounts.AddAsync(account) };
    }

    private async Task<Dictionary<FolderRole, Folder>> AddFoldersAsync(Account account)
    {
        (FolderRole Role, string Name)[] defs =
        [
            (FolderRole.Inbox, "Inbox"), (FolderRole.Sent, "Sent"), (FolderRole.Drafts, "Drafts"),
            (FolderRole.Archive, "Archive"), (FolderRole.Junk, "Junk Email"), (FolderRole.Trash, "Trash"),
        ];
        var result = new Dictionary<FolderRole, Folder>();
        foreach (var (role, name) in defs)
        {
            var folder = new Folder { AccountId = account.Id, Path = name, Name = name, Role = role };
            result[role] = folder with { Id = await folders.UpsertAsync(folder) };
        }
        return result;
    }

    private async Task AddAsync(Folder folder, string fromName, string fromAddress, string subject, DateTimeOffset date, string html,
        bool read = false, bool flagged = false, bool important = false, string? thread = null, (string Name, string MimeType)? attachment = null, string to = "you@example.com",
        (string Name, string MimeType)[]? attachments = null, (string Field, string Value)[]? headers = null, string? replyTo = null)
    {
        var mime = new MimeMessage { Subject = subject, Date = date };
        mime.From.Add(new MailboxAddress(fromName, fromAddress));
        mime.To.Add(MailboxAddress.Parse(to));
        if (replyTo is not null) mime.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        foreach (var (field, value) in headers ?? []) mime.Headers.Insert(0, new Header(field, value));
        var builder = new BodyBuilder { HtmlBody = html };
        attachments = attachment is { } single ? [single, .. attachments ?? []] : attachments ?? [];
        foreach (var a in attachments)
            // A believable size (tens to hundreds of KB, fixed per name) so screenshots don't show "16 B".
            builder.Attachments.Add(a.Name, new byte[40_000 + a.Name.Length * 7_919], ContentType.Parse(a.MimeType));
        mime.Body = builder.ToMessageBody();

        var summary = new MessageSummary
        {
            AccountId = folder.AccountId, FolderId = folder.Id, Uid = ++_uid, MessageId = mime.MessageId,
            ThreadKey = thread, Subject = subject, FromName = fromName, FromAddress = fromAddress, To = to, Recipients = to,
            Date = date, Preview = MessageRenderer.ExtractText(mime, 200), IsRead = read, IsFlagged = flagged, IsImportant = important,
            HasAttachments = attachments.Length > 0, Size = html.Length,
        };
        var id = await messages.UpsertAsync(summary, MessageRenderer.ExtractText(mime));

        using var ms = new MemoryStream();
        await mime.WriteToAsync(ms);
        ms.Position = 0;
        await bodies.SaveAsync(id, ms);
    }

    private static string Simple(string greeting, string body, string signoff) =>
        $"<p>{greeting}</p><p>{body}</p><p>{signoff}</p>";

    private static string Newsletter(string color, string title, string text, bool remoteImage) => $"""
        <table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f4f5"><tr><td align="center" style="padding:24px">
          <table width="560" cellpadding="0" cellspacing="0" style="background:#fff">
            <tr><td style="background:{color};color:#fff;padding:32px;font-size:28px;font-weight:600">{title}</td></tr>
            {(remoteImage ? "<tr><td><img src=\"https://images.example/cover.jpg\" width=\"560\" alt=\"Cover art\"></td></tr>" : "")}
            <tr><td style="padding:24px;font-size:16px">{text}</td></tr>
            <tr><td style="padding:0 24px 20px;font-size:12px;color:#777">You're getting this because you follow us. <a href="https://news.example/unsubscribe">Unsubscribe</a></td></tr>
          </table>
          {(remoteImage ? "<img src=\"https://track.example/open.gif\" width=\"1\" height=\"1\">" : "")}
        </td></tr></table>
        """;

    private static string Banner(string hello, string thanks, string ask, string cta) => $"""
        <table width="100%" cellpadding="0" cellspacing="0" style="background:#e5e7eb"><tr><td align="center" style="padding:24px">
          <table width="600" cellpadding="0" cellspacing="0" style="background:#fff">
            <tr><td style="padding:16px 24px;color:#555;text-decoration:underline;font-size:13px">Web View</td></tr>
            <tr><td style="background:#4ade80;height:64px"></td></tr>
            <tr><td style="background:#86efac;padding:24px;font-size:40px">{hello}</td></tr>
            <tr><td style="background:#bbf7d0;padding:24px;font-size:24px">{thanks}</td></tr>
            <tr><td style="padding:24px;font-size:18px">{ask}</td></tr>
            <tr><td style="padding:0 24px 32px"><a href="https://example.com" style="display:block;background:#000;color:#fff;text-align:center;padding:14px;font-size:18px;text-decoration:none">{cta}</a></td></tr>
          </table>
          <img src="https://u4821.ct.sendgrid.net/wf/open?upn=demo" width="1" height="1">
        </td></tr></table>
        """;
}
