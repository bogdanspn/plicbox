using System.Threading.Channels;
using Plicbox.Core;
using MailKit;
using MessageSummary = Plicbox.Core.MessageSummary;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Plicbox.Mail;

public enum SyncState { Idle, Syncing, Error }

/// <param name="NeedsSignIn">The server turned the sign-in down: only "Sign in again" fixes it, so the app offers it.</param>
public sealed record AccountStatus(SyncState State, string? Error = null, DateTimeOffset? LastSync = null, bool NeedsSignIn = false);

/// <summary>How much a sync pass does; requests are coalesced to the largest one pending.</summary>
/// <summary>What a sync pass covers; requests that pile up merge into the widest. Backfill brings in the rest of a long
/// history quietly (Refresh doesn't turn for it).</summary>
public enum SyncScope { PushChanges = 0, Backfill = 1, Inbox = 2, Full = 3 }

public enum SyncStep { Starting, CheckingConnection, Connecting, SendingChanges, ListingFolders, Opening, ListingMessages, FlagChanges, NewMessages }

/// <summary>What a running sync pass is doing right now; <see cref="ToString"/> is the watchdog's log text.</summary>
public sealed record SyncActivity(SyncScope Scope, DateTimeOffset Started, SyncStep Step, string? Folder = null, string? FolderPath = null,
    int Done = 0, int Total = 0, int Missing = 0)
{
    public override string ToString() => Step switch
    {
        SyncStep.Starting => "starting",
        SyncStep.CheckingConnection => "checking the connection",
        SyncStep.Connecting => "connecting",
        SyncStep.SendingChanges => "sending local changes",
        SyncStep.ListingFolders => "listing folders",
        SyncStep.Opening => $"{FolderPath}: opening",
        SyncStep.ListingMessages => $"{FolderPath}: listing messages",
        SyncStep.FlagChanges => $"{FolderPath}: flag changes",
        _ => $"{FolderPath}: new messages {Done}/{Total} ({Missing} missing)",
    };
}

/// <summary>A folder still fetching its older mail: how many messages are here so far, of how many on the server.</summary>
public sealed record SyncBacklog(string Folder, int Have, int OnServer);

/// <summary>A finished sync pass: when, how long, how many folders it looked at and how many of those were untouched.</summary>
public sealed record SyncPassSummary(SyncScope Scope, DateTimeOffset Finished, TimeSpan Took, int Folders, int Unchanged);

/// <summary>One account's sync at a glance, for Operations.</summary>
public sealed record SyncSnapshot(AccountStatus Status, SyncActivity? Current, IReadOnlyList<SyncBacklog> Backlog, SyncPassSummary? LastPass,
    bool WatchingInbox);

/// <summary>
/// Background worker for one account. Keeps three IMAP connections: one for sync passes, one for on-demand
/// body downloads/appends, and one parked in IDLE on the inbox so new mail arrives immediately.
/// </summary>
internal sealed class AccountSync : IAsyncDisposable
{
    private const int FetchBatch = 200;
    // Older mail fetched per folder per pass: Gmail sends headers with previews at about 13 a second, so a folder's
    // whole history (16 000 in Important) at once would hold the inbox and the spinner for twenty minutes.
    private const int MaxNewPerPass = 400;
    private const int PrefetchPerPass = 300; // offline download: bodies fetched per background pass
    private static readonly TimeSpan IdleRestart = TimeSpan.FromMinutes(9); // RFC 2177: re-issue before 29 min
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private const MessageSummaryItems HeaderItems =
        MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope | MessageSummaryItems.Flags |
        MessageSummaryItems.BodyStructure | MessageSummaryItems.Size | MessageSummaryItems.InternalDate |
        MessageSummaryItems.PreviewText | MessageSummaryItems.References;

    private readonly MailConnector _connector;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly IPendingOpRepository _ops;
    private readonly IBodyStore _bodies;
    private readonly ILogger _log;
    private readonly Func<TimeSpan> _fullSyncInterval;
    private readonly SemaphoreSlim _prefetching = new(1, 1);

    private readonly Channel<SyncScope> _requests = Channel.CreateUnbounded<SyncScope>();
    private readonly SemaphoreSlim _fetchLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private ImapClient? _syncClient;
    private ImapClient? _fetchClient;
    private Task? _loop;
    private Task? _idle;
    // After a rejected login, stop retrying on our own (cPanel's cPHulk and others lock out repeated failures).
    private volatile bool _authFailed;
    // Connections can die without noticing (the computer slept, the network changed): these make the next use
    // start on a fresh one, and the IDLE connection is torn down and reopened.
    private volatile bool _reconnectSync, _reconnectFetch;
    private CancellationTokenSource _idleKick = new();
    private DateTime _syncClientUsed;
    private static readonly TimeSpan CheckAfterQuiet = TimeSpan.FromMinutes(5);

    public AccountSync(Account account, MailConnector connector, IFolderRepository folders, IMessageRepository messages,
        IPendingOpRepository ops, IBodyStore bodies, ILogger log, Func<TimeSpan>? fullSyncInterval = null)
    {
        Account = account;
        (_connector, _folders, _messages, _ops, _bodies, _log) = (connector, folders, messages, ops, bodies, log);
        _fullSyncInterval = fullSyncInterval ?? (() => TimeSpan.FromMinutes(5));
    }

    public Account Account { get; }
    public AccountStatus Status { get; private set; } = new(SyncState.Idle);

    /// <summary>Raised after each sync pass with the number of new inbox messages.</summary>
    public event Action<AccountSync, int>? Synced;

    // New inbox mail found since the last TakeFreshInbox(): (folder, server UIDs), for mail rules.
    private readonly List<(long FolderId, List<long> Uids)> _freshInbox = [];
    private List<long> _lastNewUids = [];

    /// <summary>The genuinely new inbox messages found by sync passes since the last call (and forgets them).</summary>
    public IReadOnlyList<(long FolderId, List<long> Uids)> TakeFreshInbox()
    {
        lock (_freshInbox)
        {
            var taken = _freshInbox.ToList();
            _freshInbox.Clear();
            return taken;
        }
    }
    /// <summary>The folder list was refreshed from the server.</summary>
    public event Action<AccountSync>? FoldersChanged;
    /// <summary>Queued local changes were sent to the server (those it refused stay queued).</summary>
    public event Action<AccountSync>? ChangesSent;
    /// <summary>One folder finished syncing (fired as a long first sync progresses).</summary>
    public event Action<AccountSync, long>? FolderSynced;

