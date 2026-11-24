using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plicbox.App.Services;
using Plicbox.Core;
using Plicbox.Mail;

namespace Plicbox.App.ViewModels;

/// <summary>One editable alias row (the DataGrid needs settable properties and a parameterless constructor).</summary>
public sealed partial class AliasRow : ObservableObject
{
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    /// <summary>New mail from the account goes out from this one (at most one row).</summary>
    [ObservableProperty] private bool _isDefault;
}

/// <summary>Editable copy of one account; changes apply only on Save &amp; Close.</summary>
public sealed partial class AccountEditor : ObservableObject
{
    /// <summary>Choices for "Message sync time period", in days (0 = all time); index matches the combo box.</summary>
    public static readonly int[] SyncPeriods = [0, 30, 90, 180, 365];
    /// <summary>Choices for offline download, in days (0 = everything synced); index 0 is "don't".</summary>
    public static readonly int[] OfflinePeriods = [-1, 30, 180, 365, 0];
    private static readonly FolderRole[] SpecialRoles = [FolderRole.Sent, FolderRole.Drafts, FolderRole.Trash, FolderRole.Junk, FolderRole.Archive];

    public AccountEditor(Account account) => Load(account);

    public Account Original { get; private set; } = null!;
    public long Id => Original.Id;
    public string Email => Original.Email;
    public string Initial => Email.Length > 0 ? char.ToUpperInvariant(Email[0]).ToString() : "?";
    public Brush AvatarBrush => (Brush)new BrushConverter().ConvertFromString(Original.Color)!;
    /// <summary>The account's own picture (shown on mail you send); null shows the coloured letter.</summary>
    [ObservableProperty] private ImageSource? _picture;
    public bool IsGoogle => Original.Auth == AuthKind.GoogleOAuth;
    public string SignInMethod => IsGoogle ? L.T("Google sign-in (OAuth)") : L.T("Password");
    public string Summary => IsGoogle ? L.T("Mail (Google)") : L.T("Mail (IMAP)");

    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string _signature = "";
    [ObservableProperty] private string _bccAddress = "";
    [ObservableProperty] private string _imapHost = "";
    [ObservableProperty] private string _imapPort = "";
    [ObservableProperty] private string _smtpHost = "";
    [ObservableProperty] private string _smtpPort = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private bool _isDefault;

    // IMAP / SMTP tabs
    [ObservableProperty] private int _imapSecurityIndex;  // TlsMode: 0 automatic, 1 TLS, 2 STARTTLS
    [ObservableProperty] private int _smtpSecurityIndex;
    /// <summary>SmtpAuthMode: 0 same as incoming, 1 its own login, 2 none.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSmtpLogin))] private int _smtpAuthIndex;
    [ObservableProperty] private string _smtpUsername = "";
    public bool HasSmtpLogin => SmtpAuthIndex == (int)SmtpAuthMode.Separate;
    partial void OnSmtpAuthIndexChanged(int value)
    {
        if (value == (int)SmtpAuthMode.Separate && SmtpUsername.Length == 0) SmtpUsername = Username;
    }
    /// <summary>A new outgoing password typed in (empty: keep the saved one).</summary>
    public string NewSmtpPassword { get; set; } = "";
    /// <summary>An outgoing password is saved already: the empty box says so, as it can't show it.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SmtpPasswordHint))] private bool _hasSavedSmtpPassword;
    public string SmtpPasswordHint => HasSavedSmtpPassword ? L.T("Saved. Type a new one to replace it.") : "";
    [ObservableProperty] private int _syncPeriodIndex;
    [ObservableProperty] private int _offlineIndex;
    [ObservableProperty] private bool _autoDetectFolders = true;
    [ObservableProperty] private string _sentPath = "";
    [ObservableProperty] private string _draftsPath = "";
    [ObservableProperty] private string _trashPath = "";
    [ObservableProperty] private string _junkPath = "";
    [ObservableProperty] private string _archivePath = "";
    [ObservableProperty] private string _newIgnoredPath = "";
    public ObservableCollection<string> FolderPaths { get; } = [];
    public ObservableCollection<string> IgnoredPaths { get; } = [];
    public ObservableCollection<AliasRow> Aliases { get; } = [];

