using Plicbox.Core;
using Plicbox.Storage;

namespace Plicbox.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "plicbox-tests", Guid.NewGuid().ToString("N"));
    private readonly Database _db;
    private readonly MessageRepository _messages;

    public StorageTests()
    {
        _db = new Database(Path.Combine(_dir, "test.db"));
        _db.Migrate();
        _messages = new MessageRepository(_db);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Migrate_is_idempotent() => _db.Migrate();

    [Fact]
    public async Task A_folders_sync_state_keeps_the_servers_message_count()
    {
        var (inbox, _) = await SeedAccountAsync();
        var folders = new FolderRepository(_db);
        Assert.Equal(-1, (await folders.GetByAccountAsync(inbox.AccountId)).Single(f => f.Id == inbox.Id).ServerCount); // not known yet: never skipped

        await folders.UpdateSyncStateAsync(inbox.Id, uidValidity: 7, lastUid: 46236, highestModSeq: 991, serverCount: 46236);
        var saved = (await folders.GetByAccountAsync(inbox.AccountId)).Single(f => f.Id == inbox.Id);
        Assert.Equal((7, 46236, 991, 46236), (saved.UidValidity, saved.LastUid, saved.HighestModSeq, saved.ServerCount));
    }

    [Fact]
    public async Task Upsert_then_read_back_by_folder_and_unified_inbox()
    {
        var (inbox, _) = await SeedAccountAsync();
        await _messages.UpsertAsync(Msg(inbox, 1, "Hello", minutesAgo: 10));
        await _messages.UpsertAsync(Msg(inbox, 2, "Newer", minutesAgo: 1));

        var list = await _messages.GetByFolderAsync(inbox.Id);
        Assert.Equal(["Newer", "Hello"], list.Select(m => m.Subject));
        Assert.Equal(2, (await _messages.GetUnifiedInboxAsync()).Count);
    }

    [Fact]
    public async Task Upsert_existing_uid_updates_flags_without_duplicating()
    {
        var (inbox, _) = await SeedAccountAsync();
        var id1 = await _messages.UpsertAsync(Msg(inbox, 7, "Same"));
        var id2 = await _messages.UpsertAsync(Msg(inbox, 7, "Same") with { IsRead = true });

        Assert.Equal(id1, id2);
        var only = Assert.Single(await _messages.GetByFolderAsync(inbox.Id));
        Assert.True(only.IsRead);
    }

    [Fact]
    public async Task Search_matches_body_words_by_prefix_and_survives_quotes()
    {
        var (inbox, _) = await SeedAccountAsync();
        await _messages.UpsertAsync(Msg(inbox, 1, "Invoice"), bodyText: "Your boarding pass for Friday");
        await _messages.UpsertAsync(Msg(inbox, 2, "Other"), bodyText: "nothing relevant");

        Assert.Equal("Invoice", Assert.Single(await _messages.SearchAsync("board")).Subject);
        Assert.Empty(await _messages.SearchAsync("\"\" \""));
        Assert.Single(await _messages.SearchAsync("fri\"day"));
    }

    [Fact]
    public async Task List_pages_cover_everything_once_newest_first_even_with_equal_timestamps()
    {
        var (inbox, _) = await SeedAccountAsync();
        var same = DateTimeOffset.UtcNow.AddHours(-3);
        for (var i = 1; i <= 7; i++)
            await _messages.UpsertAsync(Msg(inbox, i, $"m{i}", minutesAgo: i * 10) with { Date = i is 4 or 5 ? same : DateTimeOffset.UtcNow.AddMinutes(-i * 10) });

        async Task<List<string>> AllPages(Func<int, Task<IReadOnlyList<MessageSummary>>> page)
        {
            var seen = new List<MessageSummary>();
            while (true)
            {
                var batch = await page(seen.Count);
                seen.AddRange(batch);
                if (batch.Count < 3) return seen.Select(m => m.Subject).ToList();
            }
        }

        var whole = (await _messages.GetByFolderAsync(inbox.Id)).Select(m => m.Subject).ToList();
        Assert.Equal(7, whole.Count);
        Assert.Equal(whole, await AllPages(offset => _messages.GetByFolderAsync(inbox.Id, 3, offset)));
        Assert.Equal(whole, await AllPages(offset => _messages.GetUnifiedInboxAsync(3, offset)));
        Assert.Equal(["m1", "m2", "m3"], whole.Take(3)); // newest first
        var bySender = new ListSort(ListSortKey.Sender, Descending: false);
        Assert.Equal((await _messages.GetByFolderAsync(inbox.Id, sort: bySender)).Select(m => m.Subject),
            await AllPages(offset => _messages.GetByFolderAsync(inbox.Id, 3, offset, bySender)));
    }

    [Fact]
    public async Task The_database_sorts_like_the_message_list()
    {
        var (inbox, _) = await SeedAccountAsync();
        await _messages.UpsertAsync(Msg(inbox, 1, "Re: banana", minutesAgo: 1) with { FromName = "", FromAddress = "zed@example.com", IsRead = true });
        await _messages.UpsertAsync(Msg(inbox, 2, "apple", minutesAgo: 2) with { FromName = "Ösel", HasAttachments = true });
        await _messages.UpsertAsync(Msg(inbox, 3, "Fwd: Cherry", minutesAgo: 3) with { FromName = "anna", IsFlagged = true, IsRead = true });
        await _messages.UpsertAsync(Msg(inbox, 4, "AW: date", minutesAgo: 4) with { FromName = "Bob", IsRead = true });

        async Task<string[]> Subjects(ListSortKey key, bool descending) =>
            (await _messages.GetByFolderAsync(inbox.Id, sort: new ListSort(key, descending))).Select(m => m.Subject).ToArray();

        Assert.Equal(["Re: banana", "apple", "Fwd: Cherry", "AW: date"], await Subjects(ListSortKey.Date, true));
        Assert.Equal(["AW: date", "Fwd: Cherry", "apple", "Re: banana"], await Subjects(ListSortKey.Date, false));
        // Names as shown (the address when there's none), by the user's language, ignoring case.
        Assert.Equal(["Fwd: Cherry", "AW: date", "apple", "Re: banana"], await Subjects(ListSortKey.Sender, false));
        // Subjects without their Re:/Fwd:/AW:.
        Assert.Equal(["apple", "Re: banana", "Fwd: Cherry", "AW: date"], await Subjects(ListSortKey.Subject, false));
        Assert.Equal("Fwd: Cherry", (await Subjects(ListSortKey.Flag, true))[0]);
        Assert.Equal("apple", (await Subjects(ListSortKey.ReadStatus, true))[0]); // unread first
        Assert.Equal("apple", (await Subjects(ListSortKey.Attachments, true))[0]);
    }

    [Fact]
    public void Older_mail_is_grouped_by_month_this_year_then_by_year()
    {
        var today = new DateTime(2026, 10, 16); // a Friday
        Assert.Equal("Today", MessageDates.Group(today, today));
        Assert.Equal("Yesterday", MessageDates.Group(today.AddDays(-1), today));
        Assert.Equal("Last Week", MessageDates.Group(new DateTime(2026, 10, 7), today));
        Assert.Equal("Earlier This Month", MessageDates.Group(new DateTime(2026, 10, 2), today));
        Assert.Equal("Last Month", MessageDates.Group(new DateTime(2026, 9, 3), today));
        Assert.Equal(new DateTime(2026, 8, 1).ToString("MMMM", System.Globalization.CultureInfo.CurrentCulture),
            MessageDates.Group(new DateTime(2026, 8, 20), today));
        Assert.Equal("2025", MessageDates.Group(new DateTime(2025, 12, 31), today));
        Assert.Equal("2013", MessageDates.Group(new DateTime(2013, 12, 16), today));
    }

    [Fact]
    public async Task Search_scope_narrows_to_an_account_a_folder_or_leaves_out_trash()
    {
        var (inbox, trash) = await SeedAccountAsync();
        await _messages.UpsertAsync(Msg(inbox, 1, "Invoice March"));
        await _messages.UpsertAsync(Msg(trash, 2, "Invoice April"));

        Assert.Equal(2, (await _messages.SearchAsync("invoice")).Count); // everywhere by default
        Assert.Equal("Invoice March", Assert.Single(await _messages.SearchAsync("invoice", new SearchScope(IncludeTrashAndJunk: false))).Subject);
        Assert.Equal("Invoice April", Assert.Single(await _messages.SearchAsync("invoice", new SearchScope(FolderId: trash.Id, IncludeTrashAndJunk: false))).Subject);
        Assert.Equal(2, (await _messages.SearchAsync("invoice", new SearchScope(AccountId: inbox.AccountId))).Count);
        Assert.Empty(await _messages.SearchAsync("invoice", new SearchScope(AccountId: inbox.AccountId + 1)));
    }

    [Fact]
    public async Task Counts_track_unread_and_flagged_and_ignore_trash()
    {
        var (inbox, trash) = await SeedAccountAsync();
        var a = await _messages.UpsertAsync(Msg(inbox, 1, "a"));
        await _messages.UpsertAsync(Msg(inbox, 2, "b") with { IsFlagged = true });
        await _messages.UpsertAsync(Msg(trash, 3, "binned"));

        var counts = await _messages.GetCountsAsync();
        Assert.Equal(2, counts.UnreadInboxes);
        Assert.Equal(2, counts.UnreadTotal);
        Assert.Equal(1, counts.Flagged);
        Assert.Equal(1, counts.UnreadByFolder[trash.Id]);

        await _messages.SetFlagsAsync(a, isRead: true, isFlagged: false);
        Assert.Equal(1, (await _messages.GetCountsAsync()).UnreadInboxes);
    }

    [Fact]
    public async Task Account_unread_counts_mail_filed_away_but_not_trash()
    {
        var (inbox, trash) = await SeedAccountAsync();
        var github = new Folder { AccountId = inbox.AccountId, Path = "Github", Name = "Github", Role = FolderRole.Other };
        github = github with { Id = await new FolderRepository(_db).UpsertAsync(github) };
        await _messages.UpsertAsync(Msg(github, 1, "filed by a rule"));
        await _messages.UpsertAsync(Msg(trash, 2, "binned"));

        var counts = await _messages.GetCountsAsync();
        Assert.Equal(0, counts.UnreadInboxes);
        Assert.Equal(1, counts.UnreadByAccount[inbox.AccountId]);
    }

    [Fact]
    public async Task Mark_folder_read_touches_only_that_folder()
    {
        var (inbox, trash) = await SeedAccountAsync();
        await _messages.UpsertAsync(Msg(inbox, 1, "a"));
        await _messages.UpsertAsync(Msg(inbox, 2, "b"));
        await _messages.UpsertAsync(Msg(trash, 3, "binned"));

        Assert.Equal(2, await _messages.MarkFolderReadAsync(inbox.Id));
        var counts = await _messages.GetCountsAsync();
        Assert.Equal(0, counts.UnreadByFolder.GetValueOrDefault(inbox.Id));
        Assert.Equal(1, counts.UnreadByFolder[trash.Id]);
        Assert.Equal(0, await _messages.MarkFolderReadAsync(inbox.Id));
    }

    [Fact]
    public async Task Move_and_delete_keep_search_index_consistent()
    {
        var (inbox, trash) = await SeedAccountAsync();
        var id = await _messages.UpsertAsync(Msg(inbox, 1, "Quarterly report"));

        await _messages.MoveAsync(id, trash.Id);
        Assert.Empty(await _messages.GetByFolderAsync(inbox.Id));
        Assert.Equal(-id, Assert.Single(await _messages.GetByFolderAsync(trash.Id)).Uid);

        await _messages.DeleteAsync(id);
        Assert.Empty(await _messages.SearchAsync("quarterly"));
    }

    [Fact]
    public async Task Conversations_span_folders_and_count_label_copies_once()
    {
        var (inbox, trash) = await SeedAccountAsync();
        var sent = new Folder { AccountId = inbox.AccountId, Path = "Sent", Name = "Sent", Role = FolderRole.Sent };
        sent = sent with { Id = await new FolderRepository(_db).UpsertAsync(sent) };
        var label = new Folder { AccountId = inbox.AccountId, Path = "Priority", Name = "Priority", Role = FolderRole.Other };
        label = label with { Id = await new FolderRepository(_db).UpsertAsync(label) };

        await _messages.UpsertAsync(Msg(inbox, 1, "Plans", minutesAgo: 30) with { ThreadKey = "t", MessageId = "a@x" });
        await _messages.UpsertAsync(Msg(sent, 1, "Re: Plans", minutesAgo: 20) with { ThreadKey = "t", MessageId = "b@x" });
        await _messages.UpsertAsync(Msg(inbox, 2, "Re: Plans", minutesAgo: 10) with { ThreadKey = "t", MessageId = "c@x" });
        await _messages.UpsertAsync(Msg(label, 7, "Re: Plans", minutesAgo: 10) with { ThreadKey = "t", MessageId = "c@x" }); // Gmail label copy
        await _messages.UpsertAsync(Msg(trash, 3, "Re: Plans", minutesAgo: 5) with { ThreadKey = "t", MessageId = "d@x" });   // binned: not shown

        var thread = await _messages.GetThreadAsync(inbox.AccountId, "t");
        Assert.Equal(["c@x", "b@x", "a@x"], thread.Select(m => m.MessageId));
        Assert.Equal(inbox.Id, thread[0].FolderId); // the Inbox copy wins over the label copy
        Assert.Equal(3, (await _messages.GetThreadCountsAsync(["t"]))[(inbox.AccountId, "t")]);
    }

    [Fact]
    public async Task A_message_can_be_replaced_or_deleted_while_its_being_read()
    {
        var store = new FileBodyStore(Path.Combine(_dir, "bodies"));
        static Stream Of(string text) => new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        await store.SaveAsync(1, Of("first"));

        using var reading = new StreamReader(store.Open(1)!);
        await store.SaveAsync(1, Of("second"));
        store.Delete(1);
        Assert.Null(store.Open(1));
        Assert.Equal("first", await reading.ReadToEndAsync()); // what was opened
        await store.SaveAsync(1, Of("third")); // the id is free again
        using var again = new StreamReader(store.Open(1)!);
        Assert.Equal("third", await again.ReadToEndAsync());
    }

    [Fact]
    public async Task Body_store_round_trips()
    {
        var store = new FileBodyStore(Path.Combine(_dir, "bodies"));
        await store.SaveAsync(1234, new MemoryStream("raw"u8.ToArray()));
        using (var reader = new StreamReader(store.Open(1234)!))
            Assert.Equal("raw", await reader.ReadToEndAsync());
        store.Delete(1234);
        Assert.Null(store.Open(1234));
    }

    [Fact]
    public async Task Body_store_drops_a_body_zeroed_by_a_crash()
    {
        var store = new FileBodyStore(Path.Combine(_dir, "bodies"));
        await store.SaveAsync(1234, new MemoryStream(new byte[4096]));
        Assert.Null(store.Open(1234));
        Assert.False(File.Exists(Path.Combine(_dir, "bodies", "1", "1234.eml")));
        await store.SaveAsync(1235, new MemoryStream());
        Assert.Null(store.Open(1235));
    }

    [Fact]
    public async Task Account_keeps_its_outgoing_sign_in()
    {
        var repo = new AccountRepository(_db);
        var id = await repo.AddAsync(new Account
        {
            DisplayName = "Test", Email = "t@example.com", Username = "t@example.com", ImapHost = "imap", SmtpHost = "smtp",
            SmtpSecurity = TlsMode.StartTls, SmtpAuth = SmtpAuthMode.Separate, SmtpUsername = "relay-user",
        });
        var saved = (await repo.GetAsync(id))!;
        Assert.Equal((TlsMode.StartTls, SmtpAuthMode.Separate, "relay-user"), (saved.SmtpSecurity, saved.SmtpAuth, saved.SmtpUsername));

        await repo.UpdateAsync(saved with { SmtpAuth = SmtpAuthMode.None, SmtpUsername = null });
        var updated = (await repo.GetAsync(id))!;
        Assert.Equal((SmtpAuthMode.None, (string?)null), (updated.SmtpAuth, updated.SmtpUsername));
    }

    private async Task<(Folder Inbox, Folder Trash)> SeedAccountAsync()
    {
        var accountId = await new AccountRepository(_db).AddAsync(new Account
        {
            DisplayName = "Test", Email = "t@example.com", Username = "t@example.com", ImapHost = "imap", SmtpHost = "smtp",
        });
        var folders = new FolderRepository(_db);
        var inbox = new Folder { AccountId = accountId, Path = "INBOX", Name = "Inbox", Role = FolderRole.Inbox };
        var trash = new Folder { AccountId = accountId, Path = "Trash", Name = "Trash", Role = FolderRole.Trash };
        return (inbox with { Id = await folders.UpsertAsync(inbox) }, trash with { Id = await folders.UpsertAsync(trash) });
    }

    private static MessageSummary Msg(Folder f, long uid, string subject, int minutesAgo = 0) => new()
    {
        AccountId = f.AccountId, FolderId = f.Id, Uid = uid, Subject = subject,
        FromName = "Sender", FromAddress = "sender@example.com", To = "t@example.com",
        Date = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo),
    };
}
