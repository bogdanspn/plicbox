using System.Globalization;

namespace Plicbox.Dav;

/// <summary>
/// Works out the dates of a repeating event from its RRULE (RFC 5545), for calendars on this computer: Google expands
/// its own events, nothing else does. Covers FREQ DAILY/WEEKLY/MONTHLY/YEARLY with INTERVAL, COUNT, UNTIL, BYDAY
/// (with ordinals: 2TU, -1FR), BYMONTHDAY, BYMONTH, BYSETPOS and WKST, which is what the editor writes and what
/// calendar files in practice carry. Times are wall-clock: a 9:00 meeting stays at 9:00 across daylight saving.
/// </summary>
public static class Recurrence
{
    /// <summary>A rule that never ends is still only followed this far, so nothing can loop forever.</summary>
    private const int MaxPeriods = 100_000;

    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>
    /// The starts of the occurrences in [<paramref name="from"/>, <paramref name="to"/>), in order. Counting (COUNT)
    /// always begins at <paramref name="first"/>, which is itself the first occurrence. All times are local.
    /// </summary>
    public static IEnumerable<DateTime> Occurrences(string rule, DateTime first, DateTime from, DateTime to)
    {
        var r = Rule.Parse(rule);
        if (first >= to) yield break;
        if (first >= from) yield return first;
        if (r is null) yield break; // not a rule we can follow: just the event itself

        var produced = 1;
        var time = TimeOnly.FromDateTime(first);
        var firstDay = DateOnly.FromDateTime(first);
        for (var k = 0; k < MaxPeriods; k++)
        {
            if (r.PeriodStart(firstDay, k) is not { } periodStart || periodStart.ToDateTime(TimeOnly.MinValue) >= to) yield break;
            foreach (var day in r.Candidates(firstDay, k))
            {
                var start = day.ToDateTime(time);
                if (start <= first) continue;
                if (r.Until is { } until && start > until) yield break;
                if (r.Count is { } count && ++produced > count) yield break;
                if (start >= to) yield break;
                if (start >= from) yield return start;
            }
        }
    }

    private sealed class Rule
    {
        public required string Freq { get; init; }
        public int Interval { get; init; } = 1;
        public int? Count { get; init; }
        public DateTime? Until { get; init; }
        public List<(int Ordinal, DayOfWeek Day)> ByDay { get; } = [];
        public List<int> ByMonthDay { get; } = [];
        public List<int> ByMonth { get; } = [];
        public List<int> BySetPos { get; } = [];
        public DayOfWeek WeekStart { get; init; } = DayOfWeek.Monday;

