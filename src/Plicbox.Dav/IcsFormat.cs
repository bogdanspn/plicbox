using System.Globalization;
using System.Text;
using Plicbox.Core;

namespace Plicbox.Dav;

/// <summary>
/// One VEVENT: the event (its UID as <see cref="CalendarEvent.RemoteId"/>, its RRULE as <see cref="CalendarEvent.Recurrence"/>),
/// occurrences taken out of its series (EXDATE), or, for a changed occurrence, the one it replaces (RECURRENCE-ID).
/// Those are wall-clock times (dates at midnight for all-day events).
/// </summary>
public sealed record IcsItem(CalendarEvent Event, IReadOnlyList<DateTime> ExDates, DateTime? RecurrenceId);

/// <summary>
/// iCalendar files (RFC 5545), the events part: what calendar apps export and invitations carry. Reads what Google,
/// Outlook, Apple and Thunderbird write (time zones by IANA or Windows name, folded lines, durations, alarms); writes
/// files they all read.
/// </summary>
public static class IcsFormat
{
    // ───────── writing ─────────

    public static string Write(string calendarName, IEnumerable<IcsItem> items)
    {
        var sb = new StringBuilder();
        void Line(string text)
        {
            // Lines longer than 75 bytes are folded: CRLF and a space, never inside a character.
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

        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//Plicbox//Plicbox//EN");
        Line("CALSCALE:GREGORIAN");
        Line("X-WR-CALNAME:" + Escape(calendarName));
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var (e, exDates, recurrenceId) in items)
        {
            // Repeating events keep their wall-clock time across summer time, so they go in their own time zone;
            // anything else in UTC, which every app reads the same.
            var zone = e.Recurrence is not null || recurrenceId is not null ? ZoneId(e.TimeZone) : null;
            Line("BEGIN:VEVENT");
            Line("UID:" + Escape(e.RemoteId));
            Line("DTSTAMP:" + stamp);
            if (e.IsAllDay)
            {
                Line("DTSTART;VALUE=DATE:" + e.StartDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                Line("DTEND;VALUE=DATE:" + (e.EndDate > e.StartDate ? e.EndDate : e.StartDate.AddDays(1)).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
            else
            {
                Line("DTSTART" + When(e.Start, zone));
                Line("DTEND" + When(e.End, zone));
            }
            if (recurrenceId is { } original) Line("RECURRENCE-ID" + Wall(original, e.IsAllDay, zone));
            if (e.Recurrence is { Length: > 0 } rule) Line("RRULE:" + (rule.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase) ? rule[6..] : rule));
            foreach (var skipped in exDates) Line("EXDATE" + Wall(skipped, e.IsAllDay, zone));
            Line("SUMMARY:" + Escape(e.Title));
            if (!string.IsNullOrEmpty(e.Location)) Line("LOCATION:" + Escape(e.Location));
            if (!string.IsNullOrEmpty(e.Description)) Line("DESCRIPTION:" + Escape(e.Description));
            if (!string.IsNullOrEmpty(e.MeetingUrl)) Line("URL:" + e.MeetingUrl);
            if (!e.IsBusy) Line("TRANSP:TRANSPARENT");
            if (e.Status == "tentative") Line("STATUS:TENTATIVE");
            if (e.ReminderMinutes is { } minutes)
            {
                Line("BEGIN:VALARM");
                Line("ACTION:DISPLAY");
                Line("DESCRIPTION:" + Escape(e.Title));
                Line($"TRIGGER:-PT{minutes}M");
                Line("END:VALARM");
            }
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    private static string When(DateTimeOffset t, string? zone) =>
        zone is not null && Zone(zone) is { } tz
            ? $";TZID={zone}:" + TimeZoneInfo.ConvertTime(t, tz).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)
            : ":" + t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Wall(DateTime wall, bool allDay, string? zone) =>
        allDay ? ";VALUE=DATE:" + wall.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
        : zone is not null ? $";TZID={zone}:" + wall.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture)
        : ":" + wall.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The IANA name to write ("Europe/Berlin"), the event's own or this computer's.</summary>
    private static string ZoneId(string? zone)
    {
        var name = string.IsNullOrEmpty(zone) ? Calendars.LocalIanaZone : zone;
        return TimeZoneInfo.TryConvertWindowsIdToIanaId(name, out var iana) ? iana : name;
    }

    private static string Escape(string text) =>
        text.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    // ───────── reading ─────────

    /// <summary>The file's events (cancelled ones left out). Anything it can't make sense of is skipped, not fatal.</summary>
    public static IReadOnlyList<IcsItem> Read(string text)
    {
        var items = new List<IcsItem>();
        Dictionary<string, List<(Dictionary<string, string> Params, string Value)>>? current = null;
        var depth = new Stack<string>();
        int? alarm = null;
        foreach (var raw in Unfold(text))
        {
            var (name, parameters, value) = Split(raw);
            if (name.Length == 0) continue;
            if (name == "BEGIN")
            {
                depth.Push(value.ToUpperInvariant());
                if (depth.Peek() == "VEVENT") { current = []; alarm = null; }
                continue;
            }
            if (name == "END")
            {
                var ended = depth.Count > 0 ? depth.Pop() : "";
                if (ended == "VEVENT" && current is not null)
                {
                    if (ToItem(current, alarm) is { } item) items.Add(item);
                    current = null;
                }
                continue;
            }
            if (current is null) continue;
            if (depth.Count > 0 && depth.Peek() == "VALARM")
            {
                if (name == "TRIGGER" && !parameters.ContainsKey("VALUE") && Duration(value) is { } before && before <= TimeSpan.Zero)
                    alarm = alarm is { } other ? Math.Min(other, (int)-before.TotalMinutes) : (int)-before.TotalMinutes;
                continue;
            }
            if (depth.Count > 0 && depth.Peek() != "VEVENT") continue; // inside something else in the event
            if (!current.TryGetValue(name, out var list)) current[name] = list = [];
            list.Add((parameters, value));
        }
        return items;
    }

    private static IcsItem? ToItem(Dictionary<string, List<(Dictionary<string, string> Params, string Value)>> p, int? alarm)
    {
        (Dictionary<string, string> Params, string Value)? One(string name) => p.TryGetValue(name, out var l) && l.Count > 0 ? l[0] : null;
        if (One("STATUS")?.Value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase) == true) return null;
        if (One("DTSTART") is not { } dtStart || Time(dtStart.Params, dtStart.Value) is not { } start) return null;

        var allDay = start.AllDay;
        DateTimeOffset end;
        DateOnly endDate;
        if (One("DTEND") is { } dtEnd && Time(dtEnd.Params, dtEnd.Value) is { } e)
        {
            end = e.At;
            endDate = DateOnly.FromDateTime(e.Wall);
        }
        else if (One("DURATION") is { } d && Duration(d.Value) is { } length)
        {
            end = start.At + length;
            endDate = DateOnly.FromDateTime(start.Wall + length);
        }
        else
        {
            end = allDay ? start.At.AddDays(1) : start.At;
            endDate = DateOnly.FromDateTime(start.Wall).AddDays(1);
        }
        var startDate = DateOnly.FromDateTime(start.Wall);
        if (allDay && endDate <= startDate) endDate = startDate.AddDays(1);

        var exDates = (p.GetValueOrDefault("EXDATE") ?? [])
            .SelectMany(x => x.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => Time(x.Params, v)))
            .OfType<Moment>().Select(m => allDay ? m.Wall.Date : m.At.LocalDateTime).ToList();
        var recurrenceId = One("RECURRENCE-ID") is { } rid && Time(rid.Params, rid.Value) is { } original
            ? allDay ? original.Wall.Date : original.At.LocalDateTime
            : (DateTime?)null;

        var description = One("DESCRIPTION")?.Value is { } desc ? Unescape(desc) : null;
        var url = One("URL")?.Value;
        return new IcsItem(new CalendarEvent
        {
            RemoteId = One("UID")?.Value is { Length: > 0 } uid ? Unescape(uid) : "ics-" + Guid.NewGuid().ToString("N"),
            Title = One("SUMMARY")?.Value is { } summary ? Unescape(summary) : "",
            Location = One("LOCATION")?.Value is { Length: > 0 } location ? Unescape(location) : null,
            Description = string.IsNullOrWhiteSpace(description) ? null : description,
            IsAllDay = allDay,
            Start = allDay ? new DateTimeOffset(startDate.ToDateTime(TimeOnly.MinValue)) : start.At,
            End = allDay ? new DateTimeOffset(endDate.ToDateTime(TimeOnly.MinValue)) : end,
            StartDate = allDay ? startDate : default,
            EndDate = allDay ? endDate : default,
            TimeZone = start.Zone,
            Recurrence = One("RRULE")?.Value is { Length: > 0 } rule ? rule : null,
            IsBusy = !string.Equals(One("TRANSP")?.Value, "TRANSPARENT", StringComparison.OrdinalIgnoreCase),
            Status = string.Equals(One("STATUS")?.Value, "TENTATIVE", StringComparison.OrdinalIgnoreCase) ? "tentative" : "confirmed",
            ReminderMinutes = alarm,
            MeetingUrl = url is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? url : null,
            OrganizerEmail = One("ORGANIZER")?.Value is { } organizer ? Address(organizer) : null,
            OrganizerName = One("ORGANIZER")?.Params.GetValueOrDefault("CN"),
            Updated = DateTimeOffset.UtcNow,
        }, exDates, recurrenceId);
    }