    // Diagnostics (read-only, refreshed when the tab is shown)
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _errorText;
    /// <summary>The server turned the sign-in down: General shows ErrorText with "Sign in again…" on top.</summary>
    [ObservableProperty] private bool _needsSignIn;
    public ObservableCollection<FolderStat> Folders { get; } = [];

    private void Load(Account a)
    {
        Original = a;
        Label = a.Label ?? "";
        DisplayName = a.DisplayName;
        Signature = a.Signature ?? "";
        BccAddress = a.BccAddress ?? "";
        ImapHost = a.ImapHost;
        ImapPort = a.ImapPort.ToString();
        SmtpHost = a.SmtpHost;
        SmtpPort = a.SmtpPort.ToString();
        Username = a.Username;
        IsDefault = a.IsDefault;
        ImapSecurityIndex = (int)a.ImapSecurity;
        SmtpSecurityIndex = (int)a.SmtpSecurity;
        SmtpAuthIndex = (int)a.SmtpAuth;
        SmtpUsername = a.SmtpUsername ?? "";
        SyncPeriodIndex = Math.Max(0, Array.IndexOf(SyncPeriods, a.SyncPeriodDays));
        OfflineIndex = !a.OfflineDownload ? 0 : Math.Max(1, Array.IndexOf(OfflinePeriods, a.OfflineDays));
        var overrides = a.FolderOverrides;
        AutoDetectFolders = overrides.Count == 0;
        SentPath = overrides.GetValueOrDefault(FolderRole.Sent, "");
        DraftsPath = overrides.GetValueOrDefault(FolderRole.Drafts, "");
        TrashPath = overrides.GetValueOrDefault(FolderRole.Trash, "");
        JunkPath = overrides.GetValueOrDefault(FolderRole.Junk, "");
        ArchivePath = overrides.GetValueOrDefault(FolderRole.Archive, "");
        IgnoredPaths.Clear();
        foreach (var p in a.IgnoredPaths) IgnoredPaths.Add(p);
        Aliases.Clear();
        foreach (var alias in a.Aliases) Aliases.Add(new AliasRow { Name = alias.Name, Address = alias.Address, IsDefault = alias.IsDefault });
    }

