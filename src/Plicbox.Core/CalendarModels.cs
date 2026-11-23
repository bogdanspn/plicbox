namespace Plicbox.Core;

/// <summary>
/// One calendar of an account (Google calendar list entry: own calendars, shared ones, subscribed holidays), or one
/// kept only on this computer (no account).
/// </summary>
public sealed record CalendarInfo
{
    public long Id { get; init; }
    /// <summary>The account it syncs with, or null for a calendar kept only on this computer.</summary>
    public long? AccountId { get; init; }
    public required string RemoteId { get; init; }
    public required string Name { get; init; }
    /// <summary>"#RRGGBB".</summary>
    public string Color { get; init; } = "#4285F4";
    public bool IsVisible { get; init; } = true;
    public bool IsPrimary { get; init; }
    /// <summary>owner, writer, reader or freeBusyReader.</summary>
    public string AccessRole { get; init; } = "reader";
    public string? TimeZone { get; init; }

    public bool CanEdit => AccessRole is "owner" or "writer";
    public bool IsLocal => AccountId is null;
}

/// <summary>
/// A repeating event in a calendar on this computer. There's no server to expand it, so the series is kept whole and
/// its occurrences are written out as ordinary event rows for the dates in view. Occurrences are known by their
/// original start (<see cref="Key"/>): one taken out is in <see cref="Skipped"/>, one changed on its own in <see cref="Changed"/>.
/// </summary>
public sealed record LocalSeries
{
    public long CalendarId { get; init; }
    public required string SeriesId { get; init; }
    /// <summary>The first occurrence, with the repeat rule in <see cref="CalendarEvent.Recurrence"/>.</summary>
    public required CalendarEvent First { get; init; }
    public IReadOnlySet<string> Skipped { get; init; } = new HashSet<string>();
    public IReadOnlyDictionary<string, CalendarEvent> Changed { get; init; } = new Dictionary<string, CalendarEvent>();

