using Plicbox.Core;

namespace Plicbox.App;

/// <summary>Fictional calendars and events for --demo, laid around today so every view has something to show.</summary>
internal static class DemoCalendar
{
    /// <param name="anchor">The day the busiest events fall on: today, or for the website's pictures this week's Tuesday, so the week looks the same whichever day it's recorded.</param>
    public static async Task SeedAsync(IAccountRepository accounts, ICalendarRepository calendars, DateOnly? anchor = null)
    {
        var all = await accounts.GetAllAsync();
        if (all.Count == 0) return;
        var personalAccount = all[0];
        var workAccount = all.Count > 1 ? all[1] : all[0];

        async Task<long> Add(Account a, string remoteId, string name, string color, bool primary = false, string role = "owner") =>
            await calendars.UpsertCalendarAsync(new CalendarInfo
            {
                AccountId = a.Id, RemoteId = remoteId, Name = name, Color = color, IsPrimary = primary, AccessRole = role, IsVisible = true,
            }, keepVisibility: false);

        var personal = await Add(personalAccount, "personal", "Personal", "#039BE5", primary: true);
        var family = await Add(personalAccount, "family", "Family", "#8E24AA");
        var holidays = await Add(personalAccount, "holidays", "Holidays in Germany", "#0B8043", role: "reader");
        var work = await Add(workAccount, "work", "Work", "#3F51B5", primary: true);
        var team = await Add(workAccount, "team", "Team", "#33B679");

        var today = anchor ?? DateOnly.FromDateTime(DateTime.Today);
        DateTimeOffset At(int dayOffset, int hour, int minute = 0) => new(today.AddDays(dayOffset).ToDateTime(new TimeOnly(hour, minute)));
        var me = new EventAttendee(workAccount.Email, "You", "accepted", IsSelf: true);
        var meOrganizing = me with { IsOrganizer = true };
        var n = 0;

        async Task Timed(long cal, string title, DateTimeOffset start, DateTimeOffset end, string? location = null, string? meet = null,
            string? series = null, IReadOnlyList<EventAttendee>? people = null, string? color = null, bool busy = true, int? reminder = 10, string? notes = null)
        {
            await calendars.UpsertEventAsync(new CalendarEvent
            {
                CalendarId = cal, RemoteId = $"demo-{++n}", SeriesId = series, Title = title, Location = location, MeetingUrl = meet,
                Start = start, End = end, Attendees = people ?? [], Color = color, IsBusy = busy, ReminderMinutes = reminder,
                Description = notes, HtmlLink = "https://calendar.google.com/", Updated = DateTimeOffset.UtcNow, TimeZone = "Europe/Berlin",
            });
        }

        async Task AllDay(long cal, string title, DateOnly from, DateOnly toExclusive)
        {
            await calendars.UpsertEventAsync(new CalendarEvent
            {
                CalendarId = cal, RemoteId = $"demo-{++n}", Title = title, IsAllDay = true, StartDate = from, EndDate = toExclusive,
                Start = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue)), End = new DateTimeOffset(toExclusive.ToDateTime(TimeOnly.MinValue)),
                Updated = DateTimeOffset.UtcNow,
            });
        }

        // A daily standup on work days, as occurrences of one series (the way Google sends them).
        for (var d = -14; d <= 28; d++)
        {
            var day = today.AddDays(d);
            if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            await Timed(team, "Standup", At(d, 9, 30), At(d, 9, 45), meet: "https://meet.google.com/abc-defg-hij", series: "standup",
                people: [me, new("diego@work.example", "Diego Ruiz", "accepted", IsOrganizer: true)]);
        }

        await Timed(work, "Design review", At(0, 11), At(0, 12), "Room Ada", "https://meet.google.com/xyz-review",
            people: [meOrganizing, new("mia@work.example", "Mia Chen", "accepted"), new("diego@work.example", "Diego Ruiz", "tentative")],
            notes: "Walk through the new calendar screens.");
        await Timed(personal, "Lunch with Ana", At(0, 12, 30), At(0, 13, 30), "Ramen Bar, Hauptstraße 12");
        await Timed(work, "Focus time", At(0, 14), At(0, 16), color: "#7986CB", busy: true, reminder: null);
        await Timed(work, "1:1 with Diego", At(0, 15), At(0, 15, 30), meet: "https://meet.google.com/one-on-one",
            people: [meOrganizing, new("diego@work.example", "Diego Ruiz", "accepted")]);
        await Timed(family, "Pick up the kids", At(0, 17), At(0, 17, 30), busy: false);

        await Timed(personal, "Dentist", At(1, 8), At(1, 9), "Dr. Weber, Bahnhofstraße 4", reminder: 60);
        await Timed(work, "Vendor demo", At(1, 16), At(1, 17), meet: "https://zoom.us/j/1234567890",
            people: [new(workAccount.Email, "You", "declined", IsSelf: true), new("sales@vendor.example", "Vendor", "accepted", IsOrganizer: true)]);
        await Timed(work, "Quarterly planning", At(2, 10), At(2, 11, 30), "Big room",
            people: [new(workAccount.Email, "You", "needsAction", IsSelf: true), new("ceo@work.example", "Laura Brandt", "accepted", IsOrganizer: true),
                     new("mia@work.example", "Mia Chen", "accepted")]);
        await Timed(team, "Team lunch", At(2, 12, 30), At(2, 14), "Italian place");
        await Timed(family, "Gym", At(1, 18), At(1, 19), reminder: 30);
        await Timed(family, "Book club", At(3, 19), At(3, 21), "Ana's place");
        await Timed(personal, "Night train to Munich", At(4, 22), At(5, 6, 30), "Hauptbahnhof, platform 7");

        await AllDay(family, "Trip to Munich", today.AddDays(5), today.AddDays(8));
        await AllDay(holidays, "Reformationstag (regional)", new DateOnly(today.Year, 10, 31), new DateOnly(today.Year, 11, 1));
        await AllDay(personal, "Mum's birthday", today.AddDays(2), today.AddDays(3));
        await AllDay(work, "Release week", today.AddDays(-2), today.AddDays(3));
    }
}
