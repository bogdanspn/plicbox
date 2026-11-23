using Microsoft.Data.Sqlite;
using Plicbox.Core;
using Plicbox.Dav;
using Plicbox.Storage;

namespace Plicbox.Tests;

/// <summary>Repeat rules worked out here, for calendars on this computer (Google expands its own).</summary>
public sealed class RecurrenceExpansionTests
{
    private static List<string> Dates(string rule, DateTime first, int days, DateTime? from = null) =>
        Recurrence.Occurrences(rule, first, from ?? first, first.AddDays(days)).Select(d => d.ToString("yyyy-MM-dd HH:mm")).ToList();

    [Fact]
    public void Weekly_keeps_the_day_and_the_wall_clock_time()
    {
        var monday = new DateTime(2027, 3, 15, 9, 0, 0); // spans the switch to summer time on 28 March
        Assert.Equal(["2027-03-15 09:00", "2027-03-22 09:00", "2027-03-29 09:00", "2027-04-05 09:00"], Dates("FREQ=WEEKLY;BYDAY=MO", monday, 28));
    }

    [Fact]
    public void Every_weekday_skips_the_weekend() =>
        Assert.Equal(["2026-11-13 08:30", "2026-11-16 08:30", "2026-11-17 08:30"],
            Dates("FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR", new DateTime(2026, 11, 13, 8, 30, 0), 5));

    [Fact]
    public void Monthly_on_the_31st_skips_months_without_one() =>
        Assert.Equal(["2026-01-31 10:00", "2026-03-31 10:00", "2026-05-31 10:00", "2026-07-31 10:00"],
            Dates("FREQ=MONTHLY;BYMONTHDAY=31", new DateTime(2026, 1, 31, 10, 0, 0), 190));

    [Fact]
    public void Nth_and_last_weekday_of_the_month()
    {
        Assert.Equal(["2026-11-10 18:00", "2026-12-08 18:00", "2027-01-12 18:00"], Dates("FREQ=MONTHLY;BYDAY=2TU", new DateTime(2026, 11, 10, 18, 0, 0), 70));
        Assert.Equal(["2026-11-27 18:00", "2026-12-25 18:00", "2027-01-29 18:00"], Dates("FREQ=MONTHLY;BYDAY=-1FR", new DateTime(2026, 11, 27, 18, 0, 0), 70));
        // The last working day: weekdays of the month, the last of them.
        Assert.Equal(["2026-11-30 17:00", "2026-12-31 17:00", "2027-01-29 17:00"],
            Dates("FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1", new DateTime(2026, 11, 30, 17, 0, 0), 70));
    }

    [Fact]
    public void A_29_February_birthday_comes_in_leap_years_only() =>
        Assert.Equal(["2028-02-29 00:00", "2032-02-29 00:00"], Dates("FREQ=YEARLY", new DateTime(2028, 2, 29), 365 * 5));

    [Fact]
    public void Count_starts_at_the_first_even_when_the_range_starts_later()
    {
        var first = new DateTime(2026, 11, 16, 7, 0, 0);
        Assert.Equal(["2026-11-17 07:00", "2026-11-18 07:00"], Dates("FREQ=DAILY;COUNT=3", first, 30, from: first.AddHours(1)));
        Assert.Equal(["2026-11-16 07:00", "2026-11-17 07:00", "2026-11-18 07:00"], Dates("FREQ=DAILY;UNTIL=20261118", first, 30));
        Assert.Equal(["2026-11-16 07:00", "2026-11-17 07:00"], Dates("FREQ=DAILY;UNTIL=20261118T060000", first, 30));
    }

    [Fact]
    public void Every_other_week_on_two_days() =>
        Assert.Equal(["2026-11-16 12:00", "2026-11-18 12:00", "2026-11-30 12:00", "2026-12-02 12:00"],
            Dates("RRULE:FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE", new DateTime(2026, 11, 16, 12, 0, 0), 20));

    [Fact]
    public void A_rule_it_cannot_follow_leaves_just_the_event() =>
        Assert.Equal(["2026-11-16 12:00"], Dates("FREQ=HOURLY", new DateTime(2026, 11, 16, 12, 0, 0), 20));
}

