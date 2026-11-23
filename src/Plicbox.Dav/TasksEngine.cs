using System.Collections.Concurrent;
using System.Threading.Channels;
using Plicbox.Auth;
using Plicbox.Core;
using Microsoft.Extensions.Logging;

namespace Plicbox.Dav;

/// <summary>
/// Keeps each Google account's task lists in the local database and carries out changes. Changes are saved locally
/// first (so ticking a task is instant) and then sent to Google; lists not tied to a Google account live only here.
/// </summary>
public sealed class TasksEngine(
    IAccountRepository accounts,
    ITaskRepository repo,
    GoogleTasksApi api,
    ISettingsStore settings,
    ILogger<TasksEngine> log) : IAsyncDisposable, IDisposable
{
    private readonly ConcurrentDictionary<long, CalendarAccountStatus> _status = new();
    private readonly Channel<long?> _requests = Channel.CreateUnbounded<long?>();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _localOnly;

    /// <summary>Tasks or lists changed (raised on a background thread).</summary>
    public event Action? Changed;
    public event Action? StatusChanged;
    public IReadOnlyDictionary<long, CalendarAccountStatus> Statuses => _status;

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        RequestSync();
    }

    /// <summary>Demo mode: no server; changes stay in the local database.</summary>
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
                wait.CancelAfter(TimeSpan.FromMinutes(Math.Max(5, settings.Current.SyncIntervalMinutes)));
                try { accountId = await _requests.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
            while (_requests.Reader.TryRead(out var more)) accountId = accountId == more ? accountId : null;
            await _syncLock.WaitAsync(ct);
            try
            {
                foreach (var account in await accounts.GetAllAsync(ct))
                    if (account.Auth == AuthKind.GoogleOAuth && (accountId is null || accountId == account.Id))
                        await SyncAccountAsync(account, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Tasks sync failed"); }
            finally { _syncLock.Release(); }
        }
    }

    private async Task SyncAccountAsync(Account account, CancellationToken ct)
    {
        SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Syncing, null, _status.GetValueOrDefault(account.Id)?.LastSync));
        try
        {
            var lists = await api.ListListsAsync(account, ct);
            var pending = (await repo.GetAllAsync(ct)).Where(t => t.IsDirty).ToList();
            foreach (var list in lists)
            {
                var id = await repo.UpsertListAsync(list, ct);
                // Changes made while offline (or that failed) go first, so the server's copy doesn't undo them.
                foreach (var task in pending.Where(t => t.ListId == id)) await PushAsync(account, list.RemoteId!, task, ct);
                await repo.ReplaceTasksAsync(id, await api.ListTasksAsync(account, list.RemoteId!, ct), ct);
            }
            await repo.DeleteMissingListsAsync(account.Id, lists.Select(l => l.RemoteId!).ToList(), ct);
            SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Idle, null, DateTimeOffset.Now));
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (CalendarPermissionException ex) { SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.NeedsPermission, ex.Message)); }
        catch (GoogleSignInRequiredException ex) { SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.NeedsSignIn, ex.Message)); }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Tasks sync failed for {Email}", account.Email);
            SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Error, ex.Message, _status.GetValueOrDefault(account.Id)?.LastSync));
        }
    }

    public Task<IReadOnlyList<TaskList>> GetListsAsync(CancellationToken ct = default) => repo.GetListsAsync(ct);
    public Task<IReadOnlyList<TaskItem>> GetTasksAsync(CancellationToken ct = default) => repo.GetAllAsync(ct);
    public Task<TaskItem?> GetTaskAsync(long id, CancellationToken ct = default) => repo.GetAsync(id, ct);

    // ───────── changes: local first, then Google ─────────

    /// <summary>
    /// Saves a new or changed task here, then sends it to Google. If Google can't be reached it stays marked and goes
    /// with the next sync. Returns it with its local id (and Google's id once sent).
    /// </summary>
    public async Task<TaskItem> SaveAsync(TaskItem task, CancellationToken ct = default)
    {
        var remote = await RemoteOf(task.ListId, ct);
        var local = task with
        {
            Updated = DateTimeOffset.UtcNow,
            CompletedAt = task.IsCompleted ? task.CompletedAt ?? DateTimeOffset.UtcNow : null,
            // Lists kept only here still give tasks an id of their own, so steps can point at their parent.
            RemoteId = task.RemoteId ?? (remote is null ? "local:" + Guid.NewGuid().ToString("N") : null),
            IsDirty = remote is not null,
        };
        local = local with { Id = await repo.UpsertAsync(local, ct) };
        Changed?.Invoke();
        if (remote is not { } r) return local;
        try
        {
            return await PushAsync(r.Account, r.ListId, local, ct) ?? local;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't send the task to Google; it goes with the next sync");
            SetStatus(r.Account.Id, new CalendarAccountStatus(CalendarSyncState.Error,
                L.F("Couldn't reach Google Tasks for {0}. Your changes are kept and will be sent on the next sync.", r.Account.Email),
                _status.GetValueOrDefault(r.Account.Id)?.LastSync));
            return local;
        }
    }

    /// <summary>Sends one task to Google and stores what came back; null when Google no longer has it (removed here too).</summary>
    private async Task<TaskItem?> PushAsync(Account account, string listId, TaskItem task, CancellationToken ct)
    {
        TaskItem sent;
        try
        {
            sent = IsGoogleId(task.RemoteId)
                ? await api.UpdateAsync(account, listId, task, ct)
                : await api.CreateAsync(account, listId, task with { ParentRemoteId = IsGoogleId(task.ParentRemoteId) ? task.ParentRemoteId : null }, ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound && IsGoogleId(task.RemoteId))
        {
            await repo.DeleteAsync(task.Id, ct); // deleted elsewhere meanwhile
            Changed?.Invoke();
            return null;
        }
        // Only what Google decides is taken; anything typed meanwhile is a newer save of its own.
        var current = await repo.GetAsync(task.Id, ct) ?? task;
        var stored = current with
        {
            RemoteId = sent.RemoteId, Position = sent.Position, WebLink = sent.WebLink ?? current.WebLink,
            IsDirty = current.Updated > task.Updated,
        };
        await repo.UpsertAsync(stored, ct);
        return stored;
    }

    private static bool IsGoogleId([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? id) => id is not null && !id.StartsWith("local:", StringComparison.Ordinal);

    public async Task DeleteAsync(TaskItem task, CancellationToken ct = default)
    {
        await repo.DeleteAsync(task.Id, ct);
        Changed?.Invoke();
        if (IsGoogleId(task.RemoteId) && await RemoteOf(task.ListId, ct) is { } remote)
            await api.DeleteAsync(remote.Account, remote.ListId, task.RemoteId, ct);
    }

    /// <summary>Moves a task to another list: Google has no "move between lists", so it's created there and deleted here.</summary>
    public async Task<TaskItem> MoveToListAsync(TaskItem task, long listId, CancellationToken ct = default)
    {
        if (task.ListId == listId) return task;
        var steps = task.RemoteId is null ? [] : (await repo.GetAllAsync(ct)).Where(t => t.ListId == task.ListId && t.ParentRemoteId == task.RemoteId).ToList();
        var moved = await SaveAsync(task with { Id = 0, ListId = listId, RemoteId = null, ParentRemoteId = null, Position = null, IsDirty = false }, ct);
        foreach (var step in steps)
            await SaveAsync(step with { Id = 0, ListId = listId, RemoteId = null, ParentRemoteId = moved.RemoteId, Position = null, IsDirty = false }, ct);
        await DeleteAsync(task, ct); // Google takes the steps along with their parent
        foreach (var step in steps) await repo.DeleteAsync(step.Id, ct);
        return moved;
    }

    public async Task<TaskList> CreateListAsync(long? accountId, string title, CancellationToken ct = default)
    {
        TaskList list = new() { AccountId = accountId, Title = title, Updated = DateTimeOffset.UtcNow };
        if (!_localOnly && accountId is { } id && await accounts.GetAsync(id, ct) is { Auth: AuthKind.GoogleOAuth } account)
            list = await api.CreateListAsync(account, title, ct);
        list = list with { Id = await repo.UpsertListAsync(list, ct) };
        Changed?.Invoke();
        return list;
    }

    public async Task RenameListAsync(TaskList list, string title, CancellationToken ct = default)
    {
        if (await RemoteOf(list.Id, ct) is { } remote) await api.RenameListAsync(remote.Account, remote.ListId, title, ct);
        await repo.UpsertListAsync(list with { Title = title, Updated = DateTimeOffset.UtcNow }, ct);
        Changed?.Invoke();
    }

    public async Task DeleteListAsync(TaskList list, CancellationToken ct = default)
    {
        if (await RemoteOf(list.Id, ct) is { } remote) await api.DeleteListAsync(remote.Account, remote.ListId, ct);
        await repo.DeleteListAsync(list.Id, ct);
        Changed?.Invoke();
    }

    private async Task<(Account Account, string ListId)?> RemoteOf(long listId, CancellationToken ct)
    {
        if (_localOnly) return null;
        var list = (await repo.GetListsAsync(ct)).FirstOrDefault(l => l.Id == listId);
        if (list is not { AccountId: { } accountId, RemoteId: { } remoteId }) return null;
        return await accounts.GetAsync(accountId, ct) is { Auth: AuthKind.GoogleOAuth } account ? (account, remoteId) : null;
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
