using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Plicbox.Core;
using Plicbox.Mail;
using Plicbox.Storage;

namespace Plicbox.Tests;

/// <summary>
/// New mail open in the reading pane isn't pulled away by a rule while it's being read: it stays in the inbox and goes
/// through the rules once the pane moves on.
/// </summary>
public sealed class HeldRuleTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-held", Guid.NewGuid().ToString("N"));
    private Database _db = null!;
    private MessageRepository _messages = null!;
    private MailEngine _engine = null!;
    private Folder _inbox = null!, _family = null!, _work = null!;
    private MailRule _rule = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _db = new Database(Path.Combine(_dir, "t.db"));
        _db.Migrate();
        _messages = new MessageRepository(_db);
        var folders = new FolderRepository(_db);
        var accounts = new AccountRepository(_db);
        var account = new Account { DisplayName = "Ana", Email = "ana@example.com", Username = "ana", ImapHost = "127.0.0.1", ImapPort = 1, SmtpHost = "127.0.0.1", SmtpPort = 1 };
        account = account with { Id = await accounts.AddAsync(account) };
        await folders.UpsertAsync(new Folder { AccountId = account.Id, Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox });
        await folders.UpsertAsync(new Folder { AccountId = account.Id, Path = "Family", Name = "Family" });
        await folders.UpsertAsync(new Folder { AccountId = account.Id, Path = "Work", Name = "Work" });
        var all = await folders.GetByAccountAsync(account.Id);
        (_inbox, _family, _work) = (all.Single(f => f.Role == FolderRole.Inbox), all.Single(f => f.Path == "Family"), all.Single(f => f.Path == "Work"));

        _rule = new MailRule
        {
            Conditions = [new RuleCondition { Field = RuleField.From, Operator = RuleOperator.Contains, Values = ["family.example"] }],
            Actions = [new RuleAction { Kind = RuleActionKind.MoveTo, FolderAccountId = account.Id, FolderPath = "Family" }],
        };
        var creds = new NoCredentials();
        _engine = new MailEngine(accounts, folders, _messages, new PendingOpRepository(_db), new FileBodyStore(Path.Combine(_dir, "bodies")),
            creds, new MailConnector(creds), NullLoggerFactory.Instance, new Settings(new AppSettings { Rules = [_rule] }));
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<MessageSummary> ArriveAsync(long uid)
    {
        await _messages.UpsertAsync(new MessageSummary
        {
            AccountId = _inbox.AccountId, FolderId = _inbox.Id, Uid = uid, Subject = "Weekend", FromAddress = "mum@family.example", Date = DateTimeOffset.Now,
        });
        return (await _messages.GetByUidsAsync(_inbox.Id, [uid])).Single();
    }

    [Fact]
    public async Task Mail_being_read_is_filed_only_after_the_pane_leaves_it()
    {
        var reading = await ArriveAsync(5);
        var other = await ArriveAsync(6);
        _engine.SetInView(reading.Id);

        var movedOut = await _engine.FileNewMailAsync([(_inbox.Id, [5L, 6L])], [_rule]);

        Assert.Equal(1, movedOut); // the other one went at once
        Assert.Equal(_family.Id, (await _messages.GetAsync(other.Id))!.FolderId);
        Assert.Equal(_inbox.Id, (await _messages.GetAsync(reading.Id))!.FolderId);

        var synced = new TaskCompletionSource();
        _engine.Synced += (_, _) => synced.TrySetResult();
        _engine.SetInView(other.Id); // on to another message
        await synced.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(_family.Id, (await _messages.GetAsync(reading.Id))!.FolderId);
    }

    [Fact]
    public async Task Mail_moved_by_hand_while_read_is_left_alone()
    {
        var reading = await ArriveAsync(7);
        _engine.SetInView(reading.Id);
        await _engine.FileNewMailAsync([(_inbox.Id, [7L])], [_rule]);

        await _messages.MoveAsync(reading.Id, _work.Id); // put away by hand while open
        _engine.SetInView(null);
        await Task.Delay(300);
        Assert.Equal(_work.Id, (await _messages.GetAsync(reading.Id))!.FolderId);
    }

    private sealed class Settings(AppSettings current) : ISettingsStore
    {
        public AppSettings Current { get; private set; } = current;
        public void Save(AppSettings settings) => Current = settings;
        public event Action<AppSettings>? Changed { add { } remove { } }
    }

    private sealed class NoCredentials : ICredentialStore
    {
        public void Save(long accountId, string secret) { }
        public string? Get(long accountId) => "secret";
        public void Delete(long accountId) { }
        public void SaveSmtp(long accountId, string secret) { }
        public string? GetSmtp(long accountId) => null;
        public void DeleteSmtp(long accountId) { }
    }
}
