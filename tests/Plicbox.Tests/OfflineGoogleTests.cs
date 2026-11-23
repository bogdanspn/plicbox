using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Plicbox.Auth;
using Plicbox.Core;
using Plicbox.Dav;
using Plicbox.Storage;

namespace Plicbox.Tests;

/// <summary>
/// Google calendar and contact edits made offline: shown here at once, kept, and sent when Google can be reached again
/// (before the next sync reads anything back, so nothing made offline is overwritten). Google is a fake here.
/// </summary>
public sealed class OfflineGoogleTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-offline", Guid.NewGuid().ToString("N"));
    private Database _db = null!;
    private FakeGoogle _google = null!;
    private GoogleAuth _auth = null!;
    private AccountRepository _accounts = null!;
    private JsonSettingsStore _settings = null!;
    private Account _account = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _db = new Database(Path.Combine(_dir, "t.db"));
        _db.Migrate();
        _accounts = new AccountRepository(_db);
        _settings = new JsonSettingsStore(Path.Combine(_dir, "settings.json"));
        _account = new Account
        {
            DisplayName = "Ana", Email = "ana@example.com", Username = "ana@example.com", Auth = AuthKind.GoogleOAuth, Provider = ProviderKind.Gmail,
            ImapHost = "imap.gmail.com", SmtpHost = "smtp.gmail.com",
        };
        _account = _account with { Id = await _accounts.AddAsync(_account) };

        // A sign-in that's still fresh, so asking for a token needs no network; and an OAuth client file to go with it.
        var client = Path.Combine(_dir, "client.json");
        await File.WriteAllTextAsync(client, """{"installed":{"client_id":"id","client_secret":"secret","auth_uri":"https://accounts.google.com/o/oauth2/auth","token_uri":"https://oauth2.googleapis.com/token","redirect_uris":["http://localhost"]}}""");
        var now = DateTime.UtcNow.ToString("O");
        _auth = new GoogleAuth(new OneToken($$"""{"access_token":"token","token_type":"Bearer","expires_in":3600,"refresh_token":"refresh","Issued":"{{now}}","IssuedUtc":"{{now}}"}"""), client);
        _google = new FakeGoogle();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
        return Task.CompletedTask;
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 100 && !await condition(); i++) await Task.Delay(100);
        Assert.True(await condition(), "Not done in time");
    }

    [Fact]
    public async Task Calendar_edits_offline_are_shown_kept_and_sent_later()
    {
        var repo = new CalendarRepository(_db);
        var calId = await repo.UpsertCalendarAsync(new CalendarInfo
        {
            AccountId = _account.Id, RemoteId = "ana@example.com", Name = "Ana", AccessRole = "owner", IsPrimary = true, IsVisible = true,
        }, keepVisibility: false);
        var tomorrow = new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(12));
        await repo.UpsertEventAsync(new CalendarEvent { CalendarId = calId, RemoteId = "e1", Title = "Dentist", Start = tomorrow, End = tomorrow.AddHours(1) });
        var engine = new CalendarEngine(_accounts, repo, new GoogleCalendarApi(new HttpClient(_google), _auth), _settings, NullLogger<CalendarEngine>.Instance);
        _google.Offline = true;

        // Made offline: on screen straight away, under a stand-in id.
        await engine.CreateAsync(calId, new EventDraft { Title = "Lunch", Start = tomorrow.AddHours(1), End = tomorrow.AddHours(2) });
        var shown = await engine.GetEventsAsync(tomorrow.AddHours(-12), tomorrow.AddHours(12));
        var lunch = Assert.Single(shown, e => e.Title == "Lunch");
        Assert.StartsWith("pending-", lunch.RemoteId);
        Assert.Equal(CalendarSyncState.Error, engine.Statuses[_account.Id].State);

        // Edited again before it went: still one thing to send, the latest version.
        await engine.UpdateAsync(lunch, new EventDraft { Title = "Lunch with Bob", Start = lunch.Start, End = lunch.End }, wholeSeries: false);
        var dentist = shown.Single(e => e.Title == "Dentist");
        await engine.MoveAsync(dentist, dentist.Start.AddHours(3), dentist.End.AddHours(3), wholeSeries: false);
        Assert.Equal(2, (await repo.GetPendingChangesAsync()).Count);
        Assert.Contains(await engine.GetEventsAsync(tomorrow.AddHours(-12), tomorrow.AddHours(12)), e => e.Title == "Dentist" && e.Start == tomorrow.AddHours(3));

        // Back online: the next sync sends them, in order, before reading the calendar again.
        _google.Offline = false;
        engine.Start();
        await EventuallyAsync(async () => (await repo.GetPendingChangesAsync()).Count == 0
                                          && _google.Requests.Any(r => r.StartsWith("GET") && r.Contains("/events?")));
        await engine.DisposeAsync();
        var create = Assert.Single(_google.Requests, r => r.StartsWith("POST") && r.Contains("/events?"));
        Assert.Contains("Lunch with Bob", create);
        var move = Assert.Single(_google.Requests, r => r.StartsWith("PATCH") && r.Contains("/events/e1"));
        Assert.True(_google.Requests.IndexOf(create) < _google.Requests.IndexOf(move));
    }

    [Fact]
    public async Task A_calendar_event_made_and_deleted_offline_is_never_sent()
    {
        var repo = new CalendarRepository(_db);
        var calId = await repo.UpsertCalendarAsync(new CalendarInfo { AccountId = _account.Id, RemoteId = "ana@example.com", Name = "Ana", AccessRole = "owner", IsVisible = true }, false);
        var engine = new CalendarEngine(_accounts, repo, new GoogleCalendarApi(new HttpClient(_google), _auth), _settings, NullLogger<CalendarEngine>.Instance);
        _google.Offline = true;
        var at = new DateTimeOffset(DateTime.Today.AddDays(2).AddHours(9));
        await engine.CreateAsync(calId, new EventDraft { Title = "Maybe", Start = at, End = at.AddHours(1) });
        var maybe = Assert.Single(await engine.GetEventsAsync(at.AddHours(-1), at.AddHours(2)));
        await engine.DeleteAsync(maybe, wholeSeries: false);
        Assert.Empty(await engine.GetEventsAsync(at.AddHours(-1), at.AddHours(2)));
        Assert.Empty(await repo.GetPendingChangesAsync());
        await engine.DisposeAsync();
    }

    [Fact]
    public async Task Contact_edits_offline_are_kept_and_sent_later()
    {
        var repo = new ContactRepository(_db);
        var engine = new ContactsEngine(_accounts, repo, new GoogleContactsApi(new HttpClient(_google), _auth), _settings, NullLogger<ContactsEngine>.Instance);
        var known = await repo.UpsertAsync(new Contact
        {
            AccountId = _account.Id, Source = ContactSource.Google, RemoteId = "people/c1", Etag = "e", DisplayName = "Bob", GivenName = "Bob",
        });
        _google.Offline = true;

        var created = await engine.SaveAsync(new Contact { AccountId = _account.Id, Source = ContactSource.Google, DisplayName = "Cleo", GivenName = "Cleo" });
        Assert.True(created.Id > 0);
        Assert.Null(created.RemoteId);
        var bob = (await repo.GetAsync(known))!;
        await engine.SetFavoriteAsync(bob, true);
        Assert.True((await repo.GetAsync(known))!.IsFavorite);
        await engine.DeleteAsync(bob);
        Assert.Null(await repo.GetAsync(known));
        var waiting = await repo.GetPendingChangesAsync(_account.Id);
        Assert.Equal([ContactChangeKind.Save, ContactChangeKind.Delete], waiting.Select(w => w.Kind)); // Bob's star went with him

        _google.Offline = false;
        engine.Start();
        await EventuallyAsync(async () => (await repo.GetPendingChangesAsync(_account.Id)).Count == 0
                                          && _google.Requests.Any(r => r.StartsWith("GET") && r.Contains("connections")));
        await engine.DisposeAsync();
        Assert.Contains(_google.Requests, r => r.StartsWith("POST") && r.Contains("createContact") && r.Contains("Cleo"));
        Assert.Contains(_google.Requests, r => r.StartsWith("DELETE") && r.Contains("people/c1"));
    }

    /// <summary>Google, or no network at all. Answers just enough for the calls these tests make.</summary>
    private sealed class FakeGoogle : HttpMessageHandler
    {
        public volatile bool Offline;
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline) throw new HttpRequestException("No such host is known.");
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add($"{request.Method} {url} {body}");
            var json = url switch
            {
                _ when url.Contains("/calendarList") => """{"items":[{"id":"ana@example.com","summary":"Ana","accessRole":"owner","primary":true,"selected":true}]}""",
                _ when request.Method == HttpMethod.Get => """{"items":[],"connections":[],"otherContacts":[],"nextSyncToken":"t"}""",
                _ when url.Contains("createContact") => """{"resourceName":"people/new1","etag":"x","names":[{"displayName":"Cleo","givenName":"Cleo"}]}""",
                _ => "{}",
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class OneToken(string token) : ICredentialStore
    {
        public void Save(long accountId, string secret) { }
        public string? Get(long accountId) => token;
        public void Delete(long accountId) { }
        public void SaveSmtp(long accountId, string secret) { }
        public string? GetSmtp(long accountId) => null;
        public void DeleteSmtp(long accountId) { }
    }
}
