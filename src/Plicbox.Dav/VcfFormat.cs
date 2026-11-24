using System.Globalization;
using System.Text;
using Plicbox.Core;

namespace Plicbox.Dav;

/// <summary>
/// vCard files (.vcf): what every address book exports and imports. Writes vCard 3.0, which all of them read; reads
/// 2.1, 3.0 and 4.0 (folded lines, quoted-printable, TYPE labels, birthdays without a year).
/// </summary>
public static class VcfFormat
{
    // ───────── writing ─────────

    public static string Write(IEnumerable<Contact> contacts)
    {
        var sb = new StringBuilder();
        void Line(string text)
        {
            // Folded at 75 bytes, never inside a character.
            var bytes = 0;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var size = char.IsHighSurrogate(text[i]) ? 4 : Encoding.UTF8.GetByteCount(text.AsSpan(i, 1));
                if (bytes + size > (start == 0 ? 75 : 74))
                {
                    sb.Append(text, start, i - start).Append("\r\n ");
                    start = i;
                    bytes = 0;
                }
                bytes += size;
                if (char.IsHighSurrogate(text[i])) i++;
            }
            sb.Append(text, start, text.Length - start).Append("\r\n");
        }
        static string Type(string? label) => string.IsNullOrWhiteSpace(label) ? "" : ";TYPE=" + Escape(label.Trim().ToLowerInvariant().Replace(' ', '-'));

        foreach (var c in contacts)
        {
            Line("BEGIN:VCARD");
            Line("VERSION:3.0");
            Line("FN:" + Escape(c.DisplayName));
            Line($"N:{Escape(c.FamilyName ?? "")};{Escape(c.GivenName ?? "")};;;");
            if (!string.IsNullOrEmpty(c.Nickname)) Line("NICKNAME:" + Escape(c.Nickname));
            if (!string.IsNullOrEmpty(c.Organization) || !string.IsNullOrEmpty(c.Department))
                Line($"ORG:{Escape(c.Organization ?? "")}" + (string.IsNullOrEmpty(c.Department) ? "" : ";" + Escape(c.Department)));
            if (!string.IsNullOrEmpty(c.JobTitle)) Line("TITLE:" + Escape(c.JobTitle));
            foreach (var e in c.Emails) Line("EMAIL" + Type(e.Label) + ":" + Escape(e.Value));
            foreach (var p in c.Phones) Line("TEL" + Type(p.Label) + ":" + Escape(p.Value));
            foreach (var w in c.Websites) Line("URL" + Type(w.Label) + ":" + Escape(w.Value));
            foreach (var a in c.Addresses.Where(a => !a.IsEmpty))
                Line($"ADR{Type(a.Label)}:;;{Escape(a.Street ?? "")};{Escape(a.City ?? "")};{Escape(a.Region ?? "")};{Escape(a.PostalCode ?? "")};{Escape(a.Country ?? "")}");
            if (c.Birthday is { } birthday)
                Line("BDAY:" + (c.BirthdayHasYear ? birthday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : birthday.ToString("--MM-dd", CultureInfo.InvariantCulture)));
            if (!string.IsNullOrEmpty(c.Notes)) Line("NOTE:" + Escape(c.Notes));
            if (c.PhotoUrl is { } photo && photo.StartsWith("http", StringComparison.OrdinalIgnoreCase)) Line("PHOTO;VALUE=uri:" + photo);
            if (c.IsFavorite) Line("CATEGORIES:starred");
            Line("END:VCARD");
        }
        return sb.ToString();
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    // ───────── reading ─────────

