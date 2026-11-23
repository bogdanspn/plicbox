using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plicbox.App.Services;
using Plicbox.Core;
using Plicbox.Mail;

namespace Plicbox.App.ViewModels;

/// <summary>A choice in a combo box: a value and its words.</summary>
public sealed record Option<T>(T Value, string Label);

/// <summary>One "if" row: field, test, values (comma-separated: any of them).</summary>
public sealed partial class ConditionEditor : ObservableObject
{
    public static IReadOnlyList<Option<RuleField>> Fields { get; } =
        Enum.GetValues<RuleField>().Select(f => new Option<RuleField>(f, RuleMatcher.FieldName(f))).ToList();

    private readonly Action _changed;

    public ConditionEditor(RuleCondition c, Action changed)
    {
        _changed = changed;
        _field = c.Field;
        _operator = c.Operator;
        _valuesText = string.Join(", ", c.Values);
    }

    [ObservableProperty] private RuleField _field;
    [ObservableProperty] private RuleOperator _operator;
    [ObservableProperty] private string _valuesText;

    /// <summary>The tests that make sense for the field (yes/no for attachments and the threat lists).</summary>
    public IReadOnlyList<Option<RuleOperator>> Operators => RuleMatcher.IsYesNo(Field)
        ? [new(RuleOperator.Contains, RuleMatcher.OperatorName(RuleOperator.Contains, Field)),
           new(RuleOperator.DoesNotContain, RuleMatcher.OperatorName(RuleOperator.DoesNotContain, Field))]
        : Enum.GetValues<RuleOperator>().Select(o => new Option<RuleOperator>(o, RuleMatcher.OperatorName(o))).ToList();

    public bool HasValues => !RuleMatcher.IsYesNo(Field);

    public string Hint => Field switch
    {
        RuleField.From => "ana@example.com, @newsletter.com",
        RuleField.To => "you+shop@example.com",
        RuleField.Subject => L.T("invoice, receipt"),
        RuleField.Body => L.T("unsubscribe"),
        _ => "",
    };

    partial void OnFieldChanged(RuleField value)
    {
        OnPropertyChanged(nameof(Operators));
        OnPropertyChanged(nameof(HasValues));
        OnPropertyChanged(nameof(Hint));
        if (RuleMatcher.IsYesNo(value) && Operator is not (RuleOperator.Contains or RuleOperator.DoesNotContain)) Operator = RuleOperator.Contains;
        _changed();
    }

    partial void OnOperatorChanged(RuleOperator value) => _changed();
    partial void OnValuesTextChanged(string value) => _changed();

    public RuleCondition ToModel() => new()
    {
        Field = Field, Operator = Operator,
        Values = ValuesText.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
    };
}

/// <summary>One "then" row: what to do, and the folder for Move/Copy.</summary>
public sealed partial class ActionEditor : ObservableObject
{
    public static IReadOnlyList<Option<RuleActionKind>> Kinds { get; } =
        Enum.GetValues<RuleActionKind>().Select(k => new Option<RuleActionKind>(k, RuleMatcher.ActionName(k))).ToList();

    private readonly Action _changed;

    public ActionEditor(RuleAction a, string? folderLabel, Action changed)
    {
        _changed = changed;
        _kind = a.Kind;
        FolderAccountId = a.FolderAccountId;
        FolderPath = a.FolderPath;
        _folderLabel = folderLabel;
    }

    [ObservableProperty] private RuleActionKind _kind;
    [ObservableProperty] private string? _folderLabel;
    public long? FolderAccountId { get; private set; }
    public string? FolderPath { get; private set; }
    public bool NeedsFolder => Kind is RuleActionKind.MoveTo or RuleActionKind.CopyTo;

    partial void OnKindChanged(RuleActionKind value)
    {
        OnPropertyChanged(nameof(NeedsFolder));
        _changed();
    }

    public void SetFolder(Folder folder, string label)
    {
        FolderAccountId = folder.AccountId;
        FolderPath = folder.Path;
        FolderLabel = label;
        _changed();
    }

    public RuleAction ToModel() => new() { Kind = Kind, FolderAccountId = NeedsFolder ? FolderAccountId : null, FolderPath = NeedsFolder ? FolderPath : null };
}

/// <summary>An editable rule.</summary>
public sealed partial class RuleEditor : ObservableObject
{
    private readonly RulesViewModel _owner;
    private bool _loading = true;

