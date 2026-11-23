using System.Collections.Concurrent;
using System.Threading.Channels;
using Plicbox.Auth;
using Plicbox.Core;
using Microsoft.Extensions.Logging;

namespace Plicbox.Dav;

/// <summary>
/// Keeps each Google account's contacts (and Gmail's automatically saved addresses) in the local database,
/// incrementally via sync tokens, and carries out edits (kept here and sent later when Google can't be reached).
/// Contacts not tied to a Google account live only here.
/// </summary>
public sealed class ContactsEngine(
    IAccountRepository accounts,
    IContactRepository repo,
    GoogleContactsApi api,
    ISettingsStore settings,
    ILogger<ContactsEngine> log) : IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<long, CalendarAccountStatus> _status = new();
    private readonly Channel<long?> _requests = Channel.CreateUnbounded<long?>();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _localOnly;

    /// <summary>Contacts changed (raised on a background thread).</summary>
    public event Action? Changed;
    public event Action? StatusChanged;
    public IReadOnlyDictionary<long, CalendarAccountStatus> Statuses => _status;

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        RequestSync();
    }

    /// <summary>Demo mode: no server; edits change the local database only.</summary>
    public void StartLocalOnly() => _localOnly = true;

    public void RequestSync(long? accountId = null)
    {
        if (!_localOnly) _requests.Writer.TryWrite(accountId);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            long? accountId = null;
            using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // Contacts change rarely: every half hour, or when asked.
                wait.CancelAfter(TimeSpan.FromMinutes(Math.Max(30, settings.Current.SyncIntervalMinutes)));
                try { accountId = await _requests.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            while (_requests.Reader.TryRead(out var more)) accountId = accountId == more ? accountId : null;
            try
            {
                foreach (var account in await accounts.GetAllAsync(ct))
                    if (account.Auth == AuthKind.GoogleOAuth && (accountId is null || accountId == account.Id))
                        await SyncAccountAsync(account, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Contacts sync failed"); }
        }
    }

    private async Task SyncAccountAsync(Account account, CancellationToken ct)
    {
        SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Syncing, null, _status.GetValueOrDefault(account.Id)?.LastSync));
        try
        {
            // Changes made offline go first; until they're through, what's here stays as it is.
            if (!await SendPendingAsync(account, ct))
            {
                SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Error,
                    L.F("Couldn't reach Google Contacts for {0}. Your changes are kept and will be sent on the next sync.", account.Email),
                    _status.GetValueOrDefault(account.Id)?.LastSync));
                return;
            }
            await SyncListAsync(account, ContactSource.Google, (token, c) => api.ListContactsAsync(account, token, c), ct);
            // Other contacts are a nice-to-have: an older consent without that scope shouldn't stop the rest.
            try { await SyncListAsync(account, ContactSource.GoogleOther, (token, c) => api.ListOtherContactsAsync(account, token, c), ct); }
            catch (CalendarPermissionException ex) { log.LogInformation(ex, "No access to other contacts for {Email}", account.Email); }
            SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Idle, null, DateTimeOffset.Now));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CalendarPermissionException ex) { SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.NeedsPermission, ex.Message)); }
        catch (GoogleSignInRequiredException ex) { SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.NeedsSignIn, ex.Message)); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Contacts sync failed for {Email}", account.Email);
            SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Error, ex.Message, _status.GetValueOrDefault(account.Id)?.LastSync));
        }
    }

    private async Task SyncListAsync(Account account, ContactSource source, Func<string?, CancellationToken, Task<ContactChanges>> list, CancellationToken ct)
    {
        var token = await repo.GetSyncTokenAsync(account.Id, source, ct);
        ContactChanges changes;
        try { changes = await list(token, ct); }
        catch (SyncTokenExpiredException) { changes = await list(null, ct); }

        if (changes.IsFull) await repo.ReplaceAsync(account.Id, source, changes.Changed, ct);
        else await repo.ApplyChangesAsync(account.Id, changes.Changed.Select(c => c with { Source = source }).ToList(), changes.Deleted, ct);
        await repo.SetSyncTokenAsync(account.Id, source, changes.NextSyncToken, ct);
        if (changes.IsFull || changes.Changed.Count + changes.Deleted.Count > 0) Changed?.Invoke();
    }

    public Task<IReadOnlyList<Contact>> GetAllAsync(CancellationToken ct = default) => repo.GetAllAsync(ct);

    // ───────── changes ─────────

    /// <summary>
    /// Saves a new or edited contact: to Google when it belongs to a Google account, otherwise only here. When Google
    /// can't be reached it's saved here and sent on the next sync.
    /// </summary>
    public async Task<Contact> SaveAsync(Contact contact, CancellationToken ct = default)
    {
        var saved = contact with { Updated = DateTimeOffset.UtcNow };
        if (await GoogleAccountOf(contact, ct) is not { } account)
        {
            var id = await repo.UpsertAsync(saved, ct);
            Changed?.Invoke();
            return saved with { Id = id };
        }
        if (!await HasPendingAsync(account.Id, ct))
        {
            try
            {
                var sent = await SendSaveAsync(account, saved, ct);
                Changed?.Invoke();
                return sent;
            }
            catch (Exception ex) when (Connectivity.IsOffline(ex, ct)) { log.LogInformation(ex, "Google Contacts can't be reached; the change waits"); }
        }
        // Kept here: an automatically saved address edited becomes a contact of its own, made on Google when sent.
        var other = contact.Source == ContactSource.GoogleOther;
        var local = saved with { Id = other ? 0 : contact.Id, RemoteId = other ? null : contact.RemoteId, Source = ContactSource.Google };
        local = local with { Id = await repo.UpsertAsync(local, ct) };
        await KeepAsync(account, new PendingContactChange(0, account.Id, ContactChangeKind.Save, local.Id, null), ct);
        return local;
    }

    /// <summary>Sends a contact to Google (created, or updated) and keeps what Google made of it.</summary>
    private async Task<Contact> SendSaveAsync(Account account, Contact contact, CancellationToken ct)
    {
        var remote = contact.RemoteId is null || contact.Source == ContactSource.GoogleOther
            ? await api.CreateAsync(account, contact, ct)
            : await api.UpdateAsync(account, contact, ct);
        // Kept before anything else can fail, so it's never created twice.
        var kept = remote with
        {
            Id = contact.Source == ContactSource.GoogleOther ? 0 : contact.Id, AccountId = account.Id, Source = ContactSource.Google,
            IsFavorite = contact.IsFavorite,
        };
        kept = kept with { Id = await repo.UpsertAsync(kept, ct) };
        if (contact.IsFavorite != remote.IsFavorite && remote.RemoteId is { } rid)
        {
            try { await api.SetStarredAsync(account, rid, contact.IsFavorite, ct); }
            catch (Exception ex) when (Connectivity.IsOffline(ex, ct))
            {
                await KeepAsync(account, new PendingContactChange(0, account.Id, ContactChangeKind.Save, kept.Id, null), ct);
            }
        }
        return kept;
    }

    /// <summary>Turns an automatically saved address into a real contact (Google copies it).</summary>
    public async Task<Contact> SaveOtherAsync(Contact other, CancellationToken ct = default)
    {
        if (other.Source != ContactSource.GoogleOther) return other;
        if (await GoogleAccountOf(other, ct) is not { } account)
        {
            var local = other with { Source = ContactSource.Google };
            await repo.UpsertAsync(local, ct);
            Changed?.Invoke();
            return local;
        }
        if (!await HasPendingAsync(account.Id, ct))
        {
            try
            {
                var copy = await api.SaveOtherContactAsync(account, other.RemoteId!, ct);
                var id = await repo.UpsertAsync(copy with { AccountId = account.Id, Source = ContactSource.Google }, ct);
                Changed?.Invoke();
                return copy with { Id = id, AccountId = account.Id };
            }
            catch (Exception ex) when (Connectivity.IsOffline(ex, ct)) { log.LogInformation(ex, "Google Contacts can't be reached; the change waits"); }
        }
        var kept = other with { Source = ContactSource.Google };
        await repo.UpsertAsync(kept, ct);
        await KeepAsync(account, new PendingContactChange(0, account.Id, ContactChangeKind.SaveOther, other.Id, other.RemoteId), ct);
        return kept;
    }

    public async Task SetFavoriteAsync(Contact contact, bool favorite, CancellationToken ct = default)
    {
        var starred = contact with { IsFavorite = favorite };
        if (contact is { Source: ContactSource.Google, RemoteId: { } rid } && await GoogleAccountOf(contact, ct) is { } account)
        {
            if (!await HasPendingAsync(account.Id, ct))
            {
                try
                {
                    await api.SetStarredAsync(account, rid, favorite, ct);
                    await repo.UpsertAsync(starred, ct);
                    Changed?.Invoke();
                    return;
                }
                catch (Exception ex) when (Connectivity.IsOffline(ex, ct)) { log.LogInformation(ex, "Google Contacts can't be reached; the change waits"); }
            }
            await repo.UpsertAsync(starred, ct);
            await KeepAsync(account, new PendingContactChange(0, account.Id, ContactChangeKind.Save, contact.Id, null), ct);
            return;
        }
        await repo.UpsertAsync(starred, ct); // here only, or made offline and not sent yet (its Save sends the star too)
        Changed?.Invoke();
    }

    public async Task DeleteAsync(Contact contact, CancellationToken ct = default)
    {
        await repo.RemovePendingChangesForAsync(contact.Id, ct); // nothing more to send for it
        if (contact is { Source: ContactSource.Google, RemoteId: { } rid } && !rid.StartsWith("otherContacts/", StringComparison.Ordinal)
            && await GoogleAccountOf(contact, ct) is { } account)
        {
            var sent = false;
            if (!await HasPendingAsync(account.Id, ct))
            {
                try
                {
                    await api.DeleteAsync(account, rid, ct);
                    sent = true;
                }
                catch (Exception ex) when (Connectivity.IsOffline(ex, ct)) { log.LogInformation(ex, "Google Contacts can't be reached; the change waits"); }
            }
            await repo.DeleteAsync(contact.Id, ct);
            if (!sent) await KeepAsync(account, new PendingContactChange(0, account.Id, ContactChangeKind.Delete, contact.Id, rid), ct);
            else Changed?.Invoke();
            return;
        }
        await repo.DeleteAsync(contact.Id, ct);
        Changed?.Invoke();
    }

    // ───────── offline: changes kept here and sent on the next sync ─────────

    /// <summary>The Google account a contact's changes go to; null when it's kept only here (or in demo mode).</summary>
    private async Task<Account?> GoogleAccountOf(Contact contact, CancellationToken ct) =>
        !_localOnly && contact.AccountId is { } accountId && await accounts.GetAsync(accountId, ct) is { Auth: AuthKind.GoogleOAuth } account ? account : null;

    private async Task<bool> HasPendingAsync(long accountId, CancellationToken ct) => (await repo.GetPendingChangesAsync(accountId, ct)).Count > 0;

    private async Task KeepAsync(Account account, PendingContactChange change, CancellationToken ct)
    {
        await repo.AddPendingChangeAsync(change, ct);
        SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Error,
            L.F("Couldn't reach Google Contacts for {0}. Your changes are kept and will be sent on the next sync.", account.Email),
            _status.GetValueOrDefault(account.Id)?.LastSync));
        Changed?.Invoke();
    }

    /// <summary>A change made offline that Google turned down when it was finally sent.</summary>
    public event Action<string>? ChangeRefused;

    /// <summary>Sends what was changed offline, oldest first; false when Google still can't be reached (the rest waits).</summary>
    private async Task<bool> SendPendingAsync(Account account, CancellationToken ct)
    {
        foreach (var change in await repo.GetPendingChangesAsync(account.Id, ct))
        {
            try
            {
                switch (change.Kind)
                {
                    case ContactChangeKind.Save when await repo.GetAsync(change.ContactId, ct) is { } contact:
                        await SendSaveAsync(account, contact, ct);
                        break;
                    case ContactChangeKind.Delete when change.RemoteId is { } rid:
                        await api.DeleteAsync(account, rid, ct);
                        break;
                    case ContactChangeKind.SaveOther when change.RemoteId is { } rid && await repo.GetAsync(change.ContactId, ct) is { } kept:
                        var copy = await api.SaveOtherContactAsync(account, rid, ct);
                        await repo.UpsertAsync(copy with { Id = kept.Id, AccountId = account.Id, Source = ContactSource.Google, IsFavorite = kept.IsFavorite }, ct);
                        break;
                }
            }
            catch (Exception ex) when (Connectivity.IsOffline(ex, ct) || Connectivity.IsTemporary(ex))
            {
                return false;
            }
            catch (HttpRequestException ex) when (Connectivity.IsGone(ex))
            {
                log.LogInformation("A contact change made offline was for a contact since deleted; dropped");
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "Google Contacts refused a change made offline");
                ChangeRefused?.Invoke(L.F("Google Contacts didn't accept a change you made offline: {0}", ex.Message));
            }
            await repo.RemovePendingChangeAsync(change.Id, ct);
        }
        return true;
    }

    /// <summary>Folds <paramref name="other"/> into <paramref name="keep"/> (addresses, numbers, details it lacks) and deletes it.</summary>
    public async Task<Contact> MergeAsync(Contact keep, Contact other, CancellationToken ct = default)
    {
        static IReadOnlyList<LabeledValue> Union(IReadOnlyList<LabeledValue> a, IReadOnlyList<LabeledValue> b) =>
            a.Concat(b.Where(x => !a.Any(y => string.Equals(Normal(y.Value), Normal(x.Value), StringComparison.OrdinalIgnoreCase)))).ToList();
        static string Normal(string v) => new(v.Where(c => !char.IsWhiteSpace(c) && c is not ('-' or '(' or ')' or '/')).ToArray());

        var merged = keep with
        {
            Emails = Union(keep.Emails, other.Emails), Phones = Union(keep.Phones, other.Phones), Websites = Union(keep.Websites, other.Websites),
            Organization = keep.Organization ?? other.Organization, JobTitle = keep.JobTitle ?? other.JobTitle,
            Department = keep.Department ?? other.Department, Nickname = keep.Nickname ?? other.Nickname,
            Addresses = keep.Addresses.Concat(other.Addresses.Where(a => !keep.Addresses.Any(k => k.Formatted == a.Formatted))).ToList(),
            Birthday = keep.Birthday ?? other.Birthday, Address = keep.Address ?? other.Address, PhotoUrl = keep.PhotoUrl ?? other.PhotoUrl,
            Notes = string.Join("\n\n", new[] { keep.Notes, other.Notes }.Where(n => !string.IsNullOrWhiteSpace(n))) is { Length: > 0 } notes ? notes : null,
            IsFavorite = keep.IsFavorite || other.IsFavorite,
        };
        var saved = await SaveAsync(merged, ct);
        if (other.IsSaved) await DeleteAsync(other, ct);
        return saved;
    }

    private void SetStatus(long accountId, CalendarAccountStatus status)
    {
        _status[accountId] = status;
        StatusChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _cts.Dispose();
    }

    public void Dispose() => Task.Run(() => DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
}
