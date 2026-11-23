using System.Globalization;
using System.Text.RegularExpressions;

namespace Plicbox.Core;

/// <summary>
/// Reads a due date out of a task typed in plain words, English, German, Romanian or Spanish, and returns the title without it:
/// "Call Ana tomorrow", "Pay invoice by friday", "Steuer bis 15.11.", "Plătește chiria până vineri", "Enviar informe el viernes", "Book flights in 3 days".
/// Only clear date phrases count, as whole words; anything else stays in the title.
/// </summary>
public static partial class TaskPhrases
{
    private static readonly Dictionary<string, DayOfWeek> Weekdays = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monday"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday, ["montag"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["tues"] = DayOfWeek.Tuesday, ["dienstag"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday, ["mittwoch"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["thurs"] = DayOfWeek.Thursday, ["donnerstag"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday, ["freitag"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday, ["samstag"] = DayOfWeek.Saturday, ["sonnabend"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday, ["sonntag"] = DayOfWeek.Sunday,
        // Romanian, with and without diacritics, and the "the …" forms that go with "viitoare" (vinerea viitoare)
        ["luni"] = DayOfWeek.Monday, ["lunea"] = DayOfWeek.Monday,
        ["marți"] = DayOfWeek.Tuesday, ["marti"] = DayOfWeek.Tuesday, ["marțea"] = DayOfWeek.Tuesday, ["martea"] = DayOfWeek.Tuesday,
        ["miercuri"] = DayOfWeek.Wednesday, ["miercurea"] = DayOfWeek.Wednesday,
        ["joi"] = DayOfWeek.Thursday, ["joia"] = DayOfWeek.Thursday,
        ["vineri"] = DayOfWeek.Friday, ["vinerea"] = DayOfWeek.Friday,
        ["sâmbătă"] = DayOfWeek.Saturday, ["sâmbăta"] = DayOfWeek.Saturday, ["sambata"] = DayOfWeek.Saturday,
        ["duminică"] = DayOfWeek.Sunday, ["duminica"] = DayOfWeek.Sunday,
        // Spanish, with and without accents
        ["lunes"] = DayOfWeek.Monday, ["martes"] = DayOfWeek.Tuesday, ["miércoles"] = DayOfWeek.Wednesday, ["miercoles"] = DayOfWeek.Wednesday,
        ["jueves"] = DayOfWeek.Thursday, ["viernes"] = DayOfWeek.Friday, ["sábado"] = DayOfWeek.Saturday, ["sabado"] = DayOfWeek.Saturday,
        ["domingo"] = DayOfWeek.Sunday,
    };

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["january"] = 1, ["jan"] = 1, ["januar"] = 1, ["jänner"] = 1, ["february"] = 2, ["feb"] = 2, ["februar"] = 2,
        ["march"] = 3, ["mar"] = 3, ["märz"] = 3, ["maerz"] = 3, ["april"] = 4, ["apr"] = 4, ["may"] = 5, ["mai"] = 5,
        ["june"] = 6, ["jun"] = 6, ["juni"] = 6, ["july"] = 7, ["jul"] = 7, ["juli"] = 7, ["august"] = 8, ["aug"] = 8,
        ["september"] = 9, ["sep"] = 9, ["sept"] = 9, ["october"] = 10, ["oct"] = 10, ["oktober"] = 10, ["okt"] = 10,
        ["november"] = 11, ["nov"] = 11, ["december"] = 12, ["dec"] = 12, ["dezember"] = 12, ["dez"] = 12,
        ["ianuarie"] = 1, ["ian"] = 1, ["februarie"] = 2, ["martie"] = 3, ["aprilie"] = 4, ["iunie"] = 6, ["iulie"] = 7,
        ["septembrie"] = 9, ["octombrie"] = 10, ["noiembrie"] = 11, ["decembrie"] = 12,
        ["enero"] = 1, ["febrero"] = 2, ["marzo"] = 3, ["abril"] = 4, ["mayo"] = 5, ["junio"] = 6, ["julio"] = 7, ["agosto"] = 8,
        ["septiembre"] = 9, ["setiembre"] = 9, ["octubre"] = 10, ["noviembre"] = 11, ["diciembre"] = 12,
    };

    public static (string Title, DateOnly? Due) Parse(string text, DateOnly today)
    {
        var t = text.Trim();
        foreach (var (pattern, resolve) in Rules(today))
        {
            var m = pattern.Match(t);
            if (!m.Success || resolve(m) is not { } due) continue;
            var title = (t[..m.Index] + " " + t[(m.Index + m.Length)..]).Trim();
            title = Spaces().Replace(title, " ").Trim().TrimEnd(',', ';', '-', '–', ':').Trim();
            return (title.Length > 0 ? title : t, due);
        }
        return (t, null);
    }

    // Most specific first. Each phrase may be led by "on/by/due/until/am/bis/zum/până/pe/para el/el" (removed along with it).
    private static IEnumerable<(Regex, Func<Match, DateOnly?>)> Rules(DateOnly today)
    {
        const string words = @"on|by|due|until|till|before|am|bis|zum|ab|spätestens|până\s+(?:pe|la|în)|pana\s+(?:pe|la|in)|până|pana|pe|din|para\s+el|para|hasta\s+el|antes\s+del|el";
        const string lead = @"(?:\b(?:" + words + @")\s+)?";
        const string leadRequired = @"\b(?:" + words + @")\s+";
        const RegexOptions o = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        DateOnly? Numeric(Match m) =>
            Date(today, int.Parse(m.Groups["d"].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture), m.Groups["y"].Value);

        // Numbers only when they're clearly a date ("1.2" or "2.5 kg" aren't): with a year, with the German trailing
        // dot (15.11.), or after "by"/"bis"/… (by 15/11).
        yield return (new Regex(lead + @"\b(?<d>\d{1,2})[./](?<m>\d{1,2})[./](?<y>\d{4}|\d{2})\b\.?", o), Numeric);
        yield return (new Regex(lead + @"\b(?<d>\d{1,2})\.(?<m>\d{1,2})\.(?=\s|$|[,;])", o), Numeric);
        yield return (new Regex(leadRequired + @"(?<d>\d{1,2})[./](?<m>\d{1,2})(?<y>)\b", o), Numeric);
        // 15 Nov, 15. November, 15th of November, 15 noiembrie, 15 de noviembre
        yield return (new Regex(lead + @"\b(\d{1,2})(?:st|nd|rd|th)?\.?\s+(?:of\s+|de\s+)?([a-zäöăâîșț]{3,10})\b\.?(?:\s+(?:de\s+)?(\d{4}))?", o), m =>
            Months.TryGetValue(m.Groups[2].Value, out var month) ? Date(today, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), month, m.Groups[3].Value) : null);
        // Nov 15, November 15th. Not "mai 2": in Romanian that's "2 more", and May-first isn't how it writes dates.
        yield return (new Regex(lead + @"\b([a-zäöăâîșț]{3,10})\.?\s+(\d{1,2})(?:st|nd|rd|th)?\b(?:,?\s+(\d{4}))?", o), m =>
            !m.Groups[1].Value.Equals("mai", StringComparison.OrdinalIgnoreCase) && Months.TryGetValue(m.Groups[1].Value, out var month)
                ? Date(today, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), month, m.Groups[3].Value) : null);
        // in 3 days / in 2 weeks / in 3 Tagen / in 2 Wochen / în 3 zile / peste 2 săptămâni / en 3 días / dentro de 2 semanas
        yield return (new Regex(@"\b(?:in|în|peste|en|dentro\s+de)\s+(\d{1,3})\s+(days?|weeks?|tagen|wochen|months?|monaten|zile|zi|săptămâni|saptamani|săptămână|saptamana|luni|lună|luna|días|dias|día|dia|semanas?|meses|mes)\b", o), m =>
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var unit = m.Groups[2].Value.ToLowerInvariant();
            return unit.StartsWith("day") || unit is "tagen" or "zile" or "zi" or "días" or "dias" or "día" or "dia" ? today.AddDays(n)
                : unit.StartsWith("week") || unit == "wochen" || unit.StartsWith("săpt") || unit.StartsWith("sapt") || unit.StartsWith("semana") ? today.AddDays(7 * n)
                : today.AddMonths(n);
        });
        yield return (new Regex(lead + @"\b(?:day after tomorrow|übermorgen|poimâine|poimaine|pasado\s+mañana|pasado\s+manana)\b", o), _ => today.AddDays(2));
        yield return (new Regex(lead + @"\b(?:today|tonight|heute(?:\s+abend)?|azi|astăzi|astazi|diseară|diseara|hoy|esta\s+noche)\b", o), _ => today);
        yield return (new Regex(lead + @"\b(?:tomorrow|tmrw|morgen|mâine|maine|(?<!\bla\s+)(?:mañana|manana))\b", o), _ => today.AddDays(1));
        yield return (new Regex(lead + @"\b(?:next week|nächste woche|naechste woche|săptămâna viitoare|saptamana viitoare|(?:la\s+)?semana\s+que\s+viene|(?:la\s+)?pr[oó]xima\s+semana)\b", o), _ => Next(today, DayOfWeek.Monday, strictlyAfter: true));
        yield return (new Regex(lead + @"\b(?:this weekend|(?:the |am |zum |în |in |la )?weekend(?:ul acesta)?|wochenende|(?:este\s+|el\s+)?fin\s+de\s+semana)\b", o), _ => Next(today, DayOfWeek.Saturday, strictlyAfter: false));
        yield return (new Regex(lead + @"\b(?:next month|nächsten monat|naechsten monat|luna viitoare|(?:el\s+)?mes\s+que\s+viene|(?:el\s+)?pr[oó]ximo\s+mes)\b", o), _ => new DateOnly(today.Year, today.Month, 1).AddMonths(1));
        // "by friday", "next friday", "am Freitag", "până vineri", or a weekday at the very end ("Call Ana friday"). In
        // the middle of a title ("Read Monday Morning Memo") it's just a word.
        var days = string.Join('|', Weekdays.Keys.OrderByDescending(k => k.Length));
        yield return (new Regex(@"(?:" + leadRequired + @")?\b(next\s+|nächsten\s+|naechsten\s+|kommenden\s+|pr[oó]ximo\s+|pr[oó]xima\s+)(" + days + @")\b", o), m =>
            Next(today, Weekdays[m.Groups[2].Value], strictlyAfter: true));
        // Romanian and Spanish put "next" after the day: "vinerea viitoare", "el viernes que viene".
        yield return (new Regex(@"(?:" + leadRequired + @")?\b()(" + days + @")\s+(?:viitoare|que\s+viene)\b", o), m =>
            Next(today, Weekdays[m.Groups[2].Value], strictlyAfter: true));
        yield return (new Regex(leadRequired + @"()(" + days + @")\b", o), m => Next(today, Weekdays[m.Groups[2].Value], strictlyAfter: false));
        yield return (new Regex(@"\b()(" + days + @")\s*[.!]?$", o), m => Next(today, Weekdays[m.Groups[2].Value], strictlyAfter: false));
    }

    private static DateOnly Next(DateOnly today, DayOfWeek day, bool strictlyAfter)
    {
        var ahead = ((int)day - (int)today.DayOfWeek + 7) % 7;
        if (ahead == 0 && strictlyAfter) ahead = 7;
        return today.AddDays(ahead);
    }

    /// <summary>A day and month (and maybe year); without a year, the next time it comes round.</summary>
    private static DateOnly? Date(DateOnly today, int day, int month, string year)
    {
        if (month is < 1 or > 12 || day < 1 || day > 31) return null;
        var y = year.Length switch { 4 => int.Parse(year, CultureInfo.InvariantCulture), 2 => 2000 + int.Parse(year, CultureInfo.InvariantCulture), _ => today.Year };
        if (day > DateTime.DaysInMonth(y, month)) return null;
        var date = new DateOnly(y, month, day);
        return year.Length == 0 && date < today ? date.AddYears(1) : date;
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
