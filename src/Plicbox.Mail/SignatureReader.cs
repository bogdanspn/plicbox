using System.Text.RegularExpressions;

namespace Plicbox.Mail;

/// <summary>Details found in someone's email signature.</summary>
public sealed record SignatureFacts(
    IReadOnlyList<(string Number, string Label)> Phones,
    string? JobTitle,
    string? Company,
    IReadOnlyList<string> Websites)
{
    public bool IsEmpty => Phones.Count == 0 && JobTitle is null && Company is null && Websites.Count == 0;
}

/// <summary>
/// Reads the signature at the end of a message (above any quoted reply): phone numbers with their labels,
/// job title, company and website. Plain text rules, no guessing beyond what the signature says; the contact card
/// offers each finding with an "Add" button rather than changing anything by itself.
/// </summary>
public static partial class SignatureReader
{
    private static readonly string[] SignOffs =
    [
        "best", "best regards", "kind regards", "regards", "warm regards", "thanks", "thank you", "many thanks", "cheers", "sincerely",
        "all the best", "talk soon", "br", "thx", "viele grüße", "viele grüsse", "beste grüße", "mit freundlichen grüßen", "freundliche grüße",
        "lg", "vg", "gruß", "grüße", "liebe grüße", "mfg", "cordialement", "bien à vous", "saludos", "un saludo", "cu stimă", "cu drag",
        "mulțumesc", "multumesc", "toate cele bune", "o zi bună", "salutări",
    ];

    private static readonly string[] TitleWords =
    [
        "engineer", "manager", "director", "developer", "designer", "ceo", "cto", "cfo", "coo", "cmo", "founder", "co-founder", "head of",
        "lead", "officer", "president", "vp", "vice president", "consultant", "specialist", "analyst", "architect", "partner", "owner",
        "coordinator", "assistant", "product", "marketing", "sales", "recruiter", "editor", "producer", "artist", "photographer",
        "account executive", "advisor", "attorney", "lawyer", "accountant", "teacher", "professor", "researcher", "scientist",
        "geschäftsführer", "inhaber", "leiter", "leiterin", "referent", "berater", "entwickler", "inginer", "director general",
    ];

    public static SignatureFacts Read(string? text, string senderName, string senderAddress)
    {
        var block = SignatureBlock(text, senderName);
        var phones = new List<(string, string)>();
        var websites = new List<string>();
        var rest = new List<string>();
        var domainStem = DomainStem(senderAddress);

        foreach (var raw in block)
        {
            var line = raw.Trim().Trim('|', '•', '·', '-', '–').Trim();
            if (line.Length == 0) continue;
            var used = false;

            foreach (Match m in Phone().Matches(line))
            {
                var label = m.Groups["label"].Value.ToLowerInvariant().TrimEnd('.');
                if (label is "fax" or "f") { used = true; continue; }
                var number = Regex.Replace(m.Groups["num"].Value.Trim(), @"\s{2,}", " ");
                var digits = number.Count(char.IsDigit);
                if (digits is < 8 or > 15) continue;
                if (phones.Any(p => Digits(p.Item1) == Digits(number))) { used = true; continue; }
                phones.Add((number, label is "m" or "mob" or "mobile" or "cell" or "handy" or "mobil" ? "mobile" : "work"));
                used = true;
            }
            foreach (Match m in Url().Matches(line))
            {
                var url = m.Value.TrimEnd('.', ',', ';', ')', ']');
                if (url.Contains("unsubscribe", StringComparison.OrdinalIgnoreCase)) continue;
                if (!websites.Contains(url, StringComparer.OrdinalIgnoreCase)) websites.Add(url);
                used = true;
            }
            if (!used && !line.Contains('@')) rest.Add(line);
        }

        // Title and company come from the short text lines that are left.
        string? title = null, company = null;
        var candidates = rest
            .Where(l => l.Length <= 70 && !l.EndsWith('.') && !l.EndsWith(',') && l.Count(char.IsDigit) < 4)
            .Where(l => !IsName(l, senderName) && !IsSignOff(l))
            .ToList();
        foreach (var line in candidates)
        {
            // "Senior Engineer | Contoso", "Head of Design at Acme", "CTO, Acme Inc."
            var parts = Regex.Split(line, @"\s+(?:\||at|@|bei|la)\s+|\s*[|,]\s+", RegexOptions.IgnoreCase).Where(p => p.Length > 0).ToArray();
            foreach (var part in parts)
            {
                if (title is null && TitleWords.Any(w => Regex.IsMatch(part, $@"(?<!\w){Regex.Escape(w)}(?!\w)", RegexOptions.IgnoreCase)))
                    title = part.Trim();
                else if (company is null && domainStem is not null && part.Replace(" ", "").Contains(domainStem, StringComparison.OrdinalIgnoreCase))
                    company = part.Trim();
            }
        }
        return new SignatureFacts(phones, title, company, websites);
    }

