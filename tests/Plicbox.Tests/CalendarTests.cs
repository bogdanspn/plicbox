using System.Text.Json;
using Plicbox.Core;
using Plicbox.Dav;
using Plicbox.Storage;

namespace Plicbox.Tests;

public sealed class CalendarLayoutTests
{
    private static readonly DateOnly Day = new(2026, 10, 16);

    private static CalendarEvent Timed(string title, int startHour, int startMin, int endHour, int endMin) => new()
    {
        RemoteId = title, Title = title,
        Start = new DateTimeOffset(Day.ToDateTime(new TimeOnly(startHour, startMin))),
        End = new DateTimeOffset(Day.ToDateTime(new TimeOnly(endHour, endMin))),
    };

    private static CalendarEvent AllDay(string title, DateOnly from, DateOnly toExclusive) => new()
    {
        RemoteId = title, Title = title, IsAllDay = true, StartDate = from, EndDate = toExclusive,
        Start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue)), End = new DateTimeOffset(toExclusive.ToDateTime(TimeOnly.MinValue)),
    };

    [Fact]
    public void Overlapping_events_share_the_width_and_reuse_free_columns()
    {
        var placed = CalendarLayout.LayoutDay(
        [
            Timed("A", 9, 0, 10, 0),
            Timed("B", 9, 30, 11, 0),
            Timed("C", 10, 0, 10, 30),   // A has ended: reuses column 0
            Timed("D", 13, 0, 14, 0),    // separate group: full width
        ], Day).ToDictionary(p => p.Event.Title);

        Assert.Equal((0, 2), (placed["A"].Column, placed["A"].Columns));
        Assert.Equal((1, 2), (placed["B"].Column, placed["B"].Columns));
        Assert.Equal((0, 2), (placed["C"].Column, placed["C"].Columns));
        Assert.Equal((0, 1), (placed["D"].Column, placed["D"].Columns));
    }

    [Fact]
    public void Events_crossing_midnight_are_clipped_to_the_day()
    {
        var late = Timed("Late", 23, 0, 23, 0) with { End = new DateTimeOffset(Day.AddDays(1).ToDateTime(new TimeOnly(1, 0))) };
        var p = Assert.Single(CalendarLayout.LayoutDay([late], Day));
        Assert.Equal(Day.AddDays(1).ToDateTime(TimeOnly.MinValue), p.End);
    }

    [Fact]
    public void All_day_and_multi_day_events_go_to_the_strip_in_lanes()
    {
        var monday = new DateOnly(2026, 10, 12);
        var spans = CalendarLayout.LayoutSpans(
        [
            AllDay("Trip", monday.AddDays(1), monday.AddDays(4)),      // Tue–Thu
            AllDay("Holiday", monday.AddDays(2), monday.AddDays(3)),   // Wed: collides with Trip -> lane 1
            AllDay("Friday", monday.AddDays(4), monday.AddDays(5)),    // Fri: lane 0 is free again
            AllDay("Long", monday.AddDays(-3), monday.AddDays(9)),     // covers the whole week and beyond
        ], monday, 7).ToDictionary(s => s.Event.Title);

        Assert.Equal((0, 7, true, true), (spans["Long"].FirstDay, spans["Long"].Days, spans["Long"].ContinuesBefore, spans["Long"].ContinuesAfter));
        Assert.Equal((1, 3), (spans["Trip"].FirstDay, spans["Trip"].Days));
        Assert.NotEqual(spans["Trip"].Lane, spans["Holiday"].Lane);
        Assert.Equal(spans["Trip"].Lane, spans["Friday"].Lane);
    }

    [Fact]
    public void Timed_event_ending_at_midnight_does_not_spill_into_the_next_day()
    {
        var ev = Timed("Evening", 20, 0, 20, 0) with { End = new DateTimeOffset(Day.AddDays(1).ToDateTime(TimeOnly.MinValue)) };
        Assert.Equal(Day.AddDays(1), CalendarLayout.EndDay(ev));
    }

    [Theory]
    [InlineData(DayOfWeek.Monday, "2026-10-12")]
    [InlineData(DayOfWeek.Sunday, "2026-10-11")]
    public void Week_starts_on_the_chosen_day(DayOfWeek first, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), CalendarLayout.WeekStart(Day, first));
}

public sealed class RecurrenceTests
{
    private static readonly DateTime Friday = new(2026, 10, 16, 9, 0, 0);

