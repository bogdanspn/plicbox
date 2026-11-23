using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plicbox.App.Services;
using Plicbox.Core;
using Plicbox.Dav;
using Microsoft.Extensions.Logging;

namespace Plicbox.App.ViewModels;

public enum CalendarViewMode { Day, WorkWeek, Week, Month, Agenda }

/// <summary>A calendar in the sidebar, with its colour and on/off switch.</summary>
public sealed partial class CalendarItemViewModel(CalendarInfo info, Func<CalendarItemViewModel, Task> onToggled) : ObservableObject
{
    public CalendarInfo Info { get; } = info;
    public string Name => Info.Name;
    /// <summary>The account owns it (delete for everyone) rather than having it on its list (remove from list).</summary>
    public bool IsOwned => Info.AccessRole == "owner";
    public Brush Color { get; } = CalendarBrushes.For(info.Color);

    [ObservableProperty] private bool _isVisible = info.IsVisible;

    partial void OnIsVisibleChanged(bool value) => _ = onToggled(this);
}

public sealed class CalendarGroupViewModel
{
    public required string Title { get; init; }
    /// <summary>The account, or null for the calendars kept only on this computer.</summary>
    public long? AccountId { get; init; }
    public bool IsLocal => AccountId is null;
    /// <summary>The heading's icon: a person for an account, a PC for this computer.</summary>
    public string Glyph => IsLocal ? Glyphs.Computer : "";
    public ObservableCollection<CalendarItemViewModel> Calendars { get; } = [];
}

/// <summary>One line of the "Up next" card.</summary>
public sealed record UpNextItem(CalendarEvent Event, string When, Brush Color, bool IsNow)
{
    public string Title => Event.Title;
    public bool HasMeeting => Event.MeetingUrl is not null;
}

/// <summary>Shared colour helpers so calendar colours look the same everywhere.</summary>
public static class CalendarBrushes
{
    private static readonly Dictionary<string, SolidColorBrush> Cache = [];

    public static SolidColorBrush For(string? hex)
    {
        hex ??= "#4285F4";
        lock (Cache)
        {
            if (Cache.TryGetValue(hex, out var b)) return b;
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(hex); }
            catch (FormatException) { c = Color.FromRgb(0x42, 0x85, 0xF4); }
            b = new SolidColorBrush(c);
            b.Freeze();
            return Cache[hex] = b;
        }
    }

    /// <summary>The colour mixed into the page: event backgrounds that keep their text readable (white page, or the dark one).</summary>
    public static SolidColorBrush Tint(string? hex, double amount = 0.22)
    {
        var c = For(hex).Color;
        var page = Services.Theme.Color("SurfaceBrush");
        byte Mix(byte v, byte p) => (byte)(p + (v - p) * amount);
        var b = new SolidColorBrush(Color.FromRgb(Mix(c.R, page.R), Mix(c.G, page.G), Mix(c.B, page.B)));
        b.Freeze();
        return b;
    }

    /// <summary>A shade for text on the tint: darker on the white page, lighter on the dark one.</summary>
    public static SolidColorBrush Shade(string? hex)
    {
        var c = For(hex).Color;
        var b = new SolidColorBrush(Services.Theme.IsDark
            ? Color.FromRgb((byte)(c.R + (255 - c.R) * 0.6), (byte)(c.G + (255 - c.G) * 0.6), (byte)(c.B + (255 - c.B) * 0.6))
            : Color.FromRgb((byte)(c.R * 0.55), (byte)(c.G * 0.55), (byte)(c.B * 0.55)));
        b.Freeze();
        return b;
    }
}

public sealed partial class CalendarViewModel : ObservableObject
{
    private readonly CalendarEngine _engine;
    private readonly IAccountRepository _accounts;
    private readonly ISettingsStore _settings;
    private readonly Dialogs _dialogs;
    private readonly ILogger<CalendarViewModel> _log;
    private readonly SynchronizationContext _ui;
    private readonly DispatcherTimer _clock;
    private readonly HashSet<string> _reminded = [];
    private CancellationTokenSource? _reloadCts;

