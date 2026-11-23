using Plicbox.Core;
using System.Globalization;
using System.Text.RegularExpressions;
using MimeKit;
using MimeKit.Cryptography;

namespace Plicbox.Mail;

public enum SenderRisk { None, Caution, Danger }

/// <summary>Why a message's sender looks doubtful; <see cref="Reasons"/> are short sentences for the reading pane.</summary>
/// <param name="Title">The warning's heading when it isn't about who sent it (a link to a reported phishing site).</param>
public sealed record SenderVerdict(SenderRisk Risk, IReadOnlyList<string> Reasons, string? Title = null)
{
    public static readonly SenderVerdict Safe = new(SenderRisk.None, []);
}

/// <summary>
/// Spots spoofed or impersonating senders from a received message's headers: the SPF/DKIM/DMARC results the receiving
/// server recorded, a display name showing a different address, look-alike domains, mail claiming to be from yourself,
/// and replies diverted to a free mailbox. Quiet when there's nothing to go on (e.g. your own sent mail).
/// Also warns when the sender's domain or a link in the message is on the lists of reported phishing and malware
/// sites (<see cref="ThreatLists"/>).
/// </summary>
public static partial class SenderCheck
{
    private static readonly HashSet<string> FreeMail = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com", "outlook.com", "hotmail.com", "live.com", "msn.com", "yahoo.com", "ymail.com",
        "aol.com", "icloud.com", "me.com", "mac.com", "proton.me", "protonmail.com", "gmx.com", "gmx.net", "gmx.de",
        "web.de", "mail.com", "yandex.com", "yandex.ru", "mail.ru", "zoho.com", "tutanota.com", "tuta.io",
    };

    /// <summary>Personal mailbox providers (gmail.com, outlook.com, …): no company logo, and replies diverted there are suspicious.</summary>
    public static bool IsFreeMail(string domain) => FreeMail.Contains(domain);

    /// <param name="ownAddresses">The user's own addresses (to catch mail pretending to come from them).</param>
    public static SenderVerdict Check(MimeMessage message, IEnumerable<string>? ownAddresses = null)
    {
        if (message.From.Mailboxes.FirstOrDefault() is not { } from) return SenderVerdict.Safe;
        var domain = Domain(from.Address);
        var reasons = new List<string>();
        var risk = SenderRisk.None;
        void Add(SenderRisk level, string reason)
        {
            reasons.Add(reason);
            if (level > risk) risk = level;
        }

        // 1. What the receiving server found (only the topmost header: anything below it could have been written by the sender).
        var auth = TopAuthenticationResults(message);
        var dmarc = auth?.Results.FirstOrDefault(r => Is(r.Method, "dmarc"));
        var spf = auth?.Results.FirstOrDefault(r => Is(r.Method, "spf"))?.Result ?? ReceivedSpf(message);
        var dkim = auth?.Results.Where(r => Is(r.Method, "dkim")).Select(r => r.Result).ToList() ?? [];
        var dkimPass = dkim.Any(r => Is(r, "pass"));
        var spfFailed = Is(spf, "fail") || Is(spf, "softfail");
        var failed = false;

        if (dmarc is not null && Is(dmarc.Result, "fail"))
        {
            failed = true;
            // A strict policy means the domain owner asked for mail like this to be rejected; mailing lists often break p=none.
            var strict = Strict(dmarc) && !message.Headers.Contains(HeaderId.ListId);
            Add(strict ? SenderRisk.Danger : SenderRisk.Caution,
                L.F("It failed {0}'s sender verification (DMARC), so it may not really come from {0}.", domain));
        }
        else if (dmarc is null || !Is(dmarc.Result, "pass"))
        {
            if (spfFailed && !dkimPass)
            {
                failed = true;
                Add(SenderRisk.Caution, L.F("The server that sent it isn't allowed to send mail for {0} (SPF), and it isn't signed by {0} (DKIM).", domain));
            }
            else if (dkim.Count > 0 && !dkimPass && dkim.Any(r => Is(r, "fail")) && !Is(spf, "pass"))
            {
                failed = true;
                Add(SenderRisk.Caution, L.T("Its digital signature (DKIM) doesn't match: it may have been changed on the way."));
            }
        }

        // 2. Mail "from yourself" that didn't pass: the classic "I hacked your account" scam.
        if (failed && ownAddresses?.Any(a => a.Equals(from.Address, StringComparison.OrdinalIgnoreCase)) == true)
            Add(SenderRisk.Danger, L.T("It claims to come from your own address, but wasn't sent by your mail provider."));

        // 3. A display name that is itself an address, different from the real one: "support@bank.com" <x@elsewhere.ru>.
        if (!string.IsNullOrWhiteSpace(from.Name) && AddressInName().Match(from.Name) is { Success: true } shown
            && !shown.Value.Equals(from.Address, StringComparison.OrdinalIgnoreCase))
            Add(SenderRisk.Danger, L.F("The name shows {0}, but it was sent from {1}.", shown.Value, from.Address));

        // 4. Look-alike letters in the domain: "pаypal.com" with a Cyrillic "а" (arrives as xn--pypal-4ve.com).
        var (unicodeDomain, asciiDomain) = DomainForms(domain);
        if (Confusable(unicodeDomain))
            Add(SenderRisk.Danger, L.F("The sender's domain {0} uses look-alike letters from another alphabet (it's really {1}).", unicodeDomain, asciiDomain));

        // 5. Replies quietly going to a free mailbox, while the sender looks like a company.
        if (message.ReplyTo.Mailboxes.FirstOrDefault() is { } replyTo && Domain(replyTo.Address) is var replyDomain
            && !replyDomain.Equals(domain, StringComparison.OrdinalIgnoreCase) && FreeMail.Contains(replyDomain) && !FreeMail.Contains(domain))
            Add(SenderRisk.Caution, L.F("Replies go to {0}, a free mailbox, not to {1}.", replyTo.Address, domain));

        // 6. Reported by others: the sender's domain or a link is on the public phishing and malware lists. Said first.
        string? title = null;
        var threats = ThreatLists.Current;
        if (!threats.IsEmpty && threats.CheckMessage(from.Address, ThreatLists.LinksIn(message)) is { Kind: not ThreatKind.None } listed)
        {
            (title, var reason) = (listed.FromSender, listed.Kind == ThreatKind.Phishing) switch
            {
                (true, true) => (L.T("This message comes from a reported phishing site"),
                    L.F("It was sent from {0}, which is on public lists of reported phishing sites.", listed.Host)),
                (true, false) => (L.T("This message comes from a site that spreads malware"),
                    L.F("It was sent from {0}, which is on public lists of sites that spread malware.", listed.Host)),
                (false, true) => (L.T("This message links to a reported phishing site"),
                    L.F("A link in it goes to {0}, which is on public lists of reported phishing sites. Don't open it or enter anything there.", listed.Host)),
                _ => (L.T("This message links to a site that spreads malware"),
                    L.F("A link in it goes to {0}, which is on public lists of sites that spread malware. Don't open it or download anything from it.", listed.Host)),
            };
            reasons.Insert(0, reason);
            risk = SenderRisk.Danger;
        }

        return risk == SenderRisk.None ? SenderVerdict.Safe : new SenderVerdict(risk, reasons, title);
    }

    private static AuthenticationResults? TopAuthenticationResults(MimeMessage message)
    {
        var header = message.Headers.FirstOrDefault(h => h.Id == HeaderId.AuthenticationResults);
        if (header is null) return null;
        var raw = header.RawValue;
        return AuthenticationResults.TryParse(raw, out var parsed) ? parsed : null;
    }

    // Servers without Authentication-Results may still record SPF on its own.
    private static string? ReceivedSpf(MimeMessage message) =>
        message.Headers.FirstOrDefault(h => h.Field.Equals("Received-SPF", StringComparison.OrdinalIgnoreCase))?.Value.Trim()
            .Split([' ', ';', '('], 2)[0];

    private static bool Strict(AuthenticationMethodResult dmarc)
    {
        var text = $"{dmarc.ResultComment} {dmarc.Action}";
        return Regex.IsMatch(text, @"\b(?:dis|p)=(?:quarantine|reject)\b|\b(?:o?reject|quarantine)\b", RegexOptions.IgnoreCase);
    }

    private static string Domain(string address) => address[(address.LastIndexOf('@') + 1)..].Trim().TrimEnd('.');

    internal static (string Unicode, string Ascii) DomainForms(string domain)
    {
        try
        {
            var idn = new IdnMapping();
            return domain.All(char.IsAscii) ? (idn.GetUnicode(domain), domain) : (domain, idn.GetAscii(domain));
        }
        catch (ArgumentException)
        {
            return (domain, domain);
        }
    }

    // Cyrillic and Greek letters that pass for Latin ones.
    private const string LatinLookAlikes = "аеорсухіјѕԁӏкмнтвАВЕКМНОРСТХІЈЅοорντικαΑΒΕΗΙΚΜΝΟΡΤΥΧ";

    /// <summary>
    /// A label mixing Latin with Cyrillic/Greek/Armenian letters, or made only of letters that look Latin.
    /// Accented Latin (müller.de) and genuinely Cyrillic names (пример.рф) aren't flagged.
    /// </summary>
    internal static bool Confusable(string domain) => domain.Split('.').Any(label =>
    {
        var foreign = label.Count(c => c is >= '\u0370' and <= '\u058F');
        if (foreign == 0) return false;
        var latin = label.Count(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');
        return latin > 0 || label.Where(char.IsLetter).All(c => LatinLookAlikes.Contains(c));
    });

    private static bool Is(string? value, string expected) => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex AddressInName();
}
