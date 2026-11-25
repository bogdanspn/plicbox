using Dapper;
using Plicbox.Core;
using Microsoft.Data.Sqlite;

namespace Plicbox.Storage;

public sealed class AccountRepository(Database db) : IAccountRepository
{
    private const string Columns = """
        id, display_name AS DisplayName, email, provider, auth, username,
        imap_host AS ImapHost, imap_port AS ImapPort, smtp_host AS SmtpHost, smtp_port AS SmtpPort,
        dav_url AS DavUrl, color, signature, label, sort_order AS SortOrder, is_default AS IsDefault,
        bcc_address AS BccAddress, imap_security AS ImapSecurity, smtp_security AS SmtpSecurity,
        sync_period_days AS SyncPeriodDays, offline_download AS OfflineDownload, offline_days AS OfflineDays, aliases AS AliasesJson,
        folder_overrides AS FolderOverridesJson, ignored_paths AS IgnoredPathsJson,
        smtp_auth AS SmtpAuth, smtp_username AS SmtpUsername
        """;

    public async Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<Account>(new CommandDefinition(
            $"SELECT {Columns} FROM accounts ORDER BY sort_order, id", cancellationToken: ct))).AsList();
    }

    public async Task<Account?> GetAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.QuerySingleOrDefaultAsync<Account>(new CommandDefinition(
            $"SELECT {Columns} FROM accounts WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<long> AddAsync(Account a, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO accounts (display_name, email, provider, auth, username, imap_host, imap_port,
                                  smtp_host, smtp_port, dav_url, color, signature, label, bcc_address,
                                  imap_security, smtp_security, sync_period_days, offline_download, offline_days,
                                  aliases, folder_overrides, ignored_paths, smtp_auth, smtp_username, sort_order, is_default)
            VALUES (@DisplayName, @Email, @Provider, @Auth, @Username, @ImapHost, @ImapPort,
                    @SmtpHost, @SmtpPort, @DavUrl, @Color, @Signature, @Label, @BccAddress,
                    @ImapSecurity, @SmtpSecurity, @SyncPeriodDays, @OfflineDownload, @OfflineDays,
                    @AliasesJson, @FolderOverridesJson, @IgnoredPathsJson, @SmtpAuth, @SmtpUsername,
                    (SELECT COALESCE(MAX(sort_order), 0) + 1 FROM accounts),
                    NOT EXISTS (SELECT 1 FROM accounts))
            RETURNING id
            """, a, cancellationToken: ct));
    }

    public async Task UpdateAsync(Account a, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE accounts SET display_name = @DisplayName, label = @Label, username = @Username,
                   imap_host = @ImapHost, imap_port = @ImapPort, smtp_host = @SmtpHost, smtp_port = @SmtpPort,
                   signature = @Signature, bcc_address = @BccAddress, color = @Color,
                   imap_security = @ImapSecurity, smtp_security = @SmtpSecurity,
                   sync_period_days = @SyncPeriodDays, offline_download = @OfflineDownload, offline_days = @OfflineDays,
                   aliases = @AliasesJson, folder_overrides = @FolderOverridesJson, ignored_paths = @IgnoredPathsJson,
                   smtp_auth = @SmtpAuth, smtp_username = @SmtpUsername
            WHERE id = @Id
            """, a, cancellationToken: ct));
    }

    public async Task SetDefaultAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE accounts SET is_default = (id = @id)", new { id }, cancellationToken: ct));
    }

    public async Task SetOrderAsync(IReadOnlyList<long> idsInOrder, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        for (var i = 0; i < idsInOrder.Count; i++)
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE accounts SET sort_order = @i WHERE id = @id", new { i, id = idsInOrder[i] }, tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        // Removing the default account promotes the next one, so there is always a default.
        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM accounts WHERE id = @id;
            UPDATE accounts SET is_default = 1
            WHERE id = (SELECT id FROM accounts ORDER BY sort_order, id LIMIT 1)
              AND NOT EXISTS (SELECT 1 FROM accounts WHERE is_default = 1);
            """, new { id }, cancellationToken: ct));
    }
}

public sealed class FolderRepository(Database db) : IFolderRepository
{
    public async Task<IReadOnlyList<Folder>> GetByAccountAsync(long accountId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<Folder>(new CommandDefinition("""
            SELECT id, account_id AS AccountId, path, name, role, uid_validity AS UidValidity,
                   highest_modseq AS HighestModSeq, last_uid AS LastUid, server_count AS ServerCount, is_synced AS IsSynced,
                   is_virtual AS IsVirtual, unread_count AS UnreadCount, total_count AS TotalCount
            FROM folders WHERE account_id = @accountId ORDER BY role = 0, role, name COLLATE NOCASE
            """, new { accountId }, cancellationToken: ct));
        // Special folders in the app's current language, whichever one they were saved in.
        return rows.Select(f => f.Role == FolderRole.Other ? f : f with { Name = FolderNames.Display(f.Role, f.Name) }).ToList();
    }

    public async Task<long> UpsertAsync(Folder f, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO folders (account_id, path, name, role, is_synced, is_virtual)
            VALUES (@AccountId, @Path, @Name, @Role, @IsSynced, @IsVirtual)
            ON CONFLICT (account_id, path) DO UPDATE SET
                name = excluded.name, role = excluded.role, is_synced = excluded.is_synced, is_virtual = excluded.is_virtual
            RETURNING id
            """, f, cancellationToken: ct));
    }

    public async Task UpdateSyncStateAsync(long folderId, long uidValidity, long lastUid, long highestModSeq, long serverCount, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE folders SET uid_validity = @uidValidity, last_uid = @lastUid, highest_modseq = @highestModSeq, server_count = @serverCount
            WHERE id = @folderId
            """, new { folderId, uidValidity, lastUid, highestModSeq, serverCount }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<long>> DeleteMissingAsync(long accountId, IReadOnlyCollection<string> keepPaths, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var gone = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT id FROM folders WHERE account_id = @accountId AND path NOT IN @keepPaths AND role <> @outbox",
            new { accountId, keepPaths, outbox = (int)FolderRole.Outbox }, tx, cancellationToken: ct))).AsList();
        if (gone.Count == 0) return [];

        var messageIds = (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT id FROM messages WHERE folder_id IN @gone", new { gone }, tx, cancellationToken: ct))).AsList();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM folders WHERE id IN @gone", new { gone }, tx, cancellationToken: ct));
        tx.Commit();
        return messageIds;
    }
}

public sealed class MessageRepository(Database db) : IMessageRepository
{
    private const string SelectColumns = """
        m.id, m.account_id AS AccountId, m.folder_id AS FolderId, m.uid, m.message_id AS MessageId,
        m.in_reply_to AS InReplyTo, m.thread_key AS ThreadKey, m.subject, m.from_name AS FromName,
        m.from_address AS FromAddress, m.to_list AS "To", m.recipients AS Recipients, m.date AS DateUnix, m.preview,
        m.is_read AS IsRead, m.is_flagged AS IsFlagged, m.has_attachments AS HasAttachments, m.size,
        m.is_important AS IsImportant, m.send_state AS SendState
        """;

    // Unread/Flagged smart views and counts ignore mail that's already been binned.
    private static readonly int[] ExcludedFromViews = [(int)FolderRole.Trash, (int)FolderRole.Junk];

    public async Task<MessageSummary?> GetAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM messages m WHERE m.id = @id", new { id }, cancellationToken: ct));
        return row?.ToModel();
    }

    public async Task<MessageSummary?> GetByMessageIdAsync(string messageId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        var row = await conn.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM messages m JOIN folders f ON f.id = m.folder_id WHERE m.account_id IN (SELECT id FROM accounts) AND m.message_id = @messageId ORDER BY f.is_virtual, m.date DESC LIMIT 1",
            new { messageId }, cancellationToken: ct));
        return row?.ToModel();
    }

    public Task<IReadOnlyList<MessageSummary>> GetByFolderAsync(long folderId, int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default) =>
        QueryAsync($"SELECT {SelectColumns} FROM messages m WHERE m.folder_id = @folderId ORDER BY {OrderBy(sort)} LIMIT @limit OFFSET @offset",
            new { folderId, limit, offset }, ct);

    /// <summary>
    /// The list order in SQL, ending in date and id so every row has one place (pages neither repeat nor skip).
    /// Text uses the "ui" collation and "subject_key" (see Database.Open), matching how the app sorts in memory.
    /// </summary>
    internal static string OrderBy(ListSort? sort)
    {
        sort ??= new ListSort();
        var dir = sort.Descending ? "DESC" : "ASC";
        const string newest = "m.date DESC, m.id DESC";
        return sort.Key switch
        {
            ListSortKey.Sender => $"COALESCE(NULLIF(m.from_name, ''), m.from_address) COLLATE ui {dir}, {newest}",
            ListSortKey.Subject => $"subject_key(m.subject) COLLATE ui {dir}, {newest}",
            ListSortKey.Flag => $"m.is_flagged {dir}, {newest}",
            // Descending = unread first.
            ListSortKey.ReadStatus => $"m.is_read {(sort.Descending ? "ASC" : "DESC")}, {newest}",
            ListSortKey.Attachments => $"m.has_attachments {dir}, {newest}",
            _ => $"m.date {dir}, m.id {dir}",
        };
    }

    public async Task<IReadOnlyList<long>> GetUidsWithoutRecipientsAsync(long folderId, int limit, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT uid FROM messages WHERE folder_id = @folderId AND recipients IS NULL AND uid > 0 ORDER BY date DESC LIMIT @limit",
            new { folderId, limit }, cancellationToken: ct))).AsList();
    }

    public async Task SetRecipientsAsync(long folderId, IReadOnlyList<(long Uid, string Recipients)> rows, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (uid, recipients) in rows)
            await conn.ExecuteAsync(new CommandDefinition("UPDATE messages SET recipients = @recipients WHERE folder_id = @folderId AND uid = @uid",
                new { folderId, uid, recipients }, tx, cancellationToken: ct));
        tx.Commit();
    }

    public async Task<IReadOnlyList<MessageSummary>> GetByUidsAsync(long folderId, IReadOnlyCollection<long> uids, CancellationToken ct = default)
    {
        var result = new List<MessageSummary>();
        foreach (var chunk in uids.Chunk(500))
            result.AddRange(await QueryAsync($"SELECT {SelectColumns} FROM messages m WHERE m.folder_id = @folderId AND m.uid IN @chunk ORDER BY m.date",
                new { folderId, chunk }, ct));
        return result;
    }

    // Views over several folders start from the folders (CROSS JOIN keeps SQLite to that order): started from the messages,
    // they'd go through every one there is, All Mail's too.

    public Task<IReadOnlyList<MessageSummary>> GetUnifiedInboxAsync(int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default)
    {
        // By date, the page is picked from the index alone and only its rows are read.
        var byDate = sort is null || sort.Key == ListSortKey.Date;
        return QueryAsync(byDate
            ? $"""
               SELECT {SelectColumns} FROM messages m WHERE m.id IN (
                   SELECT m.id FROM folders f CROSS JOIN messages m INDEXED BY ix_messages_folder_date ON m.folder_id = f.id
                   WHERE f.role = @inbox ORDER BY {OrderBy(sort)} LIMIT @limit OFFSET @offset)
               ORDER BY {OrderBy(sort)}
               """
            : $"""
               SELECT {SelectColumns} FROM folders f CROSS JOIN messages m ON m.folder_id = f.id
               WHERE f.role = @inbox ORDER BY {OrderBy(sort)} LIMIT @limit OFFSET @offset
               """, new { inbox = (int)FolderRole.Inbox, limit, offset }, ct);
    }

    public Task<IReadOnlyList<MessageSummary>> GetUnreadAsync(int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default) =>
        QueryAsync($"""
            SELECT {SelectColumns} FROM folders f CROSS JOIN messages m ON m.folder_id = f.id
            WHERE m.is_read = 0 AND f.role NOT IN @excluded AND f.is_virtual = 0 ORDER BY {OrderBy(sort)} LIMIT @limit OFFSET @offset
            """, new { excluded = ExcludedFromViews, limit, offset }, ct);

    public Task<IReadOnlyList<MessageSummary>> GetOutboxAsync(int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default) =>
        QueryAsync($"""
            SELECT {SelectColumns} FROM messages m JOIN folders f ON f.id = m.folder_id
            WHERE f.role = @outbox ORDER BY {OrderBy(sort)} LIMIT @limit OFFSET @offset
            """, new { outbox = (int)FolderRole.Outbox, limit, offset }, ct);

    public Task<IReadOnlyList<MessageSummary>> GetTrashAsync(int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default) =>
        QueryAsync($"""
            SELECT {SelectColumns} FROM messages m JOIN folders f ON f.id = m.folder_id
            WHERE f.role = @trash ORDER BY {OrderBy(sort)} LIMIT @limit OFFSET @offset
            """, new { trash = (int)FolderRole.Trash, limit, offset }, ct);

    public Task<IReadOnlyList<MessageSummary>> GetFlaggedAsync(int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default) =>
        QueryAsync($"""
            SELECT {SelectColumns} FROM folders f CROSS JOIN messages m ON m.folder_id = f.id
            WHERE m.is_read IN (0, 1) AND m.is_flagged = 1 AND f.role NOT IN @excluded AND f.is_virtual = 0 ORDER BY {OrderBy(sort)} LIMIT @limit OFFSET @offset
            """, new { excluded = ExcludedFromViews, limit, offset }, ct);

    public Task<IReadOnlyList<MessageSummary>> SearchAsync(string query, SearchScope? scope = null, int limit = 200, CancellationToken ct = default)
    {
        var match = ToFtsQuery(query);
        if (match.Length == 0) return Task.FromResult<IReadOnlyList<MessageSummary>>([]);
        scope ??= new SearchScope();
        return QueryAsync($"""
            SELECT {SelectColumns} FROM messages_fts
            JOIN messages m ON m.id = messages_fts.rowid
            JOIN folders f ON f.id = m.folder_id
            WHERE messages_fts MATCH @match
              AND (@accountId IS NULL OR m.account_id = @accountId)
              AND (@folderId IS NULL OR m.folder_id = @folderId)
              AND (@withTrash = 1 OR @folderId IS NOT NULL OR f.role NOT IN (@trash, @junk))
              -- Gmail's All Mail/Important repeat mail that lives in real folders: show each message once
              -- (unless searching that folder itself).
              AND (@folderId IS NOT NULL OR NOT (f.is_virtual = 1 AND m.message_id IS NOT NULL AND EXISTS (
                    SELECT 1 FROM messages o JOIN folders fo ON fo.id = o.folder_id
                    WHERE o.account_id = m.account_id AND o.message_id = m.message_id AND fo.is_virtual = 0)))
            ORDER BY m.date DESC LIMIT @limit
            """, new
        {
            match, limit, accountId = scope.AccountId, folderId = scope.FolderId, withTrash = scope.IncludeTrashAndJunk,
            trash = (int)FolderRole.Trash, junk = (int)FolderRole.Junk,
        }, ct);
    }

    public async Task<MailboxCounts> GetCountsAsync(bool withTotals = false, CancellationToken ct = default)
    {
        using var conn = db.Open();
        // Looked up in the index per folder (is_read IN (0, 1): the index has it before is_flagged), so it takes a moment
        // whatever the mailbox's size. Counting everything in a folder does not, hence withTotals.
        var perFolder = (await conn.QueryAsync<(long FolderId, long AccountId, int Role, bool IsVirtual, int Unread, int Flagged, int? Total)>(new CommandDefinition("""
            SELECT f.id, f.account_id, f.role, f.is_virtual,
                   (SELECT COUNT(*) FROM messages m WHERE m.folder_id = f.id AND m.is_read = 0),
                   (SELECT COUNT(*) FROM messages m WHERE m.folder_id = f.id AND m.is_read IN (0, 1) AND m.is_flagged = 1),
                   CASE WHEN @withTotals OR f.role IN (@drafts, @outbox) THEN (SELECT COUNT(*) FROM messages m WHERE m.folder_id = f.id) END
            FROM folders f
            """, new { withTotals, drafts = (int)FolderRole.Drafts, outbox = (int)FolderRole.Outbox }, cancellationToken: ct))).AsList();

        var counted = perFolder.Where(r => !ExcludedFromViews.Contains(r.Role) && !r.IsVirtual).ToList();
        return new MailboxCounts(
            perFolder.ToDictionary(r => r.FolderId, r => r.Unread),
            perFolder.Where(r => r.Total is not null).ToDictionary(r => r.FolderId, r => r.Total!.Value),
            UnreadInboxes: perFolder.Where(r => r.Role == (int)FolderRole.Inbox).Sum(r => r.Unread),
            UnreadTotal: counted.Sum(r => r.Unread),
            Flagged: counted.Sum(r => r.Flagged),
            counted.GroupBy(r => r.AccountId).ToDictionary(g => g.Key, g => g.Sum(r => r.Unread)));
    }

    public async Task<IReadOnlyList<MessageSummary>> GetThreadAsync(long accountId, string threadKey, CancellationToken ct = default)
    {
        var rows = await QueryAsync($"""
            SELECT {SelectColumns} FROM messages m JOIN folders f ON f.id = m.folder_id
            WHERE m.account_id = @accountId AND m.thread_key = @threadKey
              AND f.is_virtual = 0 AND f.role NOT IN @excluded
            ORDER BY m.date DESC, (f.role = @inbox) DESC, (f.role = @sent) DESC
            """, new { accountId, threadKey, excluded = ExcludedFromViews, inbox = (int)FolderRole.Inbox, sent = (int)FolderRole.Sent }, ct);
        // Gmail shows one message in every label it carries; keep the first (Inbox/Sent preferred by the ORDER BY).
        return rows.DistinctBy(m => m.MessageId ?? $"#{m.Id}").ToList();
    }

    public async Task<IReadOnlyDictionary<(long AccountId, string ThreadKey), int>> GetThreadCountsAsync(
        IReadOnlyCollection<(long AccountId, string ThreadKey)> threads, CancellationToken ct = default)
    {
        var result = new Dictionary<(long, string), int>();
        if (threads.Count == 0) return result;
        using var conn = db.Open();
        // Per account, so ix_messages_thread (account_id, thread_key) is searched key by key rather than read whole.
        foreach (var (accountId, keys) in threads.Distinct().GroupBy(t => t.AccountId, t => t.ThreadKey).Select(g => (g.Key, g)))
        foreach (var chunk in keys.Chunk(500))
        {
            var rows = await conn.QueryAsync<(long AccountId, string ThreadKey, int Count)>(new CommandDefinition("""
                SELECT m.account_id, m.thread_key, COUNT(DISTINCT COALESCE(m.message_id, m.id))
                FROM messages m JOIN folders f ON f.id = m.folder_id
                WHERE m.account_id = @accountId AND m.thread_key IN @chunk AND f.is_virtual = 0 AND f.role NOT IN @excluded
                GROUP BY m.account_id, m.thread_key
                """, new { accountId, chunk, excluded = ExcludedFromViews }, cancellationToken: ct));
            foreach (var r in rows) result[(r.AccountId, r.ThreadKey)] = r.Count;
        }
        return result;
    }

    public async Task<IReadOnlyList<long>> GetIdsByAccountAsync(long accountId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT id FROM messages WHERE account_id = @accountId", new { accountId }, cancellationToken: ct))).AsList();
    }

    public async Task<long> UpsertAsync(MessageSummary m, string? bodyText = null, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var id = await UpsertCoreAsync(conn, tx, m, bodyText, ct);
        tx.Commit();
        return id;
    }

    public async Task<long> AddLocalAsync(MessageSummary m, string? bodyText = null, Func<long, Task>? saveContent = null, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        // A UID no row of the folder has yet (UNIQUE folder_id, uid), then the usual -id placeholder once the id is known.
        var free = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT MIN(COALESCE(MIN(uid), 0), 0) - 1 FROM messages WHERE folder_id = @FolderId", new { m.FolderId }, tx, cancellationToken: ct));
        var id = await UpsertCoreAsync(conn, tx, m with { Uid = free }, bodyText, ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE messages SET uid = -id, has_body = 1 WHERE id = @id", new { id }, tx, cancellationToken: ct));
        if (saveContent is not null) await saveContent(id);
        tx.Commit();
        return id;
    }

    public async Task<IReadOnlyList<long>> DeleteByMessageIdAsync(long folderId, string messageId, long keepId = 0, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<long>(new CommandDefinition(
            "DELETE FROM messages WHERE folder_id = @folderId AND message_id = @messageId AND id <> @keepId RETURNING id",
            new { folderId, messageId, keepId }, cancellationToken: ct))).AsList();
    }

    public async Task UpsertBatchAsync(IReadOnlyList<MessageSummary> messages, CancellationToken ct = default)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var m in messages) await UpsertCoreAsync(conn, tx, m, null, ct);
        tx.Commit();
    }

    private static async Task<long> UpsertCoreAsync(SqliteConnection conn, SqliteTransaction tx, MessageSummary m, string? bodyText, CancellationToken ct)
    {
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO messages (id, account_id, folder_id, uid, message_id, in_reply_to, thread_key, subject,
                                  from_name, from_address, to_list, recipients, date, preview, is_read, is_flagged,
                                  has_attachments, size, is_important)
            VALUES ((SELECT last + 1 FROM message_ids), -- a number no message ever had (a trigger keeps it up to date)
                    @AccountId, @FolderId, @Uid, @MessageId, @InReplyTo, @ThreadKey, @Subject,
                    @FromName, @FromAddress, @To, @Recipients, @DateUnix, @Preview, @IsRead, @IsFlagged,
                    @HasAttachments, @Size, @IsImportant)
            ON CONFLICT (folder_id, uid) DO UPDATE SET
                is_read = excluded.is_read, is_flagged = excluded.is_flagged, thread_key = excluded.thread_key,
                is_important = excluded.is_important,
                recipients = coalesce(excluded.recipients, messages.recipients)
            RETURNING id
            """, new
        {
            m.AccountId, m.FolderId, m.Uid, m.MessageId, m.InReplyTo, m.ThreadKey, m.Subject,
            m.FromName, m.FromAddress, m.To, m.Recipients, DateUnix = m.Date.ToUnixTimeSeconds(), m.Preview,
            m.IsRead, m.IsFlagged, m.HasAttachments, m.Size, m.IsImportant,
        }, tx, cancellationToken: ct));

        await WriteFtsAsync(conn, tx, id, m, bodyText ?? m.Preview, ct);
        return id;
    }

    // Contentless FTS5 tables can't be updated in place: delete then re-insert the row.
    private static async Task WriteFtsAsync(SqliteConnection conn, SqliteTransaction tx, long id, MessageSummary m, string body, CancellationToken ct)
    {
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM messages_fts WHERE rowid = @id", new { id }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO messages_fts (rowid, subject, from_name, from_address, to_list, body)
            VALUES (@id, @Subject, @FromName, @FromAddress, @To, @body)
            """, new { id, m.Subject, m.FromName, m.FromAddress, m.To, body }, tx, cancellationToken: ct));
    }

    public async Task SetFlagsAsync(long id, bool isRead, bool isFlagged, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE messages SET is_read = @isRead, is_flagged = @isFlagged WHERE id = @id",
            new { id, isRead, isFlagged }, cancellationToken: ct));
    }

    public async Task SetDateAsync(long id, DateTimeOffset date, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("UPDATE messages SET date = @date WHERE id = @id",
            new { id, date = date.ToUnixTimeSeconds() }, cancellationToken: ct));
    }

    public async Task SetSendStateAsync(long id, SendState state, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("UPDATE messages SET send_state = @state WHERE id = @id",
            new { id, state = (int)state }, cancellationToken: ct));
    }

    public async Task<int> MarkFolderReadAsync(long folderId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE messages SET is_read = 1 WHERE folder_id = @folderId AND is_read = 0", new { folderId }, cancellationToken: ct));
    }

    public async Task MoveAsync(long id, long folderId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE messages SET folder_id = @folderId, uid = -id, send_state = 0 WHERE id = @id",
            new { id, folderId }, cancellationToken: ct));
    }

    public async Task DeleteAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM messages WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<long>> GetUidsAsync(long folderId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<long>(new CommandDefinition(
            "SELECT uid FROM messages WHERE folder_id = @folderId AND uid > 0", new { folderId }, cancellationToken: ct))).AsList();
    }

    public async Task<int> ApplyServerFlagsAsync(long folderId, IReadOnlyList<(long Uid, bool IsRead, bool IsFlagged)> flags, CancellationToken ct = default)
    {
        if (flags.Count == 0) return 0;
        using var conn = db.Open();
        // A server that can't say what changed (Outlook.com) sends the whole folder's flags on every pass: what's here is
        // read in one go, and only the few that differ are touched.
        if (flags.Count > 100)
        {
            var here = (await conn.QueryAsync<(long Uid, bool IsRead, bool IsFlagged)>(new CommandDefinition(
                "SELECT uid, is_read, is_flagged FROM messages WHERE folder_id = @folderId AND uid > 0", new { folderId }, cancellationToken: ct)))
                .ToDictionary(r => r.Uid, r => (r.IsRead, r.IsFlagged));
            flags = flags.Where(f => here.TryGetValue(f.Uid, out var h) && h != (f.IsRead, f.IsFlagged)).ToList();
            if (flags.Count == 0) return 0;
        }
        var changed = 0;
        using var tx = conn.BeginTransaction();
        foreach (var (uid, isRead, isFlagged) in flags)
        {
            changed += await conn.ExecuteAsync(new CommandDefinition("""
                UPDATE messages SET is_read = @isRead, is_flagged = @isFlagged
                WHERE folder_id = @folderId AND uid = @uid AND (is_read <> @isRead OR is_flagged <> @isFlagged)
                """, new { folderId, uid, isRead, isFlagged }, tx, cancellationToken: ct));
        }
        tx.Commit();
        return changed;
    }

    public async Task<int> ApplyServerImportanceAsync(long folderId, IReadOnlyList<(long Uid, bool IsImportant)> marks, CancellationToken ct = default)
    {
        if (marks.Count == 0) return 0;
        using var conn = db.Open();
        var changed = 0;
        using var tx = conn.BeginTransaction();
        foreach (var (uid, important) in marks)
            changed += await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE messages SET is_important = @important WHERE folder_id = @folderId AND uid = @uid AND is_important <> @important",
                new { folderId, uid, important }, tx, cancellationToken: ct));
        tx.Commit();
        return changed;
    }

    public async Task SetImportantAsync(long id, bool isImportant, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("UPDATE messages SET is_important = @isImportant WHERE id = @id", new { id, isImportant }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<long>> DeleteByUidsAsync(long folderId, IReadOnlyCollection<long> uids, CancellationToken ct = default)
    {
        if (uids.Count == 0) return [];
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var ids = new List<long>();
        // SQLite caps bound parameters, so go in chunks.
        foreach (var chunk in uids.Chunk(500))
        {
            ids.AddRange(await conn.QueryAsync<long>(new CommandDefinition(
                "DELETE FROM messages WHERE folder_id = @folderId AND uid IN @chunk RETURNING id",
                new { folderId, chunk }, tx, cancellationToken: ct)));
        }
        tx.Commit();
        return ids;
    }

    public async Task<IReadOnlyList<long>> WipeFolderAsync(long folderId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<long>(new CommandDefinition(
            "DELETE FROM messages WHERE folder_id = @folderId AND uid > 0 RETURNING id", new { folderId }, cancellationToken: ct))).AsList();
    }

    public async Task ConfirmUidAsync(long id, long uid, CancellationToken ct = default)
    {
        using var conn = db.Open();
        // If a sync already fetched the moved message under its new UID, keep that row and drop the placeholder.
        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE FROM messages WHERE id = @id AND uid < 0
              AND EXISTS (SELECT 1 FROM messages o WHERE o.folder_id = messages.folder_id AND o.uid = @uid);
            UPDATE messages SET uid = @uid WHERE id = @id AND uid < 0;
            """, new { id, uid }, cancellationToken: ct));
    }

    public async Task IndexBodyAsync(long id, string bodyText, CancellationToken ct = default)
    {
        if (await GetAsync(id, ct) is not { } m) return;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        await WriteFtsAsync(conn, tx, id, m, bodyText, ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE messages SET has_body = 1 WHERE id = @id", new { id }, tx, cancellationToken: ct));
        tx.Commit();
    }

    public Task<IReadOnlyList<MessageSummary>> GetWithoutBodyAsync(long folderId, DateTimeOffset? since, int limit, CancellationToken ct = default) =>
        QueryAsync($"""
            SELECT {SelectColumns} FROM messages m
            WHERE m.folder_id = @folderId AND m.has_body = 0 AND m.uid > 0 AND (@since IS NULL OR m.date >= @since)
            ORDER BY m.date DESC LIMIT @limit
            """, new { folderId, since = since?.ToUnixTimeSeconds(), limit }, ct);

    public async Task MarkDownloadedAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return;
        using var conn = db.Open();
        foreach (var chunk in ids.Chunk(500))
            await conn.ExecuteAsync(new CommandDefinition("UPDATE messages SET has_body = 1 WHERE id IN @chunk", new { chunk }, cancellationToken: ct));
    }

    private async Task<IReadOnlyList<MessageSummary>> QueryAsync(string sql, object args, CancellationToken ct)
    {
        using var conn = db.Open();
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(sql, args, cancellationToken: ct));
        return rows.Select(r => r.ToModel()).ToList();
    }

    /// <summary>Turns free user input into a safe FTS5 prefix query: each word quoted, last word prefix-matched.</summary>
    internal static string ToFtsQuery(string input)
    {
        var words = input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Replace("\"", ""))
            .Where(w => w.Length > 0)
            .Select(w => $"\"{w}\"*");
        return string.Join(' ', words);
    }

    private sealed class Row
    {
        public long Id { get; init; }
        public long AccountId { get; init; }
        public long FolderId { get; init; }
        public long Uid { get; init; }
        public string? MessageId { get; init; }
        public string? InReplyTo { get; init; }
        public string? ThreadKey { get; init; }
        public string Subject { get; init; } = "";
        public string FromName { get; init; } = "";
        public string FromAddress { get; init; } = "";
        public string To { get; init; } = "";
        public string? Recipients { get; init; }
        public long DateUnix { get; init; }
        public string Preview { get; init; } = "";
        public bool IsRead { get; init; }
        public bool IsFlagged { get; init; }
        public bool HasAttachments { get; init; }
        public long Size { get; init; }
        public bool IsImportant { get; init; }
        public long SendState { get; init; }

        public MessageSummary ToModel() => new()
        {
            Id = Id, AccountId = AccountId, FolderId = FolderId, Uid = Uid, MessageId = MessageId,
            InReplyTo = InReplyTo, ThreadKey = ThreadKey, Subject = Subject, FromName = FromName,
            FromAddress = FromAddress, To = To, Recipients = Recipients, Date = DateTimeOffset.FromUnixTimeSeconds(DateUnix),
            Preview = Preview, IsRead = IsRead, IsFlagged = IsFlagged, HasAttachments = HasAttachments, Size = Size,
            IsImportant = IsImportant, SendState = (SendState)SendState,
        };
    }
}

