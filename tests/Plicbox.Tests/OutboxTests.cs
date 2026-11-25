using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Plicbox.Core;
using Plicbox.Mail;
using Plicbox.Storage;

namespace Plicbox.Tests;

/// <summary>
/// Sending and drafts work offline: a sent message waits in the Outbox, a draft is saved here, and both go to the
/// server through the queue. No server here (sending to a closed port), so what's checked is what's kept and queued.
/// </summary>
public sealed class OutboxTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-outbox", Guid.NewGuid().ToString("N"));
    private Database _db = null!;
    private MessageRepository _messages = null!;
    private FolderRepository _folders = null!;
    private PendingOpRepository _ops = null!;
    private FileBodyStore _bodies = null!;
    private MailEngine _engine = null!;
    private Account _account = null!;
    private Folder _drafts = null!, _sent = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _db = new Database(Path.Combine(_dir, "t.db"));
        _db.Migrate();
        (_messages, _folders, _ops, _bodies) = (new(_db), new(_db), new(_db), new(Path.Combine(_dir, "bodies")));
        var accounts = new AccountRepository(_db);
        var creds = new NoCredentials();
        _engine = new MailEngine(accounts, _folders, _messages, _ops, _bodies, creds, new MailConnector(creds), NullLoggerFactory.Instance);

        _account = new Account
        {
            DisplayName = "Ana", Email = "ana@example.com", Username = "ana",
            ImapHost = "127.0.0.1", ImapPort = 1, SmtpHost = "127.0.0.1", SmtpPort = 1, // nothing listens: "offline"
        };
        _account = _account with { Id = await accounts.AddAsync(_account) };
        await _folders.UpsertAsync(new Folder { AccountId = _account.Id, Path = "Drafts", Name = "Drafts", Role = FolderRole.Drafts });
        await _folders.UpsertAsync(new Folder { AccountId = _account.Id, Path = "Sent", Name = "Sent", Role = FolderRole.Sent });
        var all = await _folders.GetByAccountAsync(_account.Id);
        (_drafts, _sent) = (all.Single(f => f.Role == FolderRole.Drafts), all.Single(f => f.Role == FolderRole.Sent));
    }

    public async Task DisposeAsync()
    {
        await _engine.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private MimeMessage Message(string subject, string body, string? messageId = null, bool asDraft = false) =>
        Composing.ToMimeMessage(_account, new ComposeDraft { AccountId = _account.Id, MessageId = messageId },
            "bob@example.com", "", "", subject, $"<p>{body}</p>", [], asDraft: asDraft);

    private string? BodyOf(long id)
    {
        using var stream = _bodies.Open(id);
        return stream is null ? null : MimeMessage.Load(stream).HtmlBody;
    }

    [Fact]
    public async Task A_draft_is_saved_here_at_once_and_queued_for_the_server()
    {
        var v1 = Message("Plans", "v1", asDraft: true);
        Assert.True(await _engine.SaveDraftAsync(_account.Id, v1));
        var v2 = Message("Plans", "v2", v1.MessageId, asDraft: true);
        Assert.True(await _engine.SaveDraftAsync(_account.Id, v2));

        // One copy, the newest, readable and searchable before any server has it.
        var draft = Assert.Single(await _messages.GetByFolderAsync(_drafts.Id));
        Assert.Equal(-draft.Id, draft.Uid);
        Assert.Contains("v2", BodyOf(draft.Id));
        Assert.Single(await _messages.SearchAsync("Plans"));

        // Only the newest copy's upload is left (the first one's went with it).
        var upload = Assert.Single(await _ops.GetByAccountAsync(_account.Id));
        Assert.Equal(draft.Id, upload.MessageId);
        Assert.Equal(PendingOpKind.SaveDraft, upload.Kind);
        Assert.Equal("Drafts", upload.FolderPath);
        Assert.Equal(v1.MessageId, upload.HeaderMessageId);

        // Discarded: gone here straight away, and every copy on the server goes too once it can be reached.
        await _engine.DeleteDraftAsync(_account.Id, v1.MessageId!);
        Assert.Empty(await _messages.GetByFolderAsync(_drafts.Id));
        Assert.Null(BodyOf(draft.Id));
        Assert.Contains(await _ops.GetByAccountAsync(_account.Id), o => o.Kind == PendingOpKind.DeleteDraft && o.HeaderMessageId == v1.MessageId);
    }

    [Fact]
    public async Task A_draft_saved_while_its_earlier_save_uploads_stays_queued()
    {
        var v1 = Message("Plans", "v1", asDraft: true);
        await _engine.SaveDraftAsync(_account.Id, v1);
        var uploading = Assert.Single(await _ops.GetByAccountAsync(_account.Id)); // the worker has picked it up
        await _engine.SaveDraftAsync(_account.Id, Message("Plans", "v2", v1.MessageId, asDraft: true));
        await _ops.RemoveAsync(uploading.Id); // and is done with it

        var draft = Assert.Single(await _messages.GetByFolderAsync(_drafts.Id));
        Assert.Equal(draft.Id, Assert.Single(await _ops.GetByAccountAsync(_account.Id)).MessageId);
    }

    [Fact]
    public async Task A_message_kept_here_shows_only_once_its_content_is_saved()
    {
        // The Outbox looks at any time: a message without its content yet would be dropped as empty, and never sent.
        MessageSummary New(string subject) => new() { AccountId = _account.Id, FolderId = _drafts.Id, Subject = subject };
        var id = await _messages.AddLocalAsync(New("a"), saveContent: async id =>
        {
            Assert.Empty(await _messages.GetByFolderAsync(_drafts.Id));
            await _bodies.SaveAsync(id, new MemoryStream("content"u8.ToArray()));
        });
        Assert.Equal(id, Assert.Single(await _messages.GetByFolderAsync(_drafts.Id)).Id);
        using (var content = _bodies.Open(id)) Assert.NotNull(content);

        // Content that can't be saved (a full disk): no message either, and the sender hears of it.
        await Assert.ThrowsAsync<IOException>(() => _messages.AddLocalAsync(New("b"), saveContent: _ => throw new IOException("full")));
        Assert.Single(await _messages.GetByFolderAsync(_drafts.Id));
    }

    [Fact]
    public async Task Sending_offline_keeps_the_message_in_the_Outbox()
    {
        await _engine.SendAsync(_account, Message("Hello", "sent while offline"));
        var outbox = Assert.Single(await _folders.GetByAccountAsync(_account.Id), f => f.Role == FolderRole.Outbox);
        Assert.False(outbox.IsSynced);

        // It tries straight away; with nothing listening the message stays put.
        await Task.Delay(1500);
        var waiting = Assert.Single(await _messages.GetByFolderAsync(outbox.Id));
        Assert.True(waiting.IsRead);
        Assert.Contains("sent while offline", BodyOf(waiting.Id));
        Assert.Equal(1, await _engine.CountOutboxAsync());

        // A refreshed folder list (the server has no Outbox) leaves it alone.
        await _folders.DeleteMissingAsync(_account.Id, ["Drafts", "Sent"]);
        Assert.Single(await _messages.GetByFolderAsync(outbox.Id));

        // Read/flag changes stay here: nothing is queued for a server that doesn't have it.
        await _engine.SetFlagsAsync(waiting, isRead: true, isFlagged: true);
        Assert.Empty(await _ops.GetByAccountAsync(_account.Id));
    }

    [Fact]
    public async Task Out_of_the_Outbox_only_into_Drafts_and_nothing_moves_in()
    {
        await _engine.SendAsync(_account, Message("Second thoughts", "not yet"));
        var outbox = (await _folders.GetByAccountAsync(_account.Id)).Single(f => f.Role == FolderRole.Outbox);
        var waiting = Assert.Single(await _messages.GetByFolderAsync(outbox.Id));
        // While it's going out it can't be taken back; once the try failed (Windows takes a couple of seconds to give
        // up on a closed port), it can.
        for (var i = 0; i < 100 && (_engine.IsBusy(waiting.Id) || i < 3); i++) await Task.Delay(100);
        Assert.False(_engine.IsBusy(waiting.Id));

        Assert.False(await _engine.MoveAsync(waiting, _sent));
        Assert.True(await _engine.MoveAsync(waiting, _drafts));
        var draft = Assert.Single(await _messages.GetByFolderAsync(_drafts.Id));
        Assert.Equal(waiting.Id, draft.Id);
        Assert.Contains(await _ops.GetByAccountAsync(_account.Id), o => o.Kind == PendingOpKind.SaveDraft && o.MessageId == draft.Id);

        Assert.False(await _engine.MoveAsync(draft, outbox));
    }

    [Fact]
    public async Task Send_later_waits_in_the_Outbox_until_its_time_and_can_be_taken_back()
    {
        (long Id, DateTimeOffset At)? queued = null;
        _engine.Queued += (_, id, _, at) => queued = (id, at);
        var at = DateTimeOffset.Now.AddHours(3);
        var id = await _engine.SendAsync(_account, Message("Tomorrow's agenda", "for later"), at);
        Assert.Equal(id, queued?.Id);

        // Not tried at all before its time: nothing being sent, listed at the time it goes.
        await Task.Delay(500);
        Assert.False(_engine.IsBusy(id));
        var waiting = (await _messages.GetAsync(id))!;
        Assert.Equal(at.ToUnixTimeSeconds(), waiting.Date.ToUnixTimeSeconds());

        // Undo: back to Drafts, to edit.
        Assert.True(await _engine.MoveAsync(waiting, _drafts));
        Assert.Equal(_drafts.Id, (await _messages.GetAsync(id))!.FolderId);

        // Send now: tried straight away (no server here, so it stays).
        var second = await _engine.SendAsync(_account, Message("Second", "also later"), DateTimeOffset.Now.AddDays(1));
        await _engine.SendNowAsync((await _messages.GetAsync(second))!);
        Assert.True((await _messages.GetAsync(second))!.Date <= DateTimeOffset.Now);
        for (var i = 0; i < 30 && !_engine.IsBusy(second); i++) await Task.Delay(50);
        Assert.True(_engine.IsBusy(second));
    }

    [Fact]
    public async Task Local_messages_each_get_their_own_placeholder_uid()
    {
        var a = await _messages.AddLocalAsync(new MessageSummary { AccountId = _account.Id, FolderId = _drafts.Id, Subject = "a" });
        var b = await _messages.AddLocalAsync(new MessageSummary { AccountId = _account.Id, FolderId = _drafts.Id, Subject = "b" });
        Assert.Equal(-a, (await _messages.GetAsync(a))!.Uid);
        Assert.Equal(-b, (await _messages.GetAsync(b))!.Uid);
        Assert.Empty(await _messages.GetUidsAsync(_drafts.Id)); // sync never mistakes them for server mail
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
