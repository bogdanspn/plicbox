using Plicbox.Core;
using System.Globalization;

namespace Plicbox.Dav;

public enum RepeatKind { None, Daily, Weekdays, Weekly, Monthly, Yearly, Custom }

/// <summary>Small calendar helpers shared by sync, editor and views.</summary>
public static class Calendars
{
    /// <summary>This PC's time zone as an IANA id ("Europe/Berlin"), which Google requires.</summary>
    public static string LocalIanaZone { get; } =
        TimeZoneInfo.TryConvertWindowsIdToIanaId(TimeZoneInfo.Local.Id, out var iana) ? iana : TimeZoneInfo.Local.Id;

    /// <summary>ISO 8601 week number (German "KW").</summary>
    public static int IsoWeek(DateOnly day) => ISOWeek.GetWeekOfYear(day.ToDateTime(TimeOnly.MinValue));

    private static readonly string[] ByDay = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>The RRULE for a preset, anchored on the event's first day.</summary>
    public static string? BuildRule(RepeatKind kind, DateTime start) => kind switch
    {
        RepeatKind.Daily => "FREQ=DAILY",
        RepeatKind.Weekdays => "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR",
        RepeatKind.Weekly => $"FREQ=WEEKLY;BYDAY={ByDay[(int)start.DayOfWeek]}",
        RepeatKind.Monthly => $"FREQ=MONTHLY;BYMONTHDAY={start.Day}",
        RepeatKind.Yearly => "FREQ=YEARLY",
        _ => null,
    };

    /// <summary>Which preset a rule is; anything fancier (intervals, end dates, nth weekday) is Custom and kept as-is.</summary>
    public static RepeatKind Classify(string? rule)
    {
        if (string.IsNullOrWhiteSpace(rule)) return RepeatKind.None;
        var parts = Parts(rule);
        if (parts.Keys.Any(k => k is "INTERVAL" or "COUNT" or "UNTIL" or "BYSETPOS") && parts.GetValueOrDefault("INTERVAL", "1") != "1")
            return RepeatKind.Custom;
        if (parts.ContainsKey("COUNT") || parts.ContainsKey("UNTIL")) return RepeatKind.Custom;
        return parts.GetValueOrDefault("FREQ") switch
        {
            "DAILY" when parts.Count == 1 => RepeatKind.Daily,
            "WEEKLY" when parts.GetValueOrDefault("BYDAY") == "MO,TU,WE,TH,FR" => RepeatKind.Weekdays,
            "WEEKLY" when parts.GetValueOrDefault("BYDAY") is null or { Length: 2 } => RepeatKind.Weekly,
            "MONTHLY" when parts.ContainsKey("BYMONTHDAY") || parts.Count == 1 => RepeatKind.Monthly,
            "YEARLY" => RepeatKind.Yearly,
            _ => RepeatKind.Custom,
        };
    }

    /// <summary>"Every week on Monday", "Every weekday", "Every 2 weeks", … in the user's language for day names.</summary>
    public static string Describe(string? rule, DateTime start)
    {
        if (string.IsNullOrWhiteSpace(rule)) return L.T("Does not repeat");
        var parts = Parts(rule);
        var interval = int.TryParse(parts.GetValueOrDefault("INTERVAL"), out var i) && i > 1 ? i : 1;
        var freq = parts.GetValueOrDefault("FREQ");
        var text = (freq, interval == 1) switch
        {
            ("DAILY", true) => L.T("Every day"),
            ("DAILY", false) => L.F("Every {0} days", interval),
            ("WEEKLY", true) => L.T("Every week"),
            ("WEEKLY", false) => L.F("Every {0} weeks", interval),
            ("MONTHLY", true) => L.T("Every month"),
            ("MONTHLY", false) => L.F("Every {0} months", interval),
            ("YEARLY", true) => L.T("Every year"),
            ("YEARLY", false) => L.F("Every {0} years", interval),
            (_, true) => L.T("Repeats"),
            _ => L.F("Repeats every {0} times", interval),
        };
        if (parts.GetValueOrDefault("BYDAY") is { } days)
        {
            text = days == "MO,TU,WE,TH,FR" && interval == 1 ? L.T("Every weekday")
                : L.F("{0} on {1}", text, string.Join(", ", days.Split(',').Select(DayName)));
        }
        else if (freq == "WEEKLY")
        {
            text = L.F("{0} on {1}", text, CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(start.DayOfWeek));
        }
        if (parts.GetValueOrDefault("COUNT") is { } count) text += L.F(", {0} times", count);
        if (parts.GetValueOrDefault("UNTIL") is { Length: >= 8 } until
            && DateTime.TryParseExact(until[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var u))
            text += L.F(", until {0}", u.ToString("d", CultureInfo.CurrentCulture));
        return text;
    }

    private static string DayName(string code)
    {
        // "2TU" (second Tuesday) and plain "TU" both end in the two-letter day.
        var day = Array.IndexOf(ByDay, code[^2..]);
        var name = day < 0 ? code : CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)day);
        return code.Length > 2 ? $"{code[..^2]}. {name}" : name;
    }

    private static Dictionary<string, string> Parts(string rule) =>
        rule.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].ToUpperInvariant(), p => p[1].ToUpperInvariant());
}

/// <summary>One of Google's public holiday calendars ("en.german#holiday@group.v.calendar.google.com").</summary>
public sealed record HolidayCalendar(string Country, string Key)
{
    public string GoogleId(string language) => $"{language}.{Key}#holiday@group.v.calendar.google.com";

    /// <summary>The countries offered in the menu; Google has many more, these are the likely ones.</summary>
    public static IReadOnlyList<HolidayCalendar> Common { get; } =
    [
        new(L.T("Germany"), "german"), new(L.T("Austria"), "austrian"), new(L.T("Romania"), "romanian"), new(L.T("United Kingdom"), "uk"),
        new(L.T("United States"), "usa"), new(L.T("France"), "french"), new(L.T("Italy"), "italian"), new(L.T("Spain"), "spain"),
        new(L.T("Netherlands"), "dutch"), new(L.T("Poland"), "polish"), new(L.T("Hungary"), "hungarian"), new(L.T("Christian holidays"), "christian"),
    ];
}
