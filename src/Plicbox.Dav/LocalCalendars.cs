using Plicbox.Core;

namespace Plicbox.Dav;

/// <summary>
/// Events of calendars kept only on this computer. One-off events are plain rows. A repeating event is kept whole
/// (<see cref="LocalSeries"/>) and its occurrences are written out as rows for the synced window, the same shape Google
/// gives its own, so the views, search and reminders treat both alike. Dates further out are added when viewed.
/// </summary>
public sealed class LocalCalendars(ICalendarRepository repo)
{
    private readonly HashSet<(DateTimeOffset, DateTimeOffset)> _expanded = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    public static string NewId() => $"local-{Guid.NewGuid():N}";

    public async Task CreateAsync(CalendarEvent ev, CancellationToken ct = default)
    {
        if (ev.Recurrence is null)
        {
            await repo.UpsertEventAsync(ev with { SeriesId = null }, ct);
            return;
        }
        var series = new LocalSeries { CalendarId = ev.CalendarId, SeriesId = ev.RemoteId, First = ev with { Id = 0, SeriesId = ev.RemoteId } };
        await repo.SaveLocalSeriesAsync(series, ct);
        await WriteAsync(series, ct);
    }

    /// <summary>
    /// <paramref name="ev"/> as it now should be (<paramref name="edited"/>). For an occurrence of a repeating event:
    /// only that one, or (<paramref name="wholeSeries"/>) every one, shifted the way this one was.
    /// </summary>
    public async Task UpdateAsync(CalendarEvent ev, CalendarEvent edited, bool wholeSeries, CancellationToken ct = default)
    {
        edited = edited with { Id = 0, CalendarId = ev.CalendarId };
        var series = ev.SeriesId is { } sid ? await repo.GetLocalSeriesAsync(ev.CalendarId, sid, ct) : null;
        if (series is null)
        {
            if (edited.Recurrence is null)
            {
                await repo.UpsertEventAsync(edited with { RemoteId = ev.RemoteId, SeriesId = null }, ct);
                return;
            }
            // Repeats from now on: the event becomes the first of a series.
            await repo.DeleteEventAsync(ev.CalendarId, ev.RemoteId, ct);
            await CreateAsync(edited with { RemoteId = ev.RemoteId }, ct);
            return;
        }

        var rule = series.First.Recurrence;
        if (!wholeSeries && series.KeyOf(ev.RemoteId) is { } key)
        {
            var changed = new Dictionary<string, CalendarEvent>(series.Changed) { [key] = edited with { RemoteId = ev.RemoteId, SeriesId = series.SeriesId, Recurrence = rule } };
            await repo.SaveLocalSeriesAsync(series with { Changed = changed }, ct);
            await repo.UpsertEventsAsync([changed[key]], ct);
            return;
        }

        if (edited.Recurrence is null)
        {
            // No longer repeats: just this one, where it now is.
            await repo.DeleteLocalSeriesAsync(series.CalendarId, series.SeriesId, ct);
            await repo.UpsertEventAsync(edited with { RemoteId = NewId(), SeriesId = null }, ct);
            return;
        }
        var first = Shift(series.First, ev, edited) with { RemoteId = series.SeriesId, SeriesId = series.SeriesId };
        // Occurrences are known by their original start: when the times or the rule change, the old ones don't line up any more.
        var was = series.First;
        var sameDates = first.Recurrence == rule && first.IsAllDay == was.IsAllDay
                        && (first.IsAllDay ? (first.StartDate, first.EndDate) == (was.StartDate, was.EndDate) : (first.Start, first.End) == (was.Start, was.End));
        var updated = sameDates ? series with { First = first }
            : series with { First = first, Skipped = new HashSet<string>(), Changed = new Dictionary<string, CalendarEvent>() };
        await repo.SaveLocalSeriesAsync(updated, ct);
        await WriteAsync(updated, ct);
    }

    public async Task DeleteAsync(CalendarEvent ev, bool wholeSeries, CancellationToken ct = default)
    {
        var series = ev.SeriesId is { } sid ? await repo.GetLocalSeriesAsync(ev.CalendarId, sid, ct) : null;
        if (series is null)
        {
            await repo.DeleteEventAsync(ev.CalendarId, ev.RemoteId, ct);
            return;
        }
        if (wholeSeries || series.KeyOf(ev.RemoteId) is not { } key)
        {
            await repo.DeleteLocalSeriesAsync(series.CalendarId, series.SeriesId, ct);
            return;
        }
        var changed = new Dictionary<string, CalendarEvent>(series.Changed);
        changed.Remove(key);
        await repo.SaveLocalSeriesAsync(series with { Skipped = new HashSet<string>(series.Skipped) { key }, Changed = changed }, ct);
        await repo.DeleteEventAsync(ev.CalendarId, ev.RemoteId, ct);
    }