    // Folders that only sync while the user looks at them (Gmail's All Mail, Important), once opened this session.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _watched = new();

    // Folders to bring up to date in the next pass, right after Inbox (Drafts once a draft was saved or removed).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _soon = new();

    // Folders with older mail still to fetch: Backfill passes bring it in, a few hundred at a time.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, SyncBacklog> _backlog = new();

    /// <summary>Starts syncing an on-demand folder the user just opened.</summary>
    public void Watch(long folderId)
    {
        if (_watched.TryAdd(folderId, 0)) Request(SyncScope.Inbox);
    }
    public event Action<AccountSync>? StatusChanged;

    public void Start()
    {
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _idle = Task.Run(() => IdleLoopAsync(_cts.Token));
        Request(SyncScope.Full);
    }

    /// <summary>The computer woke up or the network came back: drop every connection, reopen them and catch up.</summary>
    public void Reconnect()
    {
        _reconnectSync = _reconnectFetch = true;
        Interlocked.Exchange(ref _idleKick, new CancellationTokenSource()).Cancel();
        Request(SyncScope.Full);
    }

    public void Request(SyncScope scope, bool userInitiated = false)
    {
        if (userInitiated) _authFailed = false;
        _requests.Writer.TryWrite(scope);
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(15);
        var retriedFresh = false;
        while (!ct.IsCancellationRequested)
        {
            SyncScope scope;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                wait.CancelAfter(_fullSyncInterval());
                try { scope = await _requests.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    if (_authFailed) continue; // periodic pass suppressed until the user retries
                    scope = SyncScope.Full;
                }
            }
            while (_requests.Reader.TryRead(out var more)) scope = (SyncScope)Math.Max((int)scope, (int)more);
            if (_authFailed && scope == SyncScope.PushChanges) continue; // keep queued; nothing will work until sign-in is fixed

            var reused = false;
            using var watchdog = new CancellationTokenSource();
            _passScope = scope;
            _passStarted = DateTimeOffset.Now;
            Step(SyncStep.Starting);
            _ = WatchPassAsync(scope, watchdog.Token);
            try
            {
                if (scope is not (SyncScope.PushChanges or SyncScope.Backfill)) SetStatus(Status with { State = SyncState.Syncing });
                if (_reconnectSync)
                {
                    _reconnectSync = false;
                    DropClient(ref _syncClient);
                }
                // A connection left alone for a while may have died quietly: a quick NOOP says so in seconds instead
                // of the next command waiting out the full timeout.
                if (_syncClient is { IsConnected: true } existing && DateTime.UtcNow - _syncClientUsed > CheckAfterQuiet && !await IsAliveAsync(existing, ct))
                    DropClient(ref _syncClient);
                reused = _syncClient is { IsConnected: true, IsAuthenticated: true };
                Step(reused ? SyncStep.CheckingConnection : SyncStep.Connecting);
                var client = await EnsureConnectedAsync(_syncClient, ct);
                _syncClient = client;
                Step(SyncStep.SendingChanges);
                await RunPendingOpsAsync(client, ct);
                ChangesSent?.Invoke(this);
                if (scope == SyncScope.PushChanges)
                {
                    if (Status.State == SyncState.Error) SetStatus(Status with { State = SyncState.Idle, Error = null, NeedsSignIn = false });
                    continue;
                }
                var newInbox = await SyncFoldersAsync(client, scope, ct);
                _syncClientUsed = DateTime.UtcNow;
                backoff = TimeSpan.FromSeconds(15);
                retriedFresh = false;
                SetStatus(new AccountStatus(SyncState.Idle, null, DateTimeOffset.Now));
                Synced?.Invoke(this, newInbox);
                if (!_backlog.IsEmpty) _requests.Writer.TryWrite(SyncScope.Backfill); // the next few hundred, quietly
                if (Account.OfflineDownload) _ = Task.Run(() => PrefetchBodiesAsync(ct), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (AuthenticationException ex)
            {
                _authFailed = true;
                _log.LogWarning(ex, "Sign-in failed for {Email}", Account.Email);
                DropClient(ref _syncClient);
                SetStatus(new AccountStatus(SyncState.Error, AccountDiscovery.Explain(ex, Account.ImapHost, Account.Auth), Status.LastSync, NeedsSignIn: true));
            }
            catch (Exception ex) when (reused && !retriedFresh && IsConnectionLost(ex))
            {
                // The connection we kept had died (after sleep, typically): straight on with a fresh one, no error shown.
                _log.LogInformation("Connection for {Email} was gone; reconnecting", Account.Email);
                DropClient(ref _syncClient);
                retriedFresh = true;
                _requests.Writer.TryWrite(scope);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Sync failed for {Email}", Account.Email);
                DropClient(ref _syncClient);
                SetStatus(new AccountStatus(SyncState.Error, AccountDiscovery.Explain(ex, Account.ImapHost, Account.Auth), Status.LastSync));
                Synced?.Invoke(this, 0); // local changes may still need a refresh
                try { await Task.Delay(backoff, ct); } catch (OperationCanceledException) { break; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
                _requests.Writer.TryWrite(scope);
            }
            finally
            {
                watchdog.Cancel();
                _activity = null;
            }
        }
    }

    /// <summary>What the running sync pass is doing (null between passes), for the watchdog's log line and Operations.</summary>
    public SyncActivity? Activity => _activity;
    private volatile SyncActivity? _activity;
    private SyncScope _passScope;
    private DateTimeOffset _passStarted;

    private void Step(SyncStep step, Folder? folder = null, int done = 0, int total = 0, int missing = 0) =>
        _activity = new SyncActivity(_passScope, _passStarted, step, folder?.Name, folder?.Path, done, total, missing);

    /// <summary>Folders still fetching older mail, for Operations.</summary>
    public IReadOnlyList<SyncBacklog> Backlog => _backlog.Values.OrderBy(b => b.Folder, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>The last pass that finished, for Operations.</summary>
    public SyncPassSummary? LastPass { get; private set; }

    /// <summary>
    /// A pass still running after half a minute says so in the log, every half minute, with the step it's on: a
    /// Refresh that keeps turning then shows where it waits.
    /// </summary>
    private async Task WatchPassAsync(SyncScope scope, CancellationToken passDone)
    {
        var started = DateTime.UtcNow;
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), passDone);
                _log.LogWarning("Sync of {Email} ({Scope}) still running after {Seconds:0} s: {Step}",
                    Account.Email, scope, (DateTime.UtcNow - started).TotalSeconds, _activity);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ───────────────────────── pending local changes -> server ─────────────────────────

    private async Task RunPendingOpsAsync(ImapClient client, CancellationToken ct)
    {
        var ops = await _ops.GetByAccountAsync(Account.Id, ct);
        if (ops.Count == 0) return;
        var paths = (await _folders.GetByAccountAsync(Account.Id, ct)).ToDictionary(f => f.Id, f => f.Path);

        foreach (var op in ops)
        {
            try
            {
                if (await ApplyAsync(client, op, paths, ct)) await _ops.RemoveAsync(op.Id, ct);
            }
            catch (Exception ex) when (ex is ImapCommandException or FolderNotFoundException or MessageNotFoundException)
            {
                // The server refused this particular change; retry a few times, then give up on it.
                if (op.Attempts + 1 >= 5)
                {
                    _log.LogWarning(ex, "Dropping pending {Kind} for message {Id} after repeated failures", op.Kind, op.MessageId);
                    await _ops.RemoveAsync(op.Id, ct);
                }
                else
                {
                    await _ops.UpdateAsync(op with { Attempts = op.Attempts + 1 }, ct);
                }
            }
        }
    }

    /// <returns>True when the op is finished (applied or obsolete); false to keep it for a later pass.</returns>
    private async Task<bool> ApplyAsync(ImapClient client, PendingOp op, Dictionary<long, string> paths, CancellationToken ct)
    {
        switch (op.Kind)
        {
            case PendingOpKind.Flags:
            {
                var m = await _messages.GetAsync(op.MessageId, ct);
                if (m is null) return true;
                if (m.Uid <= 0) return false; // waiting for its move to complete
                if (!paths.TryGetValue(m.FolderId, out var path)) return true;
                var folder = await OpenAsync(client, path, FolderAccess.ReadWrite, ct);
                var uid = new UniqueId((uint)m.Uid);
                await folder.StoreAsync(uid, new StoreFlagsRequest(m.IsRead ? StoreAction.Add : StoreAction.Remove, MessageFlags.Seen) { Silent = true }, ct);
                await folder.StoreAsync(uid, new StoreFlagsRequest(m.IsFlagged ? StoreAction.Add : StoreAction.Remove, MessageFlags.Flagged) { Silent = true }, ct);
                return true;
            }
            case PendingOpKind.Move:
            {
                var source = await OpenAsync(client, op.FolderPath!, FolderAccess.ReadWrite, ct);
                var target = await client.GetFolderAsync(op.TargetPath!, ct);
                var newUid = await source.MoveToAsync(new UniqueId((uint)op.Uid), target, ct);
                if (newUid is { } u)
                {
                    await _messages.ConfirmUidAsync(op.MessageId, u.Id, ct);
                }
                else
                {
                    // No UIDPLUS: we can't know the new UID, so let the next sync of the target pick it up fresh.
                    await _messages.DeleteAsync(op.MessageId, ct);
                    _bodies.Delete(op.MessageId);
                }
                return true;
            }
            case PendingOpKind.Copy:
            {
                // The copy shows up locally when the target folder syncs; the original stays where it is.
                var source = await OpenAsync(client, op.FolderPath!, FolderAccess.ReadOnly, ct);
                var target = await client.GetFolderAsync(op.TargetPath!, ct);
                await source.CopyToAsync(new UniqueId((uint)op.Uid), target, ct);
                Request(SyncScope.Full);
                return true;
            }
            case PendingOpKind.Delete:
            {
                var folder = await OpenAsync(client, op.FolderPath!, FolderAccess.ReadWrite, ct);
                var uid = new UniqueId((uint)op.Uid);
                await folder.StoreAsync(uid, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
                if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await folder.ExpungeAsync([uid], ct);
                else await folder.ExpungeAsync(ct);
                return true;
            }
            case PendingOpKind.Append or PendingOpKind.SaveDraft:
                return await UploadAsync(client, op, paths, ct);
            case PendingOpKind.DeleteDraft:
            {
                if (op.FolderPath is null || string.IsNullOrEmpty(op.HeaderMessageId)) return true;
                var folder = await OpenAsync(client, op.FolderPath, FolderAccess.ReadWrite, ct);
                await DeleteByMessageIdAsync(client, folder, op.HeaderMessageId, keep: null, ct, keepNewest: false);
                return true;
            }
            default:
                return true;
        }
    }

    /// <summary>
    /// Uploads a message kept here (sent mail filed in Sent, a draft) to the folder it's in now. The local row then takes
    /// the UID the server gave it, so nothing is downloaded again. A draft replaces its earlier copies in Drafts.
    /// </summary>
    private async Task<bool> UploadAsync(ImapClient client, PendingOp op, Dictionary<long, string> paths, CancellationToken ct)
    {
        var m = await _messages.GetAsync(op.MessageId, ct);
        if (m is null || m.Uid > 0) return true; // deleted meanwhile (or a newer save took its place), or already there
        if (!paths.TryGetValue(m.FolderId, out var path) || path == Folder.OutboxPath) return true;

        MimeMessage mime;
        try
        {
            await using var stream = _bodies.Open(m.Id);
            if (stream is null) return true; // nothing left to upload
            mime = await MimeMessage.LoadAsync(stream, ct);
        }
        catch (FormatException ex)
        {
            _log.LogWarning(ex, "Message {Id} couldn't be read for uploading; dropped", m.Id);
            return true;
        }

        var draft = op.Kind == PendingOpKind.SaveDraft;
        var inDrafts = draft && path == op.FolderPath; // moved elsewhere before it went up: no longer a draft there
        var folder = await OpenAsync(client, path, FolderAccess.ReadWrite, ct);
        var added = await folder.AppendAsync(new AppendRequest(mime, inDrafts ? MessageFlags.Draft | MessageFlags.Seen : MessageFlags.Seen), ct);
        if (draft && op.FolderPath is { } draftsPath && !string.IsNullOrEmpty(op.HeaderMessageId))
        {
            var drafts = inDrafts ? folder : await OpenAsync(client, draftsPath, FolderAccess.ReadWrite, ct);
            await DeleteByMessageIdAsync(client, drafts, op.HeaderMessageId, inDrafts ? added : null, ct, keepNewest: inDrafts);
        }

        if (added is { } uid)
        {
            await _messages.ConfirmUidAsync(m.Id, uid.Id, ct);
        }
        else
        {
            // No UIDPLUS: the server doesn't say where it went, so the folder's next sync brings it in.
            await _messages.DeleteAsync(m.Id, ct);
            _bodies.Delete(m.Id);
            SyncSoon(m.FolderId);
        }
        FolderSynced?.Invoke(this, m.FolderId);
        return true;
    }

    // ───────────────────────── server -> local ─────────────────────────

    private async Task<int> SyncFoldersAsync(ImapClient client, SyncScope scope, CancellationToken ct)
    {
        var full = scope == SyncScope.Full;
        var backfill = scope == SyncScope.Backfill;
        var folders = await _folders.GetByAccountAsync(Account.Id, ct);
        if (full || folders.Count == 0)
        {
            Step(SyncStep.ListingFolders);
            await RefreshFolderListAsync(client, ct);
            folders = await _folders.GetByAccountAsync(Account.Id, ct);
        }

        var newInbox = 0;
        var pass = System.Diagnostics.Stopwatch.StartNew();
        var timings = new List<(string Path, long Ms, bool Skipped)>();
        IEnumerable<Folder> targets = folders
            .Where(f => (f.IsSynced && (full || backfill || f.Role == FolderRole.Inbox)) || _watched.ContainsKey(f.Id) || _soon.ContainsKey(f.Id))
            .OrderBy(f => f.Role != FolderRole.Inbox)
            .ThenBy(f => !_soon.ContainsKey(f.Id))
            .ThenBy(f => !_watched.ContainsKey(f.Id)); // the folder the user is looking at goes next
        if (backfill)
        {
            // One folder per quiet pass, so new mail and the user's own Refresh wait half a minute at most.
            var behind = targets.Where(f => _backlog.ContainsKey(f.Id)).ToList();
            foreach (var id in _backlog.Keys.Except(behind.Select(f => f.Id))) _backlog.TryRemove(id, out _); // gone, or no longer synced
            targets = behind.Take(1);
        }
        foreach (var folder in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var started = pass.ElapsedMilliseconds;
                var added = await SyncFolderAsync(client, folder, backfill, ct);
                timings.Add((folder.Path, pass.ElapsedMilliseconds - started, _lastSkipped));
                _soon.TryRemove(folder.Id, out _);
                if (folder.Role == FolderRole.Inbox)
                {
                    newInbox += added;
                    if (_lastNewUids.Count > 0)
                        lock (_freshInbox) _freshInbox.Add((folder.Id, _lastNewUids));
                }
                FolderSynced?.Invoke(this, folder.Id);
            }
            catch (FolderNotFoundException)
            {
                _backlog.TryRemove(folder.Id, out _);
                _log.LogInformation("Folder {Path} vanished during sync", folder.Path);
            }
        }
        LastPass = new SyncPassSummary(scope, DateTimeOffset.Now, pass.Elapsed, timings.Count, timings.Count(t => t.Skipped));
        // A slow pass says where the time went (the spinner turns as long as a sync runs).
        if (pass.ElapsedMilliseconds > 1500)
            _log.LogInformation("Sync of {Email} ({Scope}) took {Ms} ms: {Checked} folders, {Unchanged} unchanged; slowest {Slowest}",
                Account.Email, scope, pass.ElapsedMilliseconds, timings.Count, timings.Count(t => t.Skipped),
                string.Join(", ", timings.OrderByDescending(t => t.Ms).Take(3).Select(t => $"{t.Path} {t.Ms} ms")));
        return newInbox;
    }

    private async Task RefreshFolderListAsync(ImapClient client, CancellationToken ct)
    {
        var ns = client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', "");
        var ignored = Account.IgnoredPaths;
        var all = (await client.GetFoldersAsync(ns, StatusItems.None, subscribedOnly: false, ct))
            .Append(client.Inbox)
            .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .Where(f => !FolderRoles.IsIgnored(f.FullName, f.DirectorySeparator, ignored))
            .ToList();

        var keep = new List<string>();
        foreach (var (folder, role) in FolderRoles.Assign(all, client.Inbox, Account.FolderOverrides))
        {
            var name = role == FolderRole.Other
                ? FolderRoles.FriendlyPath(folder.FullName, folder.DirectorySeparator)
                : FolderRoles.DisplayName(role, folder.Name);
            var isVirtual = FolderRoles.IsVirtual(folder, role);
            await _folders.UpsertAsync(new Folder
            {
                AccountId = Account.Id, Path = folder.FullName, Name = name, Role = role,
                IsVirtual = isVirtual,
                IsSynced = !isVirtual, // Gmail's views would re-download every other folder; sync them only when opened
            }, ct);
            keep.Add(folder.FullName);
        }
        DropBodies(await _folders.DeleteMissingAsync(Account.Id, keep, ct));
        FoldersChanged?.Invoke(this);
    }

    /// <returns>Number of genuinely new messages (0 on a folder's first sync, so it doesn't trigger notifications).</returns>
    /// <summary>The last folder synced was found untouched and skipped.</summary>
    private bool _lastSkipped;
    /// <summary>Per folder: when its messages were last compared in full (accounts with a sync period).</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime> _fullCheck = new();
    private static readonly TimeSpan FullCheckEvery = TimeSpan.FromHours(6);

    private async Task<int> SyncFolderAsync(ImapClient client, Folder local, bool backfill, CancellationToken ct)
    {
        Step(SyncStep.Opening, local);
        // Selected afresh even if it's still open from the last pass: the counts below must be the server's now, not
        // what was known when it was opened (the server tells of changes only in reply to a command).
        var already = await client.GetFolderAsync(local.Path, ct);
        if (already.IsOpen) await already.CloseAsync(false, ct);
        var folder = await OpenAsync(client, local.Path, FolderAccess.ReadOnly, ct);
        long previousLastUid = local.LastUid, modSeq = local.HighestModSeq;
        _lastSkipped = false;

        // Untouched since the last pass: same numbering, same mod-sequence (new mail and flag changes raise it) and the
        // same number of messages (a deletion lowers it). Then there's nothing to fetch, and the UID SEARCH, which lists
        // every message in the folder (tens of thousands in a long-kept inbox), is skipped. With a sync period, a full
        // check still runs every few hours so mail that has aged out of it is let go.
        if (local.UidValidity == folder.UidValidity && local.HighestModSeq > 0 && folder.Supports(FolderFeature.ModSequences)
            && (long)folder.HighestModSeq == local.HighestModSeq && folder.Count == local.ServerCount
            && (Account.SyncPeriodDays <= 0 || DateTime.UtcNow - _fullCheck.GetValueOrDefault(local.Id) < FullCheckEvery))
        {
            _lastSkipped = true;
            _lastNewUids = [];
            if (local.Role == FolderRole.Sent) await BackfillRecipientsAsync(folder, local, ct);
            return 0;
        }
        _fullCheck[local.Id] = DateTime.UtcNow;

        if (local.UidValidity != folder.UidValidity)
        {
            // The server renumbered the folder: everything cached for it is meaningless now.
            if (local.UidValidity != 0) DropBodies(await _messages.WipeFolderAsync(local.Id, ct));
            previousLastUid = 0;
            modSeq = 0;
        }

        // One UID SEARCH (limited to the account's sync period) drives new-message, expunge and pruning detection.
        var query = Account.SyncPeriodDays > 0
            ? SearchQuery.DeliveredAfter(DateTime.Today.AddDays(-Account.SyncPeriodDays))
            : SearchQuery.All;
        Step(SyncStep.ListingMessages, local);
        var serverUids = (await folder.SearchAsync(query, ct)).OrderBy(u => u.Id).ToList();
        var serverSet = serverUids.Select(u => (long)u.Id).ToHashSet();
        var localUids = (await _messages.GetUidsAsync(local.Id, ct)).ToHashSet();

        // Gone from the server, or now outside the sync period.
        DropBodies(await _messages.DeleteByUidsAsync(local.Id, localUids.Where(u => !serverSet.Contains(u)).ToList(), ct));

        // Flag changes (read elsewhere, flagged on the phone, ...) for messages we already have.
        var known = localUids.Where(serverSet.Contains).ToList();
        if (known.Count > 0)
        {
            var range = new UniqueIdRange(new UniqueId((uint)known.Min()), new UniqueId((uint)known.Max()));
            Step(SyncStep.FlagChanges, local);
            var flagItems = MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | GmailExtras(client);
            var changed = folder.Supports(FolderFeature.ModSequences) && modSeq > 0
                ? await folder.FetchAsync(range, (ulong)modSeq, flagItems, ct)
                : await folder.FetchAsync(range, flagItems, ct);
            await _messages.ApplyServerFlagsAsync(local.Id, changed
                .Where(s => s.Flags.HasValue && localUids.Contains(s.UniqueId.Id))
                .Select(s => ((long)s.UniqueId.Id, s.Flags!.Value.HasFlag(MessageFlags.Seen), s.Flags.Value.HasFlag(MessageFlags.Flagged)))
                .ToList(), ct);
            // Gmail: Important comes and goes with the labels (they change the mod-sequence too).
            await _messages.ApplyServerImportanceAsync(local.Id, changed
                .Where(s => s.GMailLabels is not null && localUids.Contains(s.UniqueId.Id))
                .Select(s => ((long)s.UniqueId.Id, IsImportant(s)))
                .ToList(), ct);
        }

        // Gmail inbox: Important for the newest mail every pass (a few KB), so mail stored before this was kept, or marked
        // by Gmail later without a change we'd see, gets its dot.
        if (local.Role == FolderRole.Inbox && GmailExtras(client) != MessageSummaryItems.None && known.Count > 0)
        {
            var recent = known.OrderByDescending(u => u).Take(200).Select(u => new UniqueId((uint)u)).ToList();
            var marks = await folder.FetchAsync(recent, MessageSummaryItems.UniqueId | MessageSummaryItems.GMailLabels, ct);
            await _messages.ApplyServerImportanceAsync(local.Id, marks.Where(s => s.GMailLabels is not null).Select(s => ((long)s.UniqueId.Id, IsImportant(s))).ToList(), ct);
        }

        // Whatever is missing locally, newest first: recent mail shows up first, and an interrupted first sync simply
        // continues where it stopped. A long history comes a few hundred at a time; once a folder is behind, an ordinary
        // pass fetches only mail newer than it has seen and leaves the older to the quiet Backfill passes.
        var missing = serverUids.Where(u => !localUids.Contains(u.Id)).Reverse().ToList();
        var toFetch = (_backlog.ContainsKey(local.Id) && !backfill ? missing.Where(u => u.Id > previousLastUid) : missing)
            .Take(MaxNewPerPass).ToList();
        var complete = toFetch.Count == missing.Count;
        var fetchedSoFar = 0;
        foreach (var batch in toFetch.Chunk(FetchBatch))
        {
            Step(SyncStep.NewMessages, local, fetchedSoFar, toFetch.Count, missing.Count);
            fetchedSoFar += batch.Length;
            var summaries = await folder.FetchAsync(batch, HeaderItems | GmailExtras(client), ct);
            await _messages.UpsertBatchAsync(summaries.Select(s => ToSummary(local, s)).ToList(), ct);
            if (toFetch.Count > FetchBatch) FolderSynced?.Invoke(this, local.Id); // progress during big syncs
        }
        if (complete) _backlog.TryRemove(local.Id, out _);
        else _backlog[local.Id] = new SyncBacklog(local.Name, known.Count + toFetch.Count, serverUids.Count);

        // Still behind: no server count kept, so the next pass doesn't take the folder for untouched and skip it.
        var lastUid = serverUids.Count > 0 ? serverUids[^1].Id : previousLastUid;
        await _folders.UpdateSyncStateAsync(local.Id, folder.UidValidity, lastUid, (long)folder.HighestModSeq, complete ? folder.Count : -1, ct);
        if (local.Role == FolderRole.Sent) await BackfillRecipientsAsync(folder, local, ct);
        _lastNewUids = previousLastUid == 0 ? [] : toFetch.Where(u => u.Id > previousLastUid).Select(u => (long)u.Id).ToList();
        return _lastNewUids.Count;
    }

    /// <summary>
    /// Sent mail synced before recipient addresses were kept: fetch just their envelopes (a few hundred bytes each),
    /// a thousand per pass, so contacts know who you write to.
    /// </summary>
    private async Task BackfillRecipientsAsync(IMailFolder folder, Folder local, CancellationToken ct)
    {
        var uids = await _messages.GetUidsWithoutRecipientsAsync(local.Id, 1000, ct);
        if (uids.Count == 0) return;
        try
        {
            var summaries = await folder.FetchAsync(uids.Select(u => new UniqueId((uint)u)).ToList(), MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope, ct);
            // No envelope: store "" so the message isn't asked for again on every pass.
            await _messages.SetRecipientsAsync(local.Id, summaries
                .Select(s => ((long)s.UniqueId.Id, s.Envelope is null ? ""
                    : string.Join(", ", s.Envelope.To.Mailboxes.Concat(s.Envelope.Cc.Mailboxes).Select(m => m.ToString()))))
                .ToList(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogInformation(ex, "Couldn't backfill recipients for {Path}", local.Path);
        }
    }

    internal static MessageSummary ToSummary(Folder folder, IMessageSummary s)
    {
        var env = s.Envelope;
        var from = env?.From.Mailboxes.FirstOrDefault();
        var flags = s.Flags ?? MessageFlags.None;
        return new MessageSummary
        {
            AccountId = folder.AccountId,
            FolderId = folder.Id,
            Uid = s.UniqueId.Id,
            MessageId = env?.MessageId,
            InReplyTo = env?.InReplyTo,
            ThreadKey = ThreadKeys.For(env?.MessageId, s.References, env?.InReplyTo),
            Subject = env?.Subject ?? "",
            FromName = from?.Name ?? "",
            FromAddress = from?.Address ?? "",
            To = string.Join(", ", env?.To.Mailboxes.Select(m => string.IsNullOrWhiteSpace(m.Name) ? m.Address : m.Name) ?? []),
            Recipients = env is null ? null : string.Join(", ", env.To.Mailboxes.Concat(env.Cc.Mailboxes).Select(m => m.ToString())),
            Date = env?.Date ?? s.InternalDate ?? DateTimeOffset.Now,
            Preview = (s.PreviewText ?? "").ReplaceLineEndings(" ").Trim(),
            IsRead = flags.HasFlag(MessageFlags.Seen),
            IsFlagged = flags.HasFlag(MessageFlags.Flagged),
            HasAttachments = s.Body is not null && s.Attachments.Any(),
            Size = s.Size ?? 0,
            IsImportant = IsImportant(s),
        };
    }

    /// <summary>Gmail's labels come along with headers and flags (they say what's Important).</summary>
    private static MessageSummaryItems GmailExtras(ImapClient client) =>
        client.Capabilities.HasFlag(ImapCapabilities.GMailExt1) ? MessageSummaryItems.GMailLabels : MessageSummaryItems.None;

    private static bool IsImportant(IMessageSummary s) => s.GMailLabels?.Contains("\\Important") == true;

    // ───────────────────────── on demand: bodies, appends ─────────────────────────

    public async Task<bool> FetchBodyAsync(MessageSummary m, CancellationToken ct)
    {
        if (m.Uid <= 0) return false; // still being moved on the server
        var path = (await _folders.GetByAccountAsync(Account.Id, ct)).FirstOrDefault(f => f.Id == m.FolderId)?.Path;
        if (path is null) return false;

        return await WithFetchClientAsync(async client =>
        {
            var folder = await OpenAsync(client, path, FolderAccess.ReadOnly, ct);
            var uid = new UniqueId((uint)m.Uid);
            MimeMessage mime;
            try { mime = await folder.GetMessageAsync(uid, ct); }
            catch (MessageNotFoundException)
            {
                // This connection keeps the folder open and hears of mail that arrived since only when it asks, so a
                // message that just came in is "not found" here: open the folder afresh and ask once more.
                await folder.CloseAsync(false, ct);
                await folder.OpenAsync(FolderAccess.ReadOnly, ct);
                try { mime = await folder.GetMessageAsync(uid, ct); }
                catch (MessageNotFoundException)
                {
                    _log.LogInformation("Message {Id} (UID {Uid}) is no longer in {Folder} on the server", m.Id, m.Uid, path);
                    return false;
                }
            }

            using var ms = new MemoryStream();
            await mime.WriteToAsync(ms, ct);
            ms.Position = 0;
            await _bodies.SaveAsync(m.Id, ms, ct);
            await _messages.IndexBodyAsync(m.Id, MessageRenderer.ExtractText(mime), ct);
            return true;
        }, ct);
    }

    /// <summary>Gmail: the message's labels right now (\Important, \Inbox, Bandcamp, …).</summary>
    public async Task<IReadOnlyList<string>> GetLabelsAsync(MessageSummary m, CancellationToken ct)
    {
        if (m.Uid <= 0) return [];
        var path = (await _folders.GetByAccountAsync(Account.Id, ct)).FirstOrDefault(f => f.Id == m.FolderId)?.Path;
        if (path is null) return [];
        return await WithFetchClientAsync<IReadOnlyList<string>>(async client =>
        {
            if (!client.Capabilities.HasFlag(ImapCapabilities.GMailExt1)) return [];
            var folder = await OpenAsync(client, path, FolderAccess.ReadOnly, ct);
            var found = await folder.FetchAsync([new UniqueId((uint)m.Uid)], MessageSummaryItems.GMailLabels, ct);
            if (found.Count == 0)
            {
                await client.NoOpAsync(ct); // mail newer than this connection's view of the folder (see FetchBodyAsync)
                found = await folder.FetchAsync([new UniqueId((uint)m.Uid)], MessageSummaryItems.GMailLabels, ct);
            }
            return found.FirstOrDefault()?.GMailLabels?.ToList() ?? [];
        }, ct);
    }

    /// <summary>Gmail: takes a label off the message (on the server, straight away).</summary>
    public async Task RemoveLabelAsync(MessageSummary m, string label, CancellationToken ct)
    {
        var path = (await _folders.GetByAccountAsync(Account.Id, ct)).FirstOrDefault(f => f.Id == m.FolderId)?.Path
                   ?? throw new InvalidOperationException(L.T("The message's folder is gone."));
        await WithFetchClientAsync(async client =>
        {
            var folder = await OpenAsync(client, path, FolderAccess.ReadWrite, ct);
            await folder.StoreAsync(new UniqueId((uint)m.Uid), new StoreLabelsRequest(StoreAction.Remove, [label]) { Silent = true }, ct);
            return true;
        }, ct);
        Request(SyncScope.Full); // the folders it's listed in change
    }

    /// <summary>
    /// Offline download: full messages for the account's offline period, newest first, Inbox before the other folders,
    /// in rounds of a few hundred with a breather between them, so a message opened meanwhile isn't kept waiting.
    /// </summary>
    private async Task PrefetchBodiesAsync(CancellationToken ct)
    {
        if (!await _prefetching.WaitAsync(0, ct)) return; // a pass is already running
        try
        {
            DateTimeOffset? since = Account.OfflineDays > 0 ? DateTimeOffset.Now.AddDays(-Account.OfflineDays) : null;
            var folders = (await _folders.GetByAccountAsync(Account.Id, ct))
                .Where(f => f.IsSynced && !f.IsVirtual && f.Role is not (FolderRole.Trash or FolderRole.Junk or FolderRole.Outbox))
                .OrderBy(f => f.Role != FolderRole.Inbox).ToList();
            var failed = new HashSet<long>(); // gone from the server meanwhile, or refused: not asked for again this pass
            var fetched = 0;
            foreach (var folder in folders)
            {
                while (!ct.IsCancellationRequested)
                {
                    var batch = (await _messages.GetWithoutBodyAsync(folder.Id, since, 100 + failed.Count, ct)).Where(m => !failed.Contains(m.Id)).ToList();
                    if (batch.Count == 0) break;
                    var already = new List<long>();
                    foreach (var m in batch)
                    {
                        using (var cached = _bodies.Open(m.Id))
                            if (cached is not null) { already.Add(m.Id); continue; } // downloaded before this was noted
                        if (await FetchBodyAsync(m, ct)) fetched++;
                        else failed.Add(m.Id);
                        if (fetched > 0 && fetched % PrefetchPerPass == 0) await Task.Delay(TimeSpan.FromSeconds(2), ct);
                    }
                    await _messages.MarkDownloadedAsync(already, ct);
                }
            }
            if (fetched > 0) _log.LogInformation("Offline download for {Email}: {Count} messages", Account.Email, fetched);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Offline download for {Email} paused", Account.Email); // retried after the next sync
        }
        finally
        {
            _prefetching.Release();
        }
    }

    /// <summary>Brings one folder up to date in a quick pass (Sent once Gmail has filed a message just sent there).</summary>
    public void SyncSoon(long folderId)
    {
        _soon.TryAdd(folderId, 0);
        Request(SyncScope.Inbox);
    }

    /// <param name="keep">The copy just uploaded (null when the server doesn't report it: then the newest is kept).</param>
    private static async Task DeleteByMessageIdAsync(ImapClient client, IMailFolder folder, string? messageId, UniqueId? keep,
        CancellationToken ct, bool keepNewest = true)
    {
        if (string.IsNullOrEmpty(messageId)) return;
        var found = await folder.SearchAsync(SearchQuery.HeaderContains("Message-ID", messageId), ct);
        if (keepNewest) keep ??= found.Count > 0 ? found.Max() : (UniqueId?)null;
        var old = found.Where(u => u != keep).ToList();
        if (old.Count == 0) return;
        await folder.StoreAsync(old, new StoreFlagsRequest(StoreAction.Add, MessageFlags.Deleted) { Silent = true }, ct);
        if (client.Capabilities.HasFlag(ImapCapabilities.UidPlus)) await folder.ExpungeAsync(old, ct);
        else await folder.ExpungeAsync(ct);
    }

    // ───────────────────────── folder management (online: errors go straight back to the user) ─────────────────────────

    /// <summary>Creates a folder under <paramref name="parentPath"/> (null = top level); returns its server path.</summary>
    public async Task<string> CreateFolderAsync(string? parentPath, string name, CancellationToken ct)
    {
        var path = await WithFetchClientAsync(async client =>
        {
            var parent = parentPath is null ? Root(client) : await client.GetFolderAsync(parentPath, ct);
            var created = await parent.CreateAsync(name, isMessageFolder: true, ct)
                          ?? throw new InvalidOperationException(L.T("The server didn't create the folder."));
            try { await created.SubscribeAsync(ct); } catch (ImapCommandException) { } // some servers don't do subscriptions
            // Show it now: the next full sync may be a while behind a long pass.
            await RefreshFolderListAsync(client, ct);
            return created.FullName;
        }, ct);
        Request(SyncScope.Full);
        return path;
    }

    /// <summary>Renames a folder where it is; returns its new server path.</summary>
    public Task<string> RenameFolderAsync(string path, string newName, CancellationToken ct) =>
        RelocateFolderAsync(path, (client, folder) => Task.FromResult((folder.ParentFolder ?? Root(client), newName)), ct);

    /// <summary>Moves a folder (with its subfolders) under another one; null = top level. Returns its new server path.</summary>
    public Task<string> MoveFolderAsync(string path, string? newParentPath, CancellationToken ct) =>
        RelocateFolderAsync(path, async (client, folder) =>
            (newParentPath is null ? Root(client) : await client.GetFolderAsync(newParentPath, ct), folder.Name), ct);

    private async Task<string> RelocateFolderAsync(string path, Func<ImapClient, IMailFolder, Task<(IMailFolder Parent, string Name)>> target,
        CancellationToken ct)
    {
        var newPath = await WithFetchClientAsync(async client =>
        {
            var folder = await client.GetFolderAsync(path, ct);
            if (folder.IsOpen) await folder.CloseAsync(false, ct);
            var (parent, name) = await target(client, folder);
            await folder.RenameAsync(parent, name, ct);
            await RefreshFolderListAsync(client, ct);
            return folder.FullName;
        }, ct);
        Request(SyncScope.Full);
        return newPath;
    }

    /// <summary>Deletes a folder, its subfolders and everything in them from the server.</summary>
    public async Task DeleteFolderAsync(string path, CancellationToken ct)
    {
        await WithFetchClientAsync(async client =>
        {
            async Task DeleteTree(IMailFolder folder)
            {
                foreach (var child in await folder.GetSubfoldersAsync(false, ct)) await DeleteTree(child);
                if (folder.IsOpen) await folder.CloseAsync(false, ct);
                await folder.DeleteAsync(ct);
            }
            await DeleteTree(await client.GetFolderAsync(path, ct));
            await RefreshFolderListAsync(client, ct);
            return true;
        }, ct);
        Request(SyncScope.Full);
    }

    /// <summary>Permanently deletes every message in a folder (Empty Trash / Empty Junk).</summary>
    public async Task EmptyFolderAsync(Folder local, CancellationToken ct)
    {
        await WithFetchClientAsync(async client =>
        {
            var folder = await OpenAsync(client, local.Path, FolderAccess.ReadWrite, ct);
            if (folder.Count == 0) return true;
            await folder.AddFlagsAsync(UniqueIdRange.All, MessageFlags.Deleted, silent: true, ct);
            await folder.ExpungeAsync(ct);
            return true;
        }, ct);
        DropBodies(await _messages.WipeFolderAsync(local.Id, ct));
        Request(SyncScope.Full);
    }

    /// <summary>Marks everything in a folder read, on the server and locally.</summary>
    public async Task MarkFolderReadAsync(Folder local, CancellationToken ct)
    {
        await WithFetchClientAsync(async client =>
        {
            var folder = await OpenAsync(client, local.Path, FolderAccess.ReadWrite, ct);
            var unread = await folder.SearchAsync(SearchQuery.NotSeen, ct);
            if (unread.Count > 0) await folder.AddFlagsAsync(unread, MessageFlags.Seen, silent: true, ct);
            return true;
        }, ct);
        await _messages.MarkFolderReadAsync(local.Id, ct);
    }

    private static IMailFolder Root(ImapClient client) =>
        client.GetFolder(client.PersonalNamespaces.Count > 0 ? client.PersonalNamespaces[0] : new FolderNamespace('/', ""));

    private async Task<T> WithFetchClientAsync<T>(Func<ImapClient, Task<T>> action, CancellationToken ct)
    {
        await _fetchLock.WaitAsync(ct);
        try
        {
            if (_reconnectFetch)
            {
                _reconnectFetch = false;
                DropClient(ref _fetchClient);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _fetchClient = await EnsureConnectedAsync(_fetchClient, ct);
                    return await action(_fetchClient);
                }
                catch (Exception ex) when (attempt == 0 && ex is IOException or ServiceNotConnectedException or ImapProtocolException)
                {
                    DropClient(ref _fetchClient); // stale connection: reconnect once
                }
            }
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    // ───────────────────────── IDLE: push for new mail ─────────────────────────

    private async Task IdleLoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(30);
        while (!ct.IsCancellationRequested)
        {
            ImapClient? client = null;
            var kick = _idleKick.Token;
            try
            {
                while (_authFailed) await Task.Delay(TimeSpan.FromSeconds(30), ct);
                client = await _connector.ConnectImapAsync(Account, ct);
                // Reconnect(): close this connection outright (a dead one wouldn't even answer the end of IDLE).
                var open = client;
                using var kicked = kick.Register(() => { try { open.Dispose(); } catch (Exception) { } });
                var inbox = client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, ct);
                delay = TimeSpan.FromSeconds(30);
                IsWatchingInbox = true;

                while (!ct.IsCancellationRequested && !_authFailed && !kick.IsCancellationRequested)
                {
                    using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var changed = false;
                    void OnCount(object? s, EventArgs e) { changed = true; done.Cancel(); }
                    void OnFlags(object? s, MessageFlagsChangedEventArgs e) { changed = true; done.Cancel(); }
                    void OnExpunge(object? s, MessageEventArgs e) { changed = true; done.Cancel(); }
                    inbox.CountChanged += OnCount;
                    inbox.MessageFlagsChanged += OnFlags;
                    inbox.MessageExpunged += OnExpunge;
                    try
                    {
                        if (client.Capabilities.HasFlag(ImapCapabilities.Idle))
                        {
                            done.CancelAfter(IdleRestart);
                            await client.IdleAsync(done.Token, ct);
                        }
                        else
                        {
                            await Task.Delay(TimeSpan.FromMinutes(2), ct);
                            await client.NoOpAsync(ct); // unsolicited updates arrive with the reply
                        }
                    }
                    finally
                    {
                        inbox.CountChanged -= OnCount;
                        inbox.MessageFlagsChanged -= OnFlags;
                        inbox.MessageExpunged -= OnExpunge;
                    }
                    if (changed) Request(SyncScope.Inbox);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (AuthenticationException)
            {
                _authFailed = true; // the sync loop reports it; wait for the user
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "IDLE connection for {Email} dropped", Account.Email);
                if (kick.IsCancellationRequested) continue; // closed on purpose: reopen right away
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, MaxBackoff.Ticks));
            }
            finally
            {
                IsWatchingInbox = false;
                client?.Dispose();
            }
        }
    }

    /// <summary>The inbox connection is open and waiting: new mail shows up the moment it arrives.</summary>
    public bool IsWatchingInbox { get; private set; }

    // ───────────────────────── helpers ─────────────────────────

    private async Task<ImapClient> EnsureConnectedAsync(ImapClient? client, CancellationToken ct)
    {
        if (client is { IsConnected: true, IsAuthenticated: true }) return client;
        client?.Dispose();
        return await _connector.ConnectImapAsync(Account, ct);
    }

    private static async Task<IMailFolder> OpenAsync(ImapClient client, string path, FolderAccess access, CancellationToken ct)
    {
        var folder = await client.GetFolderAsync(path, ct);
        if (!folder.IsOpen || folder.Access < access) await folder.OpenAsync(access, ct);
        return folder;
    }

    private void DropBodies(IReadOnlyList<long> ids)
    {
        foreach (var id in ids) _bodies.Delete(id);
    }

    /// <summary>A NOOP answered within a few seconds: the connection still works.</summary>
    private static async Task<bool> IsAliveAsync(ImapClient client, CancellationToken ct)
    {
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            await client.NoOpAsync(quick.Token);
            return true;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The connection itself failed (closed by the server, timed out, reset), not the request.</summary>
    private static bool IsConnectionLost(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is IOException or System.Net.Sockets.SocketException or TimeoutException or ServiceNotConnectedException or ImapProtocolException)
                return true;
        return false;
    }

    private static void DropClient(ref ImapClient? client)
    {
        client?.Dispose();
        client = null;
    }

    private void SetStatus(AccountStatus status)
    {
        if (status == Status) return;
        Status = status;
        StatusChanged?.Invoke(this);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await Task.WhenAll(_loop ?? Task.CompletedTask, _idle ?? Task.CompletedTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _syncClient?.Dispose();
        _fetchClient?.Dispose();
        _cts.Dispose();
    }
}
