using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Plicbox.Auth;
using Plicbox.Core;
using Microsoft.Extensions.Logging;

namespace Plicbox.Dav;

public enum CalendarSyncState { Idle, Syncing, NeedsPermission, NeedsSignIn, Error }

public sealed record CalendarAccountStatus(CalendarSyncState State, string? Message = null, DateTimeOffset? LastSync = null);

/// <summary>
/// Keeps each Google account's calendars and events in the local database and carries out changes.
/// Every change is sent to Google first and the calendar is then re-read, so what you see is what Google has; when
/// Google can't be reached, the change is shown here at once and sent on the next sync.
/// Calendars kept only on this computer (no account) are changed right here, through <see cref="LocalCalendars"/>.
/// </summary>
public sealed class CalendarEngine(
    IAccountRepository accounts,
    ICalendarRepository repo,
    GoogleCalendarApi api,
    ISettingsStore settings,
    ILogger<CalendarEngine> log) : IAsyncDisposable, IDisposable
{
    private readonly LocalCalendars _local = new(repo);
    private DateOnly _localWrittenFor;
    private readonly ConcurrentDictionary<long, CalendarAccountStatus> _status = new();
    // calendar id → its default popup reminder, from the last calendar-list read
    private readonly ConcurrentDictionary<long, int?> _defaultReminders = new();
    private readonly Channel<long?> _requests = Channel.CreateUnbounded<long?>();
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private bool _localOnly;

    /// <summary>Calendars or events changed (raised on a background thread).</summary>
    public event Action? Changed;
    public event Action? StatusChanged;

    public IReadOnlyDictionary<long, CalendarAccountStatus> Statuses => _status;

    /// <summary>The span kept in sync automatically; other dates are fetched when viewed.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Window
    {
        get
        {
            var today = new DateTimeOffset(DateTime.Today);
            return (today.AddDays(-62), today.AddDays(400));
        }
    }

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        RequestSync();
    }

    /// <summary>Demo mode: no server; edits change the local database only.</summary>
    public void StartLocalOnly()
    {
        _localOnly = true;
        _ = Task.Run(() => RefreshLocalAsync(_cts.Token));
    }

    /// <summary>
    /// The synced window moves on each day: the repeating events of calendars on this computer are written out again
    /// for it (on start, then at most once a day).
    /// </summary>
    private async Task RefreshLocalAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        if (_localWrittenFor == today) return;
        try
        {
            await _local.RefreshAsync(ct);
            _localWrittenFor = today;
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Writing out repeating events on this computer failed");
        }
    }

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
            // Several requests queued up: one pass over everything they asked for.
            while (_requests.Reader.TryRead(out var more)) accountId = accountId == more ? accountId : null;
            await RefreshLocalAsync(ct);
            try { await SyncAsync(accountId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogWarning(ex, "Calendar sync failed"); }
        }
    }

    private async Task SyncAsync(long? accountId, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            foreach (var account in await accounts.GetAllAsync(ct))
                if (account.Auth == AuthKind.GoogleOAuth && (accountId is null || accountId == account.Id))
                    await SyncAccountAsync(account, ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task SyncAccountAsync(Account account, CancellationToken ct)
    {
        SetStatus(account.Id, (_status.GetValueOrDefault(account.Id) ?? new(CalendarSyncState.Idle)) with { State = CalendarSyncState.Syncing });
        try
        {
            // Changes made offline go first; until they're through, what's shown here stays as it is.
            if (!await SendPendingAsync(account, ct))
            {
                SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Error,
                    L.F("Couldn't reach Google Calendar for {0}. Your changes are kept and will be sent on the next sync.", account.Email),
                    _status.GetValueOrDefault(account.Id)?.LastSync));
                return;
            }
            var remote = await api.ListCalendarsAsync(account, ct);
            var known = (await repo.GetCalendarsAsync(ct)).Where(c => c.AccountId == account.Id).Select(c => c.RemoteId).ToHashSet();
            var synced = new List<RemoteCalendar>();
            foreach (var rc in remote)
            {
                var id = await repo.UpsertCalendarAsync(rc.Calendar, keepVisibility: known.Contains(rc.Calendar.RemoteId), ct);
                _defaultReminders[id] = rc.DefaultReminderMinutes;
                synced.Add(rc with { Calendar = rc.Calendar with { Id = id } });
            }
            await repo.DeleteMissingCalendarsAsync(account.Id, remote.Select(r => r.Calendar.RemoteId).ToList(), ct);
            Changed?.Invoke();

            var (from, to) = Window;
            foreach (var rc in synced)
            {
                var events = await api.ListEventsAsync(account, rc, from, to, ct);
                await repo.ReplaceEventsAsync(rc.Calendar.Id, from, to, events, ct);
            }
            SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Idle, null, DateTimeOffset.Now));
            Changed?.Invoke();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (CalendarPermissionException ex)
        {
            SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.NeedsPermission, ex.Message));
        }
        catch (GoogleSignInRequiredException ex)
        {
            SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.NeedsSignIn, ex.Message));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Calendar sync failed for {Email}", account.Email);
            SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Error, ex.Message, _status.GetValueOrDefault(account.Id)?.LastSync));
        }
    }

    /// <summary>Viewing dates outside the synced window: fetch them now (best effort, needs a connection).</summary>
    public async Task EnsureRangeAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var window = Window;
        if (from >= window.From && to <= window.To) return;
        try
        {
            if (await _local.EnsureRangeAsync(from, to, ct)) Changed?.Invoke();
            if (_localOnly) return;
            var byAccount = (await accounts.GetAllAsync(ct)).ToDictionary(a => a.Id);
            var waiting = (await repo.GetPendingChangesAsync(ct)).Select(p => p.CalendarId).ToHashSet(); // keep what's shown until sent
            foreach (var cal in (await repo.GetCalendarsAsync(ct)).Where(c => c.IsVisible && !waiting.Contains(c.Id)))
            {
                if (cal.AccountId is not { } accountId || byAccount.GetValueOrDefault(accountId) is not { Auth: AuthKind.GoogleOAuth } account) continue;
                var rc = new RemoteCalendar(cal, _defaultReminders.GetValueOrDefault(cal.Id));
                await repo.ReplaceEventsAsync(cal.Id, from, to, await api.ListEventsAsync(account, rc, from, to, ct), ct);
            }
            Changed?.Invoke();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogInformation(ex, "Couldn't load calendar range {From}–{To}", from, to);
        }
    }

    public Task<IReadOnlyList<CalendarInfo>> GetCalendarsAsync(CancellationToken ct = default) => repo.GetCalendarsAsync(ct);

    /// <summary>Events of the calendars that are switched on, overlapping [from, to).</summary>
    public async Task<IReadOnlyList<CalendarEvent>> GetEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var visible = (await repo.GetCalendarsAsync(ct)).Where(c => c.IsVisible).Select(c => c.Id).ToList();
        return await repo.GetEventsAsync(visible, from, to, ct);
    }

    public async Task SetVisibleAsync(long calendarId, bool visible)
    {
        await repo.SetCalendarVisibleAsync(calendarId, visible);
        Changed?.Invoke();
    }

    // ───────── changes ─────────

    public async Task CreateAsync(long calendarId, EventDraft draft, CancellationToken ct = default)
    {
        if (await CalendarAsync(calendarId, ct) is { IsLocal: true } local)
        {
            await _local.CreateAsync(FromDraft(draft with { AddVideoCall = false }, local.Id, LocalCalendars.NewId()), ct);
            Changed?.Invoke();
            return;
        }
        var (account, cal) = await ResolveAsync(calendarId, ct);
        if (_localOnly)
        {
            await repo.UpsertEventAsync(FromDraft(draft, cal.Id, $"local-{Guid.NewGuid():N}"), ct);
            Changed?.Invoke();
            return;
        }
        var change = new CalendarChange { Kind = ChangeKind.Create, Draft = draft };
        await SendOrKeepAsync(account, cal, change, async () =>
        {
            // Shown at once under a stand-in id; the next sync replaces it with the event Google made.
            var standIn = PendingPrefix + Guid.NewGuid().ToString("N");
            await repo.UpsertEventAsync(FromDraft(draft with { AddVideoCall = false }, cal.Id, standIn), ct);
            return change with { Target = standIn };
        }, ct);
    }

    /// <param name="wholeSeries">For a repeating event: change every occurrence, not just this one.</param>
    public async Task UpdateAsync(CalendarEvent ev, EventDraft draft, bool wholeSeries, CancellationToken ct = default)
    {
        if (await CalendarAsync(ev.CalendarId, ct) is { IsLocal: true })
        {
            await _local.UpdateAsync(ev, FromDraft(draft with { AddVideoCall = false }, ev.CalendarId, ev.RemoteId), wholeSeries, ct);
            Changed?.Invoke();
            return;
        }
        var (account, cal) = await ResolveAsync(ev.CalendarId, ct);
        if (_localOnly)
        {
            await repo.UpsertEventAsync(FromDraft(draft, cal.Id, ev.RemoteId) with { Id = ev.Id, SeriesId = ev.SeriesId }, ct);
            Changed?.Invoke();
            return;
        }
        var change = new CalendarChange
        {
            Kind = ChangeKind.Update, Draft = draft, Target = wholeSeries ? ev.SeriesId ?? ev.RemoteId : ev.RemoteId,
            IsInstance = !wholeSeries && ev.SeriesId is not null,
        };
        await SendOrKeepAsync(account, cal, change, async () =>
        {
            await repo.UpsertEventAsync(Shown(draft, ev), ct); // a whole series changes on screen once Google has it
            return await MergeIntoCreateAsync(ev.RemoteId, c => c with { Draft = draft }, ct) ? null : change;
        }, ct);
    }

    /// <summary>Drag in the grid: new start and end, keeping everything else.</summary>
    public async Task MoveAsync(CalendarEvent ev, DateTimeOffset newStart, DateTimeOffset newEnd, bool wholeSeries, CancellationToken ct = default)
    {
        var draft = new EventDraft
        {
            IsAllDay = ev.IsAllDay, Start = newStart, End = newEnd, TimeZone = ev.TimeZone ?? Calendars.LocalIanaZone,
            StartDate = DateOnly.FromDateTime(newStart.LocalDateTime), EndDate = DateOnly.FromDateTime(newEnd.LocalDateTime),
        };
        if (await CalendarAsync(ev.CalendarId, ct) is { IsLocal: true })
        {
            var moved = ev with
            {
                Start = newStart, End = newEnd,
                StartDate = ev.IsAllDay ? draft.StartDate : ev.StartDate, EndDate = ev.IsAllDay ? draft.EndDate : ev.EndDate,
                Updated = DateTimeOffset.UtcNow,
            };
            await _local.UpdateAsync(ev, moved, wholeSeries, ct);
            Changed?.Invoke();
            return;
        }
        var (account, cal) = await ResolveAsync(ev.CalendarId, ct);
        if (_localOnly)
        {
            await repo.UpsertEventAsync(ev with
            {
                Start = newStart, End = newEnd,
                StartDate = ev.IsAllDay ? draft.StartDate : ev.StartDate, EndDate = ev.IsAllDay ? draft.EndDate : ev.EndDate,
            }, ct);
            Changed?.Invoke();
            return;
        }

        var change = new CalendarChange
        {
            Kind = ChangeKind.Move, Draft = draft, Target = ev.RemoteId,
            Series = wholeSeries ? ev.SeriesId : null, Delta = newStart - ev.Start,
        };
        await SendOrKeepAsync(account, cal, change, async () =>
        {
            await repo.UpsertEventAsync(ev with
            {
                Start = newStart, End = newEnd,
                StartDate = ev.IsAllDay ? draft.StartDate : ev.StartDate, EndDate = ev.IsAllDay ? draft.EndDate : ev.EndDate,
            }, ct);
            return await MergeIntoCreateAsync(ev.RemoteId, c => c with
            {
                Draft = c.Draft! with { Start = newStart, End = newEnd, StartDate = draft.StartDate, EndDate = draft.EndDate },
            }, ct) ? null : change;
        }, ct);
    }

    public async Task DeleteAsync(CalendarEvent ev, bool wholeSeries, CancellationToken ct = default)
    {
        if (await CalendarAsync(ev.CalendarId, ct) is { IsLocal: true })
        {
            await _local.DeleteAsync(ev, wholeSeries, ct);
            Changed?.Invoke();
            return;
        }
        var (account, cal) = await ResolveAsync(ev.CalendarId, ct);
        var target = wholeSeries ? ev.SeriesId ?? ev.RemoteId : ev.RemoteId;
        if (_localOnly)
        {
            await repo.DeleteEventAsync(cal.Id, target, ct);
            Changed?.Invoke();
            return;
        }
        var change = new CalendarChange { Kind = ChangeKind.Delete, Target = target };
        await SendOrKeepAsync(account, cal, change, async () =>
        {
            await repo.DeleteEventAsync(cal.Id, target, ct);
            // Made offline and never sent: there's nothing for Google to delete.
            return await MergeIntoCreateAsync(ev.RemoteId, _ => null, ct) ? null : change;
        }, ct);
    }

    // ───────── .ics files ─────────

    /// <summary>
    /// A calendar as an .ics file. One on this computer goes whole (repeating events with their rule and exceptions); an
    /// account's, as the occurrences kept here (about two months back and a year ahead).
    /// </summary>
    public async Task<string> ExportAsync(long calendarId, CancellationToken ct = default)
    {
        var cal = await CalendarAsync(calendarId, ct) ?? throw new InvalidOperationException(L.T("That calendar no longer exists."));
        var events = await repo.GetEventsAsync([cal.Id], new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero), ct);
        var items = new List<IcsItem>();
        if (cal.IsLocal)
        {
            items.AddRange(events.Where(e => e.SeriesId is null).Select(e => new IcsItem(e, [], null)));
            foreach (var series in (await repo.GetLocalSeriesAsync(ct)).Where(s => s.CalendarId == cal.Id))
            {
                items.Add(new IcsItem(series.First with { RemoteId = series.SeriesId }, series.Skipped.Select(KeyTime).ToList(), null));
                foreach (var (key, changed) in series.Changed)
                    items.Add(new IcsItem(changed with { RemoteId = series.SeriesId, Recurrence = null }, [], KeyTime(key)));
            }
        }
        else
        {
            items.AddRange(events.Select(e => new IcsItem(e with { Recurrence = null }, [], null)));
        }
        return IcsFormat.Write(cal.Name, items);
    }

    private static DateTime KeyTime(string key) => DateTime.ParseExact(key, key.Length == 8 ? "yyyyMMdd" : "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

    /// <summary>
    /// The events of an .ics file into a calendar; returns how many. On this computer an event keeps its UID, so bringing
    /// the same file in again updates rather than doubles; repeating events keep their exceptions. An account's calendar
    /// gets each as a new event (sent later when offline).
    /// </summary>
    public async Task<int> ImportAsync(long calendarId, IReadOnlyList<IcsItem> items, CancellationToken ct = default)
    {
        var cal = await CalendarAsync(calendarId, ct) ?? throw new InvalidOperationException(L.T("That calendar no longer exists."));
        var count = 0;
        Account? account = null;
        foreach (var group in items.GroupBy(i => i.Event.RemoteId))
        {
            var master = group.FirstOrDefault(i => i.RecurrenceId is null) ?? group.First();
            var ev = master.Event with { Id = 0, CalendarId = cal.Id, SeriesId = null, Attendees = [] };
            if (cal.IsLocal || _localOnly)
            {
                if (ev.Recurrence is null || master.RecurrenceId is not null)
                {
                    await repo.UpsertEventAsync(ev with { Recurrence = null, RemoteId = cal.IsLocal ? ev.RemoteId : $"local-{Guid.NewGuid():N}" }, ct);
                }
                else
                {
                    var changed = new Dictionary<string, CalendarEvent>();
                    foreach (var o in group.Where(i => i.RecurrenceId is not null))
                        changed[LocalSeries.Key(ev.IsAllDay, o.RecurrenceId!.Value)] = o.Event with
                        {
                            Id = 0, CalendarId = cal.Id, SeriesId = ev.RemoteId, Recurrence = ev.Recurrence, Attendees = [],
                        };
                    await _local.ImportSeriesAsync(new LocalSeries
                    {
                        CalendarId = cal.Id, SeriesId = ev.RemoteId, First = ev with { SeriesId = ev.RemoteId },
                        Skipped = master.ExDates.Select(d => LocalSeries.Key(ev.IsAllDay, d)).ToHashSet(), Changed = changed,
                    }, ct);
                }
            }
            else
            {
                account ??= (await ResolveAsync(cal.Id, ct)).Account;
                var draft = new EventDraft
                {
                    Title = ev.Title, Description = ev.Description, Location = ev.Location, IsAllDay = ev.IsAllDay,
                    Start = ev.Start, End = ev.End, StartDate = ev.StartDate, EndDate = ev.EndDate,
                    TimeZone = ev.TimeZone ?? Calendars.LocalIanaZone, Recurrence = master.RecurrenceId is null ? ev.Recurrence : null,
                    ReminderMinutes = ev.ReminderMinutes, IsBusy = ev.IsBusy,
                };
                var change = new CalendarChange { Kind = ChangeKind.Create, Draft = draft };
                await SendOrKeepAsync(account, cal, change, async () =>
                {
                    var standIn = PendingPrefix + Guid.NewGuid().ToString("N");
                    await repo.UpsertEventAsync(FromDraft(draft, cal.Id, standIn), ct);
                    return change with { Target = standIn };
                }, ct, refresh: false);
            }
            count++;
        }
        if (account is not null)
        {
            try { await RefreshCalendarAsync(account, cal, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogInformation(ex, "Re-reading the calendar failed"); }
        }
        Changed?.Invoke();
        return count;
    }

    /// <summary>
    /// Where an invitation sent to <paramref name="accountId"/> goes: that account's main calendar, else the first calendar
    /// on this computer, else a new one there.
    /// </summary>
    public async Task<CalendarInfo> CalendarForInvitationAsync(long? accountId, CancellationToken ct = default)
    {
        var calendars = await repo.GetCalendarsAsync(ct);
        if (calendars.FirstOrDefault(c => c.AccountId == accountId && accountId is not null && c.IsPrimary && c.CanEdit) is { } main) return main;
        if (calendars.FirstOrDefault(c => c.IsLocal) is { } local) return local;
        var id = await CreateCalendarAsync(null, L.T("Calendar"), "#039BE5", ct);
        return (await CalendarAsync(id, ct))!;
    }

    // ───────── offline: changes shown here at once and sent on the next sync ─────────

    private enum ChangeKind { Create, Update, Move, Delete }

    /// <summary>A change to a Google calendar, as kept while Google can't be reached.</summary>
    private sealed record CalendarChange
    {
        public ChangeKind Kind { get; init; }
        public EventDraft? Draft { get; init; }
        /// <summary>The event (or series) changed; for Create, the stand-in shown meanwhile.</summary>
        public string? Target { get; init; }
        /// <summary>Update: just this occurrence of a series.</summary>
        public bool IsInstance { get; init; }
        /// <summary>Move of a whole series: it shifts by <see cref="Delta"/> from wherever its first occurrence is.</summary>
        public string? Series { get; init; }
        public TimeSpan Delta { get; init; }
    }

    /// <summary>The id of an event made offline, until Google gives it its own.</summary>
    private const string PendingPrefix = "pending-";

    /// <summary>A change Google turned down when it was finally sent (made offline, then refused).</summary>
    public event Action<string>? ChangeRefused;

    /// <summary>
    /// Sends a change to Google now; when Google can't be reached (or earlier changes still wait, so the order holds),
    /// <paramref name="keepHere"/> shows it on this computer and returns what to send on the next sync (null: nothing,
    /// it was folded into a change already waiting).
    /// </summary>
    /// <param name="refresh">Re-read the calendar after sending (false while bringing in a whole file: once at the end).</param>
    private async Task SendOrKeepAsync(Account account, CalendarInfo cal, CalendarChange change, Func<Task<CalendarChange?>> keepHere,
        CancellationToken ct, bool refresh = true)
    {
        if (!await HasPendingAsync(account.Id, ct))
        {
            var sent = false;
            try
            {
                await ApplyAsync(account, cal, change, ct);
                sent = true;
            }
            catch (Exception ex) when (Connectivity.IsOffline(ex, ct))
            {
                log.LogInformation(ex, "Google Calendar can't be reached; the change waits for the next sync");
            }
            if (sent)
            {
                if (!refresh) return;
                try { await RefreshCalendarAsync(account, cal, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogInformation(ex, "Re-reading the calendar failed"); RequestSync(account.Id); }
                return;
            }
        }
        if (await keepHere() is { } keep) await repo.AddPendingChangeAsync(cal.Id, JsonSerializer.Serialize(keep), ct);
        SetStatus(account.Id, new CalendarAccountStatus(CalendarSyncState.Error,
            L.F("Couldn't reach Google Calendar for {0}. Your changes are kept and will be sent on the next sync.", account.Email),
            _status.GetValueOrDefault(account.Id)?.LastSync));
        Changed?.Invoke();
    }

    private async Task ApplyAsync(Account account, CalendarInfo cal, CalendarChange change, CancellationToken ct)
    {
        switch (change.Kind)
        {
            case ChangeKind.Create:
                await api.CreateAsync(account, cal.RemoteId, change.Draft!, ct);
                break;
            case ChangeKind.Update:
                await api.UpdateAsync(account, cal.RemoteId, change.Target!, change.Draft!, change.IsInstance, ct);
                break;
            case ChangeKind.Move:
            {
                var (draft, target) = (change.Draft!, change.Target!);
                if (change.Series is { } series)
                {
                    // Shift the whole series by however far this occurrence moved.
                    var (start, _, zone) = await api.GetSeriesTimesAsync(account, cal.RemoteId, series, ct);
                    var length = draft.End - draft.Start;
                    draft = draft with
                    {
                        Start = start + change.Delta, End = start + change.Delta + length, TimeZone = zone ?? draft.TimeZone,
                        StartDate = DateOnly.FromDateTime((start + change.Delta).LocalDateTime),
                        EndDate = DateOnly.FromDateTime((start + change.Delta + length).LocalDateTime),
                    };
                    target = series;
                }
                await api.MoveAsync(account, cal.RemoteId, target, draft, ct);
                break;
            }
            case ChangeKind.Delete:
                await api.DeleteAsync(account, cal.RemoteId, change.Target!, ct);
                break;
        }
    }

    /// <summary>Sends what was changed offline, oldest first; false when Google still can't be reached (the rest waits).</summary>
    private async Task<bool> SendPendingAsync(Account account, CancellationToken ct)
    {
        var calendars = (await repo.GetCalendarsAsync(ct)).Where(c => c.AccountId == account.Id).ToDictionary(c => c.Id);
        foreach (var pending in await repo.GetPendingChangesAsync(ct))
        {
            if (!calendars.TryGetValue(pending.CalendarId, out var cal)) continue;
            CalendarChange? change;
            try { change = JsonSerializer.Deserialize<CalendarChange>(pending.Change); }
            catch (JsonException) { change = null; }
            try
            {
                if (change is not null) await ApplyAsync(account, cal, change, ct);
            }
            catch (Exception ex) when (Connectivity.IsOffline(ex, ct) || Connectivity.IsTemporary(ex))
            {
                return false;
            }
            catch (HttpRequestException ex) when (Connectivity.IsGone(ex))
            {
                log.LogInformation("A calendar change made offline was for an event since deleted; dropped");
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "Google Calendar refused a change made offline");
                ChangeRefused?.Invoke(L.F("Google Calendar didn't accept a change you made offline: {0}", ex.Message));
            }
            await repo.RemovePendingChangeAsync(pending.Id, ct);
        }
        return true;
    }

    private async Task<bool> HasPendingAsync(long accountId, CancellationToken ct)
    {
        var pending = await repo.GetPendingChangesAsync(ct);
        if (pending.Count == 0) return false;
        var mine = (await repo.GetCalendarsAsync(ct)).Where(c => c.AccountId == accountId).Select(c => c.Id).ToHashSet();
        return pending.Any(p => mine.Contains(p.CalendarId));
    }

    /// <summary>
    /// An event made offline and changed again before it was sent: the change goes into its Create (edited, or dropped
    /// when <paramref name="edit"/> returns null). False when <paramref name="remoteId"/> isn't such an event.
    /// </summary>
    private async Task<bool> MergeIntoCreateAsync(string remoteId, Func<CalendarChange, CalendarChange?> edit, CancellationToken ct)
    {
        if (!remoteId.StartsWith(PendingPrefix, StringComparison.Ordinal)) return false;
        foreach (var pending in await repo.GetPendingChangesAsync(ct))
        {
            CalendarChange? create;
            try { create = JsonSerializer.Deserialize<CalendarChange>(pending.Change); }
            catch (JsonException) { continue; }
            if (create is not { Kind: ChangeKind.Create } || create.Target != remoteId) continue;
            if (edit(create) is { } edited) await repo.UpdatePendingChangeAsync(pending.Id, JsonSerializer.Serialize(edited), ct);
            else await repo.RemovePendingChangeAsync(pending.Id, ct);
            return true;
        }
        return true; // its Create already went (or was dropped): nothing to send for a stand-in either way
    }

    /// <summary>An event as it looks after an edit, before Google has had it.</summary>
    private static CalendarEvent Shown(EventDraft d, CalendarEvent before) => before with
    {
        Title = d.Title, Description = d.Description, Location = d.Location, IsAllDay = d.IsAllDay,
        Start = d.IsAllDay ? new DateTimeOffset(d.StartDate.ToDateTime(TimeOnly.MinValue)) : d.Start,
        End = d.IsAllDay ? new DateTimeOffset(d.EndDate.ToDateTime(TimeOnly.MinValue)) : d.End,
        StartDate = d.StartDate, EndDate = d.EndDate, TimeZone = d.TimeZone, IsBusy = d.IsBusy, ReminderMinutes = d.ReminderMinutes,
        Updated = DateTimeOffset.UtcNow,
    };

    public async Task RespondAsync(CalendarEvent ev, string response, CancellationToken ct = default)
    {
        if (_localOnly || await CalendarAsync(ev.CalendarId, ct) is { IsLocal: true }) return; // nobody invites you on this computer
        var (account, cal) = await ResolveAsync(ev.CalendarId, ct);
        await api.RespondAsync(account, cal.RemoteId, ev, response, ct);
        await RefreshCalendarAsync(account, cal, ct);
    }

    /// <summary>"Lunch with Ana Friday 1pm" into the default account's main calendar.</summary>
    public async Task QuickAddAsync(string text, CancellationToken ct = default)
    {
        var all = await accounts.GetAllAsync(ct);
        var account = all.FirstOrDefault(a => a.IsDefault && a.Auth == AuthKind.GoogleOAuth) ?? all.FirstOrDefault(a => a.Auth == AuthKind.GoogleOAuth)
            ?? throw new InvalidOperationException(L.T("Quick add needs a Google account."));
        var cal = (await repo.GetCalendarsAsync(ct)).FirstOrDefault(c => c.AccountId == account.Id && c.IsPrimary)
            ?? throw new InvalidOperationException(L.T("Your calendars haven't been loaded yet. Try again in a moment."));
        await api.QuickAddAsync(account, cal.RemoteId, text, ct);
        await RefreshCalendarAsync(account, cal, ct);
    }

    /// <summary>The repeat rule of an occurrence's series, read from Google (instances don't carry it).</summary>
    public async Task<string?> GetSeriesRuleAsync(CalendarEvent ev, CancellationToken ct = default)
    {
        if (ev.Recurrence is not null) return ev.Recurrence;
        if (ev.SeriesId is null || _localOnly || await CalendarAsync(ev.CalendarId, ct) is not { IsLocal: false }) return null;
        var (account, cal) = await ResolveAsync(ev.CalendarId, ct);
        return await api.GetRecurrenceAsync(account, cal.RemoteId, ev.SeriesId, ct);
    }

    public int? DefaultReminderFor(long calendarId) => _defaultReminders.GetValueOrDefault(calendarId);

    // ───────── search ─────────

    /// <summary>Matches among the events kept locally (about two months back and a year ahead), all calendars.</summary>
    public Task<IReadOnlyList<CalendarEvent>> SearchAsync(string text, CancellationToken ct = default) =>
        repo.SearchEventsAsync(text, 400, ct);

    /// <summary>Asks Google about the years before the synced window (not stored; best effort per calendar).</summary>
    public async Task<IReadOnlyList<CalendarEvent>> SearchOlderAsync(string text, CancellationToken ct = default)
    {
        if (_localOnly) return [];
        var byAccount = (await accounts.GetAllAsync(ct)).ToDictionary(a => a.Id);
        var to = Window.From;
        var from = to.AddYears(-5);
        var lookups = (await repo.GetCalendarsAsync(ct))
            .Where(c => c.AccountId is { } id && byAccount.GetValueOrDefault(id) is { Auth: AuthKind.GoogleOAuth })
            .Select(async c =>
            {
                try
                {
                    return await api.SearchAsync(byAccount[c.AccountId!.Value], new RemoteCalendar(c, _defaultReminders.GetValueOrDefault(c.Id)), text, from, to, 25, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogDebug(ex, "Searching {Calendar} failed", c.Name);
                    return (IReadOnlyList<CalendarEvent>)[];
                }
            });
        return (await Task.WhenAll(lookups)).SelectMany(r => r).OrderBy(e => e.Start).ToList();
    }

    // ───────── calendars ─────────

    /// <param name="accountId">The account to create it in, or null for a calendar kept only on this computer.</param>
    public async Task<long> CreateCalendarAsync(long? accountId, string name, string? color, CancellationToken ct = default)
    {
        if (accountId is null)
        {
            var id = await repo.UpsertCalendarAsync(new CalendarInfo
            {
                RemoteId = LocalCalendars.NewId(), Name = name, Color = color ?? "#039BE5", AccessRole = "owner", IsVisible = true,
                TimeZone = Calendars.LocalIanaZone,
            }, keepVisibility: false, ct);
            Changed?.Invoke();
            return id;
        }
        await CreateAccountCalendarAsync(accountId.Value, name, color, ct);
        return 0;
    }

    private async Task CreateAccountCalendarAsync(long accountId, string name, string? color, CancellationToken ct)
    {
        var account = await accounts.GetAsync(accountId, ct) ?? throw new InvalidOperationException(L.T("That account no longer exists."));
        if (_localOnly)
        {
            await repo.UpsertCalendarAsync(new CalendarInfo
            {
                AccountId = accountId, RemoteId = $"local-{Guid.NewGuid():N}", Name = name, Color = color ?? "#039BE5",
                AccessRole = "owner", IsVisible = true,
            }, keepVisibility: false, ct);
            Changed?.Invoke();
            return;
        }
        var id = await api.CreateCalendarAsync(account, name, Calendars.LocalIanaZone, ct);
        if (color is not null)
        {
            try { await api.SetCalendarColorAsync(account, id, color, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { log.LogInformation(ex, "Couldn't colour the new calendar"); }
        }
        await SyncCalendarListAsync(account, ct);
    }

    public async Task RenameCalendarAsync(long calendarId, string name, CancellationToken ct = default)
    {
        var cal = await CalendarAsync(calendarId, ct) ?? throw new InvalidOperationException(L.T("That calendar no longer exists."));
        if (!_localOnly && !cal.IsLocal)
        {
            var (account, _) = await ResolveAsync(calendarId, ct);
            await api.RenameCalendarAsync(account, cal.RemoteId, name, cal.AccessRole == "owner", ct);
        }
        await repo.UpdateCalendarAsync(cal.Id, name, cal.Color, ct);
        Changed?.Invoke();
    }

    public async Task SetCalendarColorAsync(long calendarId, string color, CancellationToken ct = default)
    {
        var cal = await CalendarAsync(calendarId, ct) ?? throw new InvalidOperationException(L.T("That calendar no longer exists."));
        if (!_localOnly && !cal.IsLocal)
        {
            var (account, _) = await ResolveAsync(calendarId, ct);
            await api.SetCalendarColorAsync(account, cal.RemoteId, color, ct);
        }
        await repo.UpdateCalendarAsync(cal.Id, cal.Name, color, ct);
        Changed?.Invoke();
    }

    /// <summary>
    /// Deletes a calendar the account owns, or unsubscribes from one it doesn't; one on this computer is deleted with its
    /// events. An account's main calendar can't go.
    /// </summary>
    public async Task DeleteCalendarAsync(long calendarId, CancellationToken ct = default)
    {
        if (await CalendarAsync(calendarId, ct) is { IsLocal: true } local)
        {
            await repo.DeleteCalendarAsync(local.Id, ct); // its repeating events go with it (foreign key)
            Changed?.Invoke();
            return;
        }
        var (account, cal) = await ResolveAsync(calendarId, ct);
        if (cal.IsPrimary) throw new InvalidOperationException(L.T("An account's main calendar can't be deleted."));
        if (!_localOnly)
        {
            if (cal.AccessRole == "owner") await api.DeleteCalendarAsync(account, cal.RemoteId, ct);
            else await api.UnsubscribeAsync(account, cal.RemoteId, ct);
        }
        await repo.DeleteCalendarAsync(cal.Id, ct);
        Changed?.Invoke();
    }

    /// <summary>Subscribes the account to Google's public holiday calendar for a country, in the user's language when Google has it.</summary>
    public async Task AddHolidayCalendarAsync(long accountId, HolidayCalendar holidays, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(accountId, ct) ?? throw new InvalidOperationException(L.T("That account no longer exists."));
        if (_localOnly)
        {
            await repo.UpsertCalendarAsync(new CalendarInfo
            {
                AccountId = accountId, RemoteId = holidays.GoogleId("en"), Name = L.F("Holidays in {0}", holidays.Country), Color = "#0B8043",
                AccessRole = "reader", IsVisible = true,
            }, keepVisibility: false, ct);
            Changed?.Invoke();
            return;
        }
        var language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        try
        {
            await api.SubscribeAsync(account, holidays.GoogleId(language), ct);
        }
        catch (HttpRequestException) when (language != "en")
        {
            await api.SubscribeAsync(account, holidays.GoogleId("en"), ct); // no translation of this one: English names
        }
        await SyncCalendarListAsync(account, ct);
        RequestSync(accountId); // its events
    }

    private async Task SyncCalendarListAsync(Account account, CancellationToken ct)
    {
        var remote = await api.ListCalendarsAsync(account, ct);
        var known = (await repo.GetCalendarsAsync(ct)).Where(c => c.AccountId == account.Id).Select(c => c.RemoteId).ToHashSet();
        foreach (var rc in remote)
        {
            var id = await repo.UpsertCalendarAsync(rc.Calendar, keepVisibility: known.Contains(rc.Calendar.RemoteId), ct);
            _defaultReminders[id] = rc.DefaultReminderMinutes;
        }
        Changed?.Invoke();
    }

    private async Task RefreshCalendarAsync(Account account, CalendarInfo cal, CancellationToken ct)
    {
        var (from, to) = Window;
        var rc = new RemoteCalendar(cal, _defaultReminders.GetValueOrDefault(cal.Id));
        await repo.ReplaceEventsAsync(cal.Id, from, to, await api.ListEventsAsync(account, rc, from, to, ct), ct);
        Changed?.Invoke();
    }

    private async Task<CalendarInfo?> CalendarAsync(long calendarId, CancellationToken ct) =>
        (await repo.GetCalendarsAsync(ct)).FirstOrDefault(c => c.Id == calendarId);

    /// <summary>A calendar of an account, with that account (the callers have already dealt with calendars on this computer).</summary>
    private async Task<(Account Account, CalendarInfo Calendar)> ResolveAsync(long calendarId, CancellationToken ct)
    {
        var cal = await CalendarAsync(calendarId, ct) ?? throw new InvalidOperationException(L.T("That calendar no longer exists."));
        var account = cal.AccountId is { } accountId ? await accounts.GetAsync(accountId, ct) : null;
        return (account ?? throw new InvalidOperationException(L.T("That account no longer exists.")), cal);
    }

    private static CalendarEvent FromDraft(EventDraft d, long calendarId, string remoteId) => new()
    {
        CalendarId = calendarId, RemoteId = remoteId, Title = d.Title, Description = d.Description, Location = d.Location,
        IsAllDay = d.IsAllDay, Start = d.IsAllDay ? new DateTimeOffset(d.StartDate.ToDateTime(TimeOnly.MinValue)) : d.Start,
        End = d.IsAllDay ? new DateTimeOffset(d.EndDate.ToDateTime(TimeOnly.MinValue)) : d.End,
        StartDate = d.StartDate, EndDate = d.EndDate, TimeZone = d.TimeZone, IsBusy = d.IsBusy,
        ReminderMinutes = d.ReminderMinutes, Recurrence = d.Recurrence, Updated = DateTimeOffset.UtcNow,
        MeetingUrl = d.AddVideoCall ? "https://meet.google.com/demo-call-link" : null,
    };

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
