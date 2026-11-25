namespace Plicbox.Core;

public interface IAccountRepository
{
    Task<IReadOnlyList<Account>> GetAllAsync(CancellationToken ct = default);
    Task<Account?> GetAsync(long id, CancellationToken ct = default);
    Task<long> AddAsync(Account account, CancellationToken ct = default);
    /// <summary>Saves editable settings (names, servers, signature, Bcc). Order and default are set separately.</summary>
    Task UpdateAsync(Account account, CancellationToken ct = default);
    Task SetDefaultAsync(long id, CancellationToken ct = default);
    Task SetOrderAsync(IReadOnlyList<long> idsInOrder, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);
}

public interface IFolderRepository
{
    Task<IReadOnlyList<Folder>> GetByAccountAsync(long accountId, CancellationToken ct = default);
    /// <summary>Inserts or updates name/role/IsSynced. Sync state (UIDVALIDITY, LastUid, MODSEQ) is left untouched.</summary>
    Task<long> UpsertAsync(Folder folder, CancellationToken ct = default);
    Task UpdateSyncStateAsync(long folderId, long uidValidity, long lastUid, long highestModSeq, long serverCount, CancellationToken ct = default);
    /// <summary>
    /// Removes folders that no longer exist on the server (the Outbox, kept only here, stays); returns ids of messages
    /// that went with them.
    /// </summary>
    Task<IReadOnlyList<long>> DeleteMissingAsync(long accountId, IReadOnlyCollection<string> keepPaths, CancellationToken ct = default);
}

public interface IMessageRepository
{
    Task<MessageSummary?> GetAsync(long id, CancellationToken ct = default);
    /// <summary>A message by its Message-ID header, in a real folder rather than a view like All Mail when it's in both.</summary>
    Task<MessageSummary?> GetByMessageIdAsync(string messageId, CancellationToken ct = default);
    // The list views, newest first unless <c>sort</c> says otherwise; <c>offset</c> skips the rows already listed (paging).
    Task<IReadOnlyList<MessageSummary>> GetByFolderAsync(long folderId, int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default);
    /// <summary>Messages of a folder by server UID (new mail, for rules).</summary>
    Task<IReadOnlyList<MessageSummary>> GetByUidsAsync(long folderId, IReadOnlyCollection<long> uids, CancellationToken ct = default);
    Task<IReadOnlyList<MessageSummary>> GetUnifiedInboxAsync(int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default);
    Task<IReadOnlyList<MessageSummary>> GetUnreadAsync(int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default);
    /// <summary>Every account's Outbox: what waits to be sent.</summary>
    Task<IReadOnlyList<MessageSummary>> GetOutboxAsync(int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default);
    /// <summary>Every account's Trash: what was deleted, together.</summary>
    Task<IReadOnlyList<MessageSummary>> GetTrashAsync(int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default);
    Task<IReadOnlyList<MessageSummary>> GetFlaggedAsync(int limit = 500, int offset = 0, ListSort? sort = null, CancellationToken ct = default);
    /// <summary>Full-text search, newest first; everywhere unless <paramref name="scope"/> narrows it.</summary>
    Task<IReadOnlyList<MessageSummary>> SearchAsync(string query, SearchScope? scope = null, int limit = 200, CancellationToken ct = default);
    /// <param name="withTotals">Also count every folder's messages (many times slower); without, only Drafts' and the Outbox's.</param>
    Task<MailboxCounts> GetCountsAsync(bool withTotals = false, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetIdsByAccountAsync(long accountId, CancellationToken ct = default);
    /// <summary>Every message of a conversation across folders (newest first), each message once.</summary>
    Task<IReadOnlyList<MessageSummary>> GetThreadAsync(long accountId, string threadKey, CancellationToken ct = default);
    /// <summary>Conversation sizes across folders, keyed by (account, thread key); Trash/Junk and label duplicates not counted.</summary>
    Task<IReadOnlyDictionary<(long AccountId, string ThreadKey), int>> GetThreadCountsAsync(
        IReadOnlyCollection<(long AccountId, string ThreadKey)> threads, CancellationToken ct = default);

    Task<long> UpsertAsync(MessageSummary message, string? bodyText = null, CancellationToken ct = default);
    /// <summary>
    /// Adds a message that so far exists only here (in the Outbox, or a draft not uploaded yet). Its UID is the -id
    /// placeholder, as for a move the server hasn't confirmed; returns the new id.
    /// </summary>
    /// <param name="saveContent">
    /// Saves its content under the id given. It's done before anything else can see the message: the Outbox, looking in
    /// between, would take it for one without content.
    /// </param>
    Task<long> AddLocalAsync(MessageSummary message, string? bodyText = null, Func<long, Task>? saveContent = null, CancellationToken ct = default);
    /// <summary>Removes every copy of a message (by its Message-ID header) from a folder but <paramref name="keepId"/>; returns the ids removed.</summary>
    Task<IReadOnlyList<long>> DeleteByMessageIdAsync(long folderId, string messageId, long keepId = 0, CancellationToken ct = default);
    Task UpsertBatchAsync(IReadOnlyList<MessageSummary> messages, CancellationToken ct = default);
    Task SetFlagsAsync(long id, bool isRead, bool isFlagged, CancellationToken ct = default);
    /// <summary>Changes a message's date (in the Outbox: when it goes).</summary>
    Task SetDateAsync(long id, DateTimeOffset date, CancellationToken ct = default);
    /// <summary>How far sending a message in the Outbox got (moving it elsewhere starts over).</summary>
    Task SetSendStateAsync(long id, SendState state, CancellationToken ct = default);
    /// <summary>Marks every message in a folder read; returns how many changed.</summary>
    Task<int> MarkFolderReadAsync(long folderId, CancellationToken ct = default);
    /// <summary>Local move; the real UID is unknown until the server confirms, so it is set to -id as a placeholder.</summary>
    Task MoveAsync(long id, long folderId, CancellationToken ct = default);
    Task DeleteAsync(long id, CancellationToken ct = default);

    // Sync support
    /// <summary>Server UIDs present locally for a folder (placeholders from pending moves excluded).</summary>
    Task<IReadOnlyList<long>> GetUidsAsync(long folderId, CancellationToken ct = default);
    /// <returns>How many messages that changed.</returns>
    Task<int> ApplyServerFlagsAsync(long folderId, IReadOnlyList<(long Uid, bool IsRead, bool IsFlagged)> flags, CancellationToken ct = default);
    /// <summary>Gmail's Important label as the server has it now, for messages already stored.</summary>
    /// <returns>How many messages that changed.</returns>
    Task<int> ApplyServerImportanceAsync(long folderId, IReadOnlyList<(long Uid, bool IsImportant)> marks, CancellationToken ct = default);
    Task SetImportantAsync(long id, bool isImportant, CancellationToken ct = default);
    /// <summary>Deletes rows by UID; returns the local ids removed (so cached bodies can be dropped).</summary>
    Task<IReadOnlyList<long>> DeleteByUidsAsync(long folderId, IReadOnlyCollection<long> uids, CancellationToken ct = default);
    Task<IReadOnlyList<long>> WipeFolderAsync(long folderId, CancellationToken ct = default);
    /// <summary>Replaces the -id placeholder with the UID the server assigned after a move.</summary>
    Task ConfirmUidAsync(long id, long uid, CancellationToken ct = default);
    /// <summary>Server UIDs of a folder's messages synced before recipient addresses were kept (newest first).</summary>
    Task<IReadOnlyList<long>> GetUidsWithoutRecipientsAsync(long folderId, int limit, CancellationToken ct = default);
    Task SetRecipientsAsync(long folderId, IReadOnlyList<(long Uid, string Recipients)> rows, CancellationToken ct = default);
    /// <summary>Re-indexes a message for search once its full body is available (and notes that it's downloaded).</summary>
    Task IndexBodyAsync(long id, string bodyText, CancellationToken ct = default);
    /// <summary>Server messages of a folder not downloaded yet, newest first, received since <paramref name="since"/> (null: any).</summary>
    Task<IReadOnlyList<MessageSummary>> GetWithoutBodyAsync(long folderId, DateTimeOffset? since, int limit, CancellationToken ct = default);
    /// <summary>Notes messages whose full content turned out to be here already.</summary>
    Task MarkDownloadedAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);
}

