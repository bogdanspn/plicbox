using System.Globalization;
using System.Text.Json;
using Dapper;
using Plicbox.Core;
using Microsoft.Data.Sqlite;

namespace Plicbox.Storage;

public sealed class CalendarRepository(Database db) : ICalendarRepository
{
    private const string CalendarColumns = """
        id, account_id AS AccountId, remote_id AS RemoteId, name, color, is_visible AS IsVisible,
        is_primary AS IsPrimary, access_role AS AccessRole, time_zone AS TimeZone
        """;

    private const string EventColumns = """
        id, calendar_id AS CalendarId, remote_id AS RemoteId, series_id AS SeriesId, title, description, location,
        start_utc AS StartUtc, end_utc AS EndUtc, is_all_day AS IsAllDay, start_date AS StartDate, end_date AS EndDate,
        time_zone AS TimeZone, status, is_busy AS IsBusy, organizer_email AS OrganizerEmail, organizer_name AS OrganizerName,
        attendees, meeting_url AS MeetingUrl, html_link AS HtmlLink, color, reminder_minutes AS ReminderMinutes,
        recurrence, updated_utc AS UpdatedUtc
        """;

    public async Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<CalendarInfo>(new CommandDefinition(
            $"SELECT {CalendarColumns} FROM calendars ORDER BY account_id, is_primary DESC, name COLLATE NOCASE", cancellationToken: ct))).AsList();
    }

    public async Task<long> UpsertCalendarAsync(CalendarInfo c, bool keepVisibility, CancellationToken ct = default)
    {
        using var conn = db.Open();
        // The user's show/hide choice survives refreshes; Google's own "selected" only seeds new calendars.
        var visibility = keepVisibility ? "is_visible = calendars.is_visible" : "is_visible = excluded.is_visible";
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition($"""
            INSERT INTO calendars (account_id, remote_id, name, color, is_visible, is_primary, access_role, time_zone)
            VALUES (@AccountId, @RemoteId, @Name, @Color, @IsVisible, @IsPrimary, @AccessRole, @TimeZone)
            ON CONFLICT (account_id, remote_id) DO UPDATE SET
                name = excluded.name, color = excluded.color, is_primary = excluded.is_primary,
                access_role = excluded.access_role, time_zone = excluded.time_zone, {visibility}
            RETURNING id
            """, c, cancellationToken: ct));
    }

    public async Task SetCalendarVisibleAsync(long calendarId, bool visible, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("UPDATE calendars SET is_visible = @visible WHERE id = @calendarId",
            new { calendarId, visible }, cancellationToken: ct));
    }

    public async Task DeleteMissingCalendarsAsync(long accountId, IReadOnlyCollection<string> keepRemoteIds, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM calendars WHERE account_id = @accountId AND remote_id NOT IN @keepRemoteIds",
            new { accountId, keepRemoteIds }, cancellationToken: ct));
    }

    public async Task UpdateCalendarAsync(long calendarId, string name, string color, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("UPDATE calendars SET name = @name, color = @color WHERE id = @calendarId",
            new { calendarId, name, color }, cancellationToken: ct));
    }

    public async Task DeleteCalendarAsync(long calendarId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM events WHERE calendar_id = @calendarId", new { calendarId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM calendars WHERE id = @calendarId", new { calendarId }, tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task<IReadOnlyList<CalendarEvent>> SearchEventsAsync(string text, int limit, CancellationToken ct = default)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return [];
        // Every word must appear somewhere: "ana lunch" finds "Lunch with Ana".
        string[] columns = ["title", "location", "description", "attendees", "organizer_name", "organizer_email"];
        var where = string.Join(" AND ", words.Select((_, i) =>
            "(" + string.Join(" OR ", columns.Select(c => $"{c} LIKE @w{i} ESCAPE '!'")) + ")"));
        var args = new DynamicParameters();
        for (var i = 0; i < words.Length; i++)
            args.Add($"w{i}", "%" + words[i].Replace("!", "!!").Replace("%", "!%").Replace("_", "!_") + "%");
        args.Add("limit", limit);
        using var conn = db.Open();
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {EventColumns} FROM events WHERE status <> 'cancelled' AND {where} ORDER BY start_utc LIMIT @limit", args, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(IReadOnlyCollection<long> calendarIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (calendarIds.Count == 0) return [];
        using var conn = db.Open();
        // All-day rows store their dates at UTC midnight; a day of slack on each side catches every time zone.
        var rows = await conn.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {EventColumns} FROM events
            WHERE calendar_id IN @calendarIds AND start_utc < @to AND end_utc > @from
            ORDER BY start_utc
            """, new { calendarIds, from = from.AddDays(-1).ToUnixTimeSeconds(), to = to.AddDays(1).ToUnixTimeSeconds() }, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).Where(e => Overlaps(e, from, to)).ToList();
    }

    private static bool Overlaps(CalendarEvent e, DateTimeOffset from, DateTimeOffset to)
    {
        if (!e.IsAllDay) return e.Start < to && e.End > from;
        var fromDate = DateOnly.FromDateTime(from.LocalDateTime);
        var toDate = DateOnly.FromDateTime(to.LocalDateTime.AddTicks(-1));
        return e.StartDate <= toDate && e.EndDate > fromDate;
    }

    public async Task<CalendarEvent?> GetEventAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {EventColumns} FROM events WHERE id = @id", new { id }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task ReplaceEventsAsync(long calendarId, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<CalendarEvent> events, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var keep = events.Select(e => e.RemoteId).ToHashSet();
        var existing = await conn.QueryAsync<(long Id, string RemoteId)>(new CommandDefinition("""
            SELECT id, remote_id FROM events WHERE calendar_id = @calendarId AND start_utc < @to AND end_utc > @from
            """, new { calendarId, from = from.ToUnixTimeSeconds(), to = to.ToUnixTimeSeconds() }, tx, cancellationToken: ct));
        var gone = existing.Where(e => !keep.Contains(e.RemoteId)).Select(e => e.Id).ToList();
        foreach (var chunk in gone.Chunk(500))
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM events WHERE id IN @chunk", new { chunk }, tx, cancellationToken: ct));
        foreach (var e in events) await UpsertCoreAsync(conn, tx, e with { CalendarId = calendarId }, ct);
        tx.Commit();
    }

    public async Task UpsertEventAsync(CalendarEvent ev, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        await UpsertCoreAsync(conn, tx, ev, ct);
        tx.Commit();
    }

    public async Task DeleteEventAsync(long calendarId, string remoteId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM events WHERE calendar_id = @calendarId AND (remote_id = @remoteId OR series_id = @remoteId)",
            new { calendarId, remoteId }, cancellationToken: ct));
    }

    public async Task UpsertEventsAsync(IReadOnlyList<CalendarEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0) return;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var e in events) await UpsertCoreAsync(conn, tx, e, ct);
        tx.Commit();
    }

    // ───────── repeating events of calendars on this computer ─────────

    private static readonly JsonSerializerOptions SeriesJson = new() { IgnoreReadOnlyProperties = true };

    private const string SeriesColumns = "calendar_id AS CalendarId, series_id AS SeriesId, first, skipped, changed";

    public async Task<IReadOnlyList<LocalSeries>> GetLocalSeriesAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<SeriesRow>(new CommandDefinition($"SELECT {SeriesColumns} FROM local_series", cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    public async Task<LocalSeries?> GetLocalSeriesAsync(long calendarId, string seriesId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<SeriesRow>(new CommandDefinition(
            $"SELECT {SeriesColumns} FROM local_series WHERE calendar_id = @calendarId AND series_id = @seriesId",
            new { calendarId, seriesId }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task SaveLocalSeriesAsync(LocalSeries s, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO local_series (calendar_id, series_id, first, skipped, changed) VALUES (@CalendarId, @SeriesId, @First, @Skipped, @Changed)
            ON CONFLICT (calendar_id, series_id) DO UPDATE SET first = excluded.first, skipped = excluded.skipped, changed = excluded.changed
            """, new
        {
            s.CalendarId, s.SeriesId,
            First = JsonSerializer.Serialize(s.First, SeriesJson),
            Skipped = s.Skipped.Count == 0 ? null : JsonSerializer.Serialize(s.Skipped.Order(StringComparer.Ordinal), SeriesJson),
            Changed = s.Changed.Count == 0 ? null : JsonSerializer.Serialize(s.Changed, SeriesJson),
        }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PendingCalendarChange>> GetPendingChangesAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<(long Id, long CalendarId, string Change)>(new CommandDefinition(
            "SELECT id, calendar_id, change FROM calendar_changes ORDER BY id", cancellationToken: ct)))
            .Select(r => new PendingCalendarChange(r.Id, r.CalendarId, r.Change)).ToList();
    }

    public async Task<long> AddPendingChangeAsync(long calendarId, string change, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO calendar_changes (calendar_id, change) VALUES (@calendarId, @change) RETURNING id",
            new { calendarId, change }, cancellationToken: ct));
    }

    public async Task UpdatePendingChangeAsync(long id, string change, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("UPDATE calendar_changes SET change = @change WHERE id = @id", new { id, change }, cancellationToken: ct));
    }

    public async Task RemovePendingChangeAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM calendar_changes WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task DeleteLocalSeriesAsync(long calendarId, string seriesId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM events WHERE calendar_id = @calendarId AND series_id = @seriesId",
            new { calendarId, seriesId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM local_series WHERE calendar_id = @calendarId AND series_id = @seriesId",
            new { calendarId, seriesId }, tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task ReplaceSeriesEventsAsync(long calendarId, string seriesId, IReadOnlyList<CalendarEvent> occurrences, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var keep = occurrences.Select(e => e.RemoteId).ToHashSet();
        var existing = await conn.QueryAsync<(long Id, string RemoteId)>(new CommandDefinition(
            "SELECT id, remote_id FROM events WHERE calendar_id = @calendarId AND series_id = @seriesId",
            new { calendarId, seriesId }, tx, cancellationToken: ct));
        var gone = existing.Where(e => !keep.Contains(e.RemoteId)).Select(e => e.Id).ToList();
        foreach (var chunk in gone.Chunk(500))
            await conn.ExecuteAsync(new CommandDefinition("DELETE FROM events WHERE id IN @chunk", new { chunk }, tx, cancellationToken: ct));
        foreach (var e in occurrences) await UpsertCoreAsync(conn, tx, e with { CalendarId = calendarId, SeriesId = seriesId }, ct);
        tx.Commit();
    }

    private sealed class SeriesRow
    {
        public long CalendarId { get; init; }
        public string SeriesId { get; init; } = "";
        public string First { get; init; } = "";
        public string? Skipped { get; init; }
        public string? Changed { get; init; }

        public LocalSeries ToModel() => new()
        {
            CalendarId = CalendarId, SeriesId = SeriesId,
            First = JsonSerializer.Deserialize<CalendarEvent>(First, SeriesJson)!,
            Skipped = Skipped is null ? new HashSet<string>() : JsonSerializer.Deserialize<HashSet<string>>(Skipped, SeriesJson) ?? [],
            Changed = Changed is null ? new Dictionary<string, CalendarEvent>()
                : JsonSerializer.Deserialize<Dictionary<string, CalendarEvent>>(Changed, SeriesJson) ?? [],
        };
    }

    private static Task UpsertCoreAsync(SqliteConnection conn, SqliteTransaction tx, CalendarEvent e, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO events (calendar_id, remote_id, series_id, title, description, location, start_utc, end_utc, is_all_day,
                                start_date, end_date, time_zone, status, is_busy, organizer_email, organizer_name, attendees,
                                meeting_url, html_link, color, reminder_minutes, recurrence, updated_utc)
            VALUES (@CalendarId, @RemoteId, @SeriesId, @Title, @Description, @Location, @StartUtc, @EndUtc, @IsAllDay,
                    @StartDate, @EndDate, @TimeZone, @Status, @IsBusy, @OrganizerEmail, @OrganizerName, @Attendees,
                    @MeetingUrl, @HtmlLink, @Color, @ReminderMinutes, @Recurrence, @UpdatedUtc)
            ON CONFLICT (calendar_id, remote_id) DO UPDATE SET
                series_id = excluded.series_id, title = excluded.title, description = excluded.description,
                location = excluded.location, start_utc = excluded.start_utc, end_utc = excluded.end_utc,
                is_all_day = excluded.is_all_day, start_date = excluded.start_date, end_date = excluded.end_date,
                time_zone = excluded.time_zone, status = excluded.status, is_busy = excluded.is_busy,
                organizer_email = excluded.organizer_email, organizer_name = excluded.organizer_name,
                attendees = excluded.attendees, meeting_url = excluded.meeting_url, html_link = excluded.html_link,
                color = excluded.color, reminder_minutes = excluded.reminder_minutes, recurrence = excluded.recurrence,
                updated_utc = excluded.updated_utc
            """, Row.From(e), tx, cancellationToken: ct));

    private sealed class Row
    {
        public long Id { get; init; }
        public long CalendarId { get; init; }
        public string RemoteId { get; init; } = "";
        public string? SeriesId { get; init; }
        public string Title { get; init; } = "";
        public string? Description { get; init; }
        public string? Location { get; init; }
        public long StartUtc { get; init; }
        public long EndUtc { get; init; }
        public bool IsAllDay { get; init; }
        public string? StartDate { get; init; }
        public string? EndDate { get; init; }
        public string? TimeZone { get; init; }
        public string Status { get; init; } = "confirmed";
        public bool IsBusy { get; init; }
        public string? OrganizerEmail { get; init; }
        public string? OrganizerName { get; init; }
        public string? Attendees { get; init; }
        public string? MeetingUrl { get; init; }
        public string? HtmlLink { get; init; }
        public string? Color { get; init; }
        public int? ReminderMinutes { get; init; }
        public string? Recurrence { get; init; }
        public long UpdatedUtc { get; init; }

        private static DateOnly ParseDate(string? s) =>
            s is null ? default : DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        public CalendarEvent ToModel() => new()
        {
            Id = Id, CalendarId = CalendarId, RemoteId = RemoteId, SeriesId = SeriesId, Title = Title, Description = Description,
            Location = Location, Start = DateTimeOffset.FromUnixTimeSeconds(StartUtc), End = DateTimeOffset.FromUnixTimeSeconds(EndUtc),
            IsAllDay = IsAllDay, StartDate = ParseDate(StartDate), EndDate = ParseDate(EndDate), TimeZone = TimeZone, Status = Status,
            IsBusy = IsBusy, OrganizerEmail = OrganizerEmail, OrganizerName = OrganizerName,
            Attendees = string.IsNullOrEmpty(Attendees) ? [] : JsonSerializer.Deserialize<List<EventAttendee>>(Attendees) ?? [],
            MeetingUrl = MeetingUrl, HtmlLink = HtmlLink, Color = Color, ReminderMinutes = ReminderMinutes, Recurrence = Recurrence,
            Updated = DateTimeOffset.FromUnixTimeSeconds(UpdatedUtc),
        };

        public static object From(CalendarEvent e)
        {
            // All-day events are stored with their dates at UTC midnight, so range queries work for them too.
            var start = e.IsAllDay ? new DateTimeOffset(e.StartDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : e.Start;
            var end = e.IsAllDay ? new DateTimeOffset(e.EndDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : e.End;
            return new
            {
                e.CalendarId, e.RemoteId, e.SeriesId, e.Title, e.Description, e.Location,
                StartUtc = start.ToUnixTimeSeconds(), EndUtc = end.ToUnixTimeSeconds(), e.IsAllDay,
                StartDate = e.IsAllDay ? e.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                EndDate = e.IsAllDay ? e.EndDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                e.TimeZone, e.Status, e.IsBusy, e.OrganizerEmail, e.OrganizerName,
                Attendees = e.Attendees.Count == 0 ? null : JsonSerializer.Serialize(e.Attendees),
                e.MeetingUrl, e.HtmlLink, e.Color, e.ReminderMinutes, e.Recurrence, UpdatedUtc = e.Updated.ToUnixTimeSeconds(),
            };
        }
    }
}