    [Theory]
    [InlineData(RepeatKind.Daily, "FREQ=DAILY")]
    [InlineData(RepeatKind.Weekdays, "FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR")]
    [InlineData(RepeatKind.Weekly, "FREQ=WEEKLY;BYDAY=FR")]
    [InlineData(RepeatKind.Monthly, "FREQ=MONTHLY;BYMONTHDAY=16")]
    [InlineData(RepeatKind.Yearly, "FREQ=YEARLY")]
    public void Presets_round_trip(RepeatKind kind, string rule)
    {
        Assert.Equal(rule, Calendars.BuildRule(kind, Friday));
        Assert.Equal(kind, Calendars.Classify(rule));
    }

    [Theory]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO")]
    [InlineData("FREQ=DAILY;COUNT=5")]
    [InlineData("FREQ=MONTHLY;BYDAY=2TU")]
    public void Anything_fancier_is_kept_as_custom(string rule) => Assert.Equal(RepeatKind.Custom, Calendars.Classify(rule));

    [Fact]
    public void Describes_rules_in_words()
    {
        Assert.Equal("Every weekday", Calendars.Describe("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", Friday));
        Assert.StartsWith("Every 2 weeks on", Calendars.Describe("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO", Friday));
        Assert.Equal("Does not repeat", Calendars.Describe(null, Friday));
    }
}

public sealed class GoogleCalendarParsingTests
{
    [Fact]
    public void Parses_a_timed_occurrence_with_meet_reminder_and_my_answer()
    {
        const string json = """
            {
              "id": "abc_20261016T070000Z", "recurringEventId": "abc", "status": "confirmed",
              "summary": "Standup", "location": "Room 4",
              "start": { "dateTime": "2026-10-16T09:00:00+02:00", "timeZone": "Europe/Berlin" },
              "end":   { "dateTime": "2026-10-16T09:15:00+02:00", "timeZone": "Europe/Berlin" },
              "hangoutLink": "https://meet.google.com/abc-defg-hij",
              "colorId": "11", "transparency": "transparent",
              "reminders": { "useDefault": true },
              "attendees": [
                { "email": "me@example.com", "self": true, "responseStatus": "tentative" },
                { "email": "boss@example.com", "organizer": true, "responseStatus": "accepted" }
              ],
              "updated": "2026-10-10T12:00:00.000Z"
            }
            """;
        var ev = GoogleCalendarApi.Parse(JsonDocument.Parse(json).RootElement, defaultReminder: 10)!;
        Assert.Equal("abc", ev.SeriesId);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 7, 0, 0, TimeSpan.Zero), ev.Start.ToUniversalTime());
        Assert.Equal(TimeSpan.FromMinutes(15), ev.End - ev.Start);
        Assert.Equal("https://meet.google.com/abc-defg-hij", ev.MeetingUrl);
        Assert.Equal("#D50000", ev.Color);
        Assert.False(ev.IsBusy);
        Assert.Equal(10, ev.ReminderMinutes);
        Assert.Equal("tentative", ev.MyResponse);
    }

    [Fact]
    public void Parses_all_day_dates_without_shifting_them_and_finds_zoom_links()
    {
        const string json = """
            { "id": "h1", "summary": "Tag der Deutschen Einheit",
              "start": { "date": "2026-10-03" }, "end": { "date": "2026-10-04" },
              "description": "Call: https://us02web.zoom.us/j/123456?pwd=x" }
            """;
        var ev = GoogleCalendarApi.Parse(JsonDocument.Parse(json).RootElement, null)!;
        Assert.True(ev.IsAllDay);
        Assert.Equal((new DateOnly(2026, 10, 3), new DateOnly(2026, 10, 4)), (ev.StartDate, ev.EndDate));
        Assert.Equal("https://us02web.zoom.us/j/123456?pwd=x", ev.MeetingUrl);
    }

    [Fact]
    public void Draft_json_has_meet_request_reminder_and_rule()
    {
        var draft = new EventDraft
        {
            Title = "Review", Start = new DateTimeOffset(2026, 10, 16, 14, 0, 0, TimeSpan.FromHours(2)),
            End = new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.FromHours(2)), TimeZone = "Europe/Berlin",
            Recurrence = "FREQ=WEEKLY;BYDAY=FR", Guests = ["a@example.com"], AddVideoCall = true, ReminderMinutes = 15,
        };
        var node = GoogleCalendarApi.ToJson(draft, forInstance: false, forCreate: true);
        Assert.Equal("2026-10-16T14:00:00+02:00", (string?)node["start"]!["dateTime"]); // the writer escapes "+", so compare the value
        var json = node.ToJsonString();
        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=FR", json);
        Assert.Contains("hangoutsMeet", json);
        Assert.Contains("\"minutes\":15", json);
        Assert.DoesNotContain("\"date\":", json);

        var instance = GoogleCalendarApi.ToJson(draft, forInstance: true, forCreate: false).ToJsonString();
        Assert.DoesNotContain("RRULE", instance);
        Assert.Contains("\"date\":null", instance); // switching to timed clears an old all-day date
    }
}