public interface IPendingOpRepository
{
    Task<IReadOnlyList<PendingOp>> GetByAccountAsync(long accountId, CancellationToken ct = default);
    Task<PendingOp?> GetPendingMoveAsync(long messageId, CancellationToken ct = default);
    Task AddAsync(PendingOp op, CancellationToken ct = default);
    Task UpdateAsync(PendingOp op, CancellationToken ct = default);
    Task RemoveAsync(long id, CancellationToken ct = default);
    /// <summary>
    /// Drops the uploads (Append, SaveDraft) queued for a message kept here that's gone.
    /// </summary>
    Task RemoveUploadsAsync(IReadOnlyCollection<long> messageIds, CancellationToken ct = default);
}

public sealed record MailboxCounts(
    IReadOnlyDictionary<long, int> UnreadByFolder,
    // Drafts and the Outbox only, unless asked for with totals.
    IReadOnlyDictionary<long, int> TotalByFolder,
    int UnreadInboxes,
    int UnreadTotal,
    int Flagged,
    // What the Unread view counts (not Trash, Junk or views like All Mail), per account: mail a rule filed away included.
    IReadOnlyDictionary<long, int> UnreadByAccount);

/// <summary>Stores raw RFC 822 message bodies (.eml), keyed by local message id.</summary>
public interface IBodyStore
{
    Task SaveAsync(long messageId, Stream eml, CancellationToken ct = default);
    Stream? Open(long messageId);
    void Delete(long messageId);
}

/// <summary>Senders whose pictures load automatically ("always download pictures from this sender").</summary>
public interface ITrustedSenders
{
    bool IsTrusted(string? address);
    void Trust(string address);
    void Untrust(string address);
    IReadOnlyList<string> All();
}

/// <summary>Per-account secrets (passwords, app passwords, OAuth refresh tokens). Never stored in the database.</summary>
public interface ICredentialStore
{
    void Save(long accountId, string secret);
    string? Get(long accountId);
    void Delete(long accountId);
    /// <summary>The outgoing server's own password (accounts with a separate sending login).</summary>
    void SaveSmtp(long accountId, string secret);
    string? GetSmtp(long accountId);
    void DeleteSmtp(long accountId);
}