    /// <summary>An occurrence's key: its original start, "20261116" (all day) or "20261116T090000" (local time).</summary>
    public static string Key(bool allDay, DateTime localStart) =>
        localStart.ToString(allDay ? "yyyyMMdd" : "yyyyMMdd'T'HHmmss", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The event id of an occurrence: the series id and its key.</summary>
    public string OccurrenceId(string key) => $"{SeriesId}_{key}";

    /// <summary>The key of one of this series' occurrences, from its event id; null when it isn't one.</summary>
    public string? KeyOf(string remoteId) =>
        remoteId.StartsWith(SeriesId + "_", StringComparison.Ordinal) ? remoteId[(SeriesId.Length + 1)..] : null;
}

/// <summary>Someone invited to an event.</summary>
public sealed record EventAttendee(string Email, string? Name, string Response, bool IsSelf = false, bool IsOrganizer = false, bool Optional = false);

/// <summary>
/// One occurrence of an event as it appears on the calendar. Repeating events are stored per occurrence
/// (the server expands them), each pointing at its series through <see cref="SeriesId"/>.
/// </summary>
public sealed record CalendarEvent
{
    public long Id { get; init; }
    public long CalendarId { get; init; }
    /// <summary>The server's id for this occurrence (for a repeating event: the instance id).</summary>
    public required string RemoteId { get; init; }
    /// <summary>The repeating series this occurrence belongs to, or null for a one-off event.</summary>
    public string? SeriesId { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Location { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    /// <summary>All-day events cover whole dates: <see cref="StartDate"/> up to (excluding) <see cref="EndDate"/>.</summary>
    public bool IsAllDay { get; init; }
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    public string? TimeZone { get; init; }
    /// <summary>confirmed or tentative (cancelled ones are removed).</summary>
    public string Status { get; init; } = "confirmed";
    /// <summary>False = "show as free".</summary>
    public bool IsBusy { get; init; } = true;
    public string? OrganizerEmail { get; init; }
    public string? OrganizerName { get; init; }
    public IReadOnlyList<EventAttendee> Attendees { get; init; } = [];
    /// <summary>Video call link (Google Meet, or a Zoom/Teams link found in the event).</summary>
    public string? MeetingUrl { get; init; }
    public string? HtmlLink { get; init; }
    /// <summary>Event's own colour "#RRGGBB", or null to use the calendar's.</summary>
    public string? Color { get; init; }
    /// <summary>Minutes before start for a popup reminder; null = none.</summary>
    public int? ReminderMinutes { get; init; }
    /// <summary>The series' repeat rule (RRULE) when known, for the editor.</summary>
    public string? Recurrence { get; init; }
    public DateTimeOffset Updated { get; init; }

    public bool IsRecurring => SeriesId is not null || Recurrence is not null;

    /// <summary>My answer to the invitation (accepted, declined, tentative, needsAction), or null when I organise it.</summary>
    public string? MyResponse => Attendees.FirstOrDefault(a => a.IsSelf) is { IsOrganizer: false } me ? me.Response : null;
}

public interface ICalendarRepository
{
    Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken ct = default);
    Task<long> UpsertCalendarAsync(CalendarInfo calendar, bool keepVisibility, CancellationToken ct = default);
    Task SetCalendarVisibleAsync(long calendarId, bool visible, CancellationToken ct = default);
    Task DeleteMissingCalendarsAsync(long accountId, IReadOnlyCollection<string> keepRemoteIds, CancellationToken ct = default);
    Task UpdateCalendarAsync(long calendarId, string name, string color, CancellationToken ct = default);
    /// <summary>Removes a calendar and its events.</summary>
    Task DeleteCalendarAsync(long calendarId, CancellationToken ct = default);

    /// <summary>Events overlapping [from, to) in the given calendars.</summary>
    Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(IReadOnlyCollection<long> calendarIds, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
    Task<CalendarEvent?> GetEventAsync(long id, CancellationToken ct = default);
    /// <summary>Replaces a calendar's events in [from, to) with <paramref name="events"/> (a full refresh of that window).</summary>
    Task ReplaceEventsAsync(long calendarId, DateTimeOffset from, DateTimeOffset to, IReadOnlyList<CalendarEvent> events, CancellationToken ct = default);
    Task UpsertEventAsync(CalendarEvent ev, CancellationToken ct = default);
    Task DeleteEventAsync(long calendarId, string remoteId, CancellationToken ct = default);
    /// <summary>Events whose title, place, notes or people contain every word of <paramref name="text"/>, by start time.</summary>
    Task<IReadOnlyList<CalendarEvent>> SearchEventsAsync(string text, int limit, CancellationToken ct = default);

    // Repeating events of calendars on this computer.
    Task<IReadOnlyList<LocalSeries>> GetLocalSeriesAsync(CancellationToken ct = default);
    Task<LocalSeries?> GetLocalSeriesAsync(long calendarId, string seriesId, CancellationToken ct = default);
    Task SaveLocalSeriesAsync(LocalSeries series, CancellationToken ct = default);
    /// <summary>Removes the series and every occurrence of it.</summary>
    Task DeleteLocalSeriesAsync(long calendarId, string seriesId, CancellationToken ct = default);
    /// <summary>Makes the series' occurrence rows exactly <paramref name="occurrences"/> (rows kept where they stay).</summary>
    Task ReplaceSeriesEventsAsync(long calendarId, string seriesId, IReadOnlyList<CalendarEvent> occurrences, CancellationToken ct = default);
    /// <summary>Adds or updates these occurrences, leaving the series' other rows alone.</summary>
    Task UpsertEventsAsync(IReadOnlyList<CalendarEvent> events, CancellationToken ct = default);

    // Changes to an account's calendar made while Google couldn't be reached, oldest first (JSON the engine reads).
    Task<IReadOnlyList<PendingCalendarChange>> GetPendingChangesAsync(CancellationToken ct = default);
    Task<long> AddPendingChangeAsync(long calendarId, string change, CancellationToken ct = default);
    Task UpdatePendingChangeAsync(long id, string change, CancellationToken ct = default);
    Task RemovePendingChangeAsync(long id, CancellationToken ct = default);
}

/// <summary>A change to a Google calendar waiting to be sent (it's already shown here).</summary>
public sealed record PendingCalendarChange(long Id, long CalendarId, string Change);