    /// <summary>The file's contacts, kept on this computer (no account). Cards with no name, address or number are skipped.</summary>
    public static IReadOnlyList<Contact> Read(string text)
    {
        var result = new List<Contact>();
        List<(string Name, Dictionary<string, string> Params, string Value)>? card = null;
        foreach (var line in Unfold(text))
        {
            var (name, parameters, value) = Split(line);
            if (name == "BEGIN" && value.Equals("VCARD", StringComparison.OrdinalIgnoreCase)) { card = []; continue; }
            if (name == "END" && value.Equals("VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (card is not null && ToContact(card) is { } contact) result.Add(contact);
                card = null;
                continue;
            }
            if (card is null || name.Length == 0) continue;
            if (parameters.TryGetValue("ENCODING", out var enc) && enc.Equals("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase))
                value = QuotedPrintable(value, parameters.GetValueOrDefault("CHARSET"));
            card.Add((name, parameters, value));
        }
        return result;
    }

    private static Contact? ToContact(List<(string Name, Dictionary<string, string> Params, string Value)> card)
    {
        string? One(string name) => card.FirstOrDefault(p => p.Name == name).Value is { Length: > 0 } v ? v : null;
        IEnumerable<(Dictionary<string, string> Params, string Value)> All(string name) => card.Where(p => p.Name == name).Select(p => (p.Params, p.Value));
        static string? Label(Dictionary<string, string> p)
        {
            // TYPE=work,voice / TYPE=cell;TYPE=pref / 2.1's bare HOME, WORK…: the first that says something.
            var types = p.Where(kv => kv.Key is "TYPE" || kv.Value.Length == 0).SelectMany(kv => (kv.Key == "TYPE" ? kv.Value : kv.Key).Split(','))
                .Select(t => t.Trim().ToLowerInvariant())
                .Where(t => t is not ("pref" or "internet" or "voice" or "x400" or "")).ToList();
            return types.FirstOrDefault() switch
            {
                null => null,
                "cell" => "mobile",
                var t => t,
            };
        }

        var n = One("N")?.Let(SplitStructured) ?? [];
        string? Part(int i) => i < n.Count && n[i].Length > 0 ? n[i] : null;
        var org = One("ORG")?.Let(SplitStructured) ?? [];
        var emails = All("EMAIL").Select(e => new LabeledValue(Unescape(e.Value).Trim(), Label(e.Params))).Where(e => e.Value.Contains('@')).ToList();
        var phones = All("TEL").Select(t => new LabeledValue(Unescape(t.Value.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ? t.Value[4..] : t.Value).Trim(), Label(t.Params)))
            .Where(t => t.Value.Length > 0).ToList();
        var addresses = All("ADR").Select(a =>
        {
            var p = SplitStructured(a.Value);
            string? At(int i) => i < p.Count && p[i].Length > 0 ? p[i] : null;
            var street = string.Join("\n", new[] { At(1), At(2) }.Where(x => x is not null));
            return new PostalAddress(street.Length > 0 ? street : null, At(3), At(5), At(4), At(6), Label(a.Params));
        }).Where(a => !a.IsEmpty).ToList();

        var given = Part(1);
        var family = Part(0);
        var display = One("FN") is { } fn ? Unescape(fn).Trim()
            : string.Join(" ", new[] { given, family }.Where(x => x is not null));
        if (display.Length == 0) display = emails.FirstOrDefault()?.Value ?? phones.FirstOrDefault()?.Value ?? "";
        if (display.Length == 0) return null;

        return new Contact
        {
            Source = ContactSource.Local,
            DisplayName = display,
            GivenName = given, FamilyName = family,
            Nickname = One("NICKNAME") is { } nick ? Unescape(nick) : null,
            Organization = org.Count > 0 && org[0].Length > 0 ? org[0] : null,
            Department = org.Count > 1 && org[1].Length > 0 ? org[1] : null,
            JobTitle = One("TITLE") is { } title ? Unescape(title) : null,
            Emails = emails, Phones = phones,
            Websites = All("URL").Select(u => new LabeledValue(Unescape(u.Value).Trim(), Label(u.Params))).Where(u => u.Value.Length > 0).ToList(),
            Addresses = addresses,
            Address = addresses.FirstOrDefault()?.Formatted,
            Birthday = One("BDAY") is { } bday ? Birthday(bday) : null,
            Notes = One("NOTE") is { } note ? Unescape(note) : null,
            PhotoUrl = card.FirstOrDefault(p => p.Name == "PHOTO" && p.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase)).Value,
            IsFavorite = One("CATEGORIES")?.Contains("starred", StringComparison.OrdinalIgnoreCase) == true,
            Updated = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>"1985-04-12", "19850412", "--04-12" (no year), or a date-time.</summary>
    private static DateOnly? Birthday(string value)
    {
        value = value.Trim();
        if (value.StartsWith("--"))
        {
            var md = value[2..].Replace("-", "");
            return md.Length >= 4 && int.TryParse(md[..2], out var m) && int.TryParse(md[2..4], out var d) && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(Contact.NoYear, m)
                ? new DateOnly(Contact.NoYear, m, d) : null;
        }
        var date = value.Split('T')[0].Replace("-", "");
        return DateOnly.TryParseExact(date, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;
    }

    private static List<string> SplitStructured(string value)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                sb.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else if (value[i] == ';') { parts.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(value[i]);
        }
        parts.Add(sb.ToString().Trim());
        return parts;
    }

    private static string Unescape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                var next = text[++i];
                sb.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else sb.Append(text[i]);
        }
        return sb.ToString();
    }

    private static string QuotedPrintable(string value, string? charset)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '=' && i + 2 < value.Length && byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
            {
                bytes.Add(b);
                i += 2;
            }
            else bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
        }
        Encoding encoding;
        try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        return encoding.GetString(bytes.ToArray());
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var current = new StringBuilder();
        var softBreak = false; // 2.1 quoted-printable lines end in "=" when they go on
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (softBreak || (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && current.Length > 0))
            {
                current.Append(softBreak ? line : line[1..]);
            }
            else
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear().Append(line);
            }
            softBreak = current.Length > 0 && current[^1] == '=' && current.ToString().Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
            if (softBreak) current.Length--;
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>"item1.EMAIL;TYPE=work:a@b.c" -> EMAIL, its parameters (2.1's bare HOME/WORK kept as keys), the value.</summary>
    private static (string Name, Dictionary<string, string> Params, string Value) Split(string line)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon < 0) return ("", parameters, "");
        var parts = line[..colon].Split(';');
        var name = parts[0].ToUpperInvariant();
        if (name.IndexOf('.') is var dot and >= 0) name = name[(dot + 1)..]; // Apple's grouping prefix
        foreach (var p in parts.Skip(1))
        {
            var eq = p.IndexOf('=');
            if (eq > 0)
            {
                var key = p[..eq].ToUpperInvariant();
                var v = p[(eq + 1)..].Trim('"');
                parameters[key] = parameters.TryGetValue(key, out var earlier) ? earlier + "," + v : v;
            }
            else if (p.Length > 0) parameters[p.ToUpperInvariant()] = "";
        }
        return (name, parameters, line[(colon + 1)..]);
    }

    private static TResult Let<T, TResult>(this T value, Func<T, TResult> f) => f(value);
}