/// <summary>Calendars kept only on this computer: their events, repeating ones and their exceptions.</summary>
public sealed class LocalCalendarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-tests", Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly CalendarRepository _repo;
    private readonly LocalCalendars _local;
    private readonly long _calId;
    // A Monday a week or two ahead, at 9:00: well inside the window that's kept written out.
    private readonly DateTime _monday;

    public LocalCalendarTests()
    {
        _db = new Database(Path.Combine(_dir, "l.db"));
        _db.Migrate();
        _repo = new CalendarRepository(_db);
        _local = new LocalCalendars(_repo);
        _calId = _repo.UpsertCalendarAsync(new CalendarInfo { RemoteId = "local-mine", Name = "Mine", AccessRole = "owner" }, false).GetAwaiter().GetResult();
        var today = DateTime.Today;
        _monday = today.AddDays(7 + ((int)DayOfWeek.Monday - (int)today.DayOfWeek + 7) % 7).AddHours(9);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private CalendarEvent Weekly(string title = "Standup") => new()
    {
        CalendarId = _calId, RemoteId = "local-series", Title = title, Start = new DateTimeOffset(_monday), End = new DateTimeOffset(_monday.AddMinutes(30)),
        Recurrence = "FREQ=WEEKLY;BYDAY=MO", ReminderMinutes = 10,
    };

    private Task<IReadOnlyList<CalendarEvent>> ThreeWeeks() =>
        _repo.GetEventsAsync([_calId], new DateTimeOffset(_monday.Date), new DateTimeOffset(_monday.Date.AddDays(21)));

    [Fact]
    public async Task A_calendar_needs_no_account_and_its_repeating_event_is_written_out()
    {
        var cal = Assert.Single(await _repo.GetCalendarsAsync());
        Assert.True(cal.IsLocal);

        await _local.CreateAsync(Weekly());
        var events = await ThreeWeeks();
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(("local-series", "FREQ=WEEKLY;BYDAY=MO", 10, TimeSpan.FromHours(9)),
            (e.SeriesId, e.Recurrence, e.ReminderMinutes ?? 0, e.Start.LocalDateTime.TimeOfDay)));
        // Written out for the whole window, about a year ahead.
        var later = await _repo.GetEventsAsync([_calId], new DateTimeOffset(_monday.AddDays(300)), new DateTimeOffset(_monday.AddDays(307)));
        Assert.Single(later);
    }

    [Fact]
    public async Task One_occurrence_changed_or_deleted_stays_that_way()
    {
        await _local.CreateAsync(Weekly());
        var events = await ThreeWeeks();
        var second = events[1];
        await _local.UpdateAsync(second, second with { Title = "Planning", Start = second.Start.AddHours(2), End = second.End.AddHours(2) }, wholeSeries: false);
        await _local.DeleteAsync(events[2], wholeSeries: false);
        await _local.RefreshAsync(); // written out again, as on the next start

        events = await ThreeWeeks();
        Assert.Equal(["Standup", "Planning"], events.Select(e => e.Title));
        Assert.Equal(TimeSpan.FromHours(11), events[1].Start.LocalDateTime.TimeOfDay);
        Assert.Equal(second.RemoteId, events[1].RemoteId);
    }

    [Fact]
    public async Task Changing_every_occurrence_moves_the_series_the_same_way()
    {
        await _local.CreateAsync(Weekly());
        var events = await ThreeWeeks();
        await _local.DeleteAsync(events[2], wholeSeries: false);
        var second = events[1];
        // From the second one: a day later and an hour earlier, every week on Tuesday.
        await _local.UpdateAsync(second, second with
        {
            Title = "Weekly sync", Start = second.Start.AddDays(1).AddHours(-1), End = second.End.AddDays(1).AddHours(-1), Recurrence = "FREQ=WEEKLY;BYDAY=TU",
        }, wholeSeries: true);

        events = await ThreeWeeks();
        Assert.Equal(3, events.Count); // the deleted one is back: the dates changed, so the old exceptions went
        Assert.All(events, e => Assert.Equal(("Weekly sync", DayOfWeek.Tuesday, TimeSpan.FromHours(8)),
            (e.Title, e.Start.LocalDateTime.DayOfWeek, e.Start.LocalDateTime.TimeOfDay)));
        Assert.Equal(_monday.AddDays(1).AddHours(-1), events[0].Start.LocalDateTime);
    }

    [Fact]
    public async Task A_title_change_for_every_occurrence_keeps_the_exceptions()
    {
        await _local.CreateAsync(Weekly());
        var events = await ThreeWeeks();
        await _local.DeleteAsync(events[2], wholeSeries: false);
        await _local.UpdateAsync(events[0], events[0] with { Title = "Daily standup" }, wholeSeries: true);

        events = await ThreeWeeks();
        Assert.Equal(["Daily standup", "Daily standup"], events.Select(e => e.Title));
    }

    [Fact]
    public async Task Repeating_can_start_and_stop()
    {
        var once = Weekly() with { RemoteId = "local-once", Recurrence = null };
        await _local.CreateAsync(once);
        var single = Assert.Single(await ThreeWeeks());
        Assert.Null(single.SeriesId);

        await _local.UpdateAsync(single, single with { Recurrence = "FREQ=WEEKLY;BYDAY=MO" }, wholeSeries: false);
        var events = await ThreeWeeks();
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal("local-once", e.SeriesId));

        await _local.UpdateAsync(events[1], events[1] with { Recurrence = null }, wholeSeries: true);
        single = Assert.Single(await ThreeWeeks());
        Assert.Equal((null, _monday.AddDays(7)), (single.SeriesId, single.Start.LocalDateTime));
        Assert.Empty(await _repo.GetLocalSeriesAsync());
    }

    [Fact]
    public async Task Dates_beyond_the_window_are_added_when_viewed_and_the_calendar_takes_everything_along()
    {
        await _local.CreateAsync(Weekly());
        var (_, to) = CalendarEngine.Window;
        var from = to.AddDays(60);
        Assert.Empty(await _repo.GetEventsAsync([_calId], from, from.AddDays(7)));
        Assert.True(await _local.EnsureRangeAsync(from, from.AddDays(7)));
        Assert.False(await _local.EnsureRangeAsync(from, from.AddDays(7))); // once: no reload loop
        Assert.Single(await _repo.GetEventsAsync([_calId], from, from.AddDays(7)));

        await _repo.DeleteCalendarAsync(_calId);
        Assert.Empty(await _repo.GetLocalSeriesAsync());
        Assert.Empty(await _repo.GetEventsAsync([_calId], from, from.AddDays(7)));
    }
}

