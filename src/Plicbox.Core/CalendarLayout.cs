namespace Plicbox.Core;

/// <summary>A timed event placed in one day column: clipped to that day, in column <see cref="Column"/> of <see cref="Columns"/>.</summary>
public sealed record TimedPlacement(CalendarEvent Event, DateTime Start, DateTime End, int Column, int Columns);

/// <summary>An all-day (or multi-day) event placed in the all-day strip: from day index <see cref="FirstDay"/>, <see cref="Days"/> wide, on row <see cref="Lane"/>.</summary>
public sealed record SpanPlacement(CalendarEvent Event, int FirstDay, int Days, int Lane, bool ContinuesBefore, bool ContinuesAfter);

public static class CalendarLayout
{
    /// <summary>Events shorter than this still get room for a line of text.</summary>
    public static readonly TimeSpan MinVisible = TimeSpan.FromMinutes(20);

    /// <summary>True for events shown in the all-day strip: real all-day events, and timed ones lasting a day or more.</summary>
    public static bool IsSpanning(CalendarEvent e) => e.IsAllDay || e.End - e.Start >= TimeSpan.FromHours(24);

    /// <summary>
    /// Lays out one day's timed events side by side where they overlap: each group of overlapping events gets
    /// as many columns as it needs at its busiest moment, and every event takes the first free column.
    /// </summary>
    public static IReadOnlyList<TimedPlacement> LayoutDay(IEnumerable<CalendarEvent> events, DateOnly day)
    {
        var dayStart = day.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        var items = events
            .Where(e => !IsSpanning(e))
            .Select(e => (Event: e, Start: Max(e.Start.LocalDateTime, dayStart), End: Min(e.End.LocalDateTime, dayEnd)))
            .Where(x => x.End > x.Start || (x.Start == x.End && x.Start >= dayStart && x.Start < dayEnd))
            .OrderBy(x => x.Start).ThenByDescending(x => x.End - x.Start)
            .ToList();

        var result = new List<TimedPlacement>();
        var cluster = new List<(CalendarEvent Event, DateTime Start, DateTime End, int Column)>();
        var columnEnds = new List<DateTime>();
        var clusterEnd = DateTime.MinValue;

        void Flush()
        {
            foreach (var c in cluster) result.Add(new TimedPlacement(c.Event, c.Start, c.End, c.Column, columnEnds.Count));
            cluster.Clear();
            columnEnds.Clear();
        }

        foreach (var (ev, start, end) in items)
        {
            var visibleEnd = Max(end, start + MinVisible); // short events still need room on screen
            if (cluster.Count > 0 && start >= clusterEnd) Flush();
            var column = columnEnds.FindIndex(e => e <= start);
            if (column < 0)
            {
                column = columnEnds.Count;
                columnEnds.Add(visibleEnd);
            }
            else
            {
                columnEnds[column] = visibleEnd;
            }
            cluster.Add((ev, start, end, column));
            clusterEnd = cluster.Count == 1 ? visibleEnd : Max(clusterEnd, visibleEnd);
        }
        Flush();
        return result;
    }

    /// <summary>
    /// Lays out all-day and multi-day events across <paramref name="days"/> consecutive days as bars in lanes:
    /// longer events first, each in the lowest lane that's free for all its days.
    /// </summary>
    public static IReadOnlyList<SpanPlacement> LayoutSpans(IEnumerable<CalendarEvent> events, DateOnly firstDay, int days)
    {
        var lastDay = firstDay.AddDays(days); // exclusive
        var items = events
            .Where(IsSpanning)
            .Select(e => (Event: e, From: StartDay(e), To: EndDay(e)))
            .Where(x => x.From < lastDay && x.To > firstDay)
            .OrderBy(x => x.From).ThenByDescending(x => x.To.DayNumber - x.From.DayNumber).ThenBy(x => x.Event.Title)
            .ToList();

        var lanes = new List<bool[]>();
        var result = new List<SpanPlacement>();
        foreach (var (ev, from, to) in items)
        {
            var first = Math.Max(0, from.DayNumber - firstDay.DayNumber);
            var last = Math.Min(days, to.DayNumber - firstDay.DayNumber); // exclusive
            var lane = lanes.FindIndex(l => Enumerable.Range(first, last - first).All(d => !l[d]));
            if (lane < 0)
            {
                lane = lanes.Count;
                lanes.Add(new bool[days]);
            }
            for (var d = first; d < last; d++) lanes[lane][d] = true;
            result.Add(new SpanPlacement(ev, first, last - first, lane, from < firstDay, to > lastDay));
        }
        return result;
    }

    /// <summary>The days an event covers, as [start, end) dates in local time.</summary>
    public static DateOnly StartDay(CalendarEvent e) => e.IsAllDay ? e.StartDate : DateOnly.FromDateTime(e.Start.LocalDateTime);

    public static DateOnly EndDay(CalendarEvent e)
    {
        if (e.IsAllDay) return e.EndDate > e.StartDate ? e.EndDate : e.StartDate.AddDays(1);
        var end = e.End.LocalDateTime;
        var day = DateOnly.FromDateTime(end);
        return end.TimeOfDay == TimeSpan.Zero && end > e.Start.LocalDateTime ? day : day.AddDays(1);
    }

    /// <summary>The first day of the week containing <paramref name="day"/>.</summary>
    public static DateOnly WeekStart(DateOnly day, DayOfWeek firstDayOfWeek) =>
        day.AddDays(-(((int)day.DayOfWeek - (int)firstDayOfWeek + 7) % 7));

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;
}