    private static string? Address(string value) =>
        value.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value.Contains('@') ? value : null;

    /// <summary>A start or end: the moment, its wall-clock time, its zone (IANA) and whether it's a whole day.</summary>
    private sealed record Moment(DateTimeOffset At, DateTime Wall, string? Zone, bool AllDay);

    private static Moment? Time(Dictionary<string, string> parameters, string value)
    {
        value = value.Trim();
        if (parameters.GetValueOrDefault("VALUE")?.Equals("DATE", StringComparison.OrdinalIgnoreCase) == true || value.Length == 8)
        {
            if (!DateTime.TryParseExact(value[..Math.Min(8, value.Length)], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) return null;
            return new Moment(new DateTimeOffset(day), day, null, AllDay: true);
        }
        var utc = value.EndsWith('Z');
        if (!DateTime.TryParseExact(value.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var wall)) return null;
        if (utc)
        {
            var at = new DateTimeOffset(DateTime.SpecifyKind(wall, DateTimeKind.Utc));
            return new Moment(at, at.LocalDateTime, null, false);
        }
        // A named zone (IANA, Windows, or a path ending in one); "floating" times and unknown names are this computer's.
        var zone = parameters.GetValueOrDefault("TZID") is { Length: > 0 } tzid ? Zone(tzid) : null;
        var tz = zone ?? TimeZoneInfo.Local;
        var offset = tz.IsInvalidTime(wall) ? tz.GetUtcOffset(wall.AddHours(1)) : tz.GetUtcOffset(wall);
        return new Moment(new DateTimeOffset(wall, offset), wall, zone is null ? null : Iana(zone), false);
    }

    private static TimeZoneInfo? Zone(string id)
    {
        id = id.Trim('"');
        foreach (var candidate in new[] { id, id.Contains('/') ? string.Join('/', id.Split('/').TakeLast(2)) : id })
            if (TimeZoneInfo.TryFindSystemTimeZoneById(candidate, out var tz)) return tz;
        return null;
    }

    private static string Iana(TimeZoneInfo tz) =>
        tz.HasIanaId ? tz.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var iana) ? iana : tz.Id;