    /// <summary>A whole series from a file, with its occurrences taken out or changed (replacing one of the same id).</summary>
    public async Task ImportSeriesAsync(LocalSeries series, CancellationToken ct = default)
    {
        await repo.SaveLocalSeriesAsync(series, ct);
        await WriteAsync(series, ct);
    }

    /// <summary>Writes every series out again for the synced window (it moves on day by day).</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        foreach (var series in await repo.GetLocalSeriesAsync(ct)) await WriteAsync(series, ct);
    }

    /// <summary>Viewing dates outside the synced window: adds the occurrences there. True when it added any.</summary>
    public async Task<bool> EnsureRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!_expanded.Add((from, to))) return false;
        }
        finally
        {
            _lock.Release();
        }
        var added = new List<CalendarEvent>();
        foreach (var series in await repo.GetLocalSeriesAsync(ct)) added.AddRange(Expand(series, from, to));
        await repo.UpsertEventsAsync(added, ct);
        return added.Count > 0;
    }

    private async Task WriteAsync(LocalSeries series, CancellationToken ct)
    {
        var (from, to) = CalendarEngine.Window;
        await repo.ReplaceSeriesEventsAsync(series.CalendarId, series.SeriesId, Expand(series, from, to).ToList(), ct);
        // Rows beyond the window went with it; they come back as those dates are viewed.
        await _lock.WaitAsync(ct);
        try { _expanded.Clear(); }
        finally { _lock.Release(); }
    }

    /// <summary>The series' occurrences overlapping [from, to), with the ones changed on their own as changed.</summary>
    public static IEnumerable<CalendarEvent> Expand(LocalSeries series, DateTimeOffset from, DateTimeOffset to)
    {
        var f = series.First;
        if (f.Recurrence is not { } rule) yield break;
        var firstStart = f.IsAllDay ? f.StartDate.ToDateTime(TimeOnly.MinValue) : f.Start.LocalDateTime;
        var days = f.EndDate.DayNumber - f.StartDate.DayNumber;
        var length = f.End.LocalDateTime - f.Start.LocalDateTime;
        // Starting early enough to catch one that began before the range and is still going.
        var span = f.IsAllDay ? TimeSpan.FromDays(Math.Max(1, days)) : length;
        foreach (var start in Recurrence.Occurrences(rule, firstStart, from.LocalDateTime - span - TimeSpan.FromDays(1), to.LocalDateTime))
        {
            var key = LocalSeries.Key(f.IsAllDay, start);
            if (series.Skipped.Contains(key)) continue;
            var id = series.OccurrenceId(key);
            CalendarEvent occurrence;
            if (series.Changed.TryGetValue(key, out var changed))
            {
                occurrence = changed with { Id = 0, CalendarId = series.CalendarId, RemoteId = id, SeriesId = series.SeriesId, Recurrence = rule };
            }
            else if (f.IsAllDay)
            {
                var day = DateOnly.FromDateTime(start);
                occurrence = f with
                {
                    Id = 0, CalendarId = series.CalendarId, RemoteId = id, SeriesId = series.SeriesId, StartDate = day, EndDate = day.AddDays(days),
                    Start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue)), End = new DateTimeOffset(day.AddDays(days).ToDateTime(TimeOnly.MinValue)),
                };
            }
            else
            {
                occurrence = f with
                {
                    Id = 0, CalendarId = series.CalendarId, RemoteId = id, SeriesId = series.SeriesId,
                    Start = new DateTimeOffset(start), End = new DateTimeOffset(start + length),
                };
            }
            if (Overlaps(occurrence, from, to)) yield return occurrence;
        }
    }

    private static bool Overlaps(CalendarEvent e, DateTimeOffset from, DateTimeOffset to) => e.IsAllDay
        ? e.StartDate < DateOnly.FromDateTime(to.LocalDateTime.AddTicks(-1)).AddDays(1) && e.EndDate > DateOnly.FromDateTime(from.LocalDateTime)
        : e.Start < to && e.End > from;

    /// <summary>The series' first event, moved the way one of its occurrences was (<paramref name="before"/> -> <paramref name="after"/>).</summary>
    private static CalendarEvent Shift(CalendarEvent first, CalendarEvent before, CalendarEvent after)
    {
        static DateOnly DayOf(CalendarEvent e) => e.IsAllDay ? e.StartDate : DateOnly.FromDateTime(e.Start.LocalDateTime);
        var day = DayOf(first).AddDays(DayOf(after).DayNumber - DayOf(before).DayNumber);
        if (after.IsAllDay)
        {
            var end = day.AddDays(Math.Max(1, after.EndDate.DayNumber - after.StartDate.DayNumber));
            return after with
            {
                StartDate = day, EndDate = end,
                Start = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue)), End = new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue)),
            };
        }
        var start = day.ToDateTime(TimeOnly.FromDateTime(after.Start.LocalDateTime));
        return after with
        {
            Start = new DateTimeOffset(start), End = new DateTimeOffset(start + (after.End.LocalDateTime - after.Start.LocalDateTime)),
            StartDate = default, EndDate = default,
        };
    }
}
