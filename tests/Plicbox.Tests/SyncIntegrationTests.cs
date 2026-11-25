using Plicbox.Core;
using Plicbox.Mail;
using Plicbox.Storage;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace Plicbox.Tests;

/// <summary>
/// End-to-end tests against a local GreenMail server (IMAPS 3993, SMTPS 3465, user alice/secret).
/// Skipped unless PLICBOX_GREENMAIL=1, so the normal test run needs no server.
/// </summary>
[Collection("GreenMail")] // one mailbox: tests using it take turns
public sealed class SyncIntegrationTests : IAsyncLifetime
{
    private const string Host = "127.0.0.1";
    private const string User = "alice";
    private const string Password = "secret";
    private const string Address = "alice@localhost";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-it", Guid.NewGuid().ToString("N"));
    private Database _db = null!;
    private MessageRepository _messages = null!;
    private FolderRepository _folders = null!;
    private MailEngine _service = null!;
    private Account _account = null!;
    private ImapClient _probe = null!; // independent client: "another device"

    public static bool Enabled => Environment.GetEnvironmentVariable("PLICBOX_GREENMAIL") == "1";

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        MailConnector.CertificateValidationOverride = (_, _, _, _) => true;

        _probe = await MailConnector.ConnectImapAsync(new ServerEndpoint(Host, 3993), new MailLogin(User, Password), default);
        await ResetMailboxAsync();

        _db = new Database(Path.Combine(_dir, "it.db"));
        _db.Migrate();
        _messages = new MessageRepository(_db);
        _folders = new FolderRepository(_db);
        var creds = new MemoryCredentials();
        _service = new MailEngine(new AccountRepository(_db), _folders, _messages, new PendingOpRepository(_db),
            new FileBodyStore(Path.Combine(_dir, "bodies")), creds, new MailConnector(creds), NullLoggerFactory.Instance);

