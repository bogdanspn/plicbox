using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Plicbox.Core;
using Plicbox.Dav;
using Plicbox.Storage;

namespace Plicbox.Tests;

/// <summary>.ics and .vcf in and out, as other apps write them; and backups of what's kept only on this computer.</summary>
public sealed class FileFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-files", Guid.NewGuid().ToString("N"));

    public FileFormatTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Ics_from_Outlook_reads_zones_durations_alarms_and_folded_text()
    {
        var ics = "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nBEGIN:VTIMEZONE\r\nTZID:W. Europe Standard Time\r\nBEGIN:STANDARD\r\nDTSTART:16010101T030000\r\nEND:STANDARD\r\nEND:VTIMEZONE\r\n"
                  + "BEGIN:VEVENT\r\nUID:040000008200E00074C5B7101A82E008\r\nSUMMARY:Budget review\\, Q4\r\nLOCATION:Room 2\\; floor 3\r\n"
                  + "DESCRIPTION:Agenda:\\n1. Numbers\\n2. Plans for a very long line that Outlook folds over\r\n  more than one line\r\n"
                  + "DTSTART;TZID=W. Europe Standard Time:20270115T140000\r\nDURATION:PT1H30M\r\nORGANIZER;CN=\"Doe, Jane\":mailto:jane@example.com\r\n"
                  + "BEGIN:VALARM\r\nTRIGGER:-PT15M\r\nACTION:DISPLAY\r\nEND:VALARM\r\nEND:VEVENT\r\n"
                  + "BEGIN:VEVENT\r\nUID:holiday-1\r\nSUMMARY:Holiday\r\nDTSTART;VALUE=DATE:20270101\r\nTRANSP:TRANSPARENT\r\nEND:VEVENT\r\n"
                  + "BEGIN:VEVENT\r\nUID:gone\r\nSTATUS:CANCELLED\r\nDTSTART:20270102T100000Z\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var items = IcsFormat.Read(ics);
        Assert.Equal(2, items.Count); // the cancelled one is left out

        var meeting = items[0].Event;
        Assert.Equal("Budget review, Q4", meeting.Title);
        Assert.Equal("Room 2; floor 3", meeting.Location);
        Assert.Equal("Agenda:\n1. Numbers\n2. Plans for a very long line that Outlook folds over more than one line", meeting.Description);
        Assert.Equal(new DateTimeOffset(2027, 1, 15, 13, 0, 0, TimeSpan.Zero), meeting.Start); // 14:00 in Berlin, winter
        Assert.Equal(TimeSpan.FromMinutes(90), meeting.End - meeting.Start);
        Assert.Equal(15, meeting.ReminderMinutes);
        Assert.Equal("Europe/Berlin", meeting.TimeZone);
        Assert.Equal("jane@example.com", meeting.OrganizerEmail);
        Assert.Equal("Doe, Jane", meeting.OrganizerName);

        var holiday = items[1].Event;
        Assert.True(holiday.IsAllDay);
        Assert.Equal((new DateOnly(2027, 1, 1), new DateOnly(2027, 1, 2)), (holiday.StartDate, holiday.EndDate));
        Assert.False(holiday.IsBusy);
    }

    [Fact]
    public async Task A_calendar_on_this_computer_goes_out_and_back_whole()
    {
        var db = new Database(Path.Combine(_dir, "a.db"));
        db.Migrate();
        var repo = new CalendarRepository(db);
        var engine = new CalendarEngine(new AccountRepository(db), repo, null!, new JsonSettingsStore(Path.Combine(_dir, "s.json")), NullLogger<CalendarEngine>.Instance);
        var calId = await engine.CreateCalendarAsync(null, "Mine, really", "#F4511E");
        var monday = DateTime.Today.AddDays(((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7 + 7).AddHours(9);
        await engine.CreateAsync(calId, new EventDraft { Title = "Standup", Start = monday, End = monday.AddMinutes(15), Recurrence = "FREQ=WEEKLY;BYDAY=MO", ReminderMinutes = 5 });
        await engine.CreateAsync(calId, new EventDraft { Title = "Trip", IsAllDay = true, StartDate = DateOnly.FromDateTime(monday).AddDays(3), EndDate = DateOnly.FromDateTime(monday).AddDays(5) });
        var week2 = (await engine.GetEventsAsync(monday.AddDays(6), monday.AddDays(8))).Single(e => e.Title == "Standup");
        await engine.DeleteAsync(week2, wholeSeries: false);
        var week3 = (await engine.GetEventsAsync(monday.AddDays(13), monday.AddDays(15))).Single(e => e.Title == "Standup");
        await engine.UpdateAsync(week3, new EventDraft { Title = "Standup (long)", Start = week3.Start, End = week3.Start.AddHours(1) }, wholeSeries: false);

        var ics = await engine.ExportAsync(calId);
        Assert.Contains("RRULE:FREQ=WEEKLY;BYDAY=MO", ics);
        Assert.Contains("EXDATE;TZID=", ics);
        Assert.Contains("RECURRENCE-ID;TZID=", ics);
        Assert.Contains("X-WR-CALNAME:Mine\\, really", ics);

        // Into a fresh calendar: the same series, its gap and its changed week; and again: nothing doubles.
        var copyId = await engine.CreateCalendarAsync(null, "Copy", null);
        Assert.Equal(2, await engine.ImportAsync(copyId, IcsFormat.Read(ics)));
        Assert.Equal(2, await engine.ImportAsync(copyId, IcsFormat.Read(ics)));
        var copied = await repo.GetEventsAsync([copyId], monday.AddDays(-1), monday.AddDays(22));
        Assert.Equal(["Standup", "Standup (long)", "Standup"], copied.Where(e => !e.IsAllDay).OrderBy(e => e.Start).Select(e => e.Title));
        Assert.DoesNotContain(copied, e => e.Start.Date == monday.AddDays(7).Date);
        Assert.Single(copied, e => e.Title == "Trip");
        Assert.Equal(5, copied.First(e => e.Title == "Standup").ReminderMinutes);
        await engine.DisposeAsync();
    }

    [Fact]
    public void Vcard_21_from_an_old_phone_and_30_round_trip()
    {
        var old = "BEGIN:VCARD\r\nVERSION:2.1\r\nN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:M=C3=BCller;J=C3=BCrgen\r\nFN;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:J=C3=BCrgen M=C3=BCller\r\n"
                  + "TEL;CELL;PREF:+49 170 1234567\r\nTEL;WORK;VOICE:+49 30 123\r\nEMAIL;INTERNET;HOME:juergen@example.de\r\nBDAY:--05-17\r\nEND:VCARD\r\n"
                  + "BEGIN:VCARD\r\nVERSION:3.0\r\nN:;;;;\r\nEND:VCARD\r\n";
        var read = VcfFormat.Read(old);
        var jurgen = Assert.Single(read); // the empty card is skipped
        Assert.Equal("Jürgen Müller", jurgen.DisplayName);
        Assert.Equal(("Jürgen", "Müller"), (jurgen.GivenName, jurgen.FamilyName));
        Assert.Equal([new LabeledValue("+49 170 1234567", "mobile"), new LabeledValue("+49 30 123", "work")], jurgen.Phones);
        Assert.Equal("home", jurgen.Emails.Single().Label);
        Assert.Equal(new DateOnly(Contact.NoYear, 5, 17), jurgen.Birthday);

        var ana = new Contact
        {
            DisplayName = "Ana Popescu", GivenName = "Ana", FamilyName = "Popescu", Organization = "Acme; Ltd", Department = "Design", JobTitle = "Lead",
            Emails = [new("ana@acme.example", "work")], Phones = [new("+40 721 000 000", "mobile")],
            Addresses = [new PostalAddress("Str. Lungă 5", "Brașov", "500035", null, "Romania", "work")],
            Birthday = new DateOnly(1990, 2, 3), Notes = "Met at the fair,\nlikes tea", IsFavorite = true,
        };
        var back = Assert.Single(VcfFormat.Read(VcfFormat.Write([ana])));
        Assert.Equal(ana.DisplayName, back.DisplayName);
        Assert.Equal((ana.Organization, ana.Department, ana.JobTitle), (back.Organization, back.Department, back.JobTitle));
        Assert.Equal(ana.Emails, back.Emails);
        Assert.Equal(ana.Phones, back.Phones);
        Assert.Equal(ana.Addresses, back.Addresses);
        Assert.Equal(ana.Birthday, back.Birthday);
        Assert.Equal(ana.Notes, back.Notes);
        Assert.True(back.IsFavorite);
    }

    [Fact]
    public async Task A_backup_keeps_everything_but_mail_and_restores_in_its_place()
    {
        var dbPath = Path.Combine(_dir, "data", "plicbox.db");
        var db = new Database(dbPath);
        db.Migrate();
        var accounts = new AccountRepository(db);
        var accountId = await accounts.AddAsync(new Account { DisplayName = "A", Email = "a@example.com", Username = "a", ImapHost = "i", SmtpHost = "s" });
        var folders = new FolderRepository(db);
        var inbox = await folders.UpsertAsync(new Folder { AccountId = accountId, Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        await new MessageRepository(db).UpsertAsync(new MessageSummary { AccountId = accountId, FolderId = inbox, Uid = 1, Subject = "Hello" });
        await new ContactRepository(db).UpsertAsync(new Contact { DisplayName = "Bob" });
        var calendars = new CalendarRepository(db);
        await calendars.UpsertCalendarAsync(new CalendarInfo { RemoteId = "local-1", Name = "Mine", AccessRole = "owner" }, false);
        var settingsPath = Path.Combine(_dir, "data", "settings.json");
        await File.WriteAllTextAsync(settingsPath, """{"Language":"de"}""");

        var backups = Path.Combine(_dir, "backups");
        var zip = await Backups.CreateAsync(db, settingsPath, backups);
        using (var archive = ZipFile.OpenRead(zip))
            Assert.Equal(["plicbox.db", "settings.json"], archive.Entries.Select(e => e.FullName).Order());

        // Restoring: checked and staged, then swapped in at the next start, mail left to download again.
        await new ContactRepository(db).UpsertAsync(new Contact { DisplayName = "Made after the backup" });
        var summary = Backups.Stage(zip, Path.Combine(_dir, "data"));
        Assert.Equal((1, 1, 1), (summary.Accounts, summary.Calendars, summary.Contacts));
        SqliteConnection.ClearAllPools();
        var bodies = Path.Combine(_dir, "data", "bodies");
        Directory.CreateDirectory(bodies);
        Assert.True(Backups.ApplyStaged(Path.Combine(_dir, "data"), dbPath, settingsPath, bodies));
        Assert.True(File.Exists(dbPath + ".before-restore"));
        Assert.False(Directory.Exists(bodies));

        var restored = new Database(dbPath);
        restored.Migrate();
        Assert.Equal(["Bob"], (await new ContactRepository(restored).GetAllAsync()).Select(c => c.DisplayName));
        Assert.Empty(await new MessageRepository(restored).GetByFolderAsync(inbox));
        var folder = (await new FolderRepository(restored).GetByAccountAsync(accountId)).Single();
        Assert.Equal(0, folder.LastUid); // starts afresh, so the mail comes back
        Assert.Contains("\"de\"", await File.ReadAllTextAsync(settingsPath));
        Assert.False(Backups.ApplyStaged(Path.Combine(_dir, "data"), dbPath, settingsPath, bodies)); // nothing staged any more
    }
}