public sealed class CalendarMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Rebuilding_the_calendars_table_keeps_every_event()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "old.db");
        // A database from before calendars could be without an account, with a Google calendar and an event in it.
        using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, ForeignKeys = true, Pooling = false }.ToString()))
        {
            conn.Open();
            var rebuild = Array.FindIndex(Migrations.All, m => m.Contains("CREATE TABLE calendars_new", StringComparison.Ordinal));
            foreach (var sql in Migrations.All[..rebuild].Append($"PRAGMA user_version = {rebuild};").Append("""
                INSERT INTO accounts (id, display_name, email, provider, auth, username, imap_host, imap_port, smtp_host, smtp_port, color)
                    VALUES (1, 'T', 't@example.com', 0, 0, 't', 'i', 993, 's', 465, '#000000');
                INSERT INTO calendars (id, account_id, remote_id, name, color, access_role) VALUES (7, 1, 'primary', 'Me', '#000000', 'owner');
                INSERT INTO events (calendar_id, remote_id, title, start_utc, end_utc, is_all_day, status, is_busy, updated_utc)
                    VALUES (7, 'e1', 'Dentist', 1800000000, 1800003600, 0, 'confirmed', 1, 0);
                """))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        var db = new Database(path);
        db.Migrate();
        var repo = new CalendarRepository(db);
        var ev = Assert.Single(await repo.GetEventsAsync([7], DateTimeOffset.FromUnixTimeSeconds(1799990000), DateTimeOffset.FromUnixTimeSeconds(1800010000)));
        Assert.Equal("Dentist", ev.Title);
        await repo.UpsertCalendarAsync(new CalendarInfo { RemoteId = "local-1", Name = "Mine", AccessRole = "owner" }, false);
        Assert.Equal([1L, null], (await repo.GetCalendarsAsync()).Select(c => c.AccountId).OrderBy(a => a is null));

        // Foreign keys are back on afterwards: deleting the account still takes its calendar and events along.
        using var check = db.Open();
        using var cmd2 = check.CreateCommand();
        cmd2.CommandText = "DELETE FROM accounts; SELECT COUNT(*) FROM events;";
        Assert.Equal(0L, (long)cmd2.ExecuteScalar()!);
    }
}