    public RuleEditor(MailRule rule, RulesViewModel owner)
    {
        _owner = owner;
        Id = rule.Id;
        Name = rule.Name;
        Enabled = rule.Enabled;
        AccountId = rule.AccountId;
        MatchAll = rule.MatchAll;
        StopAfter = rule.StopAfter;
        foreach (var c in rule.Conditions) Conditions.Add(new ConditionEditor(c, Changed));
        foreach (var a in rule.Actions) Actions.Add(new ActionEditor(a, owner.FolderLabel(a), Changed));
        _loading = false;
    }

    public Guid Id { get; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private long? _accountId;
    [ObservableProperty] private bool _matchAll;
    [ObservableProperty] private bool _stopAfter;
    [ObservableProperty] private string _summary = "";
    /// <summary>Which mail the rule reaches, spelled out (all accounts or one, and why).</summary>
    [ObservableProperty] private string _scopeNote = "";
    [ObservableProperty] private bool _scopeIsProblem;

    // Preview: what the rule would catch in the inbox right now.
    [ObservableProperty] private string _previewText = "";
    public ObservableCollection<MessageSummary> PreviewMessages { get; } = [];
    [ObservableProperty] private bool _canApplyNow;

    public ObservableCollection<ConditionEditor> Conditions { get; } = [];
    public ObservableCollection<ActionEditor> Actions { get; } = [];

    /// <summary>For the "When" combo: index into the account choices (0 = any account).</summary>
    public Option<long?>? AccountChoice
    {
        get => _owner.AccountChoices.FirstOrDefault(c => c.Value == AccountId) ?? _owner.AccountChoices.FirstOrDefault();
        set
        {
            if (value is null || value.Value == AccountId) return;
            AccountId = value.Value;
        }
    }

    public Option<bool> MatchChoice
    {
        get => MatchAll ? RulesViewModel.MatchChoices[0] : RulesViewModel.MatchChoices[1];
        set => MatchAll = value?.Value ?? true;
    }

    partial void OnNameChanged(string value) => Changed();
    partial void OnEnabledChanged(bool value) => Changed();
    partial void OnAccountIdChanged(long? value)
    {
        OnPropertyChanged(nameof(AccountChoice));
        Changed();
    }
    partial void OnMatchAllChanged(bool value)
    {
        OnPropertyChanged(nameof(MatchChoice));
        Changed();
    }
    partial void OnStopAfterChanged(bool value) => Changed();

    public void Changed()
    {
        if (_loading) return;
        _owner.OnRuleChanged(this);
    }

    public MailRule ToModel() => new()
    {
        Id = Id, Name = Name.Trim().Length > 0 ? Name.Trim() : L.T("Rule"), Enabled = Enabled, AccountId = AccountId, MatchAll = MatchAll,
        StopAfter = StopAfter, Conditions = Conditions.Select(c => c.ToModel()).ToList(), Actions = Actions.Select(a => a.ToModel()).ToList(),
    };
}

/// <summary>The Rules window: rules in run order on the left, the selected one as a sentence on the right.</summary>
public sealed partial class RulesViewModel : ObservableObject
{
    public static IReadOnlyList<Option<bool>> MatchChoices { get; } = [new(true, L.T("all")), new(false, L.T("any"))];

    private readonly ISettingsStore _settings;
    private readonly MailEngine _engine;
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly Dialogs _dialogs;
    private readonly Dictionary<long, IReadOnlyList<Folder>> _foldersByAccount = [];
    private readonly Dictionary<long, string> _accountNames = [];
    private CancellationTokenSource? _previewCts;

    public RulesViewModel(ISettingsStore settings, MailEngine engine, IAccountRepository accounts, IFolderRepository folders, Dialogs dialogs) =>
        (_settings, _engine, _accounts, _folders, _dialogs) = (settings, engine, accounts, folders, dialogs);

    public ObservableCollection<RuleEditor> Rules { get; } = [];
    public List<Option<long?>> AccountChoices { get; } = [new(null, L.T("any account"))];
    [ObservableProperty] private RuleEditor? _selected;
    [ObservableProperty] private string? _error;
    public bool HasRules => Rules.Count > 0;

    /// <summary>Raised after Save &amp; Close.</summary>
    public event Action? Saved;

    /// <summary>A rule to add and select when the window opens ("Create Rule…" from a message).</summary>
    public MailRule? StartWith { get; set; }