    /// <summary>Server folders for the special-folder pickers; with auto-detect on, shows what was detected.</summary>
    public void LoadFolders(IReadOnlyList<Folder> folders)
    {
        FolderPaths.Clear();
        foreach (var f in folders.Where(f => f.Role != FolderRole.Outbox).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)) FolderPaths.Add(f.Path);
        if (!AutoDetectFolders) return;
        string Detected(FolderRole role) => folders.FirstOrDefault(f => f.Role == role)?.Path ?? "";
        SentPath = Detected(FolderRole.Sent);
        DraftsPath = Detected(FolderRole.Drafts);
        TrashPath = Detected(FolderRole.Trash);
        JunkPath = Detected(FolderRole.Junk);
        ArchivePath = Detected(FolderRole.Archive) is { Length: > 0 } archive ? archive : Detected(FolderRole.All);
    }

    [RelayCommand]
    private void AddIgnoredPath()
    {
        var path = NewIgnoredPath.Trim();
        if (path.Length == 0 || IgnoredPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        IgnoredPaths.Add(path);
        NewIgnoredPath = "";
    }

    [RelayCommand]
    private void RemoveIgnoredPath(string path) => IgnoredPaths.Remove(path);

    [RelayCommand]
    private void AddAlias() => Aliases.Add(new AliasRow { Name = DisplayName });

    [RelayCommand]
    private void RemoveAlias(AliasRow row) => Aliases.Remove(row);

    /// <summary>The star: new mail goes out from this alias; clicked again, from the account's own address.</summary>
    [RelayCommand]
    private void ToggleDefaultAlias(AliasRow row)
    {
        var on = !row.IsDefault;
        foreach (var other in Aliases) other.IsDefault = false;
        row.IsDefault = on;
    }

    /// <summary>The edited account, or an error message describing what to fix.</summary>
    public (Account? Account, string? Error) Build()
    {
        if (string.IsNullOrWhiteSpace(DisplayName)) return (null, L.F("{0}: enter your name.", Email));
        if (!int.TryParse(ImapPort, out var imapPort) || imapPort is < 1 or > 65535
            || !int.TryParse(SmtpPort, out var smtpPort) || smtpPort is < 1 or > 65535)
            return (null, L.F("{0}: ports must be numbers between 1 and 65535.", Email));
        if (string.IsNullOrWhiteSpace(ImapHost) || string.IsNullOrWhiteSpace(SmtpHost)) return (null, L.F("{0}: enter both servers.", Email));
        if (!string.IsNullOrWhiteSpace(BccAddress) && !MimeKit.MailboxAddress.TryParse(BccAddress.Trim(), out _))
            return (null, L.F("{0}: the Bcc address isn't valid.", Email));

        var aliases = Aliases.Where(r => !string.IsNullOrWhiteSpace(r.Address))
            .Select(r => new MailAlias(r.Name.Trim(), r.Address.Trim(), r.IsDefault)).ToList();
        if (aliases.FirstOrDefault(a => !MimeKit.MailboxAddress.TryParse(a.Address, out var m) || !m.Address.Contains('@')) is { } bad)
            return (null, L.F("{0}: the alias \"{1}\" isn't a valid address.", Email, bad.Address));

        var overrides = new Dictionary<FolderRole, string>();
        if (!AutoDetectFolders)
        {
            foreach (var (role, path) in SpecialRoles.Zip(new[] { SentPath, DraftsPath, TrashPath, JunkPath, ArchivePath }))
                if (!string.IsNullOrWhiteSpace(path)) overrides[role] = path.Trim();
        }

        return (Original with
        {
            Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
            DisplayName = DisplayName.Trim(),
            Signature = string.IsNullOrWhiteSpace(Signature) ? null : Signature.TrimEnd(),
            BccAddress = string.IsNullOrWhiteSpace(BccAddress) ? null : BccAddress.Trim(),
            ImapHost = ImapHost.Trim(), ImapPort = imapPort,
            SmtpHost = SmtpHost.Trim(), SmtpPort = smtpPort,
            Username = Username.Trim(),
            ImapSecurity = (TlsMode)ImapSecurityIndex,
            SmtpSecurity = (TlsMode)SmtpSecurityIndex,
            SmtpAuth = IsGoogle ? SmtpAuthMode.SameAsIncoming : (SmtpAuthMode)SmtpAuthIndex,
            SmtpUsername = HasSmtpLogin && !string.IsNullOrWhiteSpace(SmtpUsername) ? SmtpUsername.Trim() : null,
            SyncPeriodDays = SyncPeriods[Math.Clamp(SyncPeriodIndex, 0, SyncPeriods.Length - 1)],
            OfflineDownload = OfflineIndex > 0,
            OfflineDays = OfflineIndex > 0 ? OfflinePeriods[Math.Clamp(OfflineIndex, 1, OfflinePeriods.Length - 1)] : Original.OfflineDays,
            AliasesJson = aliases.Count == 0 ? null : AccountJson.Aliases(aliases),
            FolderOverridesJson = overrides.Count == 0 ? null : AccountJson.FolderOverrides(overrides),
            IgnoredPathsJson = IgnoredPaths.Count == 0 ? null : AccountJson.Paths(IgnoredPaths),
        }, null);
    }

    public bool IsChanged(Account edited) => edited != Original with { IsDefault = edited.IsDefault, SortOrder = edited.SortOrder };
}

public sealed record FolderStat(string Name, string Path, int Messages, int Unread, string Mode);

public sealed partial class AccountsViewModel : ObservableObject
{
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly MailEngine _engine;
    private readonly Dialogs _dialogs;
    private readonly GmailAliases _gmailAliases;
    private readonly Pictures _pictures;

    public AccountsViewModel(IAccountRepository accounts, IFolderRepository folders, IMessageRepository messages,
        MailEngine engine, Dialogs dialogs, GmailAliases gmailAliases, Pictures pictures)
    {
        (_accounts, _folders, _messages, _engine, _dialogs, _gmailAliases, _pictures) = (accounts, folders, messages, engine, dialogs, gmailAliases, pictures);
    }

