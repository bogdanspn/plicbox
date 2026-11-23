using System.Globalization;
using System.Text;
using Plicbox.Core;

namespace Plicbox.Mail;

/// <param name="FirstMessage">Nothing came from this address before, and it isn't someone you know.</param>
public sealed record FamiliarityVerdict(SenderRisk Risk, IReadOnlyList<string> Reasons, bool FirstMessage)
{
    public static readonly FamiliarityVerdict Nothing = new(SenderRisk.None, [], false);
}

/// <summary>
/// Who you really correspond with (your contacts, everyone you've written to, and your own addresses), for spotting
/// the impostors that the server's checks can't: a domain registered yesterday that passes SPF and DKIM just fine,
/// but is one letter away from your bank's or your own company's ("contoso-bank.com", "rnicrosoft.com"), or a known
/// person's name on an address they've never used. Built from what's on this computer; nothing is looked up.
/// </summary>
public sealed class KnownCorrespondents
{
    private readonly HashSet<string> _trusted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _domains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _byName = new(StringComparer.Ordinal); // normalised name → addresses
    private readonly HashSet<string> _ownNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PersonStats> _stats = new(StringComparer.OrdinalIgnoreCase);

    public static KnownCorrespondents Empty { get; } = new([], [], []);

    /// <param name="own">Your own names and addresses (every account's identities).</param>
    public KnownCorrespondents(IEnumerable<Contact> contacts, IEnumerable<PersonStats> stats, IEnumerable<MailAlias> own)
    {
        foreach (var me in own)
        {
            Trust(me.Address, me.Name);
            if (NormalizeName(me.Name) is { } name) _ownNames.Add(name);
        }
        foreach (var contact in contacts)
            foreach (var email in contact.Emails)
                Trust(email.Value, contact.DisplayName);
        foreach (var person in stats)
        {
            _stats[person.Address] = person;
            if (person.Sent > 0) Trust(person.Address, person.Name); // you've written to them
        }
    }

    private void Trust(string address, string? name)
    {
        address = address.Trim();
        if (!address.Contains('@')) return;
        _trusted.Add(address);
        _domains.Add(Site(address));
        if (NormalizeName(name) is { } key)
        {
            if (!_byName.TryGetValue(key, out var list)) _byName[key] = list = [];
            if (!list.Contains(address, StringComparer.OrdinalIgnoreCase)) list.Add(address);
        }
    }