    public async Task LoadAsync()
    {
        foreach (var a in await _accounts.GetAllAsync())
        {
            _accountNames[a.Id] = a.SidebarTitle;
            _foldersByAccount[a.Id] = await _folders.GetByAccountAsync(a.Id);
            AccountChoices.Add(new Option<long?>(a.Id, a.SidebarTitle));
        }
        foreach (var rule in _settings.Current.Rules) Rules.Add(new RuleEditor(rule, this));
        if (StartWith is { } start) Rules.Add(new RuleEditor(start, this));
        foreach (var r in Rules)
        {
            r.Summary = SummaryOf(r);
            UpdateScope(r);
        }
        Selected = StartWith is null ? Rules.FirstOrDefault() : Rules[^1];
        OnPropertyChanged(nameof(HasRules));
    }

    partial void OnSelectedChanged(RuleEditor? value)
    {
        if (value is not null) _ = RefreshPreviewAsync(value);
    }

    public string? FolderLabel(RuleAction a)
    {
        if (a.FolderAccountId is not { } id || a.FolderPath is null) return null;
        var folder = _foldersByAccount.GetValueOrDefault(id)?.FirstOrDefault(f => f.Path == a.FolderPath);
        var name = folder?.Name ?? a.FolderPath;
        return _accountNames.Count > 1 ? $"{name} ({_accountNames.GetValueOrDefault(id)})" : name;
    }

    private string SummaryOf(RuleEditor r) => RuleMatcher.Summary(r.ToModel(), FolderLabel);

    private string AccountName(long id) => _accountNames.GetValueOrDefault(id) ?? L.T("that account");

    // Folders belong to one account and mail can't move between accounts, so say exactly which mail a rule reaches.
    private void UpdateScope(RuleEditor rule)
    {
        var folderAccounts = rule.Actions.Where(a => a.NeedsFolder && a.FolderAccountId is not null).Select(a => a.FolderAccountId!.Value).Distinct().ToList();
        rule.ScopeIsProblem = false;
        if (rule.AccountId is { } only)
        {
            if (folderAccounts.FirstOrDefault(id => id != only) is var other && other != 0)
            {
                rule.ScopeIsProblem = true;
                rule.ScopeNote = L.F("A folder below is in {0}. Mail can't move between accounts: pick a folder in {1}, or change the account here.", AccountName(other), AccountName(only));
            }
            else
            {
                rule.ScopeNote = L.F("Only new mail arriving in {0}.", AccountName(only));
            }
        }
        else
        {
            rule.ScopeNote = folderAccounts.Count > 0
                ? L.F("New mail in every account, but only mail arriving in {0} can be moved to the folder below.", string.Join(L.T(" and "), folderAccounts.Select(AccountName)))
                : _accountNames.Count > 1 ? L.F("New mail in every account ({0}).", string.Join(", ", _accountNames.Values)) : L.T("All new mail.");
        }
    }

    internal void OnRuleChanged(RuleEditor rule)
    {
        rule.Summary = SummaryOf(rule);
        UpdateScope(rule);
        _ = RefreshPreviewAsync(rule);
    }

    private async Task RefreshPreviewAsync(RuleEditor rule)
    {
        _previewCts?.Cancel();
        var cts = _previewCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token); // typing
            var model = rule.ToModel();
            var matches = await Task.Run(() => _engine.PreviewRuleAsync(model, 500, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            rule.PreviewMessages.Clear();
            foreach (var m in matches.Take(6)) rule.PreviewMessages.Add(m);
            var hasConditions = model.Conditions.Any(c => RuleMatcher.IsYesNo(c.Field) || c.Values.Count > 0);
            rule.PreviewText = !hasConditions
                ? L.T("No conditions yet: this rule would apply to every message.")
                : matches.Count switch
                {
                    0 => L.T("None of your last 500 inbox messages match."),
                    1 => L.T("1 of your last 500 inbox messages matches:"),
                    _ => L.F("{0} of your last 500 inbox messages match:", matches.Count),
                };
            rule.CanApplyNow = hasConditions && matches.Count > 0 && model.Actions.Count > 0;
        }
        catch (OperationCanceledException) { }
    }

    // ───────── list ─────────

    [RelayCommand]
    private void NewRule()
    {
        var rule = new RuleEditor(new MailRule
        {
            Name = L.T("New rule"),
            Conditions = [new RuleCondition { Field = RuleField.From }],
            Actions = [new RuleAction { Kind = RuleActionKind.MoveTo }],
        }, this);
        rule.Summary = SummaryOf(rule);
        UpdateScope(rule);
        Rules.Add(rule);
        Selected = rule;
        OnPropertyChanged(nameof(HasRules));
    }

