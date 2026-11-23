using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plicbox.App.Services;
using Plicbox.Core;
using Plicbox.Dav;
using Plicbox.Mail;
using Microsoft.Extensions.Logging;

namespace Plicbox.App.ViewModels;

public enum TaskView { Today, Upcoming, All, FromEmail, Completed, List }

/// <summary>An entry in the sidebar: a smart list, or one of your task lists.</summary>
public sealed partial class TaskListEntry : ObservableObject
{
    public required TaskView Kind { get; init; }
    public TaskList? List { get; init; }
    public required string Title { get; init; }
    public string Glyph { get; init; } = "";
    public string? Hint { get; init; }
    /// <summary>The sidebar group: empty for the smart lists, else the account its lists belong to.</summary>
    public string Group { get; init; } = "";
    [ObservableProperty] private int _count;
    public string CountText => Count > 0 ? Count.ToString(CultureInfo.CurrentCulture) : "";
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    // The folded sidebar: an account's list shows the account, as Mail and Calendar do (its picture, else its initial
    // on its colour), so lists of different accounts don't all look the same. Filled in by the main window.
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAccountIcon))] private System.Windows.Media.Brush? _accountColor;
    [ObservableProperty] private string _accountInitial = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAccountPicture))] private System.Windows.Media.ImageSource? _accountPicture;
    public bool HasAccountIcon => AccountColor is not null;
    public bool HasAccountPicture => AccountPicture is not null;
}

/// <summary>One task in the middle list (or a step in the detail pane).</summary>
public sealed partial class TaskRow : ObservableObject
{
    public TaskRow(TaskItem model, DateOnly today, string group = "", string? listName = null, int stepsDone = 0, int stepsTotal = 0)
    {
        Model = model;
        _isCompleted = model.IsCompleted;
        Group = group;
        ListName = listName;
        if (model.Due is { } due && !model.IsCompleted)
        {
            DueText = TaskDates.Describe(due, today);
            IsOverdue = due < today;
            IsDueToday = due == today;
        }
        else if (model.Due is { } done) DueText = TaskDates.Describe(done, today);
        StepsText = stepsTotal > 0 ? $"{stepsDone}/{stepsTotal}" : null;
    }

    public TaskItem Model { get; internal set; }
    public long Id => Model.Id;
    public string Title => Model.Title.Length > 0 ? Model.Title : L.T("Untitled task");
    [ObservableProperty] private bool _isCompleted;
    public string Group { get; }
    /// <summary>Which list it's in, shown in the smart lists.</summary>
    public string? ListName { get; }
    public string? DueText { get; }
    public bool IsOverdue { get; }
    public bool IsDueToday { get; }
    public bool HasEmail => Model.MessageRef is not null;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Model.Notes);
    public string? StepsText { get; }
    public bool HasMeta => DueText is not null || ListName is not null || HasEmail || HasNotes || StepsText is not null;
}

/// <summary>A task's fields: the new-task window, and the detail pane (which saves as you type).</summary>
public sealed partial class TaskEditor : ObservableObject
{
    public TaskItem? Original { get; internal set; }
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private DateTime? _due;
    [ObservableProperty] private Option<long>? _list;
    [ObservableProperty] private bool _isCompleted;
    [ObservableProperty] private string _newStep = "";
    /// <summary>A date read out of the title ("…tomorrow"), used when no date is picked.</summary>
    [ObservableProperty] private string? _titleHint;
    public IReadOnlyList<Option<long>> Lists { get; init; } = [];
    public ObservableCollection<TaskRow> Steps { get; } = [];
    public string? MessageRef { get; init; }
    /// <summary>The email it was made from: "Invoice 2026-10 · Ana Popescu".</summary>
    [ObservableProperty] private string? _emailText;
    public bool HasEmail => MessageRef is not null;
    public bool IsNew => Original is null;
    public string Heading => IsNew ? L.T("New task") : L.T("Edit task");
    public string DueSummary => Due is { } d ? TaskDates.Long(DateOnly.FromDateTime(d), DateOnly.FromDateTime(DateTime.Today)) : L.T("No due date");
    public bool HasDue => Due is not null;

    /// <summary>The new-task window's Save: false keeps it open.</summary>
    public Func<TaskEditor, Task<bool>>? Save { get; init; }
    public Func<string, Task>? OpenEmailRequested { get; init; }

    partial void OnDueChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(DueSummary));
        OnPropertyChanged(nameof(HasDue));
        UpdateTitleHint();
    }

    partial void OnTitleChanged(string value) => UpdateTitleHint();

    private void UpdateTitleHint()
    {
        if (!IsNew || Due is not null) { TitleHint = null; return; }
        var today = DateOnly.FromDateTime(DateTime.Today);
        TitleHint = TaskPhrases.Parse(Title, today).Due is { } d ? L.F("Due {0}, from what you typed", TaskDates.Long(d, today)) : null;
    }

    [RelayCommand]
    private void SetDue(string which)
    {
        var today = DateTime.Today;
        Due = which switch
        {
            "today" => today,
            "tomorrow" => today.AddDays(1),
            "nextweek" => today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 6) % 7 + 1), // the coming Monday
            _ => null,
        };
    }

    [RelayCommand]
    private async Task OpenEmail()
    {
        if (MessageRef is not null && OpenEmailRequested is not null) await OpenEmailRequested(MessageRef);
    }
}

