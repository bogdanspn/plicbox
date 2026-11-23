using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Plicbox.Auth;
using Plicbox.Core;

namespace Plicbox.Dav;

/// <summary>The account's token lacks calendar access (granted before the calendar existed): sign in again once.</summary>
public sealed class CalendarPermissionException(string message) : Exception(message);

/// <summary>The Google Calendar API isn't switched on in the user's own Cloud project.</summary>
public sealed class CalendarApiDisabledException(string message) : Exception(message);

/// <summary>A calendar as listed by the server, plus its default reminder (applied to events that "use default").</summary>
public sealed record RemoteCalendar(CalendarInfo Calendar, int? DefaultReminderMinutes);

/// <summary>What the event editor sends: everything a new or changed event can have.</summary>
public sealed record EventDraft
{
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public string? Location { get; init; }
    public bool IsAllDay { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    /// <summary>For all-day events: first day and the day after the last.</summary>
    public DateOnly StartDate { get; init; }
    public DateOnly EndDate { get; init; }
    /// <summary>IANA time zone ("Europe/Berlin"); required by Google for repeating events.</summary>
    public string TimeZone { get; init; } = Calendars.LocalIanaZone;
    /// <summary>"FREQ=WEEKLY;BYDAY=MO" etc., or null for a one-off event.</summary>
    public string? Recurrence { get; init; }
    public IReadOnlyList<string> Guests { get; init; } = [];
    public bool AddVideoCall { get; init; }
    /// <summary>Popup reminder minutes; null = none.</summary>
    public int? ReminderMinutes { get; init; }
    public bool IsBusy { get; init; } = true;
}

/// <summary>Google Calendar REST API v3 (the same service eM Client uses for Google accounts).</summary>
public sealed partial class GoogleCalendarApi(HttpClient http, GoogleAuth google)
{
    private const string Base = "https://www.googleapis.com/calendar/v3";

    // Google Calendar's current event colours (colorId 1-11), as shown in its web UI.
    private static readonly Dictionary<string, string> EventColors = new()
    {
        ["1"] = "#7986CB", ["2"] = "#33B679", ["3"] = "#8E24AA", ["4"] = "#E67C73", ["5"] = "#F6BF26", ["6"] = "#F4511E",
        ["7"] = "#039BE5", ["8"] = "#616161", ["9"] = "#3F51B5", ["10"] = "#0B8043", ["11"] = "#D50000",
    };

    public async Task<IReadOnlyList<RemoteCalendar>> ListCalendarsAsync(Account account, CancellationToken ct)
    {
        var result = new List<RemoteCalendar>();
        string? page = null;
        do
        {
            var url = $"{Base}/users/me/calendarList?maxResults=250" + (page is null ? "" : $"&pageToken={Uri.EscapeDataString(page)}");
            using var doc = await SendAsync(HttpMethod.Get, url, account, null, ct);
            foreach (var item in Items(doc))
            {
                var defaultReminder = item.TryGetProperty("defaultReminders", out var dr)
                    ? dr.EnumerateArray().Where(r => Str(r, "method") == "popup").Select(r => (int?)r.GetProperty("minutes").GetInt32()).Min()
                    : null;
                result.Add(new RemoteCalendar(new CalendarInfo
                {
                    AccountId = account.Id,
                    RemoteId = Str(item, "id")!,
                    Name = Str(item, "summaryOverride") ?? Str(item, "summary") ?? L.T("Calendar"),
                    Color = Str(item, "backgroundColor") ?? "#4285F4",
                    IsVisible = Bool(item, "selected") && !Bool(item, "hidden"),
                    IsPrimary = Bool(item, "primary"),
                    AccessRole = Str(item, "accessRole") ?? "reader",
                    TimeZone = Str(item, "timeZone"),
                }, defaultReminder));
            }
            page = Str(doc.RootElement, "nextPageToken");
        } while (page is not null);
        return result;
    }