    [RelayCommand]
    private void DeleteRule()
    {
        if (Selected is not { } rule) return;
        if (!_dialogs.Confirm(L.F("Delete “{0}”?", rule.Name), L.T("Mail it would have filed stays where it is."), L.T("Delete"), DialogKind.Danger)) return;
        var index = Rules.IndexOf(rule);
        Rules.Remove(rule);
        Selected = Rules.Count == 0 ? null : Rules[Math.Min(index, Rules.Count - 1)];
        OnPropertyChanged(nameof(HasRules));
    }

    [RelayCommand]
    private void MoveUp() => Shift(-1);

    [RelayCommand]
    private void MoveDown() => Shift(1);

    private void Shift(int delta)
    {
        if (Selected is not { } rule) return;
        var i = Rules.IndexOf(rule);
        var j = i + delta;
        if (j < 0 || j >= Rules.Count) return;
        Rules.Move(i, j);
        Selected = rule;
    }

    // ───────── editor ─────────

    [RelayCommand]
    private void AddCondition()
    {
        if (Selected is not { } rule) return;
        rule.Conditions.Add(new ConditionEditor(new RuleCondition { Field = RuleField.Subject }, rule.Changed));
        rule.Changed();
    }

    [RelayCommand]
    private void RemoveCondition(ConditionEditor condition)
    {
        if (Selected is not { } rule) return;
        rule.Conditions.Remove(condition);
        rule.Changed();
    }

    [RelayCommand]
    private void AddAction()
    {
        if (Selected is not { } rule) return;
        rule.Actions.Add(new ActionEditor(new RuleAction { Kind = RuleActionKind.MarkRead }, null, rule.Changed));
        rule.Changed();
    }

    [RelayCommand]
    private void RemoveAction(ActionEditor action)
    {
        if (Selected is not { } rule) return;
        rule.Actions.Remove(action);
        rule.Changed();
    }

    [RelayCommand]
    private void ChooseFolder(ActionEditor action)
    {
        if (Selected is not { } rule) return;
        // A rule limited to one account can only file into that account's folders.
        var accounts = rule.AccountId is { } only ? [only] : _foldersByAccount.Keys.ToList();
        var choices = accounts.SelectMany(id => _foldersByAccount.GetValueOrDefault(id) ?? []).Where(f => !f.IsVirtual && f.Role != FolderRole.Outbox);
        if (_dialogs.PickFolder(L.T("Move matching mail to"), L.T("Choose"), choices, id => _accountNames.GetValueOrDefault(id) ?? "") is not { } folder) return;
        action.SetFolder(folder, FolderLabel(new RuleAction { FolderAccountId = folder.AccountId, FolderPath = folder.Path })!);
        // Moving into a folder ties the rule to that folder's account (mail can't move between accounts).
        if (rule.AccountId is null && _foldersByAccount.Count > 1) rule.AccountId = folder.AccountId;
    }

    /// <summary>Runs the selected rule on the inbox now (for mail that arrived before the rule existed).</summary>
    [RelayCommand]
    private async Task ApplyNow()
    {
        if (Selected is not { } rule) return;
        var model = rule.ToModel() with { Enabled = true };
        var inboxes = _foldersByAccount.Where(kv => model.AccountId is null || kv.Key == model.AccountId)
            .Select(kv => kv.Value.FirstOrDefault(f => f.Role == FolderRole.Inbox)).OfType<Folder>().ToList();
        var matched = 0;
        try
        {
            foreach (var inbox in inboxes) matched += (await _engine.RunRulesAsync([model], inbox)).Matched;
            Error = null;
            _dialogs.Info(L.T("Rule applied"), matched == 1 ? L.T("It handled 1 message in your inbox.") : L.F("It handled {0} messages in your inbox.", matched));
        }
        catch (Exception ex)
        {
            Error = L.F("Couldn't run the rule: {0}", ex.Message);
        }
        await RefreshPreviewAsync(rule);
    }

    [RelayCommand]
    private void SaveAndClose()
    {
        var incomplete = Rules.FirstOrDefault(r => r.Actions.Any(a => a.NeedsFolder && a.FolderPath is null));
        if (incomplete is not null)
        {
            Selected = incomplete;
            Error = L.F("“{0}” moves or copies mail but no folder is chosen yet.", incomplete.Name);
            return;
        }
        if (Rules.FirstOrDefault(r => r.ScopeIsProblem) is { } mismatched)
        {
            Selected = mismatched;
            Error = $"“{mismatched.Name}”: {mismatched.ScopeNote}";
            return;
        }
        _settings.Save(_settings.Current with { Rules = Rules.Select(r => r.ToModel()).ToList() });
        Saved?.Invoke();
    }
}