public sealed class CalendarRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-tests", Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly CalendarRepository _repo;
    private readonly long _accountId;

    public CalendarRepositoryTests()
    {
        _db = new Database(Path.Combine(_dir, "c.db"));
        _db.Migrate();
        _repo = new CalendarRepository(_db);
        _accountId = new AccountRepository(_db).AddAsync(new Account
        {
            DisplayName = "T", Email = "t@example.com", Username = "t", ImapHost = "i", SmtpHost = "s",
        }).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Replace_window_keeps_events_outside_it_and_visibility_survives_refresh()
    {
        var calId = await _repo.UpsertCalendarAsync(new CalendarInfo { AccountId = _accountId, RemoteId = "primary", Name = "Me" }, false);
        await _repo.SetCalendarVisibleAsync(calId, false);
        await _repo.UpsertCalendarAsync(new CalendarInfo { AccountId = _accountId, RemoteId = "primary", Name = "Renamed", IsVisible = true }, keepVisibility: true);
        var cal = Assert.Single(await _repo.GetCalendarsAsync());
        Assert.Equal(("Renamed", false), (cal.Name, cal.IsVisible));

        var oct = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
        CalendarEvent Ev(string id, int day) => new()
        {
            RemoteId = id, Title = id, Start = oct.AddDays(day).AddHours(9), End = oct.AddDays(day).AddHours(10),
            Attendees = [new EventAttendee("x@example.com", "X", "accepted")],
        };
        await _repo.ReplaceEventsAsync(calId, oct, oct.AddDays(31), [Ev("a", 1), Ev("b", 2)]);
        await _repo.UpsertEventAsync(Ev("far", 60) with { CalendarId = calId });
        await _repo.ReplaceEventsAsync(calId, oct, oct.AddDays(31), [Ev("b", 2)]); // "a" was deleted on the server

        var events = await _repo.GetEventsAsync([calId], oct, oct.AddDays(90));
        Assert.Equal(["b", "far"], events.Select(e => e.RemoteId));
        Assert.Equal("X", events[0].Attendees.Single().Name);
    }

    [Fact]
    public async Task All_day_dates_round_trip_exactly()
    {
        var calId = await _repo.UpsertCalendarAsync(new CalendarInfo { AccountId = _accountId, RemoteId = "h", Name = "Holidays" }, false);
        var day = new DateOnly(2026, 10, 3);
        await _repo.UpsertEventAsync(new CalendarEvent
        {
            CalendarId = calId, RemoteId = "unity", Title = "Unity Day", IsAllDay = true, StartDate = day, EndDate = day.AddDays(1),
        });
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue));
        var ev = Assert.Single(await _repo.GetEventsAsync([calId], from, from.AddDays(1)));
        Assert.Equal((day, day.AddDays(1)), (ev.StartDate, ev.EndDate));
        Assert.Empty(await _repo.GetEventsAsync([calId], from.AddDays(1), from.AddDays(2)));
    }

    [Fact]
    public async Task Search_needs_every_word_somewhere_and_treats_wildcards_literally()
    {
        var calId = await _repo.UpsertCalendarAsync(new CalendarInfo { AccountId = _accountId, RemoteId = "p", Name = "Me" }, false);
        var t = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        CalendarEvent Ev(string id, string title, string? place = null, string? who = null) => new()
        {
            CalendarId = calId, RemoteId = id, Title = title, Location = place, Start = t, End = t.AddHours(1),
            Attendees = who is null ? [] : [new EventAttendee($"{who.ToLowerInvariant()}@example.com", who, "accepted")],
        };
        await _repo.UpsertEventAsync(Ev("1", "Lunch", "Ramen Bar", "Ana"));
        await _repo.UpsertEventAsync(Ev("2", "Lunch with the team"));
        await _repo.UpsertEventAsync(Ev("3", "100% focus"));
        await _repo.UpsertEventAsync(Ev("4", "1000 things"));

        Assert.Equal(["1"], (await _repo.SearchEventsAsync("ana lunch", 50)).Select(e => e.RemoteId));
        Assert.Equal(["1", "2"], (await _repo.SearchEventsAsync("LUNCH", 50)).Select(e => e.RemoteId).Order());
        Assert.Equal(["3"], (await _repo.SearchEventsAsync("100%", 50)).Select(e => e.RemoteId));

        await _repo.DeleteCalendarAsync(calId);
        Assert.Empty(await _repo.SearchEventsAsync("lunch", 50));
        Assert.Empty(await _repo.GetCalendarsAsync());
    }
}