    /// <summary>The lines of the signature: after "-- ", else after the last sign-off, else the last few lines.</summary>
    internal static IReadOnlyList<string> SignatureBlock(string? text, string senderName)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var lines = text.ReplaceLineEndings("\n").Split('\n').ToList();

        // Stop at the quoted reply.
        var cut = lines.FindIndex(l => QuoteHeader().IsMatch(l.Trim()) || l.TrimStart().StartsWith('>'));
        if (cut >= 0) lines = lines[..cut];
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);

        var dashes = lines.FindLastIndex(l => l.TrimEnd() is "--" or "-- " || l == "-- ");
        if (dashes >= 0) return lines[(dashes + 1)..].Take(14).ToList();

        var tail = Math.Max(0, lines.Count - 16);
        for (var i = lines.Count - 1; i >= tail; i--)
            if (IsSignOff(lines[i].Trim())) return lines[(i + 1)..].Take(14).ToList();
        for (var i = lines.Count - 1; i >= tail; i--)
            if (IsName(lines[i].Trim(), senderName)) return lines[i..].Take(14).ToList();
        return lines.Skip(Math.Max(0, lines.Count - 8)).ToList();
    }

    private static bool IsSignOff(string line)
    {
        var l = line.Trim().TrimEnd(',', '!', '.', '-').Trim().ToLowerInvariant();
        return SignOffs.Contains(l);
    }

    private static bool IsName(string line, string senderName)
    {
        if (string.IsNullOrWhiteSpace(senderName) || line.Length > 60) return false;
        var words = senderName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 1).ToList();
        return words.Count > 0 && words.Count(w => line.Contains(w, StringComparison.OrdinalIgnoreCase)) >= Math.Min(2, words.Count)
               && line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= words.Count + 2;
    }

    /// <summary>"contoso" from "ana@contoso.com"; null for free mail providers.</summary>
    private static string? DomainStem(string address)
    {
        var at = address.LastIndexOf('@');
        if (at < 0) return null;
        var domain = address[(at + 1)..].ToLowerInvariant();
        if (SenderCheck.IsFreeMail(domain)) return null;
        var site = LinkSafety.RegistrableDomain(domain);
        var stem = site.Split('.')[0];
        return stem.Length >= 3 ? stem : null;
    }

    private static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());

    [GeneratedRegex(@"(?:(?<label>tel|phone|mobile|mobil|mob|cell|handy|direct|office|fax|t|m|p|f)\.?\s*[:.]?\s*)?(?<num>\+?\(?\d[\d\s().\-/]{6,}\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Phone();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>()""]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"^(On .+ wrote:|Am .+ schrieb.*:|Le .+ a écrit\s*:|El .+ escribió:|În .+ a scris:|-{2,}\s*Original Message\s*-{2,}|-{2,}\s*Ursprüngliche Nachricht\s*-{2,}|_{8,}|From:\s.+|Von:\s.+|De la:\s.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex QuoteHeader();
}