/// <summary>Due dates in words: L.T("Today"), L.T("Tomorrow"), "Wednesday", "Sat 24 Oct".</summary>
public static class TaskDates
{
    // "16. Oktober" where the language writes the day with a dot, as the calendar does.
    private static string Dot => CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern.Contains("d.", StringComparison.Ordinal) ? "." : "";

    /// <summary>"Friday 16 October".</summary>
    public static string Day(DateOnly day) => day.ToString($"dddd d{Dot} MMMM", CultureInfo.CurrentCulture);

    public static string Describe(DateOnly due, DateOnly today)
    {
        var days = due.DayNumber - today.DayNumber;
        var c = CultureInfo.CurrentCulture;
        return days switch
        {
            0 => L.T("Today"),
            1 => L.T("Tomorrow"),
            -1 => L.T("Yesterday"),
            > 1 and < 7 => due.ToString("dddd", c),
            _ when due.Year == today.Year => due.ToString($"ddd d{Dot} MMM", c),
            _ => due.ToString($"d{Dot} MMM yyyy", c),
        };
    }

    /// <summary>"Tomorrow, Saturday 17 October" · "Overdue: Monday 12 October".</summary>
    public static string Long(DateOnly due, DateOnly today)
    {
        var c = CultureInfo.CurrentCulture;
        var full = Day(due) + (due.Year == today.Year ? "" : due.ToString(" yyyy", c));
        var days = due.DayNumber - today.DayNumber;
        return days switch
        {
            0 => L.F("Today, {0}", full),
            1 => L.F("Tomorrow, {0}", full),
            < 0 => L.F("Overdue: {0}", full),
            _ => full,
        };
    }
}

public sealed partial class TasksViewModel : ObservableObject
{
    private readonly TasksEngine _engine;
    private readonly IAccountRepository _accounts;
    private readonly IMessageRepository _messages;
    private readonly Dialogs _dialogs;
    private readonly ILogger<TasksViewModel> _log;
    private readonly SynchronizationContext _ui;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private List<TaskList> _taskLists = [];
    private List<TaskItem> _tasks = [];
    private Dictionary<long, string> _accountNames = [];
    private CancellationTokenSource? _reloadCts, _typingCts;
    private bool _stale = true, _editorChanged;
    private long? _bannerAccountId;
    // Just ticked off: they stay where they are a moment (struck through) before leaving the list.
    private readonly HashSet<long> _lingering = [];

    /// <summary>The module is on screen: changes refresh it right away (otherwise on the next visit).</summary>
    public bool IsShown { get; set; }

    /// <summary>Opens an email in its own window (set by the main window).</summary>
    public Func<long, Task>? OpenMessageRequested { get; set; }

    public TasksViewModel(TasksEngine engine, IAccountRepository accounts, IMessageRepository messages, Dialogs dialogs, ILogger<TasksViewModel> log)
    {
        (_engine, _accounts, _messages, _dialogs, _log) = (engine, accounts, messages, dialogs, log);
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        engine.Changed += () => _ui.Post(_ => { _stale = true; if (IsShown) _ = ReloadSoonAsync(); }, null);
        engine.StatusChanged += () => _ui.Post(_ => UpdateBanner(), null);
        foreach (var smart in _smart) Lists.Add(smart);
        _selectedList = _smart[0];
    }

    private readonly TaskListEntry[] _smart =
    [
        new() { Kind = TaskView.Today, Title = L.T("Today"), Glyph = "", Hint = L.T("Due today, and anything overdue") },
        new() { Kind = TaskView.Upcoming, Title = L.T("Upcoming"), Glyph = "", Hint = L.T("The next 7 days") },
        new() { Kind = TaskView.All, Title = L.T("All tasks"), Glyph = "", Hint = L.T("Everything still to do") },
        new() { Kind = TaskView.FromEmail, Title = L.T("From email"), Glyph = "", Hint = L.T("Tasks made from a message") },
        new() { Kind = TaskView.Completed, Title = L.T("Completed"), Glyph = "", Hint = L.T("Done, newest first") },
    ];

    public ObservableCollection<TaskListEntry> Lists { get; } = [];
    [ObservableProperty] private TaskListEntry _selectedList;
    /// <summary>The tasks shown. Replaced as a whole (one redraw) rather than row by row.</summary>
    [ObservableProperty] private IReadOnlyList<TaskRow> _rows = [];
    [ObservableProperty] private TaskRow? _selected;
    [ObservableProperty] private TaskEditor? _editor;
    [ObservableProperty] private string _quickText = "";
    [ObservableProperty] private string? _quickHint;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private string _listTitle = L.T("Today");
    [ObservableProperty] private string _listHint = "";
    [ObservableProperty] private bool _isEmpty;
    [ObservableProperty] private string _emptyTitle = "";
    [ObservableProperty] private string _emptyText = "";
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private string? _banner;
    [ObservableProperty] private string? _bannerAction;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _refreshToolTip = L.T("Refresh (F5)");
    public bool CanAddHere => SelectedList.Kind != TaskView.Completed && SearchText.Length == 0;
    public string QuickPlaceholder => SelectedList.Kind switch
    {
        TaskView.Today => L.T("Add a task for today"),
        TaskView.Upcoming => L.T("Add a task, e.g. “Send report friday”"),
        _ => L.T("Add a task, e.g. “Call Ana tomorrow”"),
    };