    /// <summary>Every occurrence in [from, to), repeating events expanded by Google (so times match Google Calendar exactly).</summary>
    public async Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(Account account, RemoteCalendar calendar,
        DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var result = new List<CalendarEvent>();
        string? page = null;
        do
        {
            var url = $"{Base}/calendars/{Uri.EscapeDataString(calendar.Calendar.RemoteId)}/events?singleEvents=true&maxResults=2500"
                      + $"&timeMin={Uri.EscapeDataString(Rfc3339(from))}&timeMax={Uri.EscapeDataString(Rfc3339(to))}"
                      + (page is null ? "" : $"&pageToken={Uri.EscapeDataString(page)}");
            using var doc = await SendAsync(HttpMethod.Get, url, account, null, ct);
            foreach (var item in Items(doc))
                if (Parse(item, calendar.DefaultReminderMinutes) is { Status: not "cancelled" } ev) result.Add(ev);
            page = Str(doc.RootElement, "nextPageToken");
        } while (page is not null);
        return result;
    }

    /// <summary>The repeat rule of a series (instances don't carry it).</summary>
    public async Task<string?> GetRecurrenceAsync(Account account, string calendarId, string seriesId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, EventUrl(calendarId, seriesId), account, null, ct);
        return doc.RootElement.TryGetProperty("recurrence", out var rec)
            ? rec.EnumerateArray().Select(r => r.GetString()).FirstOrDefault(r => r?.StartsWith("RRULE:", StringComparison.Ordinal) == true)?[6..]
            : null;
    }