    public ObservableCollection<AccountEditor> Editors { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SetDefaultCommand), nameof(RemoveCommand), nameof(MoveUpCommand), nameof(MoveDownCommand),
        nameof(SignInAgainCommand), nameof(SyncNowCommand), nameof(ImportGmailAliasesCommand), nameof(ChoosePictureCommand), nameof(RemovePictureCommand))]
    private AccountEditor? _selected;

    [ObservableProperty] private string? _error;

    /// <summary>Raised when Save &amp; Close succeeded.</summary>
    public event Action? Saved;

    public async Task LoadAsync()
    {
        var pending = Editors.ToDictionary(e => e.Id); // keep unsaved edits when reloading after Add
        var selectedId = Selected?.Id;
        Editors.Clear();
        foreach (var a in await _accounts.GetAllAsync())
        {
            var editor = pending.TryGetValue(a.Id, out var kept) ? kept : new AccountEditor(a);
            editor.HasSavedSmtpPassword = _engine.HasSmtpSecret(a.Id);
            if (kept is null) editor.LoadFolders(await _folders.GetByAccountAsync(a.Id));
            editor.Picture = _pictures.ForAccount(a.Id);
            Editors.Add(editor);
        }
        Selected = Editors.FirstOrDefault(e => e.Id == selectedId) ?? Editors.FirstOrDefault();
    }

    /// <summary>Choose a picture for the selected account (applies right away, like eM Client's account picture).</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ChoosePicture()
    {
        if (Selected is not { } editor) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Choose a picture"), Filter = L.T("Pictures") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|" + L.T("All files") + "|*.*",
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _pictures.SetAccountPicture(editor.Id, dialog.FileName);
            editor.Picture = _pictures.ForAccount(editor.Id);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            _dialogs.Error(L.F("That picture couldn't be used: {0}", ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RemovePicture()
    {
        if (Selected is not { } editor) return;
        _pictures.RemoveAccountPicture(editor.Id);
        editor.Picture = null;
    }

    partial void OnSelectedChanged(AccountEditor? value) => _ = RefreshDiagnosticsAsync(value);

    public async Task RefreshDiagnosticsAsync(AccountEditor? editor)
    {
        if (editor is null) return;
        var status = _engine.Statuses.GetValueOrDefault(editor.Id);
        editor.StatusText = status switch
        {
            null => L.T("Not connected"),
            { State: SyncState.Syncing } => L.T("Syncing…"),
            { State: SyncState.Error } => L.T("Problem"),
            { LastSync: { } t } => L.F("Up to date (last sync {0:g})", t.LocalDateTime),
            _ => L.T("Waiting for first sync"),
        };
        editor.ErrorText = status?.Error;
        editor.NeedsSignIn = status?.NeedsSignIn == true;

        var counts = await _messages.GetCountsAsync();
        editor.Folders.Clear();
        foreach (var f in (await _folders.GetByAccountAsync(editor.Id)).Where(f => f.Role != FolderRole.Outbox))
        {
            editor.Folders.Add(new FolderStat(f.Name, f.Path, counts.TotalByFolder.GetValueOrDefault(f.Id),
                counts.UnreadByFolder.GetValueOrDefault(f.Id), f.IsSynced ? L.T("Always") : L.T("When opened")));
        }
    }

    private bool HasSelection() => Selected is not null;

    [RelayCommand]
    private async Task SaveAndClose()
    {
        Error = null;
        var built = new List<(AccountEditor Editor, Account Account)>();
        foreach (var editor in Editors)
        {
            var (account, error) = editor.Build();
            if (account is null)
            {
                Selected = editor;
                Error = error;
                return;
            }
            if (account.SmtpAuth == SmtpAuthMode.Separate && editor.NewSmtpPassword.Length == 0 && !_engine.HasSmtpSecret(account.Id))
            {
                Selected = editor;
                Error = L.F("{0}: enter the password for sending (SMTP tab).", account.Email);
                return;
            }
            built.Add((editor, account));
        }

        foreach (var (editor, account) in built)
        {
            // Saved before the account reconnects with its new settings.
            if (editor.NewSmtpPassword.Length > 0)
            {
                _engine.UpdateSmtpSecret(account.Id, editor.NewSmtpPassword);
                editor.HasSavedSmtpPassword = true;
            }
            if (editor.IsChanged(account) || editor.NewSmtpPassword.Length > 0) await _engine.UpdateAccountAsync(account);
        }
        await _accounts.SetOrderAsync(Editors.Select(e => e.Id).ToList());
        if (Editors.FirstOrDefault(e => e.IsDefault) is { } def) await _accounts.SetDefaultAsync(def.Id);
        Saved?.Invoke();
    }

    [RelayCommand]
    private async Task Add()
    {
        if (_dialogs.ShowAddAccount()) await LoadAsync();
    }

    /// <summary>Not for the account that is the default already.</summary>
    private bool CanSetDefault() => Selected is { IsDefault: false };

    [RelayCommand(CanExecute = nameof(CanSetDefault))]
    private void SetDefault()
    {
        foreach (var e in Editors) e.IsDefault = e == Selected;
        SetDefaultCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task Remove()
    {
        if (Selected is not { } editor) return;
        if (!_dialogs.Confirm(L.F("Remove {0}?", editor.Email), L.T("Plicbox deletes its local copy and the saved sign-in. Nothing is deleted on the server."),
                L.T("Remove"), DialogKind.Danger))
            return;
        await _engine.RemoveAccountAsync(editor.Id);
        var index = Editors.IndexOf(editor);
        Editors.Remove(editor);
        if (editor.IsDefault && Editors.Count > 0) Editors[0].IsDefault = true;
        Selected = null; // so the buttons look again even if the list already picked the next one
        Selected = Editors.Count == 0 ? null : Editors[Math.Min(index, Editors.Count - 1)];
    }

    private bool CanMoveUp() => Selected is { } editor && Editors.IndexOf(editor) > 0;
    private bool CanMoveDown() => Selected is { } editor && Editors.IndexOf(editor) < Editors.Count - 1;

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MoveUp() => Move(-1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MoveDown() => Move(+1);

    private void Move(int delta)
    {
        if (Selected is not { } editor) return;
        var from = Editors.IndexOf(editor);
        var to = from + delta;
        if (to < 0 || to >= Editors.Count) return;
        Editors.Move(from, to);
        Selected = editor;
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task ImportGmailAliases()
    {
        if (Selected is not { IsGoogle: true } editor)
        {
            _dialogs.Info(L.T("Google accounts only"), L.T("Importing aliases works for Google accounts. For others, add them by hand."));
            return;
        }
        try
        {
            var found = await _gmailAliases.GetAsync(editor.Original);
            var added = 0;
            foreach (var alias in found)
            {
                if (editor.Aliases.Any(r => r.Address.Equals(alias.Address, StringComparison.OrdinalIgnoreCase))) continue;
                // Gmail's own default comes along, unless one is starred here already.
                var isDefault = alias.IsDefault && !editor.Aliases.Any(r => r.IsDefault);
                editor.Aliases.Add(new AliasRow { Name = alias.Name, Address = alias.Address, IsDefault = isDefault });
                added++;
            }
            if (added == 0)
                _dialogs.Info(L.T("Nothing to import"), found.Count == 0
                    ? L.T("Gmail has no extra \"Send mail as\" addresses for this account.")
                    : L.T("All of Gmail's addresses are already listed."));
        }
        catch (Exception ex)
        {
            _dialogs.Error(L.F("Couldn't read aliases from Gmail: {0}", ex.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SignInAgain()
    {
        if (Selected is { } editor && await _dialogs.SignInAgainAsync(editor.Original)) editor.NeedsSignIn = false;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task SyncNow()
    {
        if (Selected is not { } editor) return;
        _engine.RequestSync(editor.Id, userInitiated: true);
        await Task.Delay(300);
        await RefreshDiagnosticsAsync(editor);
    }

    [RelayCommand]
    private static void OpenLogs()
    {
        var dir = Path.Combine(App.DataDir, "logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