        public static Rule? Parse(string text)
        {
            if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase)) text = text[6..];
            var parts = text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
                .GroupBy(p => p[0].ToUpperInvariant()).ToDictionary(g => g.Key, g => g.First()[1].ToUpperInvariant());
            if (parts.GetValueOrDefault("FREQ") is not ("DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY") || parts.ContainsKey("BYYEARDAY") || parts.ContainsKey("BYWEEKNO"))
                return null;
            var rule = new Rule
            {
                Freq = parts["FREQ"],
                Interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), CultureInfo.InvariantCulture, out var i) && i > 0 ? i : 1,
                Count = int.TryParse(parts.GetValueOrDefault("COUNT"), CultureInfo.InvariantCulture, out var c) && c > 0 ? c : null,
                Until = ParseUntil(parts.GetValueOrDefault("UNTIL")),
                WeekStart = parts.GetValueOrDefault("WKST") is { } w && Array.IndexOf(DayCodes, w) is >= 0 and var ws ? (DayOfWeek)ws : DayOfWeek.Monday,
            };
            foreach (var code in List(parts, "BYDAY"))
            {
                if (code.Length < 2 || Array.IndexOf(DayCodes, code[^2..]) is not (>= 0 and var day)) return null;
                var ordinal = code.Length > 2 ? int.TryParse(code[..^2], CultureInfo.InvariantCulture, out var o) ? o : int.MinValue : 0;
                if (ordinal == int.MinValue) return null;
                rule.ByDay.Add((ordinal, (DayOfWeek)day));
            }
            if (!Numbers(parts, "BYMONTHDAY", rule.ByMonthDay, -31, 31) || !Numbers(parts, "BYMONTH", rule.ByMonth, 1, 12)
                || !Numbers(parts, "BYSETPOS", rule.BySetPos, -366, 366))
                return null;
            return rule;
        }

        private static IEnumerable<string> List(Dictionary<string, string> parts, string key) =>
            parts.GetValueOrDefault(key)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        private static bool Numbers(Dictionary<string, string> parts, string key, List<int> into, int min, int max)
        {
            foreach (var s in List(parts, key))
            {
                if (!int.TryParse(s, CultureInfo.InvariantCulture, out var n) || n == 0 || n < min || n > max) return false;
                into.Add(n);
            }
            return true;
        }

        /// <summary>"20261231" (that whole day), "20261231T170000" (local) or "20261231T160000Z" (UTC).</summary>
        private static DateTime? ParseUntil(string? s)
        {
            if (s is null) return null;
            if (s.Length == 8 && DateTime.TryParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return day.AddDays(1).AddTicks(-1);
            var utc = s.EndsWith('Z');
            if (!DateTime.TryParseExact(s.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return null;
            return utc ? DateTime.SpecifyKind(t, DateTimeKind.Utc).ToLocalTime() : t;
        }

        /// <summary>The first day of the k-th period (day, week, month or year) after the one holding the first day.</summary>
        public DateOnly? PeriodStart(DateOnly first, int k)
        {
            try
            {
                return Freq switch
                {
                    "DAILY" => first.AddDays(k * Interval),
                    "WEEKLY" => WeekOf(first).AddDays(7 * k * Interval),
                    "MONTHLY" => new DateOnly(first.Year, first.Month, 1).AddMonths(k * Interval),
                    _ => new DateOnly(first.Year + k * Interval, 1, 1),
                };
            }
            catch (ArgumentOutOfRangeException)
            {
                return null; // past year 9999
            }
        }

        private DateOnly WeekOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek - (int)WeekStart + 7) % 7));

        /// <summary>The days the rule picks in the k-th period, in order.</summary>
        public IEnumerable<DateOnly> Candidates(DateOnly first, int k)
        {
            var start = PeriodStart(first, k)!.Value;
            List<DateOnly> days;
            switch (Freq)
            {
                case "DAILY":
                    days = Matches(start) ? [start] : [];
                    break;
                case "WEEKLY":
                    var weekdays = ByDay.Count > 0 ? ByDay.Select(d => d.Day).Distinct() : [first.DayOfWeek];
                    days = weekdays.Select(d => start.AddDays(((int)d - (int)WeekStart + 7) % 7))
                        .Where(d => ByMonth.Count == 0 || ByMonth.Contains(d.Month)).Order().ToList();
                    break;
                case "MONTHLY":
                    days = ByMonth.Count > 0 && !ByMonth.Contains(start.Month) ? [] : InMonth(start.Year, start.Month, first);
                    break;
                default:
                    if (ByDay.Count > 0 && ByMonth.Count == 0 && ByMonthDay.Count == 0)
                    {
                        // "20MO" in a yearly rule: the 20th Monday of the year.
                        days = Weekdays(new DateOnly(start.Year, 1, 1), new DateOnly(start.Year, 12, 31));
                        break;
                    }
                    var months = ByMonth.Count > 0 ? ByMonth.Order() : (IEnumerable<int>)[first.Month];
                    days = months.SelectMany(m => InMonth(start.Year, m, first)).ToList();
                    break;
            }
            return SetPositions(days);
        }

        /// <summary>DAILY with BY* parts: they only filter.</summary>
        private bool Matches(DateOnly d) =>
            (ByMonth.Count == 0 || ByMonth.Contains(d.Month))
            && (ByMonthDay.Count == 0 || ByMonthDay.Any(n => DayOfMonth(d.Year, d.Month, n) == d.Day))
            && (ByDay.Count == 0 || ByDay.Any(b => b.Day == d.DayOfWeek));

        private List<DateOnly> InMonth(int year, int month, DateOnly first)
        {
            var last = DateTime.DaysInMonth(year, month);
            IEnumerable<DateOnly> days;
            if (ByMonthDay.Count > 0)
            {
                days = ByMonthDay.Select(n => DayOfMonth(year, month, n)).Where(d => d is >= 1 && d <= last).Select(d => new DateOnly(year, month, d!.Value));
                if (ByDay.Count > 0) days = days.Where(d => ByDay.Any(b => b.Day == d.DayOfWeek));
            }
            else if (ByDay.Count > 0)
            {
                days = Weekdays(new DateOnly(year, month, 1), new DateOnly(year, month, last));
            }
            else
            {
                // The first day's date each month; months without it (the 31st in April) are skipped, as RFC 5545 says.
                days = first.Day <= last ? [new DateOnly(year, month, first.Day)] : [];
            }
            return days.Distinct().Order().ToList();
        }

        /// <summary>The BYDAY days between two dates, an ordinal counting within that span ("2TU": its second Tuesday).</summary>
        private List<DateOnly> Weekdays(DateOnly from, DateOnly to)
        {
            var all = new List<DateOnly>();
            foreach (var (ordinal, day) in ByDay)
            {
                var matching = new List<DateOnly>();
                for (var d = from.AddDays(((int)day - (int)from.DayOfWeek + 7) % 7); d <= to; d = d.AddDays(7)) matching.Add(d);
                if (ordinal == 0) all.AddRange(matching);
                else if (ordinal > 0 && ordinal <= matching.Count) all.Add(matching[ordinal - 1]);
                else if (ordinal < 0 && -ordinal <= matching.Count) all.Add(matching[matching.Count + ordinal]);
            }
            return all.Distinct().Order().ToList();
        }

        private static int? DayOfMonth(int year, int month, int n)
        {
            var last = DateTime.DaysInMonth(year, month);
            var day = n > 0 ? n : last + n + 1;
            return day >= 1 && day <= last ? day : null;
        }

        private IEnumerable<DateOnly> SetPositions(List<DateOnly> days)
        {
            if (BySetPos.Count == 0) return days;
            return BySetPos.Select(p => p > 0 ? p - 1 : days.Count + p).Where(i => i >= 0 && i < days.Count).Select(i => days[i]).Distinct().Order();
        }
    }
}