    /// <summary>What's unusual about a received message's sender.</summary>
    /// <param name="date">The message's date (to tell "the first one" from "one of several").</param>
    public FamiliarityVerdict Check(string fromAddress, string? fromName, DateTimeOffset date)
    {
        if (string.IsNullOrWhiteSpace(fromAddress) || !fromAddress.Contains('@') || _trusted.Contains(fromAddress)) return FamiliarityVerdict.Nothing;
        var reasons = new List<string>();
        var risk = SenderRisk.None;
        var site = Site(fromAddress);

        // A domain dressed up as one you know.
        if (!_domains.Contains(site) && !SenderCheck.IsFreeMail(site) && LookAlike(site) is { } known)
        {
            reasons.Add(L.F("{0} looks like {1}, which you correspond with, but it's a different domain. Check the address letter by letter.", site, known));
            risk = SenderRisk.Danger;
        }

        // A name you know, on an address that person has never used.
        if (NormalizeName(fromName) is { } name && !IsGeneric(name) && _byName.TryGetValue(name, out var addresses)
            && !addresses.Any(a => Site(a).Equals(site, StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add(_ownNames.Contains(name)
                ? L.F("It uses your own name, but comes from {0}, which isn't one of your addresses.", fromAddress)
                : L.F("The name is {0}, but you know them as {1}. This message comes from {2}.", fromName!.Trim(), addresses[0], fromAddress));
            if (risk < SenderRisk.Caution) risk = SenderRisk.Caution;
        }

        // Nothing from this address before (the statistics may already count this message, or not yet).
        var first = !_stats.TryGetValue(fromAddress, out var seen) || seen.Received == 0
                    || seen.Received == 1 && seen.First is { } firstSeen && firstSeen >= date.AddMinutes(-1);
        if (first && risk > SenderRisk.None) reasons.Add(L.T("It's the first message from this address."));
        return risk == SenderRisk.None && !first ? FamiliarityVerdict.Nothing : new FamiliarityVerdict(risk, reasons, first);
    }

    /// <summary>The known domain this one imitates, if any.</summary>
    private string? LookAlike(string site)
    {
        var (label, suffix) = Split(site);
        if (label.Length < 4) return null; // "bbc", "ibm": too short to tell a typo from another name
        var skeleton = Skeleton(label);
        foreach (var known in _domains)
        {
            var (knownLabel, knownSuffix) = Split(known);
            if (knownLabel.Length < 4) continue;
            if (label.Equals(knownLabel, StringComparison.OrdinalIgnoreCase))
            {
                // The same name elsewhere is usually the same company (contoso.de and contoso.com); a mistyped ".com" isn't.
                if (knownSuffix == "com" && ComTypos.Contains(suffix)) return known;
                continue;
            }
            // "rn" for "m", "0" for "o", "contoso-bank" for "contosobank": the same once the tricks are undone (any ending).
            if (Skeleton(knownLabel) == skeleton) return known;
            // A typo with the same ending: "contsoo.com", "micros0ft.com" (one change; two for long names).
            if (suffix != knownSuffix) continue;
            var allowed = Math.Min(label.Length, knownLabel.Length) >= 9 ? 2 : 1;
            if (Math.Abs(label.Length - knownLabel.Length) <= allowed && Distance(label, knownLabel) <= allowed) return known;
        }
        return null;
    }

    // Endings registered to catch a mistyped or misread ".com" (".co" and ".cm" are real countries, rarely your contacts').
    private static readonly HashSet<string> ComTypos = new(StringComparer.OrdinalIgnoreCase) { "co", "cm", "om", "con", "cam", "corn", "comm", "co.com" };

    private static string Site(string address) => LinkSafety.RegistrableDomain(address[(address.LastIndexOf('@') + 1)..].Trim());

    // "contoso.co.uk" → ("contoso", "co.uk").
    private static (string Label, string Suffix) Split(string site)
    {
        var dot = site.IndexOf('.');
        return dot < 0 ? (site, "") : (site[..dot], site[(dot + 1)..]);
    }

    private static string Skeleton(string label)
    {
        var s = label.ToLowerInvariant().Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        s = s.Replace("rn", "m", StringComparison.Ordinal).Replace("vv", "w", StringComparison.Ordinal).Replace("cl", "d", StringComparison.Ordinal);
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            chars[i] = chars[i] switch { '0' => 'o', '1' or 'i' or '|' => 'l', '3' => 'e', '5' => 's', '$' => 's', '@' => 'a', _ => chars[i] };
        return new string(chars);
    }

    // Damerau–Levenshtein (optimal string alignment): swapped neighbours count as one change.
    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }

    // Names that say what, not who ("Support", "Customer Service"): anyone may use them.
    private static readonly HashSet<string> GenericWords = new(StringComparer.Ordinal)
    {
        "support", "info", "admin", "service", "services", "customer", "team", "noreply", "no", "reply", "newsletter", "news",
        "billing", "accounts", "account", "notifications", "notification", "help", "helpdesk", "sales", "contact", "office",
        "mail", "mailer", "daemon", "postmaster", "security", "hr", "it", "the", "of", "and", "de", "la", "via", "me", "you",
    };

    private static bool IsGeneric(string name) => name.Split(' ').All(GenericWords.Contains);

    /// <summary>"  Ána  Popescu " → "ana popescu"; null for names too short to recognise anyone by (one short word).</summary>
    internal static string? NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var decomposed = name.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        var words = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || words.Any(w => w.Contains('@'))) return null;
        var joined = string.Join(' ', words);
        // "Ana" alone matches too many people; "Ana Popescu" or a long single name ("Alexandrescu") is someone.
        return words.Length >= 2 || joined.Length >= 8 ? joined : null;
    }
}