        _account = new Account
        {
            DisplayName = "Alice", Email = Address, Username = User,
            ImapHost = Host, ImapPort = 3993, SmtpHost = Host, SmtpPort = 3465,
        };
    }

    public async Task DisposeAsync()
    {
        if (!Enabled) return;
        await _service.DisposeAsync();
        _probe.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [SkippableFact]
    public async Task Full_round_trip_sync_flags_move_delete_send()
    {
        Skip.IfNot(Enabled, "Set PLICBOX_GREENMAIL=1 with GreenMail running to run integration tests.");

        await DeliverAsync("Hello one", "first body");
        await DeliverAsync("Hello two", "second body with keyword pineapple");
        await DeliverAsync("Hello three", "third body");

        // 1. Initial sync pulls folders and headers.
        var synced = WaitForSync();
        _account = _account with { Id = await _service.AddAccountAsync(_account, Password) };
        await synced;
        var folders = await _folders.GetByAccountAsync(_account.Id);
        var inbox = folders.Single(f => f.Role == FolderRole.Inbox);
        var trash = folders.Single(f => f.Role == FolderRole.Trash);
        Assert.Contains(folders, f => f.Role == FolderRole.Sent);
        var list = await _messages.GetByFolderAsync(inbox.Id);
        Assert.Equal(3, list.Count);
        Assert.All(list, m => Assert.False(m.IsRead));
        Assert.Contains(list, m => m.Preview.Contains("pineapple"));

        // 2. Body download + search over the full body.
        var two = list.Single(m => m.Subject == "Hello two");
        Assert.True(await _service.EnsureBodyAsync(two));
        Assert.Single(await _messages.SearchAsync("pineapple"));

        // 3. Local flag change reaches the server.
        await _service.SetFlagsAsync(two, isRead: true, isFlagged: true);
        await EventuallyAsync(async () =>
        {
            var flags = await ServerFlagsAsync("INBOX", "Hello two");
            return flags.HasFlag(MessageFlags.Seen) && flags.HasFlag(MessageFlags.Flagged);
        });

        // 4. A change made on "another device" comes back on the next sync.
        var one = list.Single(m => m.Subject == "Hello one");
        await SetServerFlagAsync("INBOX", "Hello one", MessageFlags.Seen);
        synced = WaitForSync();
        _service.RequestSync(_account.Id, userInitiated: true);
        await synced;
        Assert.True((await _messages.GetAsync(one.Id))!.IsRead);

        // 4b. Copy to Trash: the server gets a second copy, the original stays in the inbox.
        Assert.True(await _service.CopyAsync(two, trash));
        await EventuallyAsync(async () => await ServerCountAsync("Trash") == 1);
        Assert.Equal(3, await ServerCountAsync("INBOX"));
        await ExpungeOnServerAsync("Trash", "Hello two"); // tidy up so the counts below stay as before

        // 5. Move to Trash: local placeholder first, real UID after the server confirms.
        var three = list.Single(m => m.Subject == "Hello three");
        await _service.MoveAsync(three, trash);
        await EventuallyAsync(async () => (await _messages.GetAsync(three.Id))?.Uid > 0);
        Assert.Equal(trash.Id, (await _messages.GetAsync(three.Id))!.FolderId);
        Assert.Equal(1, await ServerCountAsync("Trash"));
        Assert.Equal(2, await ServerCountAsync("INBOX"));

        // 6. Delete forever from Trash expunges it on the server.
        await _service.DeleteForeverAsync((await _messages.GetAsync(three.Id))!);
        await EventuallyAsync(async () => await ServerCountAsync("Trash") == 0);

        // 7. Expunged elsewhere -> disappears locally.
        await ExpungeOnServerAsync("INBOX", "Hello one");
        synced = WaitForSync();
        _service.RequestSync(_account.Id, userInitiated: true);
        await synced;
        Assert.Null(await _messages.GetAsync(one.Id));

        // 8. Send: through the Outbox, delivered by SMTP and a copy filed in Sent.
        var outgoing = Composing.ToMimeMessage(_account, new ComposeDraft { AccountId = _account.Id },
            Address, "", "", "Sent by Plicbox", "<p>Hi <b>me</b></p>", []);
        await _service.SendAsync(_account, outgoing);
        await EventuallyAsync(async () => await ServerCountAsync("Sent") == 1);
        await EventuallyAsync(async () => await ServerCountAsync("INBOX") == 2); // two left + this one - expunged one
        synced = WaitForSync();
        _service.RequestSync(_account.Id, userInitiated: true);
        await synced;
        var sent = folders.Single(f => f.Role == FolderRole.Sent);
        var sentCopy = Assert.Single(await _messages.GetByFolderAsync(sent.Id));
        Assert.True(sentCopy.IsRead);
        Assert.Contains(await _messages.GetByFolderAsync(inbox.Id), m => m.Subject == "Sent by Plicbox");

        // 9. New mail arrives on its own (IDLE push, or NOOP polling on servers without IDLE).
        Assert.True(_probe.Capabilities.HasFlag(ImapCapabilities.Idle), "GreenMail should advertise IDLE");
        await DeliverAsync("Pushed", "arrives without asking");
        await EventuallyAsync(async () => (await _messages.GetByFolderAsync(inbox.Id)).Any(m => m.Subject == "Pushed"));

        // 10. Drafts: saving again replaces the earlier copy; deleting removes every copy.
        var draft = Composing.ToMimeMessage(_account, new ComposeDraft(), "", "", "", "Unfinished", "<p>v1</p>", [], asDraft: true);
        Assert.True(await _service.SaveDraftAsync(_account.Id, draft));
        var second = Composing.ToMimeMessage(_account, new ComposeDraft { MessageId = draft.MessageId }, "bob@example.com", "", "",
            "Unfinished", "<p>v2</p>", [], asDraft: true);
        Assert.True(await _service.SaveDraftAsync(_account.Id, second));
        // Saved here at once, uploaded by the queue: the server ends up with one copy, the newest.
        await EventuallyAsync(async () =>
        {
            var drafts = await ProbeOpenAsync("Drafts");
            var uids = await drafts.SearchAsync(SearchQuery.All); // for a moment it may still be the first
            return uids.Count == 1 && (await drafts.GetMessageAsync(uids[0])).HtmlBody.Contains("v2");
        });
        Assert.True((await ServerFlagsAsync("Drafts", "Unfinished")).HasFlag(MessageFlags.Draft));
        await _service.DeleteDraftAsync(_account.Id, draft.MessageId!);
        await EventuallyAsync(async () => await ServerCountAsync("Drafts") == 0);

        // 11. Folders: create, rename, move under another, mark read, empty, delete.
        var created = await _service.CreateFolderAsync(_account.Id, null, "Projects");
        var parent = await _service.CreateFolderAsync(_account.Id, null, "Clients");
        Assert.NotNull(await _probe.GetFolderAsync(created));
        await EventuallyAsync(async () => (await _folders.GetByAccountAsync(_account.Id)).Any(f => f.Path == created));
        var projects = (await _folders.GetByAccountAsync(_account.Id)).Single(f => f.Path == created);
        var renamed = await _service.RenameFolderAsync(projects, "Projects 2026");
        Assert.EndsWith("Projects 2026", renamed);
        await EventuallyAsync(async () => (await _folders.GetByAccountAsync(_account.Id)).Any(f => f.Path == renamed));
        var moved = await _service.MoveFolderAsync((await _folders.GetByAccountAsync(_account.Id)).Single(f => f.Path == renamed), parent);
        Assert.StartsWith(parent, moved);

        synced = WaitForSync();
        _service.RequestSync(_account.Id, userInitiated: true);
        await synced;
        var unread = (await _messages.GetByFolderAsync(inbox.Id)).Count(m => !m.IsRead);
        Assert.True(unread > 0);
        await _service.MarkFolderReadAsync(inbox);
        Assert.All(await _messages.GetByFolderAsync(inbox.Id), m => Assert.True(m.IsRead));
        Assert.True((await ServerFlagsAsync("INBOX", "Pushed")).HasFlag(MessageFlags.Seen));

        await _service.EmptyFolderAsync(trash);
        Assert.Equal(0, await ServerCountAsync("Trash"));
        Assert.Empty(await _messages.GetByFolderAsync(trash.Id));

        await _service.DeleteFolderAsync((await _folders.GetByAccountAsync(_account.Id)).Single(f => f.Path == parent));
        await Assert.ThrowsAsync<FolderNotFoundException>(() => _probe.GetFolderAsync(moved));
    }

    [SkippableFact]
    public async Task Only_a_pass_that_changed_something_asks_for_a_refresh()
    {
        Skip.IfNot(Enabled, "Set PLICBOX_GREENMAIL=1 with GreenMail running to run integration tests.");

        await DeliverAsync("Hello one", "first body");
        await DeliverAsync("Hello two", "second body");
        var refreshes = 0;
        _service.MailChanged += _ => Interlocked.Increment(ref refreshes);
        _service.FolderSynced += (_, _) => Interlocked.Increment(ref refreshes);
        // One pass, and what it raised (MailChanged comes right after Synced).
        async Task<int> Pass()
        {
            Interlocked.Exchange(ref refreshes, 0);
            var synced = WaitForSync();
            _service.RequestSync(_account.Id, userInitiated: true);
            await synced;
            await Task.Delay(500);
            return Interlocked.Exchange(ref refreshes, 0);
        }

        var first = WaitForSync();
        _account = _account with { Id = await _service.AddAccountAsync(_account, Password) };
        await first;
        await Task.Delay(1500); // the inbox connection is up
        Assert.True(refreshes >= 2); // the inbox's mail, and the pass
        Assert.Equal(0, await Pass()); // nothing happened meanwhile
        Assert.Equal(0, await Pass());

        var inbox = (await _folders.GetByAccountAsync(_account.Id)).Single(f => f.Role == FolderRole.Inbox);
        async Task ShowsUp(Func<Task> elsewhere, Func<IReadOnlyList<Plicbox.Core.MessageSummary>, bool> shown)
        {
            Interlocked.Exchange(ref refreshes, 0);
            await elsewhere();
            await EventuallyAsync(async () =>
            {
                _service.RequestSync(_account.Id);
                return refreshes > 0 && shown(await _messages.GetByFolderAsync(inbox.Id));
            });
            await Task.Delay(1500); // passes asked for meanwhile
            Assert.Equal(0, await Pass());
        }
        await ShowsUp(() => SetServerFlagAsync("INBOX", "Hello one", MessageFlags.Seen), list => list.Single(m => m.Subject == "Hello one").IsRead);
        await ShowsUp(() => DeliverAsync("Hello three", "third body"), list => list.Count == 3);
        await ShowsUp(() => ExpungeOnServerAsync("INBOX", "Hello two"), list => list.Count == 2);
        // A change made here, once the server has it.
        var one = (await _messages.GetByFolderAsync(inbox.Id)).Single(m => m.Subject == "Hello one");
        await ShowsUp(() => _service.SetFlagsAsync(one, isRead: true, isFlagged: true), list => list.Single(m => m.Subject == "Hello one").IsFlagged);
    }

    // ───────── helpers ─────────

    private Task WaitForSync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(long id, int _) { _service.Synced -= Handler; tcs.TrySetResult(); }
        _service.Synced += Handler;
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(200);
        }
    }

    private async Task ResetMailboxAsync()
    {
        // Empty rather than delete folders: GreenMail drops connections when a folder open elsewhere is deleted.
        var root = _probe.GetFolder(_probe.PersonalNamespaces[0]);
        // Folders the folder-management step creates, left over from an earlier run.
        async Task DeleteTree(IMailFolder f)
        {
            foreach (var child in await f.GetSubfoldersAsync(false)) await DeleteTree(child);
            await f.DeleteAsync();
        }
        foreach (var name in new[] { "Clients", "Projects", "Projects 2026" })
        {
            try { await DeleteTree(await _probe.GetFolderAsync(name)); }
            catch (FolderNotFoundException) { }
        }
        foreach (var name in new[] { "Trash", "Sent", "Drafts" })
        {
            try { await _probe.GetFolderAsync(name); }
            catch (FolderNotFoundException) { await root.CreateAsync(name, true); }
        }
        foreach (var name in new[] { "INBOX", "Trash", "Sent", "Drafts" })
        {
            var folder = await ProbeOpenAsync(name);
            if (folder.Count == 0) continue;
            await folder.AddFlagsAsync(UniqueIdRange.All, MessageFlags.Deleted, true);
            await folder.ExpungeAsync();
        }
    }

    private static async Task DeliverAsync(string subject, string body)
    {
        var msg = new MimeMessage { Subject = subject };
        msg.From.Add(MailboxAddress.Parse("bob@example.com"));
        msg.To.Add(MailboxAddress.Parse(Address));
        msg.Body = new TextPart("plain") { Text = body };
        using var smtp = await MailConnector.ConnectSmtpAsync(new ServerEndpoint(Host, 3465), null, default);
        await smtp.SendAsync(msg);
        await smtp.DisconnectAsync(true);
    }

    private async Task<IMailFolder> ProbeOpenAsync(string path)
    {
        var folder = await _probe.GetFolderAsync(path);
        await folder.OpenAsync(FolderAccess.ReadWrite);
        return folder;
    }

    private async Task<UniqueId> FindAsync(IMailFolder folder, string subject) =>
        (await folder.SearchAsync(SearchQuery.SubjectContains(subject))).Single();

    private async Task<MessageFlags> ServerFlagsAsync(string path, string subject)
    {
        var folder = await ProbeOpenAsync(path);
        var uid = await FindAsync(folder, subject);
        return (await folder.FetchAsync([uid], MessageSummaryItems.Flags)).Single().Flags!.Value;
    }

    private async Task SetServerFlagAsync(string path, string subject, MessageFlags flag)
    {
        var folder = await ProbeOpenAsync(path);
        await folder.AddFlagsAsync(await FindAsync(folder, subject), flag, true);
    }

    private async Task ExpungeOnServerAsync(string path, string subject)
    {
        var folder = await ProbeOpenAsync(path);
        await folder.AddFlagsAsync(await FindAsync(folder, subject), MessageFlags.Deleted, true);
        await folder.ExpungeAsync();
    }

    private async Task<int> ServerCountAsync(string path)
    {
        var folder = await _probe.GetFolderAsync(path);
        await folder.StatusAsync(StatusItems.Count);
        return folder.Count;
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        private readonly Dictionary<long, string> _secrets = [];
        public void Save(long accountId, string secret) => _secrets[accountId] = secret;
        public string? Get(long accountId) => _secrets.GetValueOrDefault(accountId);
        public void Delete(long accountId) => _secrets.Remove(accountId);
        public void SaveSmtp(long accountId, string secret) => _secrets[-accountId] = secret;
        public string? GetSmtp(long accountId) => _secrets.GetValueOrDefault(-accountId);
        public void DeleteSmtp(long accountId) => _secrets.Remove(-accountId);
    }
}