    public CalendarViewModel(CalendarEngine engine, IAccountRepository accounts, ISettingsStore settings, Dialogs dialogs,
        ILogger<CalendarViewModel> log)
    {
        (_engine, _accounts, _settings, _dialogs, _log) = (engine, accounts, settings, dialogs, log);
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        _mode = Enum.TryParse<CalendarViewMode>(settings.Current.Calendar.View, out var m) ? m : CalendarViewMode.Week;
        _miniMonth = new DateOnly(Today.Year, Today.Month, 1);

        engine.Changed += () => _ui.Post(_ => _ = ReloadSoonAsync(), null);
        engine.StatusChanged += () => _ui.Post(_ => UpdateBanner(), null);
        engine.ChangeRefused += message => _ui.Post(_ => _dialogs.Error(message), null);
        // Week start, work days and hours all change how the views are drawn, the mini month included.
        settings.Changed += _ => _ui.Post(_ => { OnPropertyChanged(nameof(MiniMonth)); _ = ReloadAsync(); }, null);

        // Every 30 s: refresh "Up next", move the now line, and pop reminders.
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _clock.Tick += (_, _) => { _ = UpdateUpNextAsync(); ClockTick?.Invoke(); };
        _clock.Start();
    }

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);
    public CalendarPrefs Prefs => _settings.Current.Calendar;

    public ObservableCollection<CalendarGroupViewModel> Groups { get; } = [];
    public ObservableCollection<UpNextItem> UpNext { get; } = [];
    public IReadOnlyList<CalendarEvent> Events { get; private set; } = [];
    public IReadOnlyDictionary<long, CalendarInfo> CalendarsById { get; private set; } = new Dictionary<long, CalendarInfo>();
    /// <summary>Days of the mini month that have something on them (a dot under the number).</summary>
    public IReadOnlySet<DateOnly> BusyDays { get; private set; } = new HashSet<DateOnly>();

    /// <summary>The view should redraw (new events, range or mode).</summary>
    public event Action? EventsChanged;
    /// <summary>The minute changed: move the now line.</summary>
    public event Action? ClockTick;
    /// <summary>(title, text) for a reminder notification.</summary>
    public event Action<string, string>? ReminderDue;

    [ObservableProperty] private CalendarViewMode _mode;
    [ObservableProperty] private DateOnly _anchor = Today;
    [ObservableProperty] private DateOnly _miniMonth;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _weekLabel = "";
    [ObservableProperty] private string _quickAddText = "";
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private string? _bannerAction;
    [ObservableProperty] private bool _hasCalendars;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _refreshToolTip = L.T("Refresh (F5)");
    private long? _bannerAccountId;

    // ───────── search ─────────

    /// <summary>The title bar's search box while the calendar is shown; non-empty replaces the view with results.</summary>
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _searchStatus = "";
    public IReadOnlyList<CalendarEvent> SearchResults { get; private set; } = [];
    public bool IsSearching => SearchText.Trim().Length > 0;
    /// <summary>The last search ended in an error (shown in <see cref="SearchStatus"/>), not in "nothing found".</summary>
    public bool SearchFailed { get; private set; }
    /// <summary>Search results changed (or search ended): redraw.</summary>
    public event Action? SearchChanged;
    private CancellationTokenSource? _searchCts;

    partial void OnSearchTextChanged(string value) => _ = RunSearchAsync(value.Trim());

    private async Task RunSearchAsync(string text)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        SearchFailed = false;
        if (text.Length == 0)
        {
            SearchResults = [];
            SearchStatus = "";
            SearchChanged?.Invoke();
            return;
        }
        try
        {
            await Task.Delay(200, cts.Token); // typing
            var local = await Task.Run(() => _engine.SearchAsync(text, cts.Token));
            if (cts.IsCancellationRequested) return;
            SearchResults = local;
            SearchStatus = local.Count == 0 ? L.T("Looking further back…") : Describe(local.Count) + ", " + L.T("looking further back…");
            SearchChanged?.Invoke();

            // Then the years before the synced window, straight from Google.
            var older = await Task.Run(() => _engine.SearchOlderAsync(text, cts.Token));
            if (cts.IsCancellationRequested) return;
            var seen = local.Select(e => (e.CalendarId, e.RemoteId)).ToHashSet();
            SearchResults = older.Where(e => !seen.Contains((e.CalendarId, e.RemoteId))).Concat(local).OrderBy(e => e.Start).ToList();
            // None: the empty state says so; the line under the heading says what was searched.
            SearchStatus = SearchResults.Count == 0 ? L.T("Titles, places, notes and people") : Describe(SearchResults.Count);
            SearchChanged?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Calendar search failed");
            SearchFailed = true;
            SearchStatus = L.F("Search failed: {0}", ex.Message);
            SearchChanged?.Invoke();
        }

        static string Describe(int n) => n == 1 ? L.T("1 event") : L.F("{0} events", n);
    }

    [RelayCommand] private void ClearSearch() => SearchText = "";

    /// <summary>From a search result: show that day in the calendar and end the search.</summary>
    public void ShowInCalendar(CalendarEvent e)
    {
        var day = CalendarLayout.StartDay(e);
        SearchText = "";
        GoTo(day, Mode is CalendarViewMode.Agenda or CalendarViewMode.Month ? CalendarViewMode.Week : null);
    }

    partial void OnModeChanged(CalendarViewMode value)
    {
        _settings.Save(_settings.Current with { Calendar = Prefs with { View = value.ToString() } });
        _ = ReloadAsync();
    }

    partial void OnAnchorChanged(DateOnly value)
    {
        MiniMonth = new DateOnly(value.Year, value.Month, 1);
        _ = ReloadAsync();
    }

    partial void OnMiniMonthChanged(DateOnly value) => _ = LoadBusyDaysAsync();

    /// <summary>The days the grid shows (Day, Work week, Week) or that the month/agenda covers.</summary>
    public IReadOnlyList<DateOnly> Days
    {
        get
        {
            var prefs = Prefs;
            switch (Mode)
            {
                case CalendarViewMode.Day:
                    return [Anchor];
                case CalendarViewMode.WorkWeek:
                {
                    var start = CalendarLayout.WeekStart(Anchor, prefs.FirstDayOfWeek);
                    var days = Enumerable.Range(0, 7).Select(start.AddDays).Where(d => prefs.WorkDays.Contains(d.DayOfWeek)).ToList();
                    return days.Count > 0 ? days : [Anchor];
                }
                case CalendarViewMode.Week:
                {
                    var start = CalendarLayout.WeekStart(Anchor, prefs.FirstDayOfWeek);
                    return Enumerable.Range(0, 7).Select(start.AddDays).ToList();
                }
                case CalendarViewMode.Month:
                {
                    var first = new DateOnly(Anchor.Year, Anchor.Month, 1);
                    var start = CalendarLayout.WeekStart(first, prefs.FirstDayOfWeek);
                    return Enumerable.Range(0, 42).Select(start.AddDays).ToList();
                }
                default:
                    return Enumerable.Range(0, 30).Select(Anchor.AddDays).ToList();
            }
        }
    }

    public async Task LoadAsync()
    {
        await LoadCalendarsAsync();
        await ReloadAsync();
        UpdateBanner();
    }

    private async Task LoadCalendarsAsync()
    {
        var calendars = await _engine.GetCalendarsAsync();
        var accounts = (await _accounts.GetAllAsync()).ToDictionary(a => a.Id);
        CalendarsById = calendars.ToDictionary(c => c.Id);
        HasCalendars = calendars.Count > 0;
        // Quick add asks Google to read the words, so it's there only with a Google account.
        CanQuickAdd = DevShowQuickAdd || accounts.Values.Any(a => a.Auth == AuthKind.GoogleOAuth);

        Groups.Clear();
        // Accounts in their sidebar order, then the calendars kept on this computer.
        foreach (var byAccount in calendars.GroupBy(c => c.AccountId)
                     .OrderBy(g => g.Key is null).ThenBy(g => g.Key is { } id ? accounts.GetValueOrDefault(id)?.SortOrder ?? 0 : 0))
        {
            var title = byAccount.Key is { } accountId ? accounts.GetValueOrDefault(accountId)?.SidebarTitle ?? L.T("Calendar") : L.T("On this computer");
            var group = new CalendarGroupViewModel { Title = title, AccountId = byAccount.Key };
            foreach (var cal in byAccount.OrderByDescending(c => c.IsPrimary).ThenByDescending(c => c.CanEdit).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
                group.Calendars.Add(new CalendarItemViewModel(cal, ToggleCalendarAsync));
            Groups.Add(group);
        }
    }

    private async Task ToggleCalendarAsync(CalendarItemViewModel item) => await _engine.SetVisibleAsync(item.Info.Id, item.IsVisible);

    private async Task ReloadSoonAsync()
    {
        // Sync raises several changes in a row; redraw once they settle.
        _reloadCts?.Cancel();
        var cts = _reloadCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            await LoadCalendarsAsync();
            await ReloadAsync();
        }
        catch (OperationCanceledException) { }
    }

    public async Task ReloadAsync()
    {
        try
        {
            var days = Days;
            var from = new DateTimeOffset(days[0].ToDateTime(TimeOnly.MinValue));
            var to = new DateTimeOffset(days[^1].AddDays(1).ToDateTime(TimeOnly.MinValue));
            UpdateTitle(days);

            Events = await Task.Run(() => _engine.GetEventsAsync(from, to));
            EventsChanged?.Invoke();
            await UpdateUpNextAsync();
            await LoadBusyDaysAsync();

            // Outside the automatically synced span: fetch from Google, then this method runs again via Changed.
            _ = _engine.EnsureRangeAsync(from, to);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading calendar failed");
        }
    }

    private async Task LoadBusyDaysAsync()
    {
        var start = CalendarLayout.WeekStart(MiniMonth, Prefs.FirstDayOfWeek);
        var from = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue));
        var events = await Task.Run(() => _engine.GetEventsAsync(from, from.AddDays(42)));
        var set = new HashSet<DateOnly>();
        foreach (var e in events.Where(e => e.MyResponse != "declined"))
            for (var d = CalendarLayout.StartDay(e); d < CalendarLayout.EndDay(e) && d < start.AddDays(42); d = d.AddDays(1))
                set.Add(d);
        BusyDays = set;
        OnPropertyChanged(nameof(BusyDays));
    }

    private void UpdateTitle(IReadOnlyList<DateOnly> days)
    {
        var culture = CultureInfo.CurrentCulture;
        var dot = culture.DateTimeFormat.ShortDatePattern.Contains("d.", StringComparison.Ordinal) ? "." : "";
        DateOnly first = days[0], last = days[^1];
        Title = Mode switch
        {
            CalendarViewMode.Day => Anchor.ToString("dddd, d" + dot + " MMMM yyyy", culture),
            CalendarViewMode.Month => Anchor.ToString("MMMM yyyy", culture),
            _ when first.Month == last.Month => $"{first.Day}{dot} – {last.Day}{dot} {last.ToString("MMMM yyyy", culture)}",
            _ when first.Year == last.Year => $"{first.ToString("d" + dot + " MMM", culture)} – {last.ToString("d" + dot + " MMM yyyy", culture)}",
            _ => $"{first.ToString("d" + dot + " MMM yyyy", culture)} – {last.ToString("d" + dot + " MMM yyyy", culture)}",
        };
        var weekWord = L.T("Week");
        WeekLabel = Prefs.ShowWeekNumbers && Mode is not (CalendarViewMode.Month or CalendarViewMode.Agenda)
            ? $"{weekWord} {Calendars.IsoWeek(first)}" + (Calendars.IsoWeek(last) != Calendars.IsoWeek(first) ? $"–{Calendars.IsoWeek(last)}" : "")
            : "";
    }

    /// <summary>The next few things on the calendar, with a Join button for calls; also fires reminders.</summary>
    private async Task UpdateUpNextAsync()
    {
        try
        {
            var now = DateTimeOffset.Now;
            var events = await Task.Run(() => _engine.GetEventsAsync(now.AddHours(-12), now.AddDays(7)));
            var upcoming = events
                .Where(e => !e.IsAllDay && e.End > now && e.MyResponse != "declined")
                .OrderBy(e => e.Start)
                .Take(4)
                .ToList();

            UpNext.Clear();
            foreach (var e in upcoming)
            {
                var color = CalendarBrushes.For(e.Color ?? CalendarsById.GetValueOrDefault(e.CalendarId)?.Color);
                UpNext.Add(new UpNextItem(e, DescribeWhen(e, now), color, e.Start <= now));
            }

            if (Prefs.NotifyReminders)
            {
                foreach (var e in events.Where(e => !e.IsAllDay && e.ReminderMinutes is not null && e.MyResponse != "declined"))
                {
                    var due = e.Start.AddMinutes(-e.ReminderMinutes!.Value);
                    var key = $"{e.CalendarId}:{e.RemoteId}:{e.Start:O}";
                    if (now >= due && now < e.Start.AddMinutes(1) && _reminded.Add(key))
                        ReminderDue?.Invoke(e.Title, $"{DescribeWhen(e, now)}{(e.Location is { Length: > 0 } l ? ", " + l : "")}");
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Up next refresh failed");
        }
    }

    /// <summary>"Now, until 10:00", "In 12 min", "Today 14:00", "Tomorrow 09:30", "Mon 09:00".</summary>
    public static string DescribeWhen(CalendarEvent e, DateTimeOffset now)
    {
        var culture = CultureInfo.CurrentCulture;
        var start = e.Start.LocalDateTime;
        if (e.Start <= now) return L.F("Now, until {0}", e.End.LocalDateTime.ToString("t", culture));
        var minutes = (int)Math.Ceiling((e.Start - now).TotalMinutes);
        if (minutes <= 60) return L.F("In {0} min", minutes);
        var day = DateOnly.FromDateTime(start);
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var time = start.ToString("t", culture);
        return day == today ? L.F("Today {0}", time)
            : day == today.AddDays(1) ? L.F("Tomorrow {0}", time)
            : $"{start.ToString("ddd", culture)} {time}";
    }

    private void UpdateBanner()
    {
        var problem = _engine.Statuses.FirstOrDefault(s => s.Value.State is CalendarSyncState.NeedsPermission or CalendarSyncState.NeedsSignIn or CalendarSyncState.Error);
        IsBusy = _engine.Statuses.Values.Any(s => s.State == CalendarSyncState.Syncing);
        RefreshToolTip = IsBusy ? L.T("Refresh (F5), syncing…")
            : _engine.Statuses.Values.Select(s => s.LastSync).Where(t => t.HasValue).Min() is { } last ? L.F("Refresh (F5), updated {0:t}", last.LocalDateTime)
            : L.T("Refresh (F5)");
        if (problem.Value is null)
        {
            Banner = null;
            BannerAction = null;
            _bannerAccountId = null;
            return;
        }
        _bannerAccountId = problem.Key;
        Banner = problem.Value.State == CalendarSyncState.NeedsPermission
            ? problem.Value.Message + " " + L.T("Sign in once more and allow \"See, edit, share and delete your calendars\".")
            : problem.Value.Message;
        BannerAction = problem.Value.State is CalendarSyncState.NeedsPermission or CalendarSyncState.NeedsSignIn ? L.T("Allow access") : L.T("Try again");
    }

    // ───────── navigation ─────────

    [RelayCommand] private void GoToday() => Anchor = Today;

    [RelayCommand]
    private void Previous() => Anchor = Mode switch
    {
        CalendarViewMode.Day => Anchor.AddDays(-1),
        CalendarViewMode.Month => Anchor.AddMonths(-1),
        CalendarViewMode.Agenda => Anchor.AddDays(-30),
        _ => Anchor.AddDays(-7),
    };

    [RelayCommand]
    private void Next() => Anchor = Mode switch
    {
        CalendarViewMode.Day => Anchor.AddDays(1),
        CalendarViewMode.Month => Anchor.AddMonths(1),
        CalendarViewMode.Agenda => Anchor.AddDays(30),
        _ => Anchor.AddDays(7),
    };

    [RelayCommand] private void SetMode(string mode) => Mode = Enum.Parse<CalendarViewMode>(mode);
    [RelayCommand] private void PreviousMiniMonth() => MiniMonth = MiniMonth.AddMonths(-1);

    /// <summary>Folds the sidebar's small month down to its heading (or opens it), remembered between runs.</summary>
    [RelayCommand]
    private void ToggleMiniMonth() =>
        _settings.Save(_settings.Current with { Calendar = Prefs with { MiniMonthCollapsed = !Prefs.MiniMonthCollapsed } });
    [RelayCommand] private void NextMiniMonth() => MiniMonth = MiniMonth.AddMonths(1);

    public void GoTo(DateOnly day, CalendarViewMode? mode = null)
    {
        if (mode is { } m) Mode = m;
        Anchor = day;
    }

    [RelayCommand]
    private void Refresh()
    {
        IsBusy = true; // the Refresh arrows turn right away
        _engine.RequestSync();
        _ = Task.Delay(1500).ContinueWith(_ => _ui.Post(_ => UpdateBanner(), null), TaskScheduler.Default); // nothing started: stop turning
        _ = ReloadAsync();
    }

    [RelayCommand]
    private async Task FixBanner()
    {
        if (_bannerAccountId is not { } id) return;
        if (BannerAction == L.T("Allow access") && await _accounts.GetAsync(id) is { } account)
            await _dialogs.SignInAgainAsync(account);
        _engine.RequestSync(id);
    }

    // ───────── events ─────────

    public CalendarInfo? CalendarOf(CalendarEvent e) => CalendarsById.GetValueOrDefault(e.CalendarId);
    public bool CanEdit(CalendarEvent e) => CalendarOf(e)?.CanEdit == true;

    /// <summary>Quick add needs Google to read the words: hidden without a Google account.</summary>
    [ObservableProperty] private bool _canQuickAdd;

    /// <summary>Dev aid (--record-site): shows Quick add although the demo has no Google account; the recorder adds the event itself.</summary>
    internal static bool DevShowQuickAdd { get; set; }

    [RelayCommand]
    private async Task QuickAdd()
    {
        var text = QuickAddText.Trim();
        if (text.Length == 0 || !CanQuickAdd) return;
        try
        {
            await _engine.QuickAddAsync(text);
            QuickAddText = "";
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.F("Couldn't add \"{0}\": {1}", text, ex.Message));
        }
    }

    /// <summary>The editable calendars, default first (for new events): an account's main one, then the rest, this computer's last.</summary>
    public IReadOnlyList<CalendarInfo> WritableCalendars =>
        CalendarsById.Values.Where(c => c.CanEdit)
            .OrderByDescending(c => c.IsPrimary && c.IsVisible).ThenBy(c => c.IsLocal).ThenBy(c => c.AccountId).ThenBy(c => c.Id).ToList();

    [RelayCommand]
    private void NewEvent()
    {
        var (start, end) = NextSlot();
        OpenEditor(null, start, end, allDay: false);
    }

    [RelayCommand]
    private void NewAllDayEvent() =>
        OpenEditor(null, Anchor.ToDateTime(TimeOnly.MinValue), Anchor.AddDays(1).ToDateTime(TimeOnly.MinValue), allDay: true);

    /// <summary>A new event with a Google Meet call already ticked.</summary>
    [RelayCommand]
    private void NewOnlineMeeting()
    {
        var (start, end) = NextSlot();
        OpenEditor(null, start, end, allDay: false, videoCall: true);
    }

    /// <summary>A meeting with someone (from a contact card): next free slot, video call on, them as a guest.</summary>
    public void NewMeetingWith(string name, string address)
    {
        var (start, end) = NextSlot();
        OpenEditor(null, start, end, allDay: false, title: L.F("Meeting with {0}", name), videoCall: true, guests: address);
    }

    /// <summary>A new event in this calendar (from its context menu).</summary>
    public void NewEventIn(CalendarItemViewModel item)
    {
        var (start, end) = NextSlot();
        OpenEditor(null, start, end, allDay: false, calendarId: item.Info.Id);
    }

    private (DateTime Start, DateTime End) NextSlot()
    {
        var start = Anchor == Today
            ? RoundUp(DateTime.Now, Prefs.SlotMinutes)
            : Anchor.ToDateTime(new TimeOnly(Math.Clamp(Prefs.WorkDayStartHour + 1, 0, 23), 0));
        return (start, start.AddMinutes(Prefs.DefaultDurationMinutes));
    }

    public void OpenEditor(CalendarEvent? existing, DateTime start, DateTime end, bool allDay, string? title = null, long? calendarId = null,
        bool videoCall = false, string? guests = null)
    {
        if (existing is null && WritableCalendars.Count == 0)
        {
            // Nothing to add to yet (no Google calendar): the first event starts a calendar on this computer.
            _ = OpenInNewLocalCalendarAsync(start, end, allDay, title, videoCall, guests);
            return;
        }
        _dialogs.EditEvent(existing, start, end, allDay, title, calendarId, videoCall, guests);
    }

    private async Task OpenInNewLocalCalendarAsync(DateTime start, DateTime end, bool allDay, string? title, bool videoCall, string? guests)
    {
        try
        {
            var id = await _engine.CreateCalendarAsync(null, L.T("Calendar"), Palette[6].Hex);
            await LoadCalendarsAsync();
            _dialogs.EditEvent(null, start, end, allDay, title, id, videoCall, guests);
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.F("Couldn't create the calendar: {0}", ex.Message));
        }
    }

    [RelayCommand] private void Open(CalendarEvent e) => OpenEditor(e, e.Start.LocalDateTime, e.End.LocalDateTime, e.IsAllDay);

    /// <summary>Create straight from the grid's quick box (title typed in place).</summary>
    public async Task CreateQuickAsync(long calendarId, string title, DateTime start, DateTime end, bool allDay)
    {
        try
        {
            await _engine.CreateAsync(calendarId, new EventDraft
            {
                Title = title, IsAllDay = allDay, Start = start, End = end,
                StartDate = DateOnly.FromDateTime(start), EndDate = allDay ? DateOnly.FromDateTime(end) : DateOnly.FromDateTime(start),
                ReminderMinutes = allDay ? null : Prefs.DefaultReminderMinutes,
            });
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.F("Couldn't create \"{0}\": {1}", title, ex.Message));
        }
    }

    /// <summary>Drag in the grid. Repeating events ask whether to move just this one or the whole series.</summary>
    public async Task MoveAsync(CalendarEvent e, DateTime newStart, DateTime newEnd)
    {
        var wholeSeries = false;
        if (e.SeriesId is not null)
        {
            var choice = MessageDialog.Choose(null, L.T("Move a repeating event"), L.F("\"{0}\" repeats. Move only this occurrence, or every one?", e.Title),
                L.T("Only this one"), L.T("All events"));
            if (choice is null) { EventsChanged?.Invoke(); return; } // cancelled: snap the block back
            wholeSeries = choice == false;
        }
        try
        {
            await _engine.MoveAsync(e, newStart, newEnd, wholeSeries);
        }
        catch (Exception ex)
        {
            EventsChanged?.Invoke();
            _dialogs.Error(L.F("Couldn't move \"{0}\": {1}", e.Title, ex.Message));
        }
    }

    [RelayCommand]
    private Task Delete(CalendarEvent e) => DeleteEventAsync(e);

    /// <returns>True when the event was deleted (not cancelled, no error).</returns>
    public async Task<bool> DeleteEventAsync(CalendarEvent e)
    {
        var wholeSeries = false;
        if (e.SeriesId is not null)
        {
            var choice = MessageDialog.Choose(null, L.T("Delete a repeating event"), L.F("\"{0}\" repeats. Delete only this occurrence, or the whole series?", e.Title),
                L.T("Only this one"), L.T("All events"), DialogKind.Danger);
            if (choice is null) return false;
            wholeSeries = choice == false;
        }
        else if (!_dialogs.Confirm(L.F("Delete \"{0}\"?", e.Title), e.Attendees.Count > 1 ? L.T("Guests will be told it's cancelled.") : "", L.T("Delete"), DialogKind.Danger))
        {
            return false;
        }
        try
        {
            await _engine.DeleteAsync(e, wholeSeries);
            return true;
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.F("Couldn't delete \"{0}\": {1}", e.Title, ex.Message));
            return false;
        }
    }

    [RelayCommand]
    private static void Join(CalendarEvent e)
    {
        if (e.MeetingUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    [RelayCommand]
    private static void OpenInGoogle(CalendarEvent e)
    {
        if (e.HtmlLink is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public async Task RespondAsync(CalendarEvent e, string response)
    {
        try { await _engine.RespondAsync(e, response); }
        catch (Exception ex) { _dialogs.Error(L.F("Couldn't send your answer: {0}", ex.Message)); }
    }

    [RelayCommand] private Task Accept(CalendarEvent e) => RespondAsync(e, "accepted");
    [RelayCommand] private Task Tentative(CalendarEvent e) => RespondAsync(e, "tentative");
    [RelayCommand] private Task Decline(CalendarEvent e) => RespondAsync(e, "declined");

    [RelayCommand]
    private async Task ShowOnly(CalendarItemViewModel item)
    {
        foreach (var other in Groups.SelectMany(g => g.Calendars))
            if (other.IsVisible != (other == item)) await _engine.SetVisibleAsync(other.Info.Id, other == item);
        await LoadCalendarsAsync();
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task ShowAll()
    {
        foreach (var c in Groups.SelectMany(g => g.Calendars).Where(c => !c.IsVisible)) await _engine.SetVisibleAsync(c.Info.Id, true);
        await LoadCalendarsAsync();
        await ReloadAsync();
    }

    // ───────── calendars (sidebar menu) ─────────

    /// <summary>The colours offered for calendars: Google Calendar's own palette.</summary>
    public static IReadOnlyList<(string Name, string Hex)> Palette { get; } =
    [
        (L.T("Tomato"), "#D50000"), (L.T("Flamingo"), "#E67C73"), (L.T("Tangerine"), "#F4511E"), (L.T("Banana"), "#F6BF26"),
        (L.T("Sage"), "#33B679"), (L.T("Basil"), "#0B8043"), (L.T("Peacock"), "#039BE5"), (L.T("Blueberry"), "#3F51B5"),
        (L.T("Lavender"), "#7986CB"), (L.T("Grape"), "#8E24AA"), (L.T("Graphite"), "#616161"), (L.T("Cocoa"), "#795548"),
    ];

    /// <param name="accountId">The account to create it in, or null for one kept only on this computer.</param>
    public async Task NewCalendarAsync(long? accountId)
    {
        var prompt = accountId is { } id
            ? L.F("Name of the new calendar in {0}:", Groups.FirstOrDefault(g => g.AccountId == id)?.Title ?? L.T("this account"))
            : L.T("Name of the new calendar on this computer:");
        if (_dialogs.AskText(L.T("New calendar"), prompt, L.T("Create")) is not { } name) return;
        // A colour not used yet in that group, so the new one stands out.
        var used = Groups.Where(g => g.AccountId == accountId).SelectMany(g => g.Calendars).Select(c => c.Info.Color.ToUpperInvariant()).ToHashSet();
        var color = Palette.Select(p => p.Hex).FirstOrDefault(h => !used.Contains(h)) ?? Palette[6].Hex;
        await Try(() => _engine.CreateCalendarAsync(accountId, name, color), L.F("Couldn't create \"{0}\"", name));
    }

    public async Task RenameCalendarAsync(CalendarItemViewModel item)
    {
        var prompt = item.IsOwned ? L.F("New name for “{0}”:", item.Name) : L.F("New name for “{0}” (only how it's named for you):", item.Name);
        if (_dialogs.AskText(L.T("Rename calendar"), prompt, L.T("Rename"), item.Name) is not { } name || name == item.Name) return;
        await Try(() => _engine.RenameCalendarAsync(item.Info.Id, name), L.T("Couldn't rename the calendar"));
    }

    /// <summary>Saves a calendar as an .ics file (a backup, or to bring into another calendar app).</summary>
    public async Task ExportCalendarAsync(CalendarItemViewModel item)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.T("Export calendar"), FileName = Dialogs.SafeFileName(item.Name) + ".ics", Filter = L.T("Calendar files") + " (*.ics)|*.ics",
        };
        if (dialog.ShowDialog() != true) return;
        await Try(async () => await File.WriteAllTextAsync(dialog.FileName, await _engine.ExportAsync(item.Info.Id), new System.Text.UTF8Encoding(false)),
            L.T("Couldn't export the calendar"));
    }

    /// <summary>Brings the events of an .ics file (from another calendar app, or an export) into a calendar.</summary>
    public async Task ImportEventsAsync(CalendarItemViewModel item)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = L.F("Import events into “{0}”", item.Name), Filter = L.T("Calendar files") + " (*.ics)|*.ics" };
        if (dialog.ShowDialog() != true) return;
        await Try(async () =>
        {
            var events = IcsFormat.Read(await File.ReadAllTextAsync(dialog.FileName));
            if (events.Count == 0)
            {
                _dialogs.Info(L.T("Nothing to import"), L.T("That file has no events in it."));
                return;
            }
            var count = await _engine.ImportAsync(item.Info.Id, events);
            _dialogs.Info(L.T("Events imported"), count == 1 ? L.F("1 event was added to “{0}”.", item.Name) : L.F("{0} events were added to “{1}”.", count, item.Name));
        }, L.T("Couldn't import the events"));
    }

    public Task SetColorAsync(CalendarItemViewModel item, string hex) =>
        Try(() => _engine.SetCalendarColorAsync(item.Info.Id, hex), L.T("Couldn't change the colour"));

    public async Task DeleteCalendarAsync(CalendarItemViewModel item)
    {
        var confirmed = item.Info.IsLocal
            ? _dialogs.Confirm(L.F("Delete “{0}”?", item.Name), L.T("The calendar and all its events are deleted from this computer. This can't be undone."),
                L.T("Delete"), DialogKind.Danger)
            : item.IsOwned
            ? _dialogs.Confirm(L.F("Delete “{0}”?", item.Name), L.T("The calendar and all its events are deleted for everyone who uses it. This can't be undone."),
                L.T("Delete"), DialogKind.Danger)
            : _dialogs.Confirm(L.F("Remove “{0}”?", item.Name), L.T("It's taken off your list. The calendar itself stays; you can add it again later."), L.T("Remove"));
        if (confirmed) await Try(() => _engine.DeleteCalendarAsync(item.Info.Id), L.F("Couldn't remove \"{0}\"", item.Name));
    }

    public Task AddHolidaysAsync(long accountId, HolidayCalendar holidays) =>
        Try(() => _engine.AddHolidayCalendarAsync(accountId, holidays), L.F("Couldn't add holidays for {0}", holidays.Country));

    public async Task SetAccountVisibleAsync(long? accountId, bool visible)
    {
        foreach (var c in Groups.Where(g => g.AccountId == accountId).SelectMany(g => g.Calendars).Where(c => c.IsVisible != visible))
            await _engine.SetVisibleAsync(c.Info.Id, visible);
        await LoadCalendarsAsync();
        await ReloadAsync();
    }

    public void RefreshAccount(long accountId) => _engine.RequestSync(accountId);

    [RelayCommand] private void OpenCalendarSettings() => _dialogs.ShowSettings("calendar");

    private async Task Try(Func<Task> action, string failure)
    {
        try { await action(); }
        catch (Exception ex) { _dialogs.Error(L.F("{0}: {1}", failure, ex.Message)); }
    }

    private static DateTime RoundUp(DateTime t, int minutes)
    {
        var slot = TimeSpan.FromMinutes(Math.Max(5, minutes));
        return new DateTime((t.Ticks + slot.Ticks - 1) / slot.Ticks * slot.Ticks, t.Kind);
    }
}
