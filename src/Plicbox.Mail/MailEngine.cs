using System.Collections.Concurrent;
using Plicbox.Core;
using MailKit;
using MessageSummary = Plicbox.Core.MessageSummary;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Plicbox.Mail;

/// <summary>
/// Entry point for everything that touches mail servers. UI actions update the local database immediately
/// (so the app feels instant) and queue the matching server change, which the account's worker applies.
/// Events are raised on background threads.
/// </summary>
public sealed class MailEngine(
    IAccountRepository accounts,
    IFolderRepository folders,
    IMessageRepository messages,
    IPendingOpRepository ops,
    IBodyStore bodies,
    ICredentialStore credentials,
    MailConnector connector,
    ILoggerFactory loggerFactory,
    ISettingsStore? settings = null) : IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<long, AccountSync> _workers = new();
    private readonly ILogger _log = loggerFactory.CreateLogger<MailEngine>();

    /// <summary>(accountId, newInboxMessages) after every sync pass.</summary>
    public event Action<long, int>? Synced;
    /// <summary>(accountId) a sync pass, or the rules run after it, changed mail here: what's listed needs a refresh.</summary>
    public event Action<long>? MailChanged;
    public event Action<long, AccountStatus>? StatusChanged;
    /// <summary>(accountId) the folder list changed.</summary>
    public event Action<long>? FoldersChanged;
    /// <summary>(accountId, folderId) one folder finished syncing.</summary>
    public event Action<long, long>? FolderSynced;
    /// <summary>(messageId) work on a message started or finished; ask <see cref="IsBusy"/> which.</summary>
    public event Action<long>? BusyChanged;

    // Messages with work under way, for the list's spinner: bodies downloading (a count, the reader and rules can
    // both ask) and flag changes or copies on their way to the server (with the account that sends them).
    private readonly object _busyLock = new();
    private readonly Dictionary<long, int> _downloading = [];
    private readonly Dictionary<long, long> _sending = [];

    /// <summary>Whether something is happening to the message in the background right now.</summary>
    public bool IsBusy(long messageId)
    {
        lock (_busyLock) return _downloading.ContainsKey(messageId) || _sending.ContainsKey(messageId) || _beingSent.Contains(messageId);
    }

    public IReadOnlyDictionary<long, AccountStatus> Statuses => _workers.ToDictionary(kv => kv.Key, kv => kv.Value.Status);

    /// <summary>What an account's sync is up to right now (Operations); null when it isn't running.</summary>
    public SyncSnapshot? GetActivity(long accountId) => _workers.TryGetValue(accountId, out var w)
        ? new SyncSnapshot(w.Status, w.Activity, w.Backlog, w.LastPass, w.IsWatchingInbox)
        : null;

    /// <summary>Local changes (moves, deletes, flags) still waiting to be applied on the account's server.</summary>
    public Task<IReadOnlyList<PendingOp>> GetPendingOpsAsync(long accountId, CancellationToken ct = default) =>
        ops.GetByAccountAsync(accountId, ct);

    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var account in await accounts.GetAllAsync(ct)) StartWorker(account);
    }

    /// <param name="smtpSecret">The outgoing server's own password (accounts with a separate sending login).</param>
    public async Task<long> AddAccountAsync(Account account, string secret, CancellationToken ct = default, string? smtpSecret = null)
    {
        var id = await accounts.AddAsync(account, ct);
        credentials.Save(id, secret);
        if (smtpSecret is not null) credentials.SaveSmtp(id, smtpSecret);
        StartWorker(account with { Id = id });
        return id;
    }

    /// <param name="endAccess">Also end Plicbox's access at Google.</param>
    /// <returns>False when that was asked for and didn't work (the account is removed all the same).</returns>
    public async Task<bool> RemoveAccountAsync(long accountId, bool endAccess = false, CancellationToken ct = default)
    {
        if (_workers.TryRemove(accountId, out var worker)) await worker.DisposeAsync();
        var ended = !endAccess || await accounts.GetAsync(accountId, ct) is not { } account || await connector.EndAccessAsync(account);
        var ids = await messages.GetIdsByAccountAsync(accountId, ct);
        await accounts.DeleteAsync(accountId, ct);
        foreach (var id in ids) bodies.Delete(id);
        credentials.Delete(accountId);
        credentials.DeleteSmtp(accountId);
        return ended;
    }

    /// <summary>Saves edited account settings and reconnects with them.</summary>
    public async Task UpdateAccountAsync(Account account, CancellationToken ct = default)
    {
        await accounts.UpdateAsync(account, ct);
        if (_workers.TryRemove(account.Id, out var old)) await old.DisposeAsync();
        if (await accounts.GetAsync(account.Id, ct) is { } saved) StartWorker(saved);
        RetryOutbox(account.Id); // perhaps the settings that kept it from sending were just fixed
    }

    /// <summary>The user opened a folder: on-demand folders (Gmail's All Mail, Important) start syncing now.</summary>
    public void OpenFolder(Folder folder)
    {
        if (!folder.IsSynced && folder.Role != FolderRole.Outbox && _workers.TryGetValue(folder.AccountId, out var worker)) worker.Watch(folder.Id);
    }

    /// <summary>Replaces an account's password or Google token (after "Sign in again") and retries straight away.</summary>
    public void UpdateSecret(long accountId, string secret)
    {
        credentials.Save(accountId, secret);
        RequestSync(accountId, userInitiated: true);
    }

    /// <summary>The password for a separate outgoing login.</summary>
    public void UpdateSmtpSecret(long accountId, string secret)
    {
        credentials.SaveSmtp(accountId, secret);
        RetryOutbox(accountId);
    }

    public bool HasSmtpSecret(long accountId) => credentials.GetSmtp(accountId) is not null;

    /// <summary>After sleep or a network change: every account reopens its connections and catches up.</summary>
    public void ReconnectAll()
    {
        foreach (var worker in _workers.Values) worker.Reconnect();
    }

    public void RequestSync(long? accountId = null, bool userInitiated = false)
    {
        foreach (var (id, worker) in _workers)
        {
            if (accountId is not null && accountId != id) continue;
            worker.Request(SyncScope.Full, userInitiated);
            if (userInitiated) RetryOutbox(id); // Refresh is also "try sending again"
        }
    }

    /// <summary>Makes sure the full message is in the local body cache, downloading it if needed.</summary>
    public async Task<bool> EnsureBodyAsync(MessageSummary m, CancellationToken ct = default)
    {
        using (var cached = bodies.Open(m.Id))
            if (cached is not null) return true;
        if (!_workers.TryGetValue(m.AccountId, out var worker)) return false;
        Downloading(m.Id, 1);
        try { return await worker.FetchBodyAsync(m, ct); }
        finally { Downloading(m.Id, -1); }
    }

    private void Downloading(long messageId, int change)
    {
        lock (_busyLock)
        {
            var count = _downloading.GetValueOrDefault(messageId) + change;
            if (count > 0) _downloading[messageId] = count;
            else _downloading.Remove(messageId);
        }
        BusyChanged?.Invoke(messageId);
    }

    /// <summary>A change to the message was queued for its server (after the op is stored, so a pass can't miss it).</summary>
    private void Sending(MessageSummary m)
    {
        // A broken account keeps it queued quietly; a spinner there would turn until the sign-in is fixed.
        if (!_workers.TryGetValue(m.AccountId, out var worker) || worker.Status.State == SyncState.Error) return;
        lock (_busyLock) _sending[m.Id] = m.AccountId;
        BusyChanged?.Invoke(m.Id);
    }

    /// <summary>After a worker sent its queued changes (or failed to): messages with none left waiting stop being busy.</summary>
    private async Task OnChangesSentAsync(AccountSync worker)
    {
        try
        {
            List<long> mine;
            lock (_busyLock) mine = _sending.Where(kv => kv.Value == worker.Account.Id).Select(kv => kv.Key).ToList();
            if (mine.Count == 0) return;
            HashSet<long> waiting = worker.Status.State == SyncState.Error ? []
                : (await ops.GetByAccountAsync(worker.Account.Id)).Select(op => op.MessageId).ToHashSet();
            foreach (var id in mine.Where(id => !waiting.Contains(id)))
            {
                lock (_busyLock) _sending.Remove(id);
                BusyChanged?.Invoke(id);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checking the sent changes failed");
        }
    }

    /// <summary>Gmail labels of a message, fetched live (empty for other accounts or when offline).</summary>
    public async Task<IReadOnlyList<string>> GetLabelsAsync(MessageSummary m, CancellationToken ct = default) =>
        _workers.TryGetValue(m.AccountId, out var worker) && worker.Account.Provider == ProviderKind.Gmail ? await worker.GetLabelsAsync(m, ct) : [];

    public Task RemoveLabelAsync(MessageSummary m, string label, CancellationToken ct = default) =>
        _workers.TryGetValue(m.AccountId, out var worker) ? worker.RemoveLabelAsync(m, label, ct) : Task.CompletedTask;

    // ───────────────────────── sending: everything goes through the Outbox ─────────────────────────

    /// <summary>
    /// (accountId, subject, why) a message in the Outbox couldn't go: the server refused it, or the sign-in. It stays in
    /// the Outbox; refused ones aren't tried again until the user asks (Refresh, or sending something else).
    /// </summary>
    public event Action<long, string, string>? SendFailed;

    /// <summary>
    /// (accountId, messageId, subject) the connection broke as a message was going out and it isn't in Sent on the server:
    /// only the user can say whether to send it again (<see cref="SendNowAsync"/>) or that it arrived
    /// (<see cref="MarkSentAsync"/>). It waits in the Outbox meanwhile.
    /// </summary>
    public event Action<long, long, string>? SendUnsure;

    // Messages the user has been asked about (asked again after a restart).
    private readonly ConcurrentDictionary<long, byte> _unsure = new();

    /// <summary>How soon a message that may have gone out is looked for on the server, and again while it isn't there.</summary>
    internal TimeSpan LookAgain { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>How long it may take to turn up in Sent (Gmail's search can lag behind) before the user is asked.</summary>
    internal TimeSpan LookFor { get; set; } = TimeSpan.FromMinutes(2);
    /// <summary>Tests only: says whether a message is in Sent on the server (null: can't tell now).</summary>
    internal Func<string, bool?>? SentLookup { get; set; }

    // Outbox messages the server refused (id -> why); left alone until the user retries.
    private readonly ConcurrentDictionary<long, (long AccountId, string Why)> _refused = new();
    // Accounts whose outgoing sign-in was turned down: no retrying on our own (servers lock out repeated failures).
    private readonly ConcurrentDictionary<long, byte> _sendPaused = new();
    // Per account: 0 idle, 1 sending, 2 sending and asked again (one more round once this one ends).
    private readonly Dictionary<long, int> _flushing = [];
    private readonly HashSet<long> _beingSent = [];

    /// <summary>(accountId, messageId, subject, when it goes) a message was put in the Outbox: for Undo, or "will be sent at".</summary>
    public event Action<long, long, string, DateTimeOffset>? Queued;

    /// <summary>
    /// Sends a message: it's kept in the account's Outbox first (on disk, so nothing is lost offline or in a crash)
    /// and goes out at <paramref name="sendAt"/>, or else after the few seconds Undo send allows (Settings > Mail);
    /// when the server can't be reached then, as soon as it can. Sent mail is filed in Sent.
    /// </summary>
    /// <param name="sendAt">Send later; <see cref="DateTimeOffset.Now"/> for no Undo delay.</param>
    /// <returns>The message's id in the Outbox.</returns>
    public async Task<long> SendAsync(Account account, MimeMessage message, DateTimeOffset? sendAt = null, CancellationToken ct = default)
    {
        var at = sendAt ?? DateTimeOffset.Now.AddSeconds(Math.Max(0, settings?.Current.UndoSendSeconds ?? 0));
        var outbox = await EnsureOutboxAsync(account.Id, ct);
        // In the Outbox a message's date is when it goes; the Date header is set when it really does.
        var id = await StoreLocalAsync(outbox, message, ct, date: at);
        FolderSynced?.Invoke(account.Id, outbox.Id);
        Queued?.Invoke(account.Id, id, message.Subject ?? "", at);
        RetryOutbox(account.Id);
        return id;
    }

    /// <summary>
    /// A message waiting in the Outbox (Undo send's seconds, or Send later) goes now; so does one that may have gone out
    /// already, as the user asked for it.
    /// </summary>
    public async Task SendNowAsync(MessageSummary m)
    {
        lock (_busyLock) if (_beingSent.Contains(m.Id)) return; // it's going
        if (await messages.GetAsync(m.Id) is { SendState: SendState.Started })
        {
            await messages.SetSendStateAsync(m.Id, SendState.Waiting);
            _unsure.TryRemove(m.Id, out _);
        }
        await messages.SetDateAsync(m.Id, DateTimeOffset.Now);
        RetryOutbox(m.AccountId);
    }

    /// <summary>The user knows a message did go out (see <see cref="SendUnsure"/>): it's filed as sent.</summary>
    public async Task MarkSentAsync(long messageId)
    {
        lock (_busyLock) if (_beingSent.Contains(messageId)) return;
        if (await messages.GetAsync(messageId) is not { SendState: SendState.Started } m) return;
        await messages.SetSendStateAsync(m.Id, SendState.Accepted);
        KickOutbox(m.AccountId);
    }

    // Per account: the wake-up for the next message whose time hasn't come yet.
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _dueTimers = new();

    private void WakeAt(long accountId, DateTimeOffset? due)
    {
        var cts = new CancellationTokenSource();
        if (_dueTimers.TryGetValue(accountId, out var old)) old.Cancel();
        if (due is null)
        {
            _dueTimers.TryRemove(accountId, out _);
            return;
        }
        _dueTimers[accountId] = cts;
        var wait = due.Value - DateTimeOffset.Now;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromDays(20) ? TimeSpan.FromDays(20) : wait, cts.Token); }
            catch (OperationCanceledException) { return; }
            KickOutbox(accountId);
        });
    }

    /// <summary>Why the server refused an Outbox message, or null.</summary>
    public string? SendError(long messageId) => _refused.TryGetValue(messageId, out var r) ? r.Why : null;

    /// <summary>Messages waiting in Outboxes, all accounts.</summary>
    public async Task<int> CountOutboxAsync(CancellationToken ct = default)
    {
        var total = 0;
        foreach (var account in await accounts.GetAllAsync(ct))
            if ((await folders.GetByAccountAsync(account.Id, ct)).FirstOrDefault(f => f.Role == FolderRole.Outbox) is { } outbox)
                total += (await messages.GetByFolderAsync(outbox.Id, 1000, ct: ct)).Count;
        return total;
    }

    /// <summary>The user asked (Refresh, Send, a new password): refused messages and a paused sign-in get another go.</summary>
    private void RetryOutbox(long accountId)
    {
        _sendPaused.TryRemove(accountId, out _);
        foreach (var (id, r) in _refused) if (r.AccountId == accountId) _refused.TryRemove(id, out _);
        KickOutbox(accountId);
    }

    private void KickOutbox(long accountId)
    {
        if (_sendPaused.ContainsKey(accountId)) return;
        lock (_flushing)
        {
            if (_flushing.GetValueOrDefault(accountId) > 0)
            {
                _flushing[accountId] = 2;
                return;
            }
            _flushing[accountId] = 1;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                try { await FlushOutboxAsync(accountId); }
                catch (Exception ex) { _log.LogWarning(ex, "Sending from the Outbox failed"); }
                lock (_flushing)
                {
                    if (_flushing[accountId] == 2)
                    {
                        _flushing[accountId] = 1;
                        continue;
                    }
                    _flushing.Remove(accountId);
                    return;
                }
            }
        });
    }

    private async Task FlushOutboxAsync(long accountId)
    {
        if (await accounts.GetAsync(accountId) is not { } account) return;
        var accountFolders = await folders.GetByAccountAsync(accountId);
        if (accountFolders.FirstOrDefault(f => f.Role == FolderRole.Outbox) is not { } outbox) return;
        var all = (await messages.GetByFolderAsync(outbox.Id, 1000, sort: new ListSort(ListSortKey.Date, Descending: false)))
            .Where(m => !_refused.ContainsKey(m.Id)).ToList();
        // Ones whose time hasn't come (Undo's seconds, Send later) wait for it.
        var now = DateTimeOffset.Now.AddSeconds(0.5);
        var due = all.Where(m => m.Date > now).Select(m => (DateTimeOffset?)m.Date).Min();
        WakeAt(accountId, due);
        var waiting = all.Where(m => m.Date <= now).ToList();
        if (waiting.Count == 0) return;

        MailKit.Net.Smtp.SmtpClient? smtp = null;
        var unsure = false;
        try
        {
            foreach (var m in waiting)
            {
                MimeMessage mime;
                await using (var stream = bodies.Open(m.Id))
                {
                    if (stream is null)
                    {
                        _log.LogWarning("Outbox message {Id} has no content; removed", m.Id);
                        await messages.DeleteAsync(m.Id);
                        continue;
                    }
                    mime = await MimeMessage.LoadAsync(stream);
                }
                // Opened for editing, deleted or put off while the ones before it went out.
                if (await messages.GetAsync(m.Id) is not { } current || current.FolderId != outbox.Id || current.Date > DateTimeOffset.Now.AddSeconds(0.5)) continue;
                if (current.SendState != SendState.Waiting)
                {
                    unsure |= await SettleAsync(account, accountFolders, current, mime);
                    FolderSynced?.Invoke(accountId, outbox.Id);
                    continue;
                }
                // Dated now, as it really goes (it may have waited); the copy kept for Sent says the same.
                mime.Date = DateTimeOffset.Now;
                using (var dated = new MemoryStream())
                {
                    await mime.WriteToAsync(dated);
                    dated.Position = 0;
                    await bodies.SaveAsync(m.Id, dated);
                }

                lock (_busyLock) _beingSent.Add(m.Id);
                BusyChanged?.Invoke(m.Id);
                try
                {
                    smtp ??= await connector.ConnectSmtpAsync(account, CancellationToken.None);
                    await messages.SetSendStateAsync(m.Id, SendState.Started);
                    var handover = new Handover();
                    try { await smtp.SendAsync(mime, CancellationToken.None, handover); }
                    // Turned down, or the connection broke before the server had all of it: it hasn't gone out. Otherwise
                    // it stays Started, and is looked for on the server before anything else (SettleAsync).
                    catch (Exception ex) when (ex is MailKit.Net.Smtp.SmtpCommandException || !handover.MayHaveArrived)
                    {
                        await messages.SetSendStateAsync(m.Id, SendState.Waiting);
                        throw;
                    }
                    catch (Exception)
                    {
                        unsure = true;
                        throw;
                    }
                    await messages.SetSendStateAsync(m.Id, SendState.Accepted);
                }
                catch (MailKit.Net.Smtp.SmtpCommandException ex) when ((int)ex.StatusCode >= 500 && ex.ErrorCode != MailKit.Net.Smtp.SmtpErrorCode.UnexpectedStatusCode)
                {
                    // This message, for good (an address that doesn't exist, too big…): the rest still go.
                    _log.LogWarning(ex, "The server refused Outbox message {Id}", m.Id);
                    _refused[m.Id] = (accountId, ex.Message);
                    SendFailed?.Invoke(accountId, m.Subject, ex.Message);
                    continue;
                }
                finally
                {
                    lock (_busyLock) _beingSent.Remove(m.Id);
                    BusyChanged?.Invoke(m.Id);
                }
                await FileSentAsync(account, accountFolders, m);
                FolderSynced?.Invoke(accountId, outbox.Id);
            }
        }
        catch (MailKit.Security.AuthenticationException ex)
        {
            _sendPaused[accountId] = 0;
            _log.LogWarning(ex, "Outgoing sign-in failed for {Email}", account.Email);
            SendFailed?.Invoke(accountId, "", AccountDiscovery.Explain(ex, account.SmtpHost, account.Auth));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Offline, the server busy (4xx), a dropped connection: everything stays and goes on the next try.
            _log.LogInformation(ex, "Outbox of {Email} waits: {Reason}", account.Email, ex.Message);
        }
        finally
        {
            if (smtp is not null)
            {
                try { if (smtp.IsConnected) await smtp.DisconnectAsync(true); }
                catch (Exception) { }
                smtp.Dispose();
            }
            if (unsure)
            {
                var soon = DateTimeOffset.Now + LookAgain;
                WakeAt(accountId, due < soon ? due : soon);
            }
        }
    }

    /// <summary>
    /// A message that was already handed to the server: filed as sent when the server took it or has it in Sent. Not
    /// there, it isn't sent again by itself (servers other than Gmail mostly don't file it, so that says little): the
    /// user is asked.
    /// </summary>
    /// <returns>Whether to look again soon.</returns>
    private async Task<bool> SettleAsync(Account account, IReadOnlyList<Folder> accountFolders, MessageSummary m, MimeMessage mime)
    {
        if (m.SendState == SendState.Accepted)
        {
            await FileSentAsync(account, accountFolders, m);
            return false;
        }
        if (_unsure.ContainsKey(m.Id)) return false;

        var sent = accountFolders.FirstOrDefault(f => f.Role == FolderRole.Sent);
        if (sent is not null && !string.IsNullOrEmpty(mime.MessageId))
        {
            lock (_busyLock) _beingSent.Add(m.Id); // not to be edited while it's looked for
            BusyChanged?.Invoke(m.Id);
            bool? found;
            try
            {
                found = await IsInSentAsync(account, sent, mime.MessageId);
                if (found == true)
                {
                    _log.LogInformation("Outbox message {Id} had gone out before the connection broke", m.Id);
                    await messages.DeleteAsync(m.Id); // the server's copy comes in with Sent
                    bodies.Delete(m.Id);
                    if (_workers.TryGetValue(account.Id, out var worker)) worker.SyncSoon(sent.Id);
                    return false;
                }
            }
            finally
            {
                lock (_busyLock) _beingSent.Remove(m.Id);
                BusyChanged?.Invoke(m.Id);
            }
            if (found is null) return false; // the server can't be reached: after the next sync
            if (DateTimeOffset.Now - mime.Date < LookFor) return true;
        }
        _log.LogWarning("Outbox message {Id} may have gone out or not; asking", m.Id);
        if (_unsure.TryAdd(m.Id, 0)) SendUnsure?.Invoke(account.Id, m.Id, m.Subject);
        return false;
    }

    /// <summary>Whether the server has a message in Sent; null when it can't be asked now.</summary>
    private async Task<bool?> IsInSentAsync(Account account, Folder sent, string messageId)
    {
        if (SentLookup is { } lookup) return lookup(messageId);
        try
        {
            using var imap = await connector.ConnectImapAsync(account, CancellationToken.None);
            var folder = await imap.GetFolderAsync(sent.Path);
            await folder.OpenAsync(FolderAccess.ReadOnly);
            var found = await folder.SearchAsync(MailKit.Search.SearchQuery.HeaderContains("Message-ID", messageId));
            await imap.DisconnectAsync(true);
            return found.Count > 0;
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Can't look for a message in Sent of {Email} yet", account.Email);
            return null;
        }
    }

    /// <summary>Follows a message on its way to the server: until all of it is there, the server can't have taken it.</summary>
    private sealed class Handover : ITransferProgress
    {
        // The size is worked out before the last touches (dots doubled at line starts), so near the end counts as all of it.
        private const long Margin = 4096;

        public bool MayHaveArrived { get; private set; }

        public void Report(long bytesTransferred, long totalSize)
        {
            if (bytesTransferred >= totalSize - Margin) MayHaveArrived = true;
        }

        public void Report(long bytesTransferred) => MayHaveArrived = true; // no size to compare with
    }

    /// <summary>A message just went out: Gmail files it in Sent itself; elsewhere it moves to Sent and is uploaded there.</summary>
    private async Task FileSentAsync(Account account, IReadOnlyList<Folder> accountFolders, MessageSummary m)
    {
        _unsure.TryRemove(m.Id, out _);
        var sent = accountFolders.FirstOrDefault(f => f.Role == FolderRole.Sent);
        if (sent is null || account.Provider == ProviderKind.Gmail)
        {
            await messages.DeleteAsync(m.Id);
            bodies.Delete(m.Id);
            if (sent is not null && _workers.TryGetValue(account.Id, out var worker)) worker.SyncSoon(sent.Id);
            return;
        }
        await messages.MoveAsync(m.Id, sent.Id);
        await ops.AddAsync(new PendingOp { AccountId = account.Id, Kind = PendingOpKind.Append, MessageId = m.Id });
        FolderSynced?.Invoke(account.Id, sent.Id);
        Nudge(account.Id);
    }

    private async Task<Folder> EnsureOutboxAsync(long accountId, CancellationToken ct)
    {
        if ((await folders.GetByAccountAsync(accountId, ct)).FirstOrDefault(f => f.Role == FolderRole.Outbox) is { } outbox) return outbox;
        await folders.UpsertAsync(new Folder
        {
            AccountId = accountId, Path = Folder.OutboxPath, Name = L.T("Outbox"), Role = FolderRole.Outbox, IsSynced = false,
        }, ct);
        FoldersChanged?.Invoke(accountId);
        return (await folders.GetByAccountAsync(accountId, ct)).First(f => f.Role == FolderRole.Outbox);
    }

    /// <summary>Keeps a message here, in a folder of its account: listed, searchable and readable before any server has it.</summary>
    /// <param name="date">The list's date for it when it isn't the message's own (in the Outbox: when it goes).</param>
    private async Task<long> StoreLocalAsync(Folder folder, MimeMessage message, CancellationToken ct, DateTimeOffset? date = null)
    {
        using var eml = new MemoryStream();
        await message.WriteToAsync(eml, ct);
        var from = message.From.Mailboxes.FirstOrDefault();
        var text = MessageRenderer.ExtractText(message);
        return await messages.AddLocalAsync(new MessageSummary
        {
            AccountId = folder.AccountId, FolderId = folder.Id,
            MessageId = message.MessageId, InReplyTo = message.InReplyTo,
            ThreadKey = ThreadKeys.For(message.MessageId, message.References, message.InReplyTo),
            Subject = message.Subject ?? "", FromName = from?.Name ?? "", FromAddress = from?.Address ?? "",
            To = string.Join(", ", message.To.Mailboxes.Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.Address : a.Name)),
            Recipients = string.Join(", ", message.To.Mailboxes.Concat(message.Cc.Mailboxes).Select(a => a.ToString())),
            Date = date ?? (message.Date == default ? DateTimeOffset.Now : message.Date),
            Preview = text.Length > 200 ? text[..200].ReplaceLineEndings(" ").Trim() : text.ReplaceLineEndings(" ").Trim(),
            IsRead = true, HasAttachments = message.Attachments.Any(), Size = eml.Length,
        }, text, id =>
        {
            eml.Position = 0;
            return bodies.SaveAsync(id, eml, ct);
        }, ct);
    }

    /// <summary>
    /// Saves (or replaces) a draft in the account's Drafts folder: here at once, on the server as soon as it can be
    /// reached (earlier copies of it are replaced there too). False when the account has no Drafts folder.
    /// </summary>
    public async Task<bool> SaveDraftAsync(long accountId, MimeMessage message, CancellationToken ct = default)
    {
        if ((await folders.GetByAccountAsync(accountId, ct)).FirstOrDefault(f => f.Role == FolderRole.Drafts) is not { } drafts) return false;
        if (message.Date == default) message.Date = DateTimeOffset.Now;
        // The new copy first, then the earlier ones go: there's a draft here whatever happens in between.
        var id = await StoreLocalAsync(drafts, message, ct);
        if (!string.IsNullOrEmpty(message.MessageId)) await DropLocalCopiesAsync(drafts, message.MessageId, keepId: id, ct);
        await ops.AddAsync(new PendingOp
        {
            AccountId = accountId, Kind = PendingOpKind.SaveDraft, MessageId = id, FolderPath = drafts.Path, HeaderMessageId = message.MessageId,
        }, ct);
        FolderSynced?.Invoke(accountId, drafts.Id);
        Nudge(accountId);
        return true;
    }

    /// <summary>Deletes every saved version of a draft from the account's Drafts folder: here at once, then on the server.</summary>
    public async Task DeleteDraftAsync(long accountId, string messageId, CancellationToken ct = default)
    {
        if ((await folders.GetByAccountAsync(accountId, ct)).FirstOrDefault(f => f.Role == FolderRole.Drafts) is not { } drafts) return;
        await DropLocalCopiesAsync(drafts, messageId, keepId: 0, ct);
        await ops.AddAsync(new PendingOp
        {
            AccountId = accountId, Kind = PendingOpKind.DeleteDraft, FolderPath = drafts.Path, HeaderMessageId = messageId,
        }, ct);
        FolderSynced?.Invoke(accountId, drafts.Id);
        Nudge(accountId);
    }

    /// <summary>A draft's earlier copies, here: rows, contents and any upload still queued for them.</summary>
    private async Task DropLocalCopiesAsync(Folder drafts, string messageId, long keepId, CancellationToken ct)
    {
        var gone = await messages.DeleteByMessageIdAsync(drafts.Id, messageId, keepId, ct);
        foreach (var id in gone) bodies.Delete(id);
        await ops.RemoveUploadsAsync(gone, ct);
    }

    // ───────────────────────── folder management ─────────────────────────

    private AccountSync Worker(long accountId) =>
        _workers.TryGetValue(accountId, out var worker) ? worker : throw new InvalidOperationException(L.T("This account isn't connected."));

    /// <returns>The new folder's server path.</returns>
    public Task<string> CreateFolderAsync(long accountId, string? parentPath, string name, CancellationToken ct = default) =>
        Worker(accountId).CreateFolderAsync(parentPath, name, ct);

    /// <returns>The folder's new server path.</returns>
    public Task<string> RenameFolderAsync(Folder folder, string newName, CancellationToken ct = default) =>
        Worker(folder.AccountId).RenameFolderAsync(folder.Path, newName, ct);

    /// <param name="newParentPath">The folder to move it into; null = top level.</param>
    /// <returns>The folder's new server path.</returns>
    public Task<string> MoveFolderAsync(Folder folder, string? newParentPath, CancellationToken ct = default) =>
        Worker(folder.AccountId).MoveFolderAsync(folder.Path, newParentPath, ct);

    public Task DeleteFolderAsync(Folder folder, CancellationToken ct = default) =>
        Worker(folder.AccountId).DeleteFolderAsync(folder.Path, ct);

    public Task EmptyFolderAsync(Folder folder, CancellationToken ct = default) =>
        Worker(folder.AccountId).EmptyFolderAsync(folder, ct);

    public Task MarkFolderReadAsync(Folder folder, CancellationToken ct = default) =>
        Worker(folder.AccountId).MarkFolderReadAsync(folder, ct);

    // ───────────────────────── local-first actions ─────────────────────────

    public async Task SetFlagsAsync(MessageSummary m, bool isRead, bool isFlagged)
    {
        await messages.SetFlagsAsync(m.Id, isRead, isFlagged);
        if ((await FolderAsync(m.AccountId, m.FolderId))?.Role == FolderRole.Outbox) return; // no server has it
        await ops.AddAsync(new PendingOp { AccountId = m.AccountId, Kind = PendingOpKind.Flags, MessageId = m.Id });
        Sending(m);
        Nudge(m.AccountId);
    }

    /// <summary>
    /// Moves a message to another folder of its account. Nothing goes into the Outbox this way, and out of it only to
    /// Drafts (not sent after all: it's kept as a draft, and uploaded there).
    /// </summary>
    public async Task<bool> MoveAsync(MessageSummary m, Folder target)
    {
        if (target.Id == m.FolderId || target.Role == FolderRole.Outbox) return false;
        if ((await FolderAsync(m.AccountId, m.FolderId))?.Role == FolderRole.Outbox)
        {
            lock (_busyLock)
                if (target.Role != FolderRole.Drafts || _beingSent.Contains(m.Id)) return false;
            _refused.TryRemove(m.Id, out _);
            _unsure.TryRemove(m.Id, out _);
            await messages.MoveAsync(m.Id, target.Id);
            await ops.AddAsync(new PendingOp
            {
                AccountId = m.AccountId, Kind = PendingOpKind.SaveDraft, MessageId = m.Id, FolderPath = target.Path, HeaderMessageId = m.MessageId,
            });
            Nudge(m.AccountId);
            return true;
        }
        if (m.Uid > 0)
        {
            var source = await FolderAsync(m.AccountId, m.FolderId);
            if (source is null) return false;
            await ops.AddAsync(new PendingOp
            {
                AccountId = m.AccountId, Kind = PendingOpKind.Move, MessageId = m.Id,
                FolderPath = source.Path, Uid = m.Uid, TargetPath = target.Path,
            });
        }
        else if (await ops.GetPendingMoveAsync(m.Id) is { } pending)
        {
            await ops.UpdateAsync(pending with { TargetPath = target.Path }); // moved again before the server caught up
        }
        await messages.MoveAsync(m.Id, target.Id);
        Nudge(m.AccountId);
        return true;
    }

    /// <summary>Copies a message to another folder of the same account (it appears there after the next sync).</summary>
    public async Task<bool> CopyAsync(MessageSummary m, Folder target)
    {
        if (target.Id == m.FolderId || m.Uid <= 0) return false; // a message still being moved has no server copy yet
        var source = await FolderAsync(m.AccountId, m.FolderId);
        if (source is null) return false;
        await ops.AddAsync(new PendingOp
        {
            AccountId = m.AccountId, Kind = PendingOpKind.Copy, MessageId = m.Id,
            FolderPath = source.Path, Uid = m.Uid, TargetPath = target.Path,
        });
        Sending(m);
        Nudge(m.AccountId);
        return true;
    }

    public async Task DeleteForeverAsync(MessageSummary m)
    {
        if (m.Uid > 0)
        {
            if (await FolderAsync(m.AccountId, m.FolderId) is { } source)
                await ops.AddAsync(new PendingOp
                {
                    AccountId = m.AccountId, Kind = PendingOpKind.Delete, MessageId = m.Id, FolderPath = source.Path, Uid = m.Uid,
                });
        }
        else if (await ops.GetPendingMoveAsync(m.Id) is { } pending)
        {
            // Never reached its destination on the server: delete it where it still is.
            await ops.UpdateAsync(pending with { Kind = PendingOpKind.Delete, TargetPath = null });
        }
        else
        {
            await ops.RemoveUploadsAsync([m.Id]); // kept only here (unsent, or a draft or sent copy not uploaded yet)
        }
        await messages.DeleteAsync(m.Id);
        bodies.Delete(m.Id);
        _refused.TryRemove(m.Id, out _);
        _unsure.TryRemove(m.Id, out _);
        Nudge(m.AccountId);
    }

    // ───────────────────────── mail rules ─────────────────────────

    /// <summary>What a rules pass did.</summary>
    public sealed record RulesResult(int Matched, int MovedOut);

    // New inbox mail goes through the rules before anyone hears about it, so a message a rule files away
    // doesn't pop up a "new mail" notification.
    private void OnWorkerSynced(AccountSync worker, int newInbox, bool changed)
    {
        KickOutbox(worker.Account.Id); // also after a failed pass: sending may work when the incoming server doesn't

        var fresh = worker.TakeFreshInbox();
        var rules = settings?.Current.Rules.Where(r => r.Enabled).ToList() ?? [];
        if (fresh.Count == 0 || (rules.Count == 0 && settings?.Current.BlockedSenders is not { Count: > 0 }))
        {
            Synced?.Invoke(worker.Account.Id, newInbox);
            if (changed) MailChanged?.Invoke(worker.Account.Id);
            return;
        }
        _ = Task.Run(async () =>
        {
            var movedOut = 0;
            try
            {
                movedOut = await FileNewMailAsync(fresh, rules);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Running mail rules failed");
            }
            Synced?.Invoke(worker.Account.Id, Math.Max(0, newInbox - movedOut));
            MailChanged?.Invoke(worker.Account.Id);
        });
    }

    /// <summary>New inbox mail through blocked senders and the rules; how many messages left the inbox.</summary>
    internal async Task<int> FileNewMailAsync(IReadOnlyList<(long FolderId, List<long> Uids)> fresh, IReadOnlyList<MailRule> rules)
    {
        var movedOut = 0;
        foreach (var (folderId, uids) in fresh)
            movedOut += (await ApplyRulesAsync(await messages.GetByUidsAsync(folderId, uids), rules, fetchBodies: true, holdInView: true)).MovedOut;
        return movedOut;
    }

    /// <summary>Moves mail from blocked senders out of a folder (after blocking someone, for what's already there).</summary>
    public async Task<int> MoveBlockedAsync(Folder folder, CancellationToken ct = default) =>
        (await ApplyRulesAsync(await messages.GetByFolderAsync(folder.Id, 5000, ct: ct), [], fetchBodies: false, ct)).MovedOut;

    /// <summary>Runs rules on mail already in a folder ("Run now").</summary>
    public async Task<RulesResult> RunRulesAsync(IReadOnlyList<MailRule> rules, Folder folder, CancellationToken ct = default) =>
        await ApplyRulesAsync(await messages.GetByFolderAsync(folder.Id, 5000, ct: ct), rules.Where(r => r.Enabled).ToList(), fetchBodies: true, ct);

    /// <summary>Which recent inbox messages a rule would match (body conditions use bodies already downloaded).</summary>
    public async Task<IReadOnlyList<MessageSummary>> PreviewRuleAsync(MailRule rule, int recent = 500, CancellationToken ct = default)
    {
        var inbox = await messages.GetUnifiedInboxAsync(recent, ct: ct);
        var needsBody = RuleMatcher.NeedsBody(rule);
        return inbox.Where(m =>
        {
            var (text, links) = needsBody ? CachedContent(m.Id) : default;
            return RuleMatcher.Matches(rule, m, text, links);
        }).ToList();
    }

    // ───── the message being read ─────
    // New mail that just showed up in a list may already be open in the reading pane (in Unread, say) when the rules get
    // to it. Filing it away right then would pull it out from under the reader, so it stays put until the pane moves
    // on to another message, and goes through the rules then.
    private readonly Lock _viewLock = new();
    private long? _inView;
    private readonly Dictionary<long, long> _heldBack = []; // message id -> the folder it was in when held

    /// <summary>The reading pane shows this message now (null: none). A message the rules held back while it was shown
    /// goes through them once the pane leaves it.</summary>
    public void SetInView(long? messageId)
    {
        long? release = null;
        long folderId = 0;
        lock (_viewLock)
        {
            if (_inView == messageId) return;
            if (_inView is { } previous && _heldBack.Remove(previous, out folderId)) release = previous;
            _inView = messageId;
        }
        if (release is { } id) _ = Task.Run(() => RunHeldBackAsync(id, folderId));
    }

    private bool HoldIfInView(MessageSummary m)
    {
        lock (_viewLock)
        {
            if (_inView != m.Id) return false;
            _heldBack[m.Id] = m.FolderId;
            return true;
        }
    }

    private async Task RunHeldBackAsync(long messageId, long folderId)
    {
        try
        {
            // Moved or deleted while it was open: it's been dealt with by hand.
            if (await messages.GetAsync(messageId) is not { } m || m.FolderId != folderId) return;
            var rules = settings?.Current.Rules.Where(r => r.Enabled).ToList() ?? [];
            if ((await ApplyRulesAsync([m], rules, fetchBodies: true, holdInView: true)).MovedOut > 0)
            {
                Synced?.Invoke(m.AccountId, 0);
                MailChanged?.Invoke(m.AccountId); // the lists drop it
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Running mail rules on a message after reading it failed");
        }
    }

    /// <param name="holdInView">New mail: leave the message open in the reading pane where it is for now (see SetInView).</param>
    private async Task<RulesResult> ApplyRulesAsync(IReadOnlyList<MessageSummary> list, IReadOnlyList<MailRule> rules, bool fetchBodies,
        CancellationToken ct = default, bool holdInView = false)
    {
        int matched = 0, movedOut = 0;
        var folderCache = new Dictionary<long, IReadOnlyList<Folder>>();
        async Task<IReadOnlyList<Folder>> FoldersOf(long accountId) =>
            folderCache.TryGetValue(accountId, out var f) ? f : folderCache[accountId] = await folders.GetByAccountAsync(accountId, ct);

        // Blocked senders first: their mail goes to Junk (or Trash) and no rule runs on it.
        var blocked = settings?.Current.BlockedSenders ?? [];
        var blockedRole = settings?.Current.BlockedToTrash == true ? FolderRole.Trash : FolderRole.Junk;

        foreach (var original in list)
        {
            ct.ThrowIfCancellationRequested();
            var m = original;
            if (blocked.Count > 0 && blocked.Any(b => Addresses.Covers(b, m.FromAddress)))
            {
                if (holdInView && HoldIfInView(m)) continue;
                if ((await FoldersOf(m.AccountId)).FirstOrDefault(f => f.Role == blockedRole) is { } away && away.Id != m.FolderId
                    && await MoveAsync(m, away))
                {
                    matched++;
                    movedOut++;
                }
                continue;
            }
            string? body = null;
            IReadOnlyCollection<string>? links = null;
            var bodyLoaded = false;
            var hit = false;
            foreach (var rule in rules)
            {
                if (RuleMatcher.NeedsBody(rule) && !bodyLoaded)
                {
                    bodyLoaded = true;
                    if (fetchBodies) await EnsureBodyAsync(m, ct);
                    (body, links) = CachedContent(m.Id);
                }
                if (!RuleMatcher.Matches(rule, m, body, links)) continue;
                // Checked at the first match, after any body download (it may have been opened meanwhile), and only
                // there: once a rule has acted, the rest follow, so nothing (a copy) is done twice.
                if (!hit && holdInView && HoldIfInView(m)) break;
                hit = true;
                m = await ApplyActionsAsync(rule, m, await FoldersOf(m.AccountId));
                if (rule.StopAfter) break;
            }
            if (hit) matched++;
            if (m.FolderId != original.FolderId) movedOut++;
        }
        return new RulesResult(matched, movedOut);
    }

    // Flags first, then copies, then at most one move (the last one wins), so the order in the editor doesn't matter.
    private async Task<MessageSummary> ApplyActionsAsync(MailRule rule, MessageSummary m, IReadOnlyList<Folder> accountFolders)
    {
        var read = m.IsRead || rule.Actions.Any(a => a.Kind == RuleActionKind.MarkRead);
        var flagged = m.IsFlagged || rule.Actions.Any(a => a.Kind == RuleActionKind.Flag);
        if (read != m.IsRead || flagged != m.IsFlagged)
        {
            await SetFlagsAsync(m, read, flagged);
            m = m with { IsRead = read, IsFlagged = flagged };
        }

        Folder? ByPath(RuleAction a) => a.FolderAccountId == m.AccountId
            ? accountFolders.FirstOrDefault(f => string.Equals(f.Path, a.FolderPath, StringComparison.Ordinal))
            : null; // a folder of another account: rules can't move mail between accounts
        foreach (var copy in rule.Actions.Where(a => a.Kind == RuleActionKind.CopyTo))
            if (ByPath(copy) is { } target) await CopyAsync(m, target);

        Folder? destination = null;
        foreach (var action in rule.Actions)
        {
            var target = action.Kind switch
            {
                RuleActionKind.MoveTo => ByPath(action),
                RuleActionKind.Archive => accountFolders.FirstOrDefault(f => f.Role == FolderRole.Archive)
                                          ?? accountFolders.FirstOrDefault(f => f.Role == FolderRole.All),
                RuleActionKind.Delete => accountFolders.FirstOrDefault(f => f.Role == FolderRole.Trash), // never deletes for good
                RuleActionKind.MoveToJunk => accountFolders.FirstOrDefault(f => f.Role == FolderRole.Junk),
                _ => null,
            };
            if (target is not null) destination = target;
        }
        if (destination is not null && destination.Id != m.FolderId && await MoveAsync(m, destination))
            m = m with { FolderId = destination.Id };
        return m;
    }

    /// <summary>A downloaded message's text and web links (for rules); nulls when it isn't downloaded.</summary>
    private (string? Text, IReadOnlyCollection<string>? Links) CachedContent(long messageId)
    {
        try
        {
            using var stream = bodies.Open(messageId);
            if (stream is null) return default;
            var mime = MimeMessage.Load(stream);
            // The links only matter when there are lists to check them against (parsing the HTML isn't free).
            return (MessageRenderer.ExtractText(mime, 200_000), ThreatLists.Current.IsEmpty ? [] : ThreatLists.LinksIn(mime));
        }
        catch (Exception)
        {
            return default;
        }
    }

    private async Task<Folder?> FolderAsync(long accountId, long folderId) =>
        (await folders.GetByAccountAsync(accountId)).FirstOrDefault(f => f.Id == folderId);

    private void Nudge(long accountId)
    {
        if (_workers.TryGetValue(accountId, out var worker)) worker.Request(SyncScope.PushChanges);
    }

    private void StartWorker(Account account)
    {
        var worker = new AccountSync(account, connector, folders, messages, ops, bodies, loggerFactory.CreateLogger<AccountSync>(),
            () => TimeSpan.FromMinutes(Math.Max(1, settings?.Current.SyncIntervalMinutes ?? 5)));
        worker.Synced += OnWorkerSynced;
        worker.StatusChanged += w =>
        {
            StatusChanged?.Invoke(w.Account.Id, w.Status);
            if (w.Status.State == SyncState.Error) _ = OnChangesSentAsync(w);
        };
        worker.ChangesSent += w => _ = OnChangesSentAsync(w);
        worker.FoldersChanged += w => FoldersChanged?.Invoke(w.Account.Id);
        worker.FolderSynced += (w, folderId) => FolderSynced?.Invoke(w.Account.Id, folderId);
        if (_workers.TryAdd(account.Id, worker)) worker.Start();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workers.Values) await worker.DisposeAsync().ConfigureAwait(false);
        _workers.Clear();
    }

    // The DI container disposes synchronously on app exit; run the async shutdown off the UI thread with a cap.
    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
}