    /// <summary>"PT1H30M", "-PT15M", "P1D", "P2W".</summary>
    private static TimeSpan? Duration(string value)
    {
        value = value.Trim();
        var negative = value.StartsWith('-');
        value = value.TrimStart('+', '-');
        if (!value.StartsWith('P')) return null;
        var total = TimeSpan.Zero;
        var number = 0;
        var inTime = false;
        foreach (var c in value[1..])
        {
            if (char.IsDigit(c)) { number = number * 10 + (c - '0'); continue; }
            switch (c)
            {
                case 'T': inTime = true; break;
                case 'W': total += TimeSpan.FromDays(7 * number); break;
                case 'D': total += TimeSpan.FromDays(number); break;
                case 'H' when inTime: total += TimeSpan.FromHours(number); break;
                case 'M' when inTime: total += TimeSpan.FromMinutes(number); break;
                case 'S' when inTime: total += TimeSpan.FromSeconds(number); break;
                default: return null;
            }
            number = 0;
        }
        return negative ? -total : total;
    }

    private static IEnumerable<string> Unfold(string text)
    {
        var current = new StringBuilder();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                current.Append(line, 1, line.Length - 1);
                continue;
            }
            if (current.Length > 0) yield return current.ToString();
            current.Clear().Append(line);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>"DTSTART;TZID=Europe/Berlin:20261116T090000" → name, parameters (quoted values may hold ':' and ';'), value.</summary>
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
        var head = line[..colon];
        var parts = new List<string>();
        var part = new StringBuilder();
        quoted = false;
        foreach (var c in head)
        {
            if (c == '"') quoted = !quoted;
            if (c == ';' && !quoted) { parts.Add(part.ToString()); part.Clear(); }
            else part.Append(c);
        }
        parts.Add(part.ToString());
        foreach (var p in parts.Skip(1))
            if (p.IndexOf('=') is var eq and > 0) parameters[p[..eq]] = p[(eq + 1)..].Trim('"');
        return (parts[0].ToUpperInvariant(), parameters, line[(colon + 1)..]);
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
}