public sealed class PendingOpRepository(Database db) : IPendingOpRepository
{
    private const string Columns = """
        id, account_id AS AccountId, kind, message_id AS MessageId, folder_path AS FolderPath,
        uid, target_path AS TargetPath, attempts, header_message_id AS HeaderMessageId
        """;

    public async Task<IReadOnlyList<PendingOp>> GetByAccountAsync(long accountId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return (await conn.QueryAsync<PendingOp>(new CommandDefinition(
            $"SELECT {Columns} FROM pending_ops WHERE account_id = @accountId ORDER BY id",
            new { accountId }, cancellationToken: ct))).AsList();
    }

    public async Task<PendingOp?> GetPendingMoveAsync(long messageId, CancellationToken ct = default)
    {
        using var conn = db.Open();
        return await conn.QueryFirstOrDefaultAsync<PendingOp>(new CommandDefinition(
            $"SELECT {Columns} FROM pending_ops WHERE message_id = @messageId AND kind = @kind ORDER BY id DESC",
            new { messageId, kind = (int)PendingOpKind.Move }, cancellationToken: ct));
    }

    public async Task AddAsync(PendingOp op, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO pending_ops (account_id, kind, message_id, folder_path, uid, target_path, attempts, header_message_id)
            VALUES (@AccountId, @Kind, @MessageId, @FolderPath, @Uid, @TargetPath, @Attempts, @HeaderMessageId)
            """, op, cancellationToken: ct));
    }

    public async Task UpdateAsync(PendingOp op, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE pending_ops SET kind = @Kind, folder_path = @FolderPath, uid = @Uid,
                   target_path = @TargetPath, attempts = @Attempts
            WHERE id = @Id
            """, op, cancellationToken: ct));
    }

    public async Task RemoveAsync(long id, CancellationToken ct = default)
    {
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM pending_ops WHERE id = @id", new { id }, cancellationToken: ct));
    }

    public async Task RemoveUploadsAsync(IReadOnlyCollection<long> messageIds, CancellationToken ct = default)
    {
        if (messageIds.Count == 0) return;
        using var conn = db.Open();
        await conn.ExecuteAsync(new CommandDefinition("DELETE FROM pending_ops WHERE message_id IN @messageIds AND kind IN @kinds",
            new { messageIds, kinds = new[] { (int)PendingOpKind.Append, (int)PendingOpKind.SaveDraft } }, cancellationToken: ct));
    }
}