    /// <summary>Start and end of a series' first occurrence (to shift a whole series when one occurrence is dragged).</summary>
    public async Task<(DateTimeOffset Start, DateTimeOffset End, string? Zone)> GetSeriesTimesAsync(Account account, string calendarId, string seriesId, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, EventUrl(calendarId, seriesId), account, null, ct);
        var ev = Parse(doc.RootElement, null)!;
        return (ev.Start, ev.End, ev.TimeZone);
    }

    public async Task CreateAsync(Account account, string calendarId, EventDraft draft, CancellationToken ct)
    {
        var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events?sendUpdates=all&conferenceDataVersion=1";
        using var _ = await SendAsync(HttpMethod.Post, url, account, ToJson(draft, forInstance: false, forCreate: true), ct);
    }

    /// <param name="isInstance">Changing one occurrence of a series: Google refuses a repeat rule on those.</param>
    public async Task UpdateAsync(Account account, string calendarId, string eventId, EventDraft draft, bool isInstance, CancellationToken ct)
    {
        var url = EventUrl(calendarId, eventId) + "?sendUpdates=all&conferenceDataVersion=1";
        using var _ = await SendAsync(HttpMethod.Patch, url, account, ToJson(draft, isInstance, forCreate: false), ct);
    }

    /// <summary>Changes only the times (dragging an event in the grid).</summary>
    public async Task MoveAsync(Account account, string calendarId, string eventId, EventDraft times, CancellationToken ct)
    {
        var body = new JsonObject { ["start"] = When(times, start: true, clearOther: true), ["end"] = When(times, start: false, clearOther: true) };
        using var _ = await SendAsync(HttpMethod.Patch, EventUrl(calendarId, eventId) + "?sendUpdates=all", account, body, ct);
    }

    public async Task DeleteAsync(Account account, string calendarId, string eventId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, EventUrl(calendarId, eventId) + "?sendUpdates=all", account, null, ct);
    }

    /// <summary>Google's natural-language quick add: "Lunch with Ana Friday 1pm".</summary>
    public async Task QuickAddAsync(Account account, string calendarId, string text, CancellationToken ct)
    {
        var url = $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events/quickAdd?text={Uri.EscapeDataString(text)}";
        using var _ = await SendAsync(HttpMethod.Post, url, account, null, ct);
    }

    /// <summary>Answers an invitation (accepted, declined, tentative) for this occurrence.</summary>
    public async Task RespondAsync(Account account, string calendarId, CalendarEvent ev, string response, CancellationToken ct)
    {
        var attendees = new JsonArray(ev.Attendees.Select(a => (JsonNode)new JsonObject
        {
            ["email"] = a.Email,
            ["responseStatus"] = a.IsSelf ? response : a.Response,
        }).ToArray());
        using var _ = await SendAsync(HttpMethod.Patch, EventUrl(calendarId, ev.RemoteId) + "?sendUpdates=all", account,
            new JsonObject { ["attendees"] = attendees }, ct);
    }

    /// <summary>Google's own full-text search over one calendar, for dates outside the synced window.</summary>
    public async Task<IReadOnlyList<CalendarEvent>> SearchAsync(Account account, RemoteCalendar calendar, string text,
        DateTimeOffset from, DateTimeOffset to, int max, CancellationToken ct)
    {
        var url = $"{Base}/calendars/{Uri.EscapeDataString(calendar.Calendar.RemoteId)}/events?singleEvents=true&orderBy=startTime"
                  + $"&q={Uri.EscapeDataString(text)}&maxResults={max}"
                  + $"&timeMin={Uri.EscapeDataString(Rfc3339(from))}&timeMax={Uri.EscapeDataString(Rfc3339(to))}";
        using var doc = await SendAsync(HttpMethod.Get, url, account, null, ct);
        return Items(doc).Select(i => Parse(i, calendar.DefaultReminderMinutes))
            .Where(e => e is { Status: not "cancelled" })
            .Select(e => e! with { CalendarId = calendar.Calendar.Id })
            .ToList();
    }

    // ───────── calendars ─────────

    /// <summary>A new calendar owned by the account; returns its id.</summary>
    public async Task<string> CreateCalendarAsync(Account account, string name, string timeZone, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Post, $"{Base}/calendars", account,
            new JsonObject { ["summary"] = name, ["timeZone"] = timeZone }, ct);
        return Str(doc.RootElement, "id") ?? throw new InvalidOperationException(L.T("Google didn't return the new calendar."));
    }

    /// <summary>
    /// Renames for everyone when the account owns the calendar; otherwise only the account's own label for it
    /// (Google's "summaryOverride"), which is what renaming a subscribed calendar means.
    /// </summary>
    public async Task RenameCalendarAsync(Account account, string calendarId, string name, bool owner, CancellationToken ct)
    {
        using var _ = owner
            ? await SendAsync(HttpMethod.Patch, $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}", account, new JsonObject { ["summary"] = name }, ct)
            : await SendAsync(HttpMethod.Patch, CalendarListUrl(calendarId), account, new JsonObject { ["summaryOverride"] = name }, ct);
    }

    /// <summary>The calendar's colour in this account's list (any colour; Google keeps it per user).</summary>
    public async Task SetCalendarColorAsync(Account account, string calendarId, string color, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Patch, CalendarListUrl(calendarId) + "?colorRgbFormat=true", account,
            new JsonObject { ["backgroundColor"] = color, ["foregroundColor"] = "#ffffff" }, ct);
    }

    /// <summary>Deletes a calendar the account owns (with all its events).</summary>
    public async Task DeleteCalendarAsync(Account account, string calendarId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}", account, null, ct);
    }

    /// <summary>Takes a calendar someone else owns (or a holiday calendar) off this account's list.</summary>
    public async Task UnsubscribeAsync(Account account, string calendarId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Delete, CalendarListUrl(calendarId), account, null, ct);
    }

    /// <summary>Adds a public calendar (such as Google's holiday calendars) to the account's list.</summary>
    public async Task SubscribeAsync(Account account, string calendarId, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"{Base}/users/me/calendarList", account, new JsonObject { ["id"] = calendarId }, ct);
    }

    private static string CalendarListUrl(string calendarId) => $"{Base}/users/me/calendarList/{Uri.EscapeDataString(calendarId)}";

    // ───────── JSON ↔ model ─────────

    internal static CalendarEvent? Parse(JsonElement e, int? defaultReminder)
    {
        if (Str(e, "id") is not { } id) return null;
        var status = Str(e, "status") ?? "confirmed";
        if (!e.TryGetProperty("start", out var start) || !e.TryGetProperty("end", out var end))
            return status == "cancelled" ? new CalendarEvent { RemoteId = id, Status = status } : null;

        var allDay = start.TryGetProperty("date", out var sd);
        var ev = new CalendarEvent
        {
            RemoteId = id,
            SeriesId = Str(e, "recurringEventId"),
            Title = Str(e, "summary") is { Length: > 0 } t ? t : L.T("(No title)"),
            Description = Str(e, "description"),
            Location = Str(e, "location"),
            IsAllDay = allDay,
            TimeZone = Str(start, "timeZone"),
            Status = status,
            IsBusy = Str(e, "transparency") != "transparent",
            OrganizerEmail = e.TryGetProperty("organizer", out var org) ? Str(org, "email") : null,
            OrganizerName = e.TryGetProperty("organizer", out var org2) ? Str(org2, "displayName") : null,
            Attendees = e.TryGetProperty("attendees", out var att)
                ? att.EnumerateArray().Select(a => new EventAttendee(Str(a, "email") ?? "", Str(a, "displayName"),
                    Str(a, "responseStatus") ?? "needsAction", Bool(a, "self"), Bool(a, "organizer"), Bool(a, "optional"))).ToList()
                : [],
            HtmlLink = Str(e, "htmlLink"),
            Color = Str(e, "colorId") is { } c && EventColors.TryGetValue(c, out var hex) ? hex : null,
            Recurrence = e.TryGetProperty("recurrence", out var rec)
                ? rec.EnumerateArray().Select(r => r.GetString()).FirstOrDefault(r => r?.StartsWith("RRULE:", StringComparison.Ordinal) == true)?[6..]
                : null,
            Updated = Str(e, "updated") is { } u ? DateTimeOffset.Parse(u, CultureInfo.InvariantCulture) : DateTimeOffset.UtcNow,
        };

        if (allDay)
        {
            var from = DateOnly.ParseExact(sd.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            var to = DateOnly.ParseExact(end.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            ev = ev with
            {
                StartDate = from, EndDate = to,
                Start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue)), End = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue)),
            };
        }
        else
        {
            ev = ev with
            {
                Start = DateTimeOffset.Parse(start.GetProperty("dateTime").GetString()!, CultureInfo.InvariantCulture),
                End = DateTimeOffset.Parse(end.GetProperty("dateTime").GetString()!, CultureInfo.InvariantCulture),
            };
        }

        int? reminder = null;
        if (e.TryGetProperty("reminders", out var rem))
        {
            reminder = Bool(rem, "useDefault") ? defaultReminder
                : rem.TryGetProperty("overrides", out var ov)
                    ? ov.EnumerateArray().Where(r => Str(r, "method") == "popup").Select(r => (int?)r.GetProperty("minutes").GetInt32()).Min()
                    : null;
        }

        return ev with { ReminderMinutes = reminder, MeetingUrl = MeetingLink(e, ev) };
    }

    /// <summary>The video call: Google Meet, a conference entry point, or a Zoom/Teams/Webex link in location or notes.</summary>
    private static string? MeetingLink(JsonElement e, CalendarEvent ev)
    {
        if (Str(e, "hangoutLink") is { } meet) return meet;
        if (e.TryGetProperty("conferenceData", out var conf) && conf.TryGetProperty("entryPoints", out var eps))
            foreach (var ep in eps.EnumerateArray())
                if (Str(ep, "entryPointType") == "video" && Str(ep, "uri") is { } uri) return uri;
        var m = MeetingUrl().Match($"{ev.Location} {ev.Description}");
        return m.Success ? m.Value : null;
    }

    [GeneratedRegex(@"https://[\w.-]*(zoom\.us|teams\.microsoft\.com|teams\.live\.com|webex\.com|meet\.google\.com|whereby\.com|meet\.jit\.si)/[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex MeetingUrl();

    internal static JsonObject ToJson(EventDraft d, bool forInstance, bool forCreate)
    {
        var body = new JsonObject
        {
            ["summary"] = d.Title,
            ["description"] = d.Description ?? "",
            ["location"] = d.Location ?? "",
            // A change between all-day and timed must clear the other field; a new event simply leaves it out.
            ["start"] = When(d, start: true, clearOther: !forCreate),
            ["end"] = When(d, start: false, clearOther: !forCreate),
            ["transparency"] = d.IsBusy ? "opaque" : "transparent",
            ["reminders"] = d.ReminderMinutes is { } min
                ? new JsonObject { ["useDefault"] = false, ["overrides"] = new JsonArray(new JsonObject { ["method"] = "popup", ["minutes"] = min }) }
                : new JsonObject { ["useDefault"] = false, ["overrides"] = new JsonArray() },
        };
        if (!forInstance)
            body["recurrence"] = d.Recurrence is { Length: > 0 } rule ? new JsonArray("RRULE:" + rule) : new JsonArray();
        body["attendees"] = new JsonArray(d.Guests.Select(g => (JsonNode)new JsonObject { ["email"] = g }).ToArray());
        if (d.AddVideoCall)
            body["conferenceData"] = new JsonObject
            {
                ["createRequest"] = new JsonObject
                {
                    ["requestId"] = Guid.NewGuid().ToString("N"),
                    ["conferenceSolutionKey"] = new JsonObject { ["type"] = "hangoutsMeet" },
                },
            };
        return body;
    }

    private static JsonObject When(EventDraft d, bool start, bool clearOther)
    {
        var o = d.IsAllDay
            ? new JsonObject { ["date"] = (start ? d.StartDate : d.EndDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) }
            : new JsonObject { ["dateTime"] = Rfc3339(start ? d.Start : d.End), ["timeZone"] = d.TimeZone };
        if (clearOther)
        {
            if (d.IsAllDay) o["dateTime"] = null;
            else o["date"] = null;
        }
        return o;
    }

    // ───────── HTTP ─────────

    private async Task<JsonDocument> SendAsync(HttpMethod method, string url, Account account, JsonNode? body, CancellationToken ct)
    {
        var token = await google.GetAccessTokenAsync(account.Id, account.Email, ct);
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);

        var reason = ErrorReason(text);
        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new GoogleSignInRequiredException(L.F("Google sign-in for {0} has expired. Right-click the account and choose \"Sign in again\".", account.Email)),
            HttpStatusCode.Forbidden when reason is "insufficientPermissions" or "ACCESS_TOKEN_SCOPE_INSUFFICIENT" =>
                new CalendarPermissionException(L.F("{0} hasn't given Plicbox calendar access yet.", account.Email)),
            HttpStatusCode.Forbidden when reason is "accessNotConfigured" or "SERVICE_DISABLED" =>
                new CalendarApiDisabledException(L.T("The Google Calendar API is switched off in your Google Cloud project. Enable it under APIs & Services → Library → Google Calendar API.")),
            _ => new HttpRequestException(L.F("Google Calendar answered {0}: {1}", (int)response.StatusCode, ErrorMessage(text) ?? response.ReasonPhrase), null, response.StatusCode),
        };
    }

    private static string? ErrorReason(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var err = doc.RootElement.GetProperty("error");
            if (err.TryGetProperty("errors", out var errors))
                foreach (var e in errors.EnumerateArray())
                    if (Str(e, "reason") is { } r) return r;
            if (err.TryGetProperty("details", out var details))
                foreach (var d in details.EnumerateArray())
                    if (Str(d, "reason") is { } r) return r;
            return Str(err, "status");
        }
        catch (Exception) { return null; }
    }

    private static string? ErrorMessage(string json)
    {
        try { using var doc = JsonDocument.Parse(json); return Str(doc.RootElement.GetProperty("error"), "message"); }
        catch (Exception) { return null; }
    }

    private static string EventUrl(string calendarId, string eventId) =>
        $"{Base}/calendars/{Uri.EscapeDataString(calendarId)}/events/{Uri.EscapeDataString(eventId)}";

    private static IEnumerable<JsonElement> Items(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("items", out var items) ? items.EnumerateArray() : [];

    internal static string Rfc3339(DateTimeOffset t) => t.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