    partial void OnSelectedListChanged(TaskListEntry value)
    {
        _lingering.Clear();
        OnPropertyChanged(nameof(CanAddHere));
        OnPropertyChanged(nameof(QuickPlaceholder));
        OnQuickTextChanged(QuickText);
        Refilter();
        Selected = Rows.FirstOrDefault(r => r.Id == Selected?.Id);
    }

    [RelayCommand] private void ClearSearch() => SearchText = "";

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanAddHere));
        Refilter();
    }

    partial void OnQuickTextChanged(string value)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (_, due) = TaskPhrases.Parse(value, today);
        var list = TargetList();
        var where = SelectedList.Kind == TaskView.List || list is null ? "" : " · " + L.F("in {0}", ListName(list));
        QuickHint = value.Trim().Length == 0 ? null
            : due is { } d ? L.F("Due {0}", TaskDates.Long(d, today)) + where
            : DefaultDue() is { } dd ? L.F("Due {0}", TaskDates.Long(dd, today)) + where
            : where.Length > 0 ? where[3..] : null;
    }

    partial void OnSelectedChanged(TaskRow? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DoneLabel));
        OnPropertyChanged(nameof(SelectedHasEmail));
        _ = OpenEditorAsync(value);
    }

    // The toolbar's actions follow the selected task.
    public bool HasSelection => Selected is not null;
    public string DoneLabel => Selected?.Model.IsCompleted == true ? L.T("Not Done") : L.T("Done");
    public bool SelectedHasEmail => Selected?.HasEmail == true;

    [RelayCommand]
    private Task ToggleSelected() => Selected is { } row ? ToggleComplete(row) : Task.CompletedTask;

    [RelayCommand]
    private Task OpenSelectedEmail() => Selected is { } row ? OpenRowEmail(row) : Task.CompletedTask;

    // ───────── loading ─────────

    public async Task LoadAsync()
    {
        try
        {
            var accounts = await _accounts.GetAllAsync();
            _accountNames = accounts.ToDictionary(a => a.Id, a => a.SidebarTitle);
            _taskLists = (await _engine.GetListsAsync()).ToList();
            _tasks = (await _engine.GetTasksAsync()).ToList();
            _stale = false;
            RebuildSidebar();
            Refilter();
            RefreshEditor();
            UpdateBanner();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading tasks failed");
        }
    }

    /// <summary>Coming back to the module: reload only if something changed meanwhile.</summary>
    public Task EnsureFreshAsync() => _stale ? LoadAsync() : Task.CompletedTask;

    private async Task ReloadSoonAsync()
    {
        _reloadCts?.Cancel();
        var cts = _reloadCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            await LoadAsync();
        }
        catch (OperationCanceledException) { }
    }

    private void RebuildSidebar()
    {
        var keep = SelectedList;
        var wanted = new List<TaskListEntry>(_smart);
        var multipleAccounts = _taskLists.Select(l => l.AccountId).Distinct().Count() > 1;
        // Google lists first, by account (the first account's "My Tasks" on top), then lists kept only here.
        foreach (var list in _taskLists.OrderBy(l => l.AccountId is null).ThenBy(l => l.AccountId).ThenBy(l => l.Id))
        {
            var group = !multipleAccounts ? L.T("Lists") : list.AccountId is { } id ? _accountNames.GetValueOrDefault(id, "Google") : L.T("On this computer");
            wanted.Add(new TaskListEntry { Kind = TaskView.List, List = list, Title = list.Title, Group = group, Hint = AccountHint(list) });
        }
        // Only touch the sidebar when the lists themselves changed (keeps the selection and avoids a redraw).
        var same = wanted.Count == Lists.Count && wanted.Zip(Lists).All(p => p.First.Kind == p.Second.Kind && p.First.List?.Id == p.Second.List?.Id
                                                                                && p.First.Title == p.Second.Title && p.First.Group == p.Second.Group);
        if (!same)
        {
            Lists.Clear();
            foreach (var entry in wanted) Lists.Add(entry);
            SelectedList = Lists.FirstOrDefault(e => e.Kind == keep.Kind && e.List?.Id == keep.List?.Id) ?? Lists[0];
        }
        UpdateCounts();
    }

    private string AccountHint(TaskList list) =>
        list.AccountId is { } id ? $"Google Tasks · {_accountNames.GetValueOrDefault(id, "")}" : L.T("Kept only in Plicbox");

    private void UpdateCounts()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var open = _tasks.Where(t => !t.IsCompleted).ToList();
        foreach (var entry in Lists)
            entry.Count = entry.Kind switch
            {
                TaskView.Today => open.Count(t => t.Due <= today),
                TaskView.Upcoming => open.Count(t => t.Due > today && t.Due <= today.AddDays(7)),
                TaskView.All => open.Count(t => !IsStep(t)),
                TaskView.FromEmail => open.Count(t => t.MessageRef is not null),
                TaskView.List => open.Count(t => t.ListId == entry.List!.Id && !IsStep(t)),
                _ => 0,
            };
    }

    private bool IsStep(TaskItem t) => t.ParentRemoteId is not null && _tasks.Any(p => p.ListId == t.ListId && p.RemoteId == t.ParentRemoteId);

    // ───────── what's shown ─────────

    private void Refilter()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var keep = Selected?.Id;
        var listNames = _taskLists.ToDictionary(l => l.Id, ListName);
        // The open task stays while you look at it, even once done (as read mail stays in Unread).
        bool Open(TaskItem t) => !t.IsCompleted || _lingering.Contains(t.Id) || t.Id == keep;
        var view = SelectedList.Kind;
        IEnumerable<TaskItem> items;
        Func<TaskItem, string> group;
        var query = ContactsViewModel.Fold(SearchText.Trim());
        if (query.Length > 0)
        {
            items = _tasks.Where(t => ContactsViewModel.Fold($"{t.Title} {t.Notes}").Contains(query, StringComparison.Ordinal))
                .OrderBy(t => t.IsCompleted).ThenBy(t => t.Due ?? DateOnly.MaxValue);
            group = t => t.IsCompleted ? L.T("Completed") : L.T("To do");
            ListTitle = L.F("Search: “{0}”", SearchText.Trim());
            ListHint = L.T("Titles and notes, done tasks too");
        }
        else
        {
            ListTitle = SelectedList.Title;
            ListHint = SelectedList.Hint ?? "";
            switch (view)
            {
                case TaskView.Today:
                    items = _tasks.Where(t => Open(t) && t.Due <= today);
                    group = t => t.Due < today ? L.T("Overdue") : L.T("Today");
                    break;
                case TaskView.Upcoming:
                    items = _tasks.Where(t => Open(t) && t.Due > today && t.Due <= today.AddDays(7));
                    group = t => t.Due == today.AddDays(1) ? L.T("Tomorrow") : TaskDates.Day(t.Due!.Value);
                    break;
                case TaskView.Completed:
                    items = _tasks.Where(t => t.IsCompleted).OrderByDescending(t => t.CompletedAt ?? t.Updated);
                    group = t => DoneGroup(t, today);
                    break;
                case TaskView.FromEmail:
                    items = _tasks.Where(t => Open(t) && t.MessageRef is not null);
                    group = t => DueGroup(t, today);
                    break;
                case TaskView.List:
                    var id = SelectedList.List!.Id;
                    items = _tasks.Where(t => t.ListId == id && !IsStep(t));
                    // Google's own order for the list; done ones underneath.
                    group = t => t.IsCompleted && !_lingering.Contains(t.Id) ? L.T("Completed") : "";
                    items = items.OrderBy(t => t.IsCompleted && !_lingering.Contains(t.Id))
                        .ThenBy(t => t.IsCompleted ? -(t.CompletedAt ?? t.Updated).ToUnixTimeSeconds() : 0)
                        .ThenBy(t => t.Position is null ? 0 : 1).ThenBy(t => t.Position, StringComparer.Ordinal);
                    break;
                default:
                    items = _tasks.Where(t => Open(t) && !IsStep(t));
                    group = t => DueGroup(t, today);
                    break;
            }
            if (view is not (TaskView.List or TaskView.Completed))
                items = items.OrderBy(t => t.Due ?? DateOnly.MaxValue).ThenBy(t => t.Position is null ? 0 : 1).ThenBy(t => t.Position, StringComparer.Ordinal);
        }

        var showList = view != TaskView.List || query.Length > 0;
        Rows = items.Select(t =>
        {
            var steps = t.RemoteId is null ? [] : _tasks.Where(s => s.ListId == t.ListId && s.ParentRemoteId == t.RemoteId).ToList();
            return new TaskRow(t, today, group(t), showList && _taskLists.Count > 1 ? listNames.GetValueOrDefault(t.ListId) : null,
                steps.Count(s => s.IsCompleted), steps.Count);
        }).ToList();
        IsEmpty = Rows.Count == 0;
        IsSearching = query.Length > 0;
        EmptyText = IsSearching ? L.T("Check the spelling, or try a shorter word.") : "";
        EmptyTitle = IsSearching ? L.F("No tasks match “{0}”", SearchText.Trim()) : view switch
        {
            TaskView.Today => L.T("Nothing due today. Enjoy it."),
            TaskView.Upcoming => L.T("Nothing due in the next 7 days."),
            TaskView.Completed => L.T("Nothing completed yet."),
            TaskView.FromEmail => L.T("Right-click a message and choose Create Task to make one from it."),
            _ => L.T("No tasks here yet."),
        };
        if (keep is not null && Rows.FirstOrDefault(r => r.Id == keep) is { } again)
        {
            // Same task, fresh row: keep it selected without reopening the editor.
            _keepingSelection = true;
            Selected = again;
            _keepingSelection = false;
        }
        UpdateCounts();
    }

    private bool _keepingSelection;

    private static string DueGroup(TaskItem t, DateOnly today) => t.Due switch
    {
        null => L.T("No date"),
        var d when d < today => L.T("Overdue"),
        var d when d == today => L.T("Today"),
        var d when d == today.AddDays(1) => L.T("Tomorrow"),
        var d when d <= today.AddDays(7) => L.T("Next 7 days"),
        _ => L.T("Later"),
    };

    private static string DoneGroup(TaskItem t, DateOnly today)
    {
        var day = DateOnly.FromDateTime((t.CompletedAt ?? t.Updated).LocalDateTime);
        return day == today ? L.T("Today") : day == today.AddDays(-1) ? L.T("Yesterday") : day > today.AddDays(-7) ? L.T("This week") : L.T("Earlier");
    }

    /// <summary>A list's name; with its account when another account has a list of the same name (both have "My Tasks").</summary>
    private string ListName(TaskList list) =>
        list.AccountId is { } id && _taskLists.Any(l => l.Id != list.Id && l.AccountId != list.AccountId && l.Title == list.Title)
            ? $"{list.Title} · {_accountNames.GetValueOrDefault(id, "Google")}" : list.Title;

    // ───────── quick add, ticking off ─────────

    /// <summary>Where a task added from here goes: the open list, or your first list (Google's "My Tasks").</summary>
    private TaskList? TargetList() =>
        SelectedList.List ?? _taskLists.Where(l => l.AccountId is not null).OrderBy(l => l.AccountId).ThenBy(l => l.Id).FirstOrDefault() ?? _taskLists.FirstOrDefault();

    private DateOnly? DefaultDue() => SelectedList.Kind switch
    {
        TaskView.Today => DateOnly.FromDateTime(DateTime.Today),
        TaskView.Upcoming => DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
        _ => null,
    };

    [RelayCommand]
    private async Task AddQuick()
    {
        var text = QuickText.Trim();
        if (text.Length == 0) return;
        var (title, due) = TaskPhrases.Parse(text, DateOnly.FromDateTime(DateTime.Today));
        var list = TargetList() ?? await CreateLocalListAsync();
        QuickText = "";
        var saved = await SaveAsync(new TaskItem { ListId = list.Id, Title = title, Due = due ?? DefaultDue() });
        if (saved is not null) await LoadAsync();
    }

    private Task<TaskList> CreateLocalListAsync() => _engine.CreateListAsync(null, L.T("Tasks"));

    [RelayCommand]
    private async Task ToggleComplete(TaskRow row)
    {
        var done = !row.Model.IsCompleted;
        row.IsCompleted = done;
        if (done && SelectedList.Kind != TaskView.Completed)
        {
            // Stays put a moment, struck through, so a wrong click is easy to undo.
            _lingering.Add(row.Id);
            _ = LeaveSoonAsync(row.Id);
        }
        await UpdateAsync(row.Id, t => t with { IsCompleted = done, CompletedAt = done ? DateTimeOffset.UtcNow : null });
        if (Editor is { } editor && editor.Original?.Id == row.Id) Load(editor, () => editor.IsCompleted = done);
    }

    private async Task LeaveSoonAsync(long id)
    {
        await Task.Delay(1500);
        if (_lingering.Remove(id)) Refilter();
    }

    // ───────── the detail pane ─────────

    private async Task OpenEditorAsync(TaskRow? row)
    {
        if (_keepingSelection) return;
        await FlushEditorAsync();
        if (row is null) { Editor = null; return; }
        var t = _tasks.FirstOrDefault(x => x.Id == row.Id) ?? row.Model;
        var editor = new TaskEditor
        {
            Original = t, Lists = ListOptions(), MessageRef = t.MessageRef,
            OpenEmailRequested = OpenEmailAsync,
        };
        Load(editor, () => Fill(editor, t));
        editor.PropertyChanged += Editor_PropertyChanged;
        Editor = editor;
        await DescribeEmailAsync(editor);
    }

    private void Fill(TaskEditor editor, TaskItem t)
    {
        editor.Title = t.Title;
        editor.Notes = t.Notes ?? "";
        editor.Due = t.Due?.ToDateTime(TimeOnly.MinValue);
        editor.IsCompleted = t.IsCompleted;
        editor.List = editor.Lists.FirstOrDefault(l => l.Value == t.ListId);
        var today = DateOnly.FromDateTime(DateTime.Today);
        editor.Steps.Clear();
        if (t.RemoteId is not null)
            foreach (var step in _tasks.Where(s => s.ListId == t.ListId && s.ParentRemoteId == t.RemoteId)
                         .OrderBy(s => s.Position is null ? 0 : 1).ThenBy(s => s.Position, StringComparer.Ordinal))
                editor.Steps.Add(new TaskRow(step, today));
    }

    private bool _loadingEditor;

    private void Load(TaskEditor editor, Action fill)
    {
        _loadingEditor = true;
        try { fill(); }
        finally { _loadingEditor = false; }
    }

    /// <summary>After a reload: the open task picks up changes from elsewhere (Gmail, the phone) unless you're typing in it.</summary>
    private void RefreshEditor()
    {
        if (Editor?.Original is not { } original) return;
        if (_tasks.FirstOrDefault(t => t.Id == original.Id) is not { } fresh)
        {
            Editor = null; // deleted elsewhere
            return;
        }
        Editor.Original = fresh;
        if (_editorChanged) return;
        if (fresh.Title != Editor.Title || (fresh.Notes ?? "") != Editor.Notes || fresh.Due?.ToDateTime(TimeOnly.MinValue) != Editor.Due
            || fresh.IsCompleted != Editor.IsCompleted || fresh.ListId != Editor.List?.Value || StepsChanged(Editor, fresh))
            Load(Editor, () => Fill(Editor, fresh));
    }

    private bool StepsChanged(TaskEditor editor, TaskItem t)
    {
        var now = t.RemoteId is null ? [] : _tasks.Where(s => s.ListId == t.ListId && s.ParentRemoteId == t.RemoteId).ToList();
        return now.Count != editor.Steps.Count || now.Any(s => editor.Steps.FirstOrDefault(r => r.Id == s.Id) is not { } r || r.Model != s);
    }

    private IReadOnlyList<Option<long>> ListOptions()
    {
        var multipleAccounts = _taskLists.Select(l => l.AccountId).Distinct().Count() > 1;
        return _taskLists.Select(l => new Option<long>(l.Id,
            multipleAccounts ? $"{l.Title} · {(l.AccountId is { } a ? _accountNames.GetValueOrDefault(a, "Google") : L.T("this computer"))}" : l.Title)).ToList();
    }

    private async Task DescribeEmailAsync(TaskEditor editor)
    {
        if (editor.MessageRef is not { } reference) return;
        var message = await _messages.GetByMessageIdAsync(reference);
        editor.EmailText = message is null ? L.T("The email isn't in Plicbox (anymore)")
            : $"{message.Subject} · {(message.FromName.Length > 0 ? message.FromName : message.FromAddress)}";
    }

    private void Editor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_loadingEditor || sender is not TaskEditor editor || editor != Editor) return;
        switch (e.PropertyName)
        {
            case nameof(TaskEditor.Title) or nameof(TaskEditor.Notes):
                _editorChanged = true;
                _ = SaveTypingSoonAsync();
                break;
            case nameof(TaskEditor.Due) or nameof(TaskEditor.IsCompleted):
                _editorChanged = true;
                _ = FlushEditorAsync();
                break;
            case nameof(TaskEditor.List) when editor.List is { } list && editor.Original is { } t && list.Value != t.ListId:
                _ = MoveAsync(editor, list.Value);
                break;
        }
    }

    private async Task SaveTypingSoonAsync()
    {
        _typingCts?.Cancel();
        var cts = _typingCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(700, cts.Token);
            await FlushEditorAsync();
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Saves what's typed in the detail pane (if anything changed).</summary>
    public async Task FlushEditorAsync()
    {
        _typingCts?.Cancel();
        if (!_editorChanged || Editor?.Original is not { } original) return;
        _editorChanged = false;
        var editor = Editor;
        var due = editor.Due is { } d ? DateOnly.FromDateTime(d) : (DateOnly?)null;
        await UpdateAsync(original.Id, t => t with
        {
            Title = editor.Title.Trim(), Notes = editor.Notes.Length > 0 ? editor.Notes : null, Due = due,
            IsCompleted = editor.IsCompleted, CompletedAt = editor.IsCompleted ? t.CompletedAt ?? DateTimeOffset.UtcNow : null,
        });
    }

    private async Task MoveAsync(TaskEditor editor, long listId)
    {
        await FlushEditorAsync();
        if (editor.Original is not { } t) return;
        await _saveLock.WaitAsync();
        try
        {
            var moved = await _engine.MoveToListAsync(await _engine.GetTaskAsync(t.Id) ?? t, listId);
            await LoadAsync();
            Selected = Rows.FirstOrDefault(r => r.Id == moved.Id);
            if (Selected is null && Lists.FirstOrDefault(l => l.List?.Id == listId) is { } entry)
            {
                SelectedList = entry; // follow it to its new list
                Selected = Rows.FirstOrDefault(r => r.Id == moved.Id);
            }
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.F("Couldn't move the task: {0}", ex.Message));
        }
        finally
        {
            _saveLock.Release();
        }
    }

    [RelayCommand]
    private async Task AddStep()
    {
        if (Editor is not { Original: { } parent } editor || editor.NewStep.Trim() is not { Length: > 0 } title) return;
        editor.NewStep = "";
        await SaveAsync(new TaskItem { ListId = parent.ListId, ParentRemoteId = parent.RemoteId, Title = title });
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ToggleStep(TaskRow step)
    {
        var done = !step.Model.IsCompleted;
        step.IsCompleted = done;
        await UpdateAsync(step.Id, t => t with { IsCompleted = done, CompletedAt = done ? DateTimeOffset.UtcNow : null });
    }

    [RelayCommand]
    private async Task DeleteStep(TaskRow step)
    {
        await Try(() => _engine.DeleteAsync(step.Model), L.T("Couldn't delete the step"));
        await LoadAsync();
    }

    [RelayCommand]
    private async Task Delete()
    {
        if (Selected is not { } row) return;
        var question = row.StepsText is not null ? L.F("Delete “{0}” and its steps?", row.Title) : L.F("Delete “{0}”?", row.Title);
        if (!_dialogs.Confirm(L.T("Delete task"), question, L.T("Delete"), DialogKind.Danger)) return;
        _editorChanged = false;
        var index = Rows.ToList().IndexOf(row);
        await Try(() => _engine.DeleteAsync(row.Model), L.T("Couldn't delete the task"));
        await LoadAsync();
        Selected = Rows.Count == 0 ? null : Rows[Math.Clamp(index, 0, Rows.Count - 1)];
    }

    [RelayCommand]
    private void CloseEditor() => Selected = null;

    /// <summary>The detail pane's round tick.</summary>
    public void ToggleEditorDone()
    {
        if (Editor is { } editor) editor.IsCompleted = !editor.IsCompleted;
    }

    public Task SetDueAsync(TaskRow row, DateOnly? due) => UpdateAsync(row.Id, t => t with { Due = due });

    public async Task MoveRowAsync(TaskRow row, long listId)
    {
        await FlushEditorAsync();
        await _saveLock.WaitAsync();
        try { await _engine.MoveToListAsync(await _engine.GetTaskAsync(row.Id) ?? row.Model, listId); }
        catch (Exception ex) { _dialogs.Error(L.F("Couldn't move the task: {0}", ex.Message)); }
        finally { _saveLock.Release(); }
        await LoadAsync();
    }

    /// <summary>The lists a task can move to (all but its own).</summary>
    public IEnumerable<TaskListEntry> MoveTargets(TaskRow row) => Lists.Where(l => l.List is not null && l.List.Id != row.Model.ListId);

    public Task OpenRowEmailAsync(TaskRow row) => OpenRowEmail(row);

    private async Task OpenEmailAsync(string messageRef)
    {
        if (await _messages.GetByMessageIdAsync(messageRef) is { } message && OpenMessageRequested is not null)
            await OpenMessageRequested(message.Id);
        else
            _dialogs.Info(L.T("Email not found"), L.T("The email this task was made from isn't in Plicbox anymore. It may have been deleted."));
    }

    [RelayCommand]
    private Task OpenRowEmail(TaskRow row) => row.Model.MessageRef is { } r ? OpenEmailAsync(r) : Task.CompletedTask;

    // ───────── new task window ─────────

    [RelayCommand]
    public async Task NewTask() => await OpenNewTaskAsync(null, null, null);

    /// <summary>"Create Task" on a message: the subject as the title, linked to the email.</summary>
    public async Task CreateFromMessageAsync(MessageSummary message)
    {
        var from = message.FromName.Length > 0 ? message.FromName : message.FromAddress;
        await OpenNewTaskAsync(RuleMatcher.CleanSubject(message.Subject), message.MessageId, $"{message.Subject} · {from}");
    }

    private async Task OpenNewTaskAsync(string? title, string? messageRef, string? emailText)
    {
        if (_stale) await LoadAsync();
        var target = TargetList();
        var lists = ListOptions();
        if (lists.Count == 0) lists = [new Option<long>(0, L.T("Tasks"))]; // made when saving
        var editor = new TaskEditor
        {
            Lists = lists, MessageRef = messageRef, EmailText = emailText, OpenEmailRequested = OpenEmailAsync,
            Save = SaveNewAsync,
        };
        editor.Title = title ?? "";
        editor.Due = SelectedList.Kind == TaskView.Today && IsShown && messageRef is null ? DateTime.Today : null;
        editor.List = lists.FirstOrDefault(l => l.Value == target?.Id) ?? lists[0];
        _dialogs.EditTask(editor);
    }

    private async Task<bool> SaveNewAsync(TaskEditor editor)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (title, parsed) = editor.Due is null ? TaskPhrases.Parse(editor.Title, today) : (editor.Title.Trim(), null);
        if (title.Length == 0)
        {
            _dialogs.Error(L.T("Give the task a title."));
            return false;
        }
        var listId = editor.List is { Value: > 0 } chosen ? chosen.Value : (await CreateLocalListAsync()).Id;
        var saved = await SaveAsync(new TaskItem
        {
            ListId = listId, Title = title, Notes = editor.Notes.Length > 0 ? editor.Notes : null, MessageRef = editor.MessageRef,
            Due = editor.Due is { } d ? DateOnly.FromDateTime(d) : parsed,
        });
        if (saved is null) return false;
        await LoadAsync();
        if (IsShown) Selected = Rows.FirstOrDefault(r => r.Id == saved.Id);
        return true;
    }

    // ───────── lists ─────────

    [RelayCommand]
    private async Task NewList()
    {
        var accounts = (await _accounts.GetAllAsync()).Where(a => a.Auth == AuthKind.GoogleOAuth).ToList();
        // In the account of the list you're in (or your first Google account).
        var accountId = SelectedList.List?.AccountId ?? accounts.FirstOrDefault()?.Id;
        var prompt = accountId is { } id ? L.F("Name of the new task list in {0}:", _accountNames.GetValueOrDefault(id, "Google")) : L.T("Name of the new task list:");
        if (_dialogs.AskText(L.T("New list"), prompt, L.T("Create")) is not { Length: > 0 } name) return;
        TaskList? list = null;
        await Try(async () => list = await _engine.CreateListAsync(accountId, name.Trim()), L.T("Couldn't create the list"));
        await LoadAsync();
        if (list is not null && Lists.FirstOrDefault(l => l.List?.Id == list.Id) is { } entry) SelectedList = entry;
    }

    public async Task RenameListAsync(TaskListEntry entry)
    {
        if (entry.List is not { } list) return;
        if (_dialogs.AskText(L.T("Rename list"), L.F("New name for “{0}”:", list.Title), L.T("Rename"), list.Title) is not { Length: > 0 } name || name == list.Title) return;
        await Try(() => _engine.RenameListAsync(list, name.Trim()), L.T("Couldn't rename the list"));
        await LoadAsync();
    }

    public async Task DeleteListAsync(TaskListEntry entry)
    {
        if (entry.List is not { } list) return;
        var count = _tasks.Count(t => t.ListId == list.Id);
        var question = count == 0 ? L.F("Delete “{0}”?", list.Title)
            : count == 1 ? L.F("Delete “{0}” and its task?", list.Title)
            : L.F("Delete “{0}” and its {1} tasks?", list.Title, count);
        if (list.AccountId is not null) question += " " + L.T("This deletes it in Google Tasks too.");
        if (!_dialogs.Confirm(L.T("Delete list"), question, L.T("Delete"), DialogKind.Danger)) return;
        await Try(() => _engine.DeleteListAsync(list), L.T("Couldn't delete the list"));
        await LoadAsync();
    }

    /// <summary>Clears the completed tasks of a list (they stay in Google's history, as in Google Tasks).</summary>
    public async Task DeleteCompletedAsync(TaskListEntry entry)
    {
        var done = _tasks.Where(t => t.IsCompleted && (entry.List is null || t.ListId == entry.List.Id)).ToList();
        if (done.Count == 0) return;
        if (!_dialogs.Confirm(L.T("Delete completed tasks"), done.Count == 1 ? L.T("Delete 1 completed task?") : L.F("Delete {0} completed tasks?", done.Count),
                L.T("Delete"), DialogKind.Danger)) return;
        await Try(async () => { foreach (var t in done) await _engine.DeleteAsync(t); }, L.T("Couldn't delete the completed tasks"));
        await LoadAsync();
    }

    public bool HasCompleted(TaskListEntry entry) => _tasks.Any(t => t.IsCompleted && (entry.List is null || t.ListId == entry.List.Id));

    // ───────── saving ─────────

    private async Task<TaskItem?> SaveAsync(TaskItem task)
    {
        await _saveLock.WaitAsync();
        try { return await _engine.SaveAsync(task); }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Saving a task failed");
            _dialogs.Error(L.F("Couldn't save the task: {0}", ex.Message));
            return null;
        }
        finally { _saveLock.Release(); }
    }

    /// <summary>Changes a task, starting from what's stored now (saves run one at a time, so nothing is lost or sent twice).</summary>
    private async Task UpdateAsync(long id, Func<TaskItem, TaskItem> change)
    {
        await _saveLock.WaitAsync();
        try
        {
            if (await _engine.GetTaskAsync(id) is not { } latest) return;
            var saved = await _engine.SaveAsync(change(latest));
            if (Editor?.Original?.Id == id) Editor.Original = saved;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Saving a task failed");
            _dialogs.Error(L.F("Couldn't save the task: {0}", ex.Message));
        }
        finally { _saveLock.Release(); }
    }

    // ───────── sync banner ─────────

    private void UpdateBanner()
    {
        var problem = _engine.Statuses.FirstOrDefault(s => s.Value.State is CalendarSyncState.NeedsPermission or CalendarSyncState.NeedsSignIn or CalendarSyncState.Error);
        IsBusy = _engine.Statuses.Values.Any(s => s.State == CalendarSyncState.Syncing);
        RefreshToolTip = IsBusy ? L.T("Refresh (F5) · Syncing…")
            : _engine.Statuses.Values.Select(s => s.LastSync).Where(t => t.HasValue).Min() is { } last ? L.F("Refresh (F5) · Updated {0:t}", last.LocalDateTime)
            : L.T("Refresh (F5)");
        _bannerAccountId = problem.Value is null ? null : problem.Key;
        Banner = problem.Value?.State == CalendarSyncState.NeedsPermission
            ? problem.Value.Message + " " + L.T("Sign in once more and allow access to your tasks.")
            : problem.Value?.Message;
        BannerAction = problem.Value?.State is CalendarSyncState.NeedsPermission or CalendarSyncState.NeedsSignIn ? L.T("Allow access") : problem.Value is null ? null : L.T("Try again");
    }

    [RelayCommand]
    private async Task FixBanner()
    {
        if (_bannerAccountId is not { } id) return;
        if (BannerAction == L.T("Allow access") && await _accounts.GetAsync(id) is { } account) await _dialogs.SignInAgainAsync(account);
        _engine.RequestSync(id);
    }

    [RelayCommand]
    private async Task Refresh()
    {
        await FlushEditorAsync();
        _engine.RequestSync();
        await LoadAsync();
    }

    private async Task Try(Func<Task> action, string failure)
    {
        try { await action(); }
        catch (Exception ex) { _dialogs.Error(L.F("{0}: {1}", failure, ex.Message)); }
    }
}
