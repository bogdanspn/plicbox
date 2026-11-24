using System.Net.Http;
using System.Collections.ObjectModel;
using System.IO;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plicbox.App.Services;
using Plicbox.Auth;
using Plicbox.Core;
using Plicbox.Mail;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Plicbox.App.ViewModels;

/// <summary>Message list sort, picked by clicking a column header.</summary>
public enum MessageSortColumn { Received, From, Subject, Flag, Attachments, ReadStatus }

public enum SearchArea { All, Account, Folder }

/// <summary>An attachment chip. Made in the background: its safety check may look inside a zip.</summary>
public sealed record AttachmentItem(string FileName, string SizeText, MimePart Part)
{
    public AttachmentAssessment Safety { get; } = AttachmentSafety.Assess(Part);
    public bool IsDanger => Safety.Risk == AttachmentRisk.Danger;
    public bool IsCaution => Safety.Risk == AttachmentRisk.Caution;
    /// <summary>An invitation or event file (.ics): it can go straight into a calendar.</summary>
    public bool IsCalendar => Safety.Extension.Equals(".ics", StringComparison.OrdinalIgnoreCase) || Part.ContentType.MimeType.Equals("text/calendar", StringComparison.OrdinalIgnoreCase);
    public string ExtensionText => Safety.Extension.Length > 0 ? Safety.Extension.TrimStart('.').ToUpperInvariant() : L.T("FILE");
    public string ToolTipText => (Safety.Warning is { } w ? w + "\n\n" : "") + L.F("{0}\nClick to open, right-click to save.", FileName);
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IAccountRepository _accounts;
    private readonly IFolderRepository _folders;
    private readonly IMessageRepository _messages;
    private readonly IBodyStore _bodies;
    private readonly MessageRenderer _renderer;
    private readonly MailEngine _engine;
    private readonly Dialogs _dialogs;
    private readonly ISettingsStore _settings;
    private readonly ITrustedSenders _trusted;
    private readonly Pictures _pictures;
    private readonly ILogger<MainViewModel> _log;
    private readonly SynchronizationContext _ui;

    private readonly Dictionary<long, IReadOnlyList<Folder>> _foldersByAccount = [];
    private readonly Dictionary<long, Account> _accountsById = [];
    private CancellationTokenSource? _searchCts;
    private bool _repopulating; // list rebuilt under the selection; ignore the transient null selection
    private bool _rebuildingSidebar; // sidebar rebuilt under the selection; don't treat it as a new folder choice
    private CancellationTokenSource? _progressCts;
    private CancellationTokenSource? _readCts;

    /// <summary>A message counts as read after it has been on screen this long (so flicking through folders doesn't).</summary>

    // eM Client's order: the standard folders first, then everything else under "More".
    private static readonly FolderRole[] RoleOrder =
    [
        FolderRole.Inbox, FolderRole.Sent, FolderRole.Trash, FolderRole.Drafts, FolderRole.Outbox, FolderRole.Junk,
        FolderRole.Archive, FolderRole.All, FolderRole.Snoozed,
    ];

    public MainViewModel(IAccountRepository accounts, IFolderRepository folders, IMessageRepository messages,
        IBodyStore bodies, MessageRenderer renderer, MailEngine engine, Dialogs dialogs, ISettingsStore settings,
        ITrustedSenders trusted, Pictures pictures, MessageReader reader, ILogger<MainViewModel> log)
    {
        (_accounts, _folders, _messages, _bodies, _renderer, _engine, _dialogs, _settings, _trusted, _pictures, _log) =
            (accounts, folders, messages, bodies, renderer, engine, dialogs, settings, trusted, pictures, log);
        Reader = reader;
        reader.ReplyAction = () => ReplyCommand.Execute(null);
        reader.ReplyAllAction = () => ReplyAllCommand.Execute(null);
        reader.ForwardAction = () => ForwardCommand.Execute(null);
        reader.Status += text => StatusText = text;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        SidebarView = CollectionViewSource.GetDefaultView(SidebarNodes);
        SidebarView.Filter = n => n is SidebarNode node && node.IsVisible
            // "Show if unread" folders stay listed while selected, so the open folder doesn't vanish under you.
            && (!node.ShowOnlyIfUnread || node.Count > 0 || node == SelectedNode);

        MessagesView = CollectionViewSource.GetDefaultView(Messages);
        _sortColumn = Enum.TryParse<MessageSortColumn>(_settings.Current.MessageSort, out var sort) ? sort : MessageSortColumn.Received;
        _sortDescending = _settings.Current.MessageSortDescending;
        ApplyGrouping();
        _readingLayout = _settings.Current.ReadingPane;
        _settings.Changed += s => _ui.Post(_ => ReadingLayout = s.ReadingPane, null);

        _engine.Synced += (accountId, newInbox) => _ui.Post(_ => _ = OnSyncedAsync(accountId, newInbox), null);
        _engine.StatusChanged += (_, _) => _ui.Post(_ => UpdateStatus(), null);
        _engine.FoldersChanged += accountId => _ui.Post(_ => _ = OnFoldersChangedAsync(accountId), null);
        _engine.FolderSynced += (_, _) => _ui.Post(_ => _ = OnFolderProgressAsync(), null);
        _engine.BusyChanged += messageId => _ui.Post(_ => OnBusyChanged(messageId), null);
        _engine.SendFailed += (accountId, subject, why) => _ui.Post(_ => OnSendFailed(accountId, subject, why), null);
        _engine.Queued += (_, messageId, subject, at) => _ui.Post(_ => OnQueued(messageId, subject, at), null);
    }

    // ───────── Undo send / Send later ─────────

    /// <summary>Next to Refresh: "Sending “…”" with Undo while its seconds last, or when a message sent later goes.</summary>
    [ObservableProperty] private string? _sendNote;
    [ObservableProperty] private bool _canUndoSend;
    private long _undoId;
    private CancellationTokenSource? _sendNoteCts;

    private void OnQueued(long messageId, string subject, DateTimeOffset at)
    {
        var name = subject.Length > 0 ? subject : L.T("(no subject)");
        var wait = at - DateTimeOffset.Now;
        _undoId = messageId;
        CanUndoSend = wait > TimeSpan.FromSeconds(1);
        // Undo send's wait (up to five minutes): the note and Undo stay until the message goes. Send later: a few
        // seconds to read when it goes.
        var undoWindow = wait <= TimeSpan.FromMinutes(5.1);
        SendNote = undoWindow ? L.F("Sending “{0}”", name) : L.F("“{0}” goes {1:g}", name, at.LocalDateTime);
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _sendNoteCts, cts)?.Cancel();
        var show = undoWindow ? wait + TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(8);
        _ = Task.Delay(show < TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : show, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _ui.Post(_ => { SendNote = null; CanUndoSend = false; }, null);
        }, TaskScheduler.Default);
    }

    /// <summary>Takes the message just sent back out of the Outbox (into Drafts) and opens it to go on writing.</summary>
    [RelayCommand]
    private async Task UndoSend()
    {
        _sendNoteCts?.Cancel();
        (SendNote, CanUndoSend) = (null, false);
        if (await _messages.GetAsync(_undoId) is not { } m || !IsInOutbox(m)
            || _accountsById.GetValueOrDefault(m.AccountId) is not { } account
            || _foldersByAccount.GetValueOrDefault(m.AccountId)?.FirstOrDefault(f => f.Role == FolderRole.Drafts) is not { } drafts
            || !await _engine.MoveAsync(m, drafts))
        {
            _dialogs.Info(L.T("Already sent"), L.T("The message had already gone out."));
            return;
        }
        await RefreshCountsAsync();
        await ReloadMessagesAsync(preserveSelection: true);
        await using var stream = _bodies.Open(m.Id);
        if (stream is not null) _dialogs.Compose(Composing.FromDraft(await MimeMessage.LoadAsync(stream), account, _renderer), account);
    }

    /// <summary>Message menu, in the Outbox: send it now instead of at its time.</summary>
    public bool SelectedInOutbox => SelectedMessage is { } m && IsInOutbox(m.Model);

    [RelayCommand]
    private async Task SendNow()
    {
        foreach (var m in Targets().Where(t => IsInOutbox(t.Model))) await _engine.SendNowAsync(m.Model);
        await ReloadMessagesAsync(preserveSelection: true);
    }

    /// <summary>The Outbox couldn't send: refused by the server (that message waits there to be fixed), or the sign-in.</summary>
    private void OnSendFailed(long accountId, string subject, string why)
    {
        var account = _accountsById.GetValueOrDefault(accountId)?.Email ?? "";
        if (subject.Length > 0)
            MessageDialog.Show(null, L.F("\"{0}\" wasn't sent", subject),
                L.F("The server refused it: {0}\n\nIt's waiting in the Outbox. Open it there to fix it, or delete it.", why), DialogKind.Warning);
        else
            MessageDialog.Show(null, L.F("Mail from {0} can't be sent", account),
                L.F("{0}\n\nYour messages wait in the Outbox and go out once this is fixed (press Refresh to try again).", why), DialogKind.Warning);
    }

    public ObservableCollection<SidebarNode> SidebarNodes { get; } = [];
    public ICollectionView SidebarView { get; }
    public BulkObservableCollection<MessageItemViewModel> Messages { get; } = [];
    public ICollectionView MessagesView { get; }

    // ───────── sorting (column headers) ─────────

    private MessageSortColumn _sortColumn;
    private bool _sortDescending;

    public MessageSortColumn SortColumn => _sortColumn;
    public bool SortDescending => _sortDescending;

    // The caret beside the active column header: down = descending (newest, Z to A, flagged/unread first), up = ascending.
    public string FlagCaret => Caret(MessageSortColumn.Flag);
    public string ReadStatusCaret => Caret(MessageSortColumn.ReadStatus);
    public string FromCaret => Caret(MessageSortColumn.From);
    public string SubjectCaret => Caret(MessageSortColumn.Subject);
    public string ReceivedCaret => Caret(MessageSortColumn.Received);
    public string AttachmentsCaret => Caret(MessageSortColumn.Attachments);
    private string Caret(MessageSortColumn column) => column != _sortColumn ? "" : _sortDescending ? "\uE70D" : "\uE70E";

    /// <summary>A column header was clicked: the same column flips direction, another starts in its natural order.</summary>
    [RelayCommand]
    private async Task SortBy(string column)
    {
        if (!Enum.TryParse<MessageSortColumn>(column, out var picked)) return;
        if (picked == _sortColumn) _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = picked;
            // Newest, flagged, unread and with-attachments first; names and subjects A to Z.
            _sortDescending = picked is not (MessageSortColumn.From or MessageSortColumn.Subject);
        }
        foreach (var name in new[] { nameof(SortColumn), nameof(SortDescending), nameof(FlagCaret), nameof(ReadStatusCaret), nameof(FromCaret),
                     nameof(SubjectCaret), nameof(ReceivedCaret), nameof(AttachmentsCaret) })
            OnPropertyChanged(name);
        _settings.Save(_settings.Current with { MessageSort = _sortColumn.ToString(), MessageSortDescending = _sortDescending });

        ApplyGrouping();
        if (SearchText.Length == 0)
        {
            // A folder: listed again from the top, in the new order (the database sorts, a page at a time).
            _rawLoaded = 0;
            await ReloadMessagesAsync(preserveSelection: true, force: true);
            return;
        }
        // Search results: re-sort what's listed without going back to the database.
        var heads = Messages.Where(m => !m.IsThreadChild)
            .Select(m => new ThreadHead(m.Model with { IsRead = m.IsRead, IsFlagged = m.IsFlagged }, m.ThreadCount)).ToList();
        await PopulateAsync(heads, preserveSelection: true, selectNewest: false, force: true);
    }

    // ───────── paging: the list loads newest first, a page at a time, as you scroll down ─────────

    private const int PageSize = 500;
    /// <summary>Messages fetched for the listed view so far (before conversations fold): where the next page starts.</summary>
    private int _rawLoaded;
    /// <summary>Bumped whenever the list is loaded afresh, so a page fetched for the old list is dropped.</summary>
    private int _listVersion;
    private bool _loadingMore;

    /// <summary>More mail is waiting below what's listed (scrolling to the end loads it).</summary>
    [ObservableProperty] private bool _hasMoreMessages;

    /// <summary>The column-header sort, for the database.</summary>
    private ListSort CurrentListSort => new(_sortColumn switch
    {
        MessageSortColumn.From => ListSortKey.Sender,
        MessageSortColumn.Subject => ListSortKey.Subject,
        MessageSortColumn.Flag => ListSortKey.Flag,
        MessageSortColumn.ReadStatus => ListSortKey.ReadStatus,
        MessageSortColumn.Attachments => ListSortKey.Attachments,
        _ => ListSortKey.Date,
    }, _sortDescending);

    private Task<IReadOnlyList<MessageSummary>> FetchListAsync(SidebarNode node, int limit, int offset, ListSort sort) => node.Kind switch
    {
        NodeKind.UnifiedInbox => _messages.GetUnifiedInboxAsync(limit, offset, sort),
        NodeKind.Unread => _messages.GetUnreadAsync(limit, offset, sort),
        NodeKind.Flagged => _messages.GetFlaggedAsync(limit, offset, sort),
        NodeKind.Outbox => _messages.GetOutboxAsync(limit, offset, sort),
        NodeKind.Trash => _messages.GetTrashAsync(limit, offset, sort),
        _ => _messages.GetByFolderAsync(node.FolderId, limit, offset, sort),
    };

    /// <summary>The next page (older mail, in date order), added below what's listed. True when rows were added.</summary>
    public async Task<bool> LoadMoreMessagesAsync()
    {
        var node = SelectedNode;
        if (_loadingMore || !HasMoreMessages || SearchText.Length > 0 || node is null || node.IsGroup) return false;
        _loadingMore = true;
        try
        {
            var collapse = node.Kind is NodeKind.UnifiedInbox or NodeKind.Folder;
            var (version, offset, sort) = (_listVersion, _rawLoaded, CurrentListSort);
            var (heads, raw) = await Task.Run(async () =>
            {
                var list = await FetchListAsync(node, PageSize, offset, sort);
                var folded = collapse ? await WithConversationCountsAsync(Threading.Collapse(list)) : list.Select(m => new ThreadHead(m, 1)).ToList();
                return (Sorted(folded), list.Count);
            });
            // Another folder, a search, or a reload meanwhile: this page belongs to a list that's gone.
            if (node != SelectedNode || SearchText.Length > 0 || version != _listVersion) return false;
            _rawLoaded += raw;
            HasMoreMessages = raw == PageSize;

            // A conversation already listed keeps its one row (its older messages are in its badge count).
            var listed = Messages.Select(m => collapse ? ConversationKey(m.Model) : $"#{m.Model.Id}").ToHashSet();
            var now = DateTime.Now;
            var rows = heads.Where(h => !listed.Contains(collapse ? ConversationKey(h.Latest) : $"#{h.Latest.Id}")).Select(h =>
            {
                var row = NewRow(h.Latest, h.Count, now);
                row.Group = GroupOf(row);
                return row;
            }).ToList();
            if (rows.Count == 0) return false;

            var selected = SelectedMessage;
            var multi = _selectedMessages.Count > 1 ? _selectedMessages.ToList() : null;
            _repopulating = true;
            try { Messages.AddRange(rows); }
            finally { _repopulating = false; }
            SelectedMessage = selected; // one Reset for the whole page: put the selection back
            if (multi is not null) SelectionRestoreRequested?.Invoke(multi);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Loading older messages failed");
            return false;
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private static string ConversationKey(MessageSummary m) => m.ThreadKey is null ? $"#{m.Id}" : $"{m.AccountId}:{m.ThreadKey}";

    private void ApplyGrouping()
    {
        MessagesView.GroupDescriptions.Clear();
        // Like eM Client, the headings follow the sort; subjects are listed without headings.
        if (_sortColumn != MessageSortColumn.Subject)
            MessagesView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(MessageItemViewModel.Group)));
    }

    private string? GroupOf(MessageItemViewModel item) => _sortColumn switch
    {
        MessageSortColumn.From => item.Sender,
        MessageSortColumn.Flag => item.Model.IsFlagged ? L.T("Flagged") : L.T("Not flagged"),
        MessageSortColumn.ReadStatus => item.Model.IsRead ? L.T("Read") : L.T("Unread"),
        MessageSortColumn.Attachments => item.Model.HasAttachments ? L.T("With attachments") : L.T("Without attachments"),
        MessageSortColumn.Subject => null,
        _ => item.DateGroup,
    };

    private IReadOnlyList<ThreadHead> Sorted(IReadOnlyList<ThreadHead> heads)
    {
        IOrderedEnumerable<ThreadHead> By<TKey>(Func<ThreadHead, TKey> key, IComparer<TKey>? comparer = null) =>
            _sortDescending ? heads.OrderByDescending(key, comparer) : heads.OrderBy(key, comparer);

        var text = StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture, ignoreCase: true);
        var ordered = _sortColumn switch
        {
            MessageSortColumn.From => By(h => string.IsNullOrWhiteSpace(h.Latest.FromName) ? h.Latest.FromAddress : h.Latest.FromName, text),
            MessageSortColumn.Subject => By(h => Subjects.WithoutReplyPrefix(h.Latest.Subject), text),
            MessageSortColumn.Flag => By(h => h.Latest.IsFlagged),
            MessageSortColumn.ReadStatus => By(h => !h.Latest.IsRead),
            MessageSortColumn.Attachments => By(h => h.Latest.HasAttachments),
            _ => By(h => h.Latest.Date),
        };
        return ordered.ThenByDescending(h => h.Latest.Date).ToList(); // within a sender, subject, … newest first
    }

    /// <summary>The reading pane: the selected message with its notices, labels and attachments.</summary>
    public MessageReader Reader { get; }

    [ObservableProperty] private bool _hasAccounts = true;
    [ObservableProperty] private SidebarNode? _selectedNode;
    [ObservableProperty] private string _searchText = "";
    /// <summary>Where mail search looks; "this account/folder" means the one selected in the sidebar. All mail by default.</summary>
    [ObservableProperty] private SearchArea _searchArea = SearchArea.All;
    [ObservableProperty] private bool _searchIncludesTrash = true;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _statusIsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HasSingleSelection), nameof(SelectedFolderName))]
    [NotifyCanExecuteChangedFor(nameof(ToggleReadCommand), nameof(ToggleFlagCommand), nameof(DeleteCommand), nameof(ArchiveCommand),
        nameof(ReplyCommand), nameof(ReplyAllCommand), nameof(ForwardCommand), nameof(ForwardAsAttachmentCommand), nameof(ForwardAsOriginalCommand),
        nameof(ResendCommand), nameof(OpenMessageCommand), nameof(MarkReadCommand),
        nameof(MarkUnreadCommand), nameof(SaveAsCommand), nameof(MoveToFolderCommand), nameof(CopyToFolderCommand), nameof(MoveToJunkCommand),
        nameof(NotJunkCommand), nameof(PrintCommand), nameof(DownloadOfflineCommand), nameof(ViewSourceCommand), nameof(ViewHeadersCommand))]
    private MessageItemViewModel? _selectedMessage;

    public bool HasSelection => SelectedMessage is not null;

    private IReadOnlyList<MessageItemViewModel> _selectedMessages = [];

    /// <summary>Every selected row (Shift/Ctrl-click). <see cref="SelectedMessage"/> is the one the reading pane shows.</summary>
    public IReadOnlyList<MessageItemViewModel> SelectedMessages => _selectedMessages;

    /// <summary>Reply, Open, Save As and the like work on one message only.</summary>
    public bool HasSingleSelection => SelectedMessage is not null && _selectedMessages.Count <= 1;

    /// <summary>The list was rebuilt under a multi-row selection: the view re-selects these rows.</summary>
    public event Action<IReadOnlyList<MessageItemViewModel>>? SelectionRestoreRequested;

    /// <summary>The view reports the list's selection whenever it changes.</summary>
    public void SetSelectedMessages(IReadOnlyList<MessageItemViewModel> rows)
    {
        _selectedMessages = rows;
        OnPropertyChanged(nameof(SelectedMessages));
        OnPropertyChanged(nameof(HasSingleSelection));
        foreach (var command in new IRelayCommand[]
                 {
                     ToggleReadCommand, ToggleFlagCommand, ReplyCommand, ReplyAllCommand, ForwardCommand, ForwardAsAttachmentCommand,
                     ForwardAsOriginalCommand, ResendCommand, OpenMessageCommand,
                     MarkReadCommand, MarkUnreadCommand, SaveAsCommand, MoveToFolderCommand, CopyToFolderCommand, PrintCommand,
                     ViewSourceCommand, ViewHeadersCommand,
                 })
            command.NotifyCanExecuteChanged();
    }

    /// <summary>The rows an action applies to: the whole multi-row selection, or else the shown message.</summary>
    private List<MessageItemViewModel> Targets() =>
        _selectedMessages.Count > 1 ? _selectedMessages.ToList()
        : SelectedMessage is { } m ? [m]
        : [];

    /// <summary>Print… was chosen; the window prints what the reading pane shows.</summary>
    public event Action? PrintRequested;

    /// <summary>For the message menu: is the selected message in Junk (offer "Not Junk" instead of "Move to Junk")?</summary>
    public bool SelectedInJunk => SelectedMessage is { } m
        && _foldersByAccount.GetValueOrDefault(m.Model.AccountId)?.FirstOrDefault(f => f.Id == m.Model.FolderId)?.Role == FolderRole.Junk;

    /// <summary>Called as the message menu opens, so read/unread and junk items reflect the current message.</summary>
    public void RefreshMenuState()
    {
        MarkReadCommand.NotifyCanExecuteChanged();
        MarkUnreadCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(SelectedInJunk));
        OnPropertyChanged(nameof(SelectedInOutbox));
        // Several messages picked: the menu speaks of all their senders ("Block 4 senders…").
        var senders = TargetSenders();
        RuleSender = senders.Count > 1 ? L.F("{0} senders", senders.Count)
            : (senders.FirstOrDefault() ?? SelectedMessage?.Model) is { } m ? NameOf(m) : L.T("this sender");
        var domains = TargetDomains(senders);
        BlockDomain = domains.Count switch { 0 => null, 1 => domains[0], _ => L.F("{0} domains", domains.Count) };
        RuleSubjectHeader = TargetSubjects().Count > 1 ? L.T("Move mail with these subjects to…") : L.T("Move mail with this subject to…");
    }

    /// <summary>"Move mail with this subject to…", or "these subjects" when the picked messages have several.</summary>
    [ObservableProperty] private string _ruleSubjectHeader = L.T("Move mail with this subject to…");

    /// <summary>
    /// The senders of the messages an action applies to (all the selected ones), each address once; your own
    /// addresses left out (Sent mail in the selection), unless that's all there is.
    /// </summary>
    private List<MessageSummary> TargetSenders()
    {
        var all = Targets().Select(r => r.Model).Where(m => m.FromAddress.Contains('@'))
            .GroupBy(m => m.FromAddress.Trim(), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var others = all.Where(m => !_accountsById.Values.Any(a => a.Identities.Any(i => i.Address.Equals(m.FromAddress.Trim(), StringComparison.OrdinalIgnoreCase)))).ToList();
        return others.Count > 0 ? others : all;
    }

    /// <summary>"@shop.example" for each company domain among these senders. Never free mail: blocking @gmail.com would block half the world.</summary>
    private static List<string> TargetDomains(IEnumerable<MessageSummary> senders) =>
        senders.Select(m => m.FromAddress.Trim()).Where(a => a.Contains('@')).Select(a => a[(a.LastIndexOf('@') + 1)..].ToLowerInvariant())
            .Where(d => d.Length > 0 && !SenderCheck.IsFreeMail(d)).Distinct().Select(d => "@" + d).ToList();

    /// <summary>The picked messages' subjects, without Re:/Fwd:, each once.</summary>
    private List<string> TargetSubjects() =>
        Targets().Select(r => RuleMatcher.CleanSubject(r.Model.Subject)).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>"Ana", "Ana, Shop", "Ana, Shop and 3 more": for titles and rule names.</summary>
    private static string ListNames(IReadOnlyList<string> names) => names.Count switch
    {
        0 => "",
        1 => names[0],
        2 => $"{names[0]}, {names[1]}",
        _ => L.F("{0}, {1} and {2} more", names[0], names[1], names.Count - 2),
    };

    private static string NameOf(MessageSummary m) => m.FromName.Length > 0 ? m.FromName : m.FromAddress;

    /// <summary>"@shop.example" for the Block menu; null for free mail providers.</summary>
    [ObservableProperty] private string? _blockDomain;

    /// <summary>Block the selected message's sender (or their whole domain), offering to move what's already in the inbox.</summary>
    /// <remarks>With several messages picked: all their senders, or all their company domains, in one go.</remarks>
    [RelayCommand]
    private Task BlockSender(string scope)
    {
        var senders = TargetSenders();
        if (senders.Count > 1)
        {
            if (scope == "domain")
            {
                var domains = TargetDomains(senders);
                return domains.Count == 0 ? Task.CompletedTask : BlockAsync(domains, L.F("everyone at {0}", ListNames(domains)));
            }
            return BlockAsync(senders.Select(m => m.FromAddress.Trim().ToLowerInvariant()).ToList(), ListNames(senders.Select(NameOf).ToList()));
        }
        return (senders.FirstOrDefault() ?? SelectedMessage?.Model) is { } one ? BlockSenderAsync(one, scope) : Task.CompletedTask;
    }

    /// <summary>Blocks a message's sender ("sender") or their company's domain ("domain").</summary>
    public Task BlockSenderAsync(MessageSummary m, string scope)
    {
        var domain = TargetDomains([m]).FirstOrDefault();
        var entry = scope == "domain" && domain is not null ? domain : m.FromAddress.Trim().ToLowerInvariant();
        var who = entry == domain ? L.F("everyone at {0}", entry) : (m.FromName.Length > 0 ? $"{m.FromName} ({m.FromAddress})" : m.FromAddress);
        return BlockAsync([entry], who);
    }

    /// <summary>Adds addresses or "@domain"s to the blocked senders, offering to move what's already in the inboxes.</summary>
    private async Task BlockAsync(IReadOnlyList<string> entries, string who)
    {
        var where = _settings.Current.BlockedToTrash ? L.T("Trash") : L.T("Junk");

        var inboxes = _foldersByAccount.Values.Select(f => f.FirstOrDefault(x => x.Role == FolderRole.Inbox)).OfType<Folder>().ToList();
        var already = 0;
        foreach (var inbox in inboxes)
            already += (await _messages.GetByFolderAsync(inbox.Id, 5000)).Count(x => entries.Any(e => Addresses.Covers(e, x.FromAddress)));

        bool move;
        if (already > 0)
        {
            var choice = MessageDialog.Choose(null, L.F("Block {0}?", who),
                already == 1 ? L.F("New mail from them goes to {0} as it arrives. 1 message from them is already in your inbox.", where)
                    : L.F("New mail from them goes to {0} as it arrives. {1} messages from them are already in your inbox.", where, already),
                already == 1 ? L.T("Block and move it") : L.T("Block and move them"), L.T("Just block"), DialogKind.Warning);
            if (choice is null) return;
            move = choice == true;
        }
        else
        {
            if (!_dialogs.Confirm(L.F("Block {0}?", who), L.F("New mail from them goes to {0} as it arrives. You can unblock them in Settings > Senders.", where), L.T("Block"), DialogKind.Warning)) return;
            move = false;
        }

        var list = _settings.Current.BlockedSenders;
        var added = entries.Where(e => !list.Contains(e, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (added.Count > 0) _settings.Save(_settings.Current with { BlockedSenders = [.. list, .. added] });
        if (move)
        {
            foreach (var inbox in inboxes) await _engine.MoveBlockedAsync(inbox);
            await RefreshCountsAsync();
            await ReloadMessagesAsync(preserveSelection: false);
        }
        StatusText = L.F("Blocked {0}", entries.Count == 1 ? entries[0] : who);
    }

    [RelayCommand]
    private async Task OpenSendersSettings()
    {
        if (_dialogs.ShowSettings("senders")) await ReloadMessagesAsync(preserveSelection: true);
    }

    /// <summary>The selected message's sender, for the "Create Rule" menu.</summary>
    [ObservableProperty] private string _ruleSender = L.T("this sender");

    /// <summary>(title, text, how many) about newly arrived mail: a desktop notification and the taskbar badge.</summary>
    public event Action<string, string, int>? NewMailArrived;

    public string? SelectedFolderName => SelectedMessage is { } m
        ? _foldersByAccount.GetValueOrDefault(m.Model.AccountId)?.FirstOrDefault(f => f.Id == m.Model.FolderId)?.Name
        : null;

    // ───────────────────────── loading ─────────────────────────

    public async Task LoadAsync()
    {
        var previous = SelectedNode;
        await RebuildSidebarAsync();
        var same = SidebarNodes.FirstOrDefault(n => previous is not null && n.Kind == previous.Kind && n.FolderId == previous.FolderId
                                                    && n.AccountId == previous.AccountId && n.IsFavoriteShortcut == previous.IsFavoriteShortcut)
                   ?? SidebarNodes.FirstOrDefault(n => previous is { Kind: NodeKind.Folder } && n.FolderId == previous.FolderId);
        if (same is not null)
        {
            _rebuildingSidebar = true;
            try { SelectedNode = same; }
            finally { _rebuildingSidebar = false; }
            await ReloadMessagesAsync(preserveSelection: true);
        }
        else
        {
            SelectedNode = StartupNode() ?? SidebarNodes.First(n => n.Kind == NodeKind.UnifiedInbox);
        }
        UpdateStatus();
    }

    /// <summary>The folder chosen under Settings > Show on startup ("unified" or "inbox:{accountId}").</summary>
    private SidebarNode? StartupNode() =>
        _settings.Current.StartupView.StartsWith("inbox:", StringComparison.Ordinal)
        && long.TryParse(_settings.Current.StartupView["inbox:".Length..], out var accountId)
            ? SidebarNodes.FirstOrDefault(n => n.AccountId == accountId && n.Role == FolderRole.Inbox && n.Kind == NodeKind.Folder)
            : null;

    private async Task OnFoldersChangedAsync(long accountId)
    {
        try
        {
            var folders = await _folders.GetByAccountAsync(accountId);
            var known = _foldersByAccount.GetValueOrDefault(accountId);
            if (known is null || !folders.Select(f => (f.Id, f.Name)).SequenceEqual(known.Select(f => (f.Id, f.Name))))
                await LoadAsync();
        }
        catch (Exception ex) { _log.LogError(ex, "Refreshing folders failed"); }
    }

    /// <summary>A folder finished syncing: refresh counts and the visible list, at most a couple of times a second.</summary>
    private async Task OnFolderProgressAsync()
    {
        _progressCts?.Cancel();
        var cts = _progressCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(500, cts.Token);
            await RefreshCountsAsync();
            if (SearchText.Length == 0) await ReloadMessagesAsync(preserveSelection: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Refreshing after folder sync failed"); }
    }

    private async Task RebuildSidebarAsync()
    {
        var accounts = await _accounts.GetAllAsync();
        HasAccounts = accounts.Count > 0;
        var expanded = SidebarNodes.Where(n => n.IsGroup).ToDictionary(n => (n.Title, n.AccountId, n.Level), n => n.IsExpanded);

        SidebarNodes.Clear();
        _foldersByAccount.Clear();
        _accountsById.Clear();
        foreach (var account in accounts)
        {
            _accountsById[account.Id] = account;
            _foldersByAccount[account.Id] = await _folders.GetByAccountAsync(account.Id);
        }

        var favorites = new SidebarNode { Title = L.T("Favorites"), Glyph = Glyphs.Star, Kind = NodeKind.Group };
        SidebarNodes.Add(favorites);
        SidebarNodes.Add(new SidebarNode { Title = L.T("All Inboxes"), Glyph = Glyphs.Inbox, Kind = NodeKind.UnifiedInbox, Parent = favorites, Level = 1 });
        SidebarNodes.Add(new SidebarNode { Title = L.T("Unread"), Glyph = Glyphs.Eye, Kind = NodeKind.Unread, Parent = favorites, Level = 1 });
        SidebarNodes.Add(new SidebarNode { Title = L.T("Flagged"), Glyph = Glyphs.Flag, Kind = NodeKind.Flagged, Parent = favorites, Level = 1 });
        // One Outbox for every account, always here: what's waiting to go (Undo send's seconds, Send later, or offline).
        SidebarNodes.Add(new SidebarNode { Title = L.T("Outbox"), Glyph = Glyphs.Outbox, Kind = NodeKind.Outbox, Parent = favorites, Level = 1 });
        // One Trash for every account: shows what was deleted everywhere; mail dropped on it goes to its own account's Trash.
        SidebarNodes.Add(new SidebarNode { Title = L.T("Trash"), Glyph = Glyphs.Trash, Kind = NodeKind.Trash, Parent = favorites, Level = 1 });

        // Folders added to Favorites (Add to Favorites in the folder menu); the account name only when names clash.
        var favoriteFolders = accounts.SelectMany(a => SidebarOrder(_foldersByAccount[a.Id])).Where(f => PrefsOf(f).Favorite).ToList();
        foreach (var f in favoriteFolders)
        {
            var clash = favoriteFolders.Count(o => o.Name.Equals(f.Name, StringComparison.CurrentCultureIgnoreCase)) > 1;
            var title = clash ? $"{f.Name} ({_accountsById[f.AccountId].SidebarTitle})" : f.Name;
            SidebarNodes.Add(FolderNode(f, favorites, level: 1, title, shortcut: true));
        }

        foreach (var account in accounts)
        {
            // The accounts' Outboxes are listed together under Favorites.
            var folders = _foldersByAccount[account.Id].Where(f => PrefsOf(f).Visibility != FolderVisibility.Hide && f.Role != FolderRole.Outbox).ToList();
            var group = new SidebarNode { Title = account.SidebarTitle, Glyph = Glyphs.Person, Kind = NodeKind.Group, AccountId = account.Id, AccountColor = account.Color };
            SidebarNodes.Add(group);
            foreach (var f in SidebarOrder(folders).Where(f => f.Role != FolderRole.Other))
                SidebarNodes.Add(FolderNode(f, group, level: 1));

            var others = SidebarOrder(folders).Where(f => f.Role == FolderRole.Other).ToList();
            if (others.Count > 0)
            {
                var more = new SidebarNode
                {
                    Title = L.T("More"), Glyph = Glyphs.More, Kind = NodeKind.Group, Parent = group,
                    AccountId = account.Id, Level = 1, IsExpanded = false,
                };
                SidebarNodes.Add(more);
                foreach (var f in others) SidebarNodes.Add(FolderNode(f, more, level: 2));
            }
        }

        foreach (var node in SidebarNodes.Where(n => n.IsGroup))
            if (expanded.TryGetValue((node.Title, node.AccountId, node.Level), out var isExpanded)) node.IsExpanded = isExpanded;
        SidebarView.Refresh();
        await RefreshCountsAsync();
    }

    /// <summary>eM Client's order: the standard folders first, then the rest alphabetically.</summary>
    private static IEnumerable<Folder> SidebarOrder(IEnumerable<Folder> folders) =>
        folders.OrderBy(f => f.Role == FolderRole.Other).ThenBy(f => Array.IndexOf(RoleOrder, f.Role))
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase);

    private FolderPrefs PrefsOf(Folder f) => _settings.Current.Folders.GetValueOrDefault(FolderPrefs.Key(f.AccountId, f.Path)) ?? new();

    private SidebarNode FolderNode(Folder f, SidebarNode parent, int level, string? title = null, bool shortcut = false)
    {
        var prefs = PrefsOf(f);
        return new SidebarNode
        {
            Title = title ?? f.Name, Glyph = Glyphs.ForRole(f.Role), Kind = NodeKind.Folder, Parent = parent, Level = level,
            AccountId = f.AccountId, FolderId = f.Id, Role = f.Role,
            GlyphBrush = prefs.Color is { } color ? FolderColors.Brush(color) : null,
            ShowOnlyIfUnread = prefs.Visibility == FolderVisibility.ShowIfUnread && !shortcut,
            IsFavoriteShortcut = shortcut,
            IsBusy = _busyFolders.Contains(f.Id), // the sidebar can be rebuilt while an action runs
        };
    }

    private async Task OnSyncedAsync(long accountId, int newInbox)
    {
        try
        {
            if (newInbox > 0) await AnnounceNewMailAsync(accountId, newInbox);
            var folders = await _folders.GetByAccountAsync(accountId);
            var known = _foldersByAccount.GetValueOrDefault(accountId);
            if (known is null || !folders.Select(f => (f.Id, f.Name)).SequenceEqual(known.Select(f => (f.Id, f.Name))))
                await LoadAsync(); // folder list changed (first sync, renamed folders…)
            else
                await RefreshCountsAsync();

            if (SearchText.Length == 0) await ReloadMessagesAsync(preserveSelection: true);
        }
        catch (Exception ex) { _log.LogError(ex, "Refreshing after sync failed"); }
    }

    /// <summary>Dev aid: a message arrives in the first account's inbox, as a sync would deliver it.</summary>
    internal async Task SimulateNewMailAsync(string subject, string fromName = "New Sender", string fromAddress = "new@sender.example",
        string preview = "Just arrived.")
    {
        var account = _accountsById.Values.First();
        var inbox = _foldersByAccount[account.Id].First(f => f.Role == FolderRole.Inbox);
        await _messages.UpsertAsync(new MessageSummary
        {
            AccountId = account.Id, FolderId = inbox.Id, Uid = 900_000 + Random.Shared.Next(1000), Subject = subject,
            FromName = fromName, FromAddress = fromAddress, Date = DateTimeOffset.Now, Preview = preview,
        });
        await OnSyncedAsync(account.Id, 1);
    }

    /// <summary>Dev aid (--record-site): a reply you just sent, filed in Sent within its conversation (the demo has no server).</summary>
    internal async Task DevAddSentReplyAsync(MessageSummary original, string preview)
    {
        var account = _accountsById[original.AccountId];
        var sent = _foldersByAccount[account.Id].First(f => f.Role == FolderRole.Sent);
        await _messages.UpsertAsync(new MessageSummary
        {
            AccountId = account.Id, FolderId = sent.Id, Uid = 910_000 + Random.Shared.Next(1000), ThreadKey = original.ThreadKey,
            Subject = original.Subject.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? original.Subject : "Re: " + original.Subject,
            FromName = account.DisplayName, FromAddress = account.Email, To = original.FromAddress, Recipients = original.FromAddress,
            Date = DateTimeOffset.Now, Preview = preview, IsRead = true,
        });
        await ReloadMessagesAsync(preserveSelection: true);
    }

    /// <summary>Dev aid (--screenshot-site): takes demo messages out of the list, to make room in a screenshot.</summary>
    internal async Task DevRemoveAsync(params string[] subjects)
    {
        foreach (var row in Messages.Where(m => subjects.Contains(m.Subject)).ToList())
            await _messages.DeleteAsync(row.Model.Id);
        await ReloadMessagesAsync(preserveSelection: true);
    }

    private async Task AnnounceNewMailAsync(long accountId, int count)
    {
        var account = _accountsById.GetValueOrDefault(accountId);
        var inbox = _foldersByAccount.GetValueOrDefault(accountId)?.FirstOrDefault(f => f.Role == FolderRole.Inbox);
        if (account is null || inbox is null) return;
        var newest = (await _messages.GetByFolderAsync(inbox.Id, 1)).FirstOrDefault();
        if (count == 1 && newest is not null)
            NewMailArrived?.Invoke(string.IsNullOrWhiteSpace(newest.FromName) ? newest.FromAddress : newest.FromName, newest.Subject, count);
        else
            NewMailArrived?.Invoke(L.F("{0} new messages", count), account.SidebarTitle, count);
    }

    partial void OnSelectedNodeChanged(SidebarNode? value)
    {
        // "This account/folder" follow the selection.
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(IsSearchNarrowed));
        // Views that mix accounts: the reading pane's folder chip names the account too.
        Reader.ShowAccount = value is { Kind: not (NodeKind.Folder or NodeKind.Group) } && _accountsById.Count > 1;
        if (value is null || _rebuildingSidebar) return;
        CloseReading(); // Fill screen: another folder shows its list
        if (value.Kind == NodeKind.Folder
            && _foldersByAccount.GetValueOrDefault(value.AccountId)?.FirstOrDefault(f => f.Id == value.FolderId) is { } folder)
            _engine.OpenFolder(folder); // Gmail's All Mail/Important sync when first opened
        if (SearchText.Length > 0) SearchText = ""; // leaving search returns to the folder
        else _ = ReloadMessagesAsync(preserveSelection: false, selectNewest: true);
    }

    partial void OnSearchTextChanged(string value) => _ = SearchDebouncedAsync(value);

    partial void OnSearchAreaChanged(SearchArea value) => SearchScopeChanged();
    partial void OnSearchIncludesTrashChanged(bool value) => SearchScopeChanged();

    private void SearchScopeChanged()
    {
        OnPropertyChanged(nameof(SearchPlaceholder));
        OnPropertyChanged(nameof(IsSearchNarrowed));
        if (SearchText.Length > 0) _ = SearchDebouncedAsync(SearchText);
    }

    /// <summary>The account the "This account" choice means: the selected folder's or account's.</summary>
    public Account? SearchAccount => SelectedNode is { AccountId: > 0 } n ? _accountsById.GetValueOrDefault(n.AccountId) : null;

    /// <summary>The folder the "This folder" choice means: the one selected in the sidebar.</summary>
    public Folder? SearchFolder => FolderOf(SelectedNode);

    // "This folder" with no folder selected (All Inboxes, Unread…) widens to the account, then to everything.
    private SearchArea EffectiveSearchArea => SearchArea switch
    {
        SearchArea.Folder when SearchFolder is not null => SearchArea.Folder,
        SearchArea.Folder or SearchArea.Account when SearchAccount is not null => SearchArea.Account,
        _ => SearchArea.All,
    };

    /// <summary>The search box says where it looks: "Search all mail", "Search Inbox"…</summary>
    public string SearchPlaceholder => EffectiveSearchArea switch
    {
        SearchArea.Folder => L.F("Search {0}", SearchFolder!.Name),
        SearchArea.Account => L.F("Search {0}", SearchAccount!.SidebarTitle),
        _ => L.T("Search all mail"),
    };

    /// <summary>Anything other than all mail with Trash and Junk: the scope button shows it.</summary>
    public bool IsSearchNarrowed => EffectiveSearchArea != SearchArea.All || !SearchIncludesTrash;

    private SearchScope CurrentSearchScope() => EffectiveSearchArea switch
    {
        SearchArea.Folder => new SearchScope(FolderId: SearchFolder!.Id),
        SearchArea.Account => new SearchScope(AccountId: SearchAccount!.Id, IncludeTrashAndJunk: SearchIncludesTrash),
        _ => new SearchScope(IncludeTrashAndJunk: SearchIncludesTrash),
    };

    // ───────── Reading pane layout (Settings > Appearance) ─────────

    /// <summary>"bottom", "right", "full" (the list fills the area; a message opens in its place) or "popout" (no pane).</summary>
    [ObservableProperty] private string _readingLayout = "bottom";

    /// <summary>Fill screen: a message is open in place of the list.</summary>
    [ObservableProperty] private bool _isReadingOpen;

    /// <summary>Whether picking a row shows it (and starts counting it as read): not when there's no pane to show it in.</summary>
    private bool PaneFollowsSelection => ReadingLayout is not ("full" or "popout") || (ReadingLayout == "full" && IsReadingOpen);

    partial void OnReadingLayoutChanged(string value)
    {
        IsReadingOpen = false;
        ShowInPane(PaneFollowsSelection ? SelectedMessage : null);
    }

    /// <summary>Fill screen: the selected message opens in place of the list.</summary>
    [RelayCommand]
    private void OpenInPlace()
    {
        if (SelectedMessage is null) return;
        IsReadingOpen = true;
        ShowInPane(SelectedMessage);
    }

    /// <summary>Fill screen: back to the list.</summary>
    [RelayCommand]
    private void CloseReading()
    {
        if (!IsReadingOpen) return;
        IsReadingOpen = false;
        ShowInPane(null);
    }

    /// <summary>
    /// Enter or double-click on a row: in place with Fill screen, otherwise in its own window. Drafts and the Outbox
    /// open for editing either way.
    /// </summary>
    [RelayCommand]
    private Task OpenSelected()
    {
        if (ReadingLayout == "full" && SelectedMessage is { } item && !IsDraft(item.Model) && !IsInOutbox(item.Model))
        {
            OpenInPlace();
            return Task.CompletedTask;
        }
        return OpenMessageCommand.ExecuteAsync(null);
    }

    partial void OnSelectedMessageChanged(MessageItemViewModel? value)
    {
        if (_repopulating) return;
        if (value?.Model.Id == Reader.ShownId)
        {
            // The same message re-selected after a list refresh: only its row object changed.
            if (value is not null) Reader.Rebind(value);
            return;
        }
        ShowInPane(PaneFollowsSelection ? value : null);
    }

    /// <summary>Shows a message in the reading pane (or empties it), and counts it as read after the set time.</summary>
    private void ShowInPane(MessageItemViewModel? value)
    {
        _readCts?.Cancel();
        if (value is null && Reader.ShownId is null) return;
        _ = Reader.ShowAsync(value);
        _engine.SetInView(value?.Model.Id); // new mail being read stays put until it's left, even if a rule files it away
        // Settings > Mail > Reading: how long a message stays open before it counts as read (or never by itself).
        if (value is { IsRead: false } && _settings.Current.MarkReadAfterSeconds >= 0)
        {
            var cts = _readCts = new CancellationTokenSource();
            _ = MarkReadLaterAsync(value.Model.Id, cts.Token);
        }
    }

    private async Task MarkReadLaterAsync(long messageId, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(_settings.Current.MarkReadAfterSeconds), ct);
            // Compare by id: a background refresh may have replaced the row object meanwhile.
            if (SelectedMessage is { IsRead: false } current && current.Model.Id == messageId)
                await SetFlagsAsync(current, isRead: true, current.IsFlagged);
        }
        catch (OperationCanceledException) { }
    }

    private async Task SearchDebouncedAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, cts.Token);
            if (string.IsNullOrWhiteSpace(query)) await ReloadMessagesAsync(preserveSelection: false);
            else
            {
                var scope = CurrentSearchScope();
                var results = await Task.Run(() => _messages.SearchAsync(query, scope, ct: cts.Token));
                await PopulateAsync(results.Select(m => new ThreadHead(m, 1)).ToList(), preserveSelection: false, selectNewest: false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Search failed for {Query}", query); }
    }

    /// <param name="force">Rebuild the rows even when the same messages are listed (the sort changed).</param>
    private async Task ReloadMessagesAsync(bool preserveSelection, bool selectNewest = false, bool force = false)
    {
        try
        {
            var node = SelectedNode;
            if (node is null || node.IsGroup) return;
            // A fresh view starts with one page; a refresh (sync, a change) keeps every page scrolled into, in one query.
            var limit = preserveSelection ? Math.Max(PageSize, _rawLoaded) : PageSize;
            var sort = CurrentListSort;
            var version = ++_listVersion;
            // SQLite's "async" calls run synchronously; keep them off the UI thread so clicks stay instant.
            var collapse = node.Kind is NodeKind.UnifiedInbox or NodeKind.Folder;
            // In Unread and Flagged, a message you just read (or unflagged) stays in the list until you leave it:
            // it shouldn't vanish from under you while you're reading it.
            var keep = preserveSelection && node.Kind is NodeKind.Unread or NodeKind.Flagged
                ? Messages.Where(m => !m.IsThreadChild).Select(m => (m.Model.Id, m.Model.FolderId)).ToList()
                : [];
            var clock = PerfTrace ? System.Diagnostics.Stopwatch.StartNew() : null;
            var (heads, raw) = await Task.Run(async () =>
            {
                var list = await FetchListAsync(node, limit, 0, sort);
                var fetched = list.Count;
                Trace(clock, "query", list.Count);
                if (keep.Count > 0)
                {
                    var listed = list.Select(m => m.Id).ToHashSet();
                    var stay = new List<MessageSummary>();
                    foreach (var (id, folderId) in keep.Where(k => !listed.Contains(k.Id)))
                        if (await _messages.GetAsync(id) is { } m && m.FolderId == folderId && (node.Kind == NodeKind.Unread ? m.IsRead : !m.IsFlagged))
                            stay.Add(m); // only left because it was read/unflagged, not moved or deleted
                    if (stay.Count > 0) list = list.Concat(stay).OrderByDescending(m => m.Date).ToList();
                }
                var collapsed = collapse ? Threading.Collapse(list) : list.Select(m => new ThreadHead(m, 1)).ToList();
                Trace(clock, "fold", collapsed.Count);
                var folded = collapse ? await WithConversationCountsAsync(collapsed) : collapsed;
                Trace(clock, "conversation counts", folded.Count);
                return (folded, fetched);
            });
            if (node != SelectedNode || version != _listVersion) return;
            _rawLoaded = raw;
            HasMoreMessages = raw == limit;
            await PopulateAsync(heads, preserveSelection, selectNewest, force, clock);
            ListedNode = node;
        }
        catch (Exception ex) { _log.LogError(ex, "Loading messages failed"); }
    }

    /// <summary>The list folds a conversation into one row; its badge counts the whole conversation (incl. Sent).</summary>
    private async Task<IReadOnlyList<ThreadHead>> WithConversationCountsAsync(IReadOnlyList<ThreadHead> heads)
    {
        var keys = heads.Where(h => h.Latest.ThreadKey is not null).Select(h => h.Latest.ThreadKey!).ToList();
        if (keys.Count == 0) return heads;
        var counts = await _messages.GetThreadCountsAsync(keys);
        return heads.Select(h => h.Latest.ThreadKey is { } key && counts.TryGetValue((h.Latest.AccountId, key), out var n) && n > h.Count
            ? h with { Count = n } : h).ToList();
    }

    /// <param name="force">Rebuild even when the same conversations are listed (the sort or grouping changed).</param>
    /// <summary>Dev aid (--dev-perf): the sidebar entry whose messages were listed last.</summary>
    internal SidebarNode? ListedNode { get; private set; }

    /// <summary>Dev aid (--dev-perf): log how long each step of loading the message list takes.</summary>
    internal static bool PerfTrace { get; set; }

    private void Trace(System.Diagnostics.Stopwatch? clock, string step, int count)
    {
        if (clock is null) return;
        _log.LogInformation("PERF   {Step}: {Ms} ms ({Count})", step, clock.ElapsedMilliseconds, count);
    }

    private async Task PopulateAsync(IReadOnlyList<ThreadHead> heads, bool preserveSelection, bool selectNewest, bool force = false,
        System.Diagnostics.Stopwatch? clock = null)
    {
        UpdateListEmptyState();
        heads = Sorted(heads);
        Trace(clock, "sorted", heads.Count);
        // Same conversations as before: refresh flags in place so scroll position, selection and open threads stay put.
        var currentHeads = Messages.Where(m => !m.IsThreadChild).ToList();
        if (!force && preserveSelection && heads.Count == currentHeads.Count
            && heads.Select(h => (h.Latest.Id, h.Count)).SequenceEqual(currentHeads.Select(m => (m.Model.Id, m.ThreadCount))))
        {
            for (var i = 0; i < heads.Count; i++)
            {
                currentHeads[i].IsRead = heads[i].Latest.IsRead;
                currentHeads[i].IsFlagged = heads[i].Latest.IsFlagged;
            }
            return;
        }

        var selectedId = preserveSelection ? SelectedMessage?.Model.Id : null;
        var multiIds = preserveSelection && _selectedMessages.Count > 1 ? _selectedMessages.Select(m => m.Model.Id).ToHashSet() : null;
        var expanded = preserveSelection ? currentHeads.Where(m => m.IsExpanded).Select(m => m.Model.ThreadKey).ToHashSet() : [];
        var now = DateTime.Now;
        // A refresh of the list on screen (not a folder switch): rows that weren't there and are recent just arrived.
        var shownIds = preserveSelection && Messages.Count > 0 ? Messages.Select(m => m.Model.Id).ToHashSet() : null;
        var wasAtTop = IsListAtTop?.Invoke() ?? false;
        _repopulating = true;
        try
        {
            var built = heads.Select(h =>
            {
                var row = NewRow(h.Latest, h.Count, now);
                row.Group = GroupOf(row);
                row.IsFresh = shownIds is not null && !shownIds.Contains(h.Latest.Id) && h.Latest.Date > DateTimeOffset.Now.AddDays(-2);
                return row;
            }).ToList();
            Trace(clock, "rows built", built.Count);
            Messages.ReplaceAll(built);
            Trace(clock, "list replaced (grouped)", built.Count);
            foreach (var head in Messages.Where(m => m.IsThread && expanded.Contains(m.Model.ThreadKey)).ToList())
                await ExpandAsync(head); // keep conversations the user had open
        }
        finally
        {
            _repopulating = false;
        }

        SelectedMessage = selectedId is not null ? Messages.FirstOrDefault(m => m.Model.Id == selectedId)
            : selectNewest ? Messages.FirstOrDefault() // like eM Client: opening a folder shows its newest mail
            : null;
        if (SelectedMessage is null && Reader.ShownId is not null)
        {
            _ = Reader.ShowAsync(null);
            _engine.SetInView(null);
        }
        if (multiIds is not null && Messages.Where(m => multiIds.Contains(m.Model.Id)).ToList() is { Count: > 1 } rows)
            SelectionRestoreRequested?.Invoke(rows);
        Trace(clock, "selection set", Messages.Count);

        var fresh = Messages.Where(m => m.IsFresh).ToList();
        if (fresh.Count > 0)
        {
            // You were at the top: stay there, so the new mail is in view rather than just above it.
            if (wasAtTop) ScrollToTopRequested?.Invoke();
            _ = UnmarkFreshAsync(fresh);
        }
    }

    private static async Task UnmarkFreshAsync(IReadOnlyList<MessageItemViewModel> rows)
    {
        await Task.Delay(3500);
        foreach (var row in rows) row.IsFresh = false;
    }

    /// <summary>Set by the window: whether the message list is scrolled to the very top.</summary>
    public Func<bool>? IsListAtTop { get; set; }
    /// <summary>New mail came in while the list was at the top: keep it at the top.</summary>
    public event Action? ScrollToTopRequested;

    /// <summary>A message row as the list shows it, its sender's picture fetched when the row is first drawn.</summary>
    private MessageItemViewModel NewRow(MessageSummary m, int threadCount, DateTime now, MessageItemViewModel? parent = null, string? folderHint = null) =>
        new(m, threadCount, now, parent, folderHint) { PictureLoader = LoadPictureAsync, IsBusy = _engine.IsBusy(m.Id) };

    private void OnBusyChanged(long messageId)
    {
        var busy = _engine.IsBusy(messageId);
        foreach (var row in Messages)
            if (row.Model.Id == messageId) row.IsBusy = busy;
    }

    /// <summary>One row's sender picture; the row keeps its coloured letter until (unless) one arrives.</summary>
    /// <summary>The picture chosen for one of your accounts (Accounts > picture), or null.</summary>
    public System.Windows.Media.ImageSource? AccountPicture(long accountId) => _pictures.ForAccount(accountId);

    /// <summary>An account's colour, as set in Accounts.</summary>
    public string? AccountColor(long accountId) => _accountsById.GetValueOrDefault(accountId)?.Color;

    private async Task LoadPictureAsync(MessageItemViewModel row)
    {
        await Task.Yield(); // not inside the binding that asked for it
        try
        {
            if (string.IsNullOrWhiteSpace(row.Model.FromAddress)) return;
            var own = _accountsById.Values
                .SelectMany(a => a.Identities.Select(i => (i.Address, a.Id)))
                .DistinctBy(x => x.Address, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Address, x => x.Id, StringComparer.OrdinalIgnoreCase);
            // Never contact a spammer's servers.
            var inJunk = _foldersByAccount.GetValueOrDefault(row.Model.AccountId)?.FirstOrDefault(f => f.Id == row.Model.FolderId)?.Role == FolderRole.Junk;
            row.Picture = await _pictures.ForSenderAsync(row.Model.FromAddress.Trim().ToLowerInvariant(), own, mayLookUp: !inJunk);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Loading a sender picture failed");
        }
    }

    private async Task ExpandAsync(MessageItemViewModel head)
    {
        if (head.IsExpanded || head.Model.ThreadKey is not { } key) return;
        var conversation = await Task.Run(() => _messages.GetThreadAsync(head.Model.AccountId, key));
        var index = Messages.IndexOf(head);
        if (index < 0 || head.IsExpanded) return;

        var folderNames = _foldersByAccount.GetValueOrDefault(head.Model.AccountId) ?? [];
        var now = DateTime.Now;
        var at = index + 1;
        foreach (var m in conversation.Where(m => m.Id != head.Model.Id))
        {
            var hint = m.FolderId == head.Model.FolderId ? null : folderNames.FirstOrDefault(f => f.Id == m.FolderId)?.Name;
            Messages.Insert(at++, NewRow(m, 1, now, head, hint));
        }
        head.IsExpanded = true;
    }

    private void CollapseThread(MessageItemViewModel head)
    {
        if (!head.IsExpanded) return;
        if (SelectedMessage?.Parent == head) SelectedMessage = head;
        foreach (var child in Messages.Where(m => m.Parent == head).ToList()) Messages.Remove(child);
        head.IsExpanded = false;
    }

    private async Task SetFlagsAsync(MessageItemViewModel item, bool isRead, bool isFlagged)
    {
        item.IsRead = isRead;
        item.IsFlagged = isFlagged;
        try
        {
            await _engine.SetFlagsAsync(item.Model, isRead, isFlagged);
            await RefreshCountsAsync();
        }
        catch (Exception ex) { _log.LogError(ex, "Updating flags failed"); }
    }

    private async Task RefreshCountsAsync()
    {
        var counts = await Task.Run(() => _messages.GetCountsAsync());
        var showIfUnreadChanged = false;
        foreach (var node in SidebarNodes)
        {
            var count = node.Kind switch
            {
                NodeKind.UnifiedInbox => counts.UnreadInboxes,
                NodeKind.Unread => counts.UnreadTotal,
                NodeKind.Flagged => counts.Flagged,
                // Like Drafts: how many wait (shown muted).
                NodeKind.Outbox => _foldersByAccount.Values.SelectMany(f => f).Where(f => f.Role == FolderRole.Outbox)
                    .Sum(f => counts.TotalByFolder.GetValueOrDefault(f.Id)),
                // Like eM Client: Drafts counts what's waiting in it (shown muted, not as unread); Sent has no badge.
                NodeKind.Folder when node.Role is FolderRole.Drafts or FolderRole.Outbox => counts.TotalByFolder.GetValueOrDefault(node.FolderId),
                NodeKind.Folder when node.Role == FolderRole.Sent => 0,
                NodeKind.Folder => counts.UnreadByFolder.GetValueOrDefault(node.FolderId),
                _ => 0,
            };
            if (node.ShowOnlyIfUnread && (node.Count > 0) != (count > 0)) showIfUnreadChanged = true;
            node.Count = count;
            if (node is { IsGroup: true, Parent: null, AccountId: not 0 })
                node.HasUnread = counts.UnreadByAccount.GetValueOrDefault(node.AccountId) > 0;
        }
        UpdateFoldedCounts();
        if (showIfUnreadChanged) SidebarView.Refresh();
    }

    /// <summary>
    /// A folded "More" shows the unread mail of the folders in it, so new mail a rule filed there doesn't go unseen;
    /// unfolded, each folder shows its own.
    /// </summary>
    private void UpdateFoldedCounts()
    {
        foreach (var more in SidebarNodes.Where(n => n.IsGroup && n.Parent is not null))
            more.Count = more.IsExpanded ? 0 : SidebarNodes.Where(n => n.Parent == more).Sum(n => n.Count);
    }

    private void UpdateStatus()
    {
        var statuses = _engine.Statuses;
        // Syncing shows on the Refresh button itself (its arrows turn); "Updated 21:04" is in its tooltip. Worked
        // out first, so an account's error never leaves the arrows turning.
        IsSyncing = statuses.Values.Any(s => s.State == SyncState.Syncing);
        RefreshToolTip = IsSyncing ? L.T("Refresh (F5), syncing…")
            : statuses.Values.Select(s => s.LastSync).Where(t => t.HasValue).Min() is { } last ? L.F("Refresh (F5), updated {0:t}", last.LocalDateTime)
            : L.T("Refresh (F5)");
        // An account that needs signing in again goes first: the line then offers the fix.
        var error = statuses.Where(s => s.Value.State == SyncState.Error).OrderBy(s => !s.Value.NeedsSignIn).FirstOrDefault();
        if (error.Value is not null)
        {
            var email = _accountsById.GetValueOrDefault(error.Key)?.Email ?? L.T("Account");
            StatusText = $"{email}: {error.Value.Error}";
            StatusIsError = true;
            SignInNeededFor = error.Value.NeedsSignIn ? error.Key : null;
            return;
        }
        StatusIsError = false;
        StatusText = "";
        SignInNeededFor = null;
    }

    /// <summary>The account the status line says needs signing in again; its "Sign in again…" link is shown next to it.</summary>
    [ObservableProperty] private long? _signInNeededFor;

    [RelayCommand]
    private async Task SignInFromStatus()
    {
        if (SignInNeededFor is { } id && _accountsById.TryGetValue(id, out var account))
            await _dialogs.SignInAgainAsync(account);
    }

    /// <summary>Any account is syncing: the Refresh button's arrows turn.</summary>
    [ObservableProperty] private bool _isSyncing;
    [ObservableProperty] private string _refreshToolTip = L.T("Refresh (F5)");

    /// <summary>
    /// Delete, Archive, Junk: like a folder move, a closed conversation goes as a whole (its messages in this folder);
    /// an open one, or a single row of it, just that message.
    /// </summary>
    private Task MoveSelectedToRoleAsync(FolderRole role) => MoveToRoleAsync(Targets(), role, wholeConversations: true);

    /// <param name="wholeConversations">A closed conversation's row stands for all of it (the list); false: just that message (a message window).</param>
    /// <returns>Whether anything was moved.</returns>
    private async Task<bool> MoveToRoleAsync(List<MessageItemViewModel> items, FolderRole role, bool wholeConversations)
    {
        if (items.Count == 0) return false;
        var index = items.Min(Messages.IndexOf);
        var messages = wholeConversations ? await MessagesOfAsync(items) : items.Select(i => i.Model).ToList();
        var plan = messages.Select(m =>
        {
            var folders = _foldersByAccount.GetValueOrDefault(m.AccountId) ?? [];
            // Gmail has no Archive folder: archiving there means "All Mail".
            var target = folders.FirstOrDefault(f => f.Role == role)
                         ?? (role == FolderRole.Archive ? folders.FirstOrDefault(f => f.Role == FolderRole.All) : null);
            var forever = role == FolderRole.Trash && (target is null || target.Id == m.FolderId);
            // Not sent yet: deleting it from the Outbox means not sending it (there's no server copy to bin).
            var unsent = folders.FirstOrDefault(f => f.Id == m.FolderId)?.Role == FolderRole.Outbox;
            return (Message: m, Target: unsent ? null : target, Forever: (forever || unsent) && role == FolderRole.Trash);
        }).ToList();

        var unsentCount = plan.Count(p => p.Forever && p.Target is null && IsInOutbox(p.Message));
        var forever = plan.Count(p => p.Forever) - unsentCount;
        if (unsentCount > 0 && forever == 0)
        {
            var text = unsentCount == 1 ? L.T("It's deleted from the Outbox and won't be sent.")
                : L.F("These {0} messages are deleted from the Outbox and won't be sent.", unsentCount);
            if (!_dialogs.Confirm(unsentCount == 1 ? L.T("Don't send this message?") : L.T("Don't send these messages?"), text, L.T("Delete"), DialogKind.Danger))
                return false;
        }
        else if (forever > 0)
        {
            var text = plan.Count == 1
                ? L.T("This message is already in Trash. Deleting it removes it from the server for good.")
                : forever == plan.Count
                    ? L.F("These {0} messages are already in Trash. Deleting them removes them from the server for good.", forever)
                    : L.F("{0} of these messages are already in Trash. Deleting them removes them from the server for good.", forever);
            if (!_dialogs.Confirm(L.T("Delete permanently?"), text, L.T("Delete"), DialogKind.Danger)) return false;
        }

        var handled = new HashSet<long>();
        try
        {
            foreach (var (message, target, deleteForever) in plan)
            {
                if (deleteForever)
                {
                    await _engine.DeleteForeverAsync(message);
                    handled.Add(message.Id);
                }
                else if (target is not null && await _engine.MoveAsync(message, target))
                {
                    handled.Add(message.Id);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Moving messages failed");
        }

        var done = items.Where(i => handled.Contains(i.Model.Id)).ToList();
        if (done.Count == 0) return false;
        RemoveRows(done, index);
        await RefreshCountsAsync();
        return true;
    }

    // ───────── a message window's actions (on its own message, wherever the list is) ─────────

    /// <summary>The list's row for a message, if it's listed (a window may hold an older row object).</summary>
    private MessageItemViewModel? RowOf(MessageItemViewModel item) => Messages.FirstOrDefault(m => m.Model.Id == item.Model.Id);

    public async Task UpdateFlagsAsync(MessageItemViewModel item, bool? isRead = null, bool? isFlagged = null)
    {
        var read = isRead ?? item.IsRead;
        var flagged = isFlagged ?? item.IsFlagged;
        if (RowOf(item) is { } row && row != item) (row.IsRead, row.IsFlagged) = (read, flagged);
        if (item.Model.Id == SelectedMessage?.Model.Id && !read) _readCts?.Cancel();
        await SetFlagsAsync(item, read, flagged);
    }

    /// <summary>Moves just this message to Trash (or deletes it for good from Trash); true when done.</summary>
    public Task<bool> DeleteMessageAsync(MessageItemViewModel item) => MoveToRoleAsync([RowOf(item) ?? item], FolderRole.Trash, wholeConversations: false);

    /// <summary>The account a message belongs to.</summary>
    public Account? AccountOf(MessageSummary m) => _accountsById.GetValueOrDefault(m.AccountId);

    /// <summary>
    /// The messages behind rows: a closed conversation stands for all of its messages in the row's folder; an open one,
    /// or a row inside it, for just that message. Each message once (a head and a row of its conversation may both be selected).
    /// </summary>
    private async Task<List<MessageSummary>> MessagesOfAsync(IEnumerable<MessageItemViewModel> rows)
    {
        var result = new Dictionary<long, MessageSummary>();
        foreach (var item in rows)
        {
            result.TryAdd(item.Model.Id, item.Model);
            if (item is not { IsThread: true, IsExpanded: false, Model.ThreadKey: { } key }) continue;
            var conversation = await Task.Run(() => _messages.GetThreadAsync(item.Model.AccountId, key));
            foreach (var m in conversation.Where(m => m.FolderId == item.Model.FolderId)) result.TryAdd(m.Id, m);
        }
        return result.Values.ToList();
    }

    /// <summary>Removes rows (and the opened conversation under a head) and selects the row that took their place.</summary>
    private void RemoveRows(IReadOnlyCollection<MessageItemViewModel> rows, int index)
    {
        // A message window deleting a row you aren't reading leaves your selection alone.
        var selectionGone = SelectedMessage is { } selected && (rows.Contains(selected) || (selected.Parent is { } p && rows.Contains(p)));
        foreach (var row in rows.ToList())
        {
            foreach (var child in Messages.Where(m => m.Parent == row).ToList()) Messages.Remove(child);
            Messages.Remove(row);
        }
        if (selectionGone || (SelectedMessage is null && index >= 0))
            SelectedMessage = Messages.Count == 0 ? null : Messages[Math.Clamp(index, 0, Messages.Count - 1)];
    }

    /// <summary>Drag and drop: can these rows go into that sidebar folder? (Same account, a different real folder.)</summary>
    /// <remarks>Nothing goes into the Outbox this way (that's what Send is for), and out of it only to Drafts.</remarks>
    public bool CanMoveTo(IReadOnlyCollection<MessageItemViewModel> items, SidebarNode node) =>
        node.Kind == NodeKind.Trash ? items.Any(i => !IsInTrash(i.Model)) // each goes to its own account's Trash
        : node.Kind == NodeKind.Folder && items.Count > 0 && node.Role != FolderRole.Outbox
        && items.All(i => i.Model.AccountId == node.AccountId) && items.Any(i => i.Model.FolderId != node.FolderId)
        && (node.Role == FolderRole.Drafts || !items.Any(i => IsInOutbox(i.Model)));

    private bool IsInOutbox(MessageSummary m) =>
        _foldersByAccount.GetValueOrDefault(m.AccountId)?.FirstOrDefault(f => f.Id == m.FolderId)?.Role == FolderRole.Outbox;

    private bool IsInTrash(MessageSummary m) =>
        _foldersByAccount.GetValueOrDefault(m.AccountId)?.FirstOrDefault(f => f.Id == m.FolderId)?.Role == FolderRole.Trash;

    /// <summary>
    /// Moves dragged rows into a folder. A closed conversation moves as a whole (every message of it in this folder);
    /// an open one, or a single row of it, moves just that message.
    /// </summary>
    public async Task MoveToFolderAsync(IReadOnlyCollection<MessageItemViewModel> items, SidebarNode node)
    {
        if (!CanMoveTo(items, node)) return;
        if (node.Kind == NodeKind.Trash)
        {
            // As Delete does: each message to its own account's Trash (what's already there stays put).
            await MoveToRoleAsync(items.Where(i => !IsInTrash(i.Model)).ToList(), FolderRole.Trash, wholeConversations: true);
            return;
        }
        var target = _foldersByAccount.GetValueOrDefault(node.AccountId)?.FirstOrDefault(f => f.Id == node.FolderId);
        if (target is not null) await MoveToFolderAsync(items, target);
    }

    private async Task MoveToFolderAsync(IReadOnlyCollection<MessageItemViewModel> items, Folder target)
    {
        var rows = items.Where(i => i.Model.AccountId == target.AccountId && i.Model.FolderId != target.Id).ToList();
        if (rows.Count == 0) return;
        var toMove = await MessagesOfAsync(rows);

        var index = rows.Min(Messages.IndexOf);
        try
        {
            foreach (var m in toMove) await _engine.MoveAsync(m, target);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Moving messages failed");
            return;
        }
        RemoveRows(rows, index);
        await RefreshCountsAsync();
    }

    /// <summary>
    /// The account a new message is written from: the one whose folder is open; in the shared views (All Inboxes,
    /// Unread, Flagged) always the default account, whichever account's message happens to be selected there.
    /// </summary>
    private Account? DefaultAccount() =>
        (SelectedNode is { AccountId: > 0 } n ? _accountsById.GetValueOrDefault(n.AccountId) : null)
        ?? _accountsById.Values.FirstOrDefault(a => a.IsDefault)
        ?? AccountList.FirstOrDefault();

    /// <summary>The account a new mail (Ctrl+N, the New button) is written from: the one picked in the sidebar, else the default.</summary>
    public Account? ComposeAccount => DefaultAccount();

    private async Task OpenReplyAsync(ComposeMode mode)
    {
        if (SelectedMessage is not { } item || _accountsById.GetValueOrDefault(item.Model.AccountId) is not { } account) return;
        if (await LoadMimeAsync(item) is not { } mime) return;
        // The quote shows its web pictures while you write only if you let them show while reading.
        var pictures = Reader.ShownId == item.Model.Id ? Reader.PicturesAllowed : _trusted.IsTrusted(item.Model.FromAddress);
        _dialogs.Compose(Composing.Build(mode, mime, account, _renderer) with { ShowRemotePictures = pictures }, account);
    }

    /// <summary>The full message, downloading it first if needed (menu actions can run before the pane has loaded it).</summary>
    private async Task<MimeMessage?> LoadMimeAsync(MessageItemViewModel item)
    {
        if (Reader.CurrentMime is { } shown && Reader.ShownId == item.Model.Id) return shown;
        if (!await _engine.EnsureBodyAsync(item.Model))
        {
            _dialogs.Info(L.T("Not downloaded yet"), L.T("This message couldn't be downloaded right now. Try again once you're online."));
            return null;
        }
        await using var stream = _bodies.Open(item.Model.Id);
        return stream is null ? null : await MimeMessage.LoadAsync(stream);
    }

    private async Task<Stream?> OpenRawAsync(MessageItemViewModel item)
    {
        if (!await _engine.EnsureBodyAsync(item.Model))
        {
            _dialogs.Info(L.T("Not downloaded yet"), L.T("This message couldn't be downloaded right now. Try again once you're online."));
            return null;
        }
        return _bodies.Open(item.Model.Id);
    }

    // ───────────────────────── commands ─────────────────────────

    [RelayCommand]
    private async Task ToggleThread(MessageItemViewModel? item)
    {
        var head = (item ?? SelectedMessage) is { } m ? m.Parent ?? m : null;
        if (head is not { IsThread: true }) return;
        if (head.IsExpanded) CollapseThread(head);
        else await ExpandAsync(head);
    }

    [RelayCommand]
    private async Task ExpandThread()
    {
        if (SelectedMessage is { IsThread: true, IsExpanded: false } head) await ExpandAsync(head);
    }

    [RelayCommand]
    private void CollapseSelectedThread()
    {
        if (SelectedMessage is { } m && (m.Parent ?? m) is { IsExpanded: true } head) CollapseThread(head);
    }

    [RelayCommand]
    private void ToggleNode(SidebarNode node)
    {
        node.IsExpanded = !node.IsExpanded;
        UpdateFoldedCounts();
        SidebarView.Refresh();
    }

    // Row buttons pass their own item; toolbar buttons and shortcuts pass nothing and act on the selection.
    private bool HasTarget(MessageItemViewModel? item) => (item ?? SelectedMessage) is not null;

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private async Task ToggleRead(MessageItemViewModel? item)
    {
        if (item is not null)
        {
            await SetFlagsAsync(item, !item.IsRead, item.IsFlagged);
            return;
        }
        // Several rows: anything unread -> mark them all read; all read -> mark them all unread.
        var targets = Targets();
        var read = targets.Any(m => !m.IsRead);
        if (!read) _readCts?.Cancel();
        foreach (var m in targets.Where(m => m.IsRead != read)) await SetFlagsAsync(m, read, m.IsFlagged);
    }

    [RelayCommand(CanExecute = nameof(HasTarget))]
    private async Task ToggleFlag(MessageItemViewModel? item)
    {
        if (item is not null)
        {
            await SetFlagsAsync(item, item.IsRead, !item.IsFlagged);
            return;
        }
        var targets = Targets();
        var flag = targets.Any(m => !m.IsFlagged);
        foreach (var m in targets.Where(m => m.IsFlagged != flag)) await SetFlagsAsync(m, m.IsRead, flag);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Delete() => MoveSelectedToRoleAsync(FolderRole.Trash);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task Archive() => MoveSelectedToRoleAsync(FolderRole.Archive);

    [RelayCommand]
    private async Task Refresh()
    {
        IsSyncing = true; // turn right away; the accounts' status takes over once the sync starts
        _engine.RequestSync(userInitiated: true);
        // Nothing started (no accounts, demo, offline): settle on the real state instead of turning forever.
        _ = Task.Delay(1500).ContinueWith(_ => _ui.Post(_ => UpdateStatus(), null), TaskScheduler.Default);
        await ReloadMessagesAsync(preserveSelection: true);
    }

    [RelayCommand]
    private void New()
    {
        if (DefaultAccount() is { } account) _dialogs.Compose(Composing.New(account), account);
        else if (_dialogs.ShowAddAccount()) _ = LoadAsync();
    }

    /// <summary>A new mail from a chosen account (the New menu's Mail ▸ list).</summary>
    [RelayCommand]
    private void NewFrom(Account account) => _dialogs.Compose(Composing.New(account), account);

    [RelayCommand(CanExecute = nameof(HasSingleSelection))] private Task Reply() => OpenReplyAsync(ComposeMode.Reply);
    [RelayCommand(CanExecute = nameof(HasSingleSelection))] private Task ReplyAll() => OpenReplyAsync(ComposeMode.ReplyAll);
    [RelayCommand(CanExecute = nameof(HasSingleSelection))] private Task Forward() => OpenReplyAsync(ComposeMode.Forward);
    [RelayCommand(CanExecute = nameof(HasSingleSelection))] private Task ForwardAsAttachment() => OpenReplyAsync(ComposeMode.ForwardAsAttachment);
    [RelayCommand(CanExecute = nameof(HasSingleSelection))] private Task ForwardAsOriginal() => OpenReplyAsync(ComposeMode.ForwardAsOriginal);
    [RelayCommand(CanExecute = nameof(HasSingleSelection))] private Task Resend() => OpenReplyAsync(ComposeMode.Resend);

    /// <summary>Accounts in sidebar order, for per-account menu items ("Receive … only").</summary>
    public IReadOnlyList<Account> AccountList => _accountsById.Values.OrderBy(a => a.SortOrder).ThenBy(a => a.Id).ToList();

    [RelayCommand]
    private async Task RefreshAccount(Account account)
    {
        _engine.RequestSync(account.Id, userInitiated: true);
        await ReloadMessagesAsync(preserveSelection: true);
    }

    /// <summary>Like eM Client's "Show Operations": what each account's sync is doing, live, and what's waiting for its server.</summary>
    [RelayCommand]
    private void ShowOperations() => _dialogs.ShowOperations(AccountList, DescribeOps);

    private static string DescribeOps(IReadOnlyList<PendingOp> ops)
    {
        var parts = ops.GroupBy(o => o.Kind).OrderBy(g => g.Key).Select(g => (g.Key, N: g.Count()) switch
        {
            (PendingOpKind.Move, var n) => n == 1 ? L.T("1 move") : L.F("{0} moves", n),
            (PendingOpKind.Delete, var n) => n == 1 ? L.T("1 deletion") : L.F("{0} deletions", n),
            (PendingOpKind.Copy, var n) => n == 1 ? L.T("1 copy") : L.F("{0} copies", n),
            (PendingOpKind.Append, var n) => n == 1 ? L.T("1 sent message to file in Sent") : L.F("{0} sent messages to file in Sent", n),
            (PendingOpKind.SaveDraft, var n) => n == 1 ? L.T("1 draft to upload") : L.F("{0} drafts to upload", n),
            (PendingOpKind.DeleteDraft, var n) => n == 1 ? L.T("1 draft to remove") : L.F("{0} drafts to remove", n),
            (_, var n) => n == 1 ? L.T("1 read/flag change") : L.F("{0} read/flag changes", n),
        });
        var retrying = ops.Count(o => o.Attempts > 0);
        return string.Join(", ", parts) + (retrying > 0 ? L.F(" ({0} being retried)", retrying) : "");
    }

    // ───────── message menu ─────────

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private async Task OpenMessage()
    {
        if (SelectedMessage is not { } item || _accountsById.GetValueOrDefault(item.Model.AccountId) is not { } account) return;
        if (await LoadMimeAsync(item) is not { } mime) return;
        // Waiting in the Outbox: opening it takes it out (into Drafts, so closing the window doesn't lose it) to edit.
        if (IsInOutbox(item.Model)
            && _foldersByAccount.GetValueOrDefault(account.Id)?.FirstOrDefault(f => f.Role == FolderRole.Drafts) is { } drafts)
        {
            if (!await _engine.MoveAsync(item.Model, drafts))
            {
                _dialogs.Info(L.T("Being sent"), L.T("This message is going out right now, so it can't be changed any more."));
                return;
            }
            await RefreshCountsAsync();
            await ReloadMessagesAsync(preserveSelection: false);
            _dialogs.Compose(Composing.FromDraft(mime, account, _renderer), account);
            return;
        }
        if (IsDraft(item.Model))
        {
            _dialogs.Compose(Composing.FromDraft(mime, account, _renderer), account); // continue writing it
            return;
        }
        var pictures = _trusted.IsTrusted(item.Model.FromAddress) || (Reader.AllowRemoteContent && Reader.ShownId == item.Model.Id);
        _dialogs.OpenMessage(item, mime, account, pictures);
        // Popout only: the window is where it's read (with a reading pane, the pane counts it as read).
        if (ReadingLayout == "popout" && !item.IsRead && _settings.Current.MarkReadAfterSeconds >= 0)
            await SetFlagsAsync(item, isRead: true, item.IsFlagged);
    }

    /// <summary>Opens any stored message in its own window (from a contact's conversation list).</summary>
    public async Task OpenMessageWindowAsync(long messageId)
    {
        if (await _messages.GetAsync(messageId) is not { } m || _accountsById.GetValueOrDefault(m.AccountId) is not { } account) return;
        var item = NewRow(m, 1, DateTime.Now);
        if (await LoadMimeAsync(item) is not { } mime) return;
        _dialogs.OpenMessage(item, mime, account, _trusted.IsTrusted(m.FromAddress));
    }

    /// <summary>A new message to someone (from a contact card).</summary>
    public void ComposeTo(string to)
    {
        if (DefaultAccount() is { } account) _dialogs.Compose(Composing.New(account) with { To = to }, account);
    }

    private bool IsDraft(MessageSummary m) =>
        _foldersByAccount.GetValueOrDefault(m.AccountId)?.FirstOrDefault(f => f.Id == m.FolderId)?.Role == FolderRole.Drafts;

    private bool CanMarkRead() => Targets().Any(m => !m.IsRead);
    private bool CanMarkUnread() => Targets().Any(m => m.IsRead);

    [RelayCommand(CanExecute = nameof(CanMarkRead))]
    private async Task MarkRead()
    {
        foreach (var m in Targets().Where(m => !m.IsRead)) await SetFlagsAsync(m, true, m.IsFlagged);
    }

    [RelayCommand(CanExecute = nameof(CanMarkUnread))]
    private async Task MarkUnread()
    {
        _readCts?.Cancel(); // otherwise the "read after 1.5 s" timer would undo this straight away
        foreach (var m in Targets().Where(m => m.IsRead)) await SetFlagsAsync(m, false, m.IsFlagged);
    }

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private async Task SaveAs()
    {
        if (SelectedMessage is not { } item) return;
        await using var raw = await OpenRawAsync(item);
        if (raw is not null) _dialogs.SaveMessage(raw, item.Model.Subject);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task MoveToFolder()
    {
        var items = Targets();
        if (!OneAccount(items, copy: false)) return;
        var heading = items.Count == 1 ? L.T("Move to folder") : L.F("Move {0} messages to folder", items.Count);
        var target = _dialogs.PickFolder(heading, L.T("Move"), FolderChoices(items), AccountTitle);
        if (target is not null) await MoveToFolderAsync(items, target);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task CopyToFolder()
    {
        var items = Targets();
        if (!OneAccount(items, copy: true)) return;
        var heading = items.Count == 1 ? L.T("Copy to folder") : L.F("Copy {0} messages to folder", items.Count);
        var target = _dialogs.PickFolder(heading, L.T("Copy"), FolderChoices(items), AccountTitle);
        if (target is null) return;
        var notCopied = 0;
        foreach (var item in items.Where(i => i.Model.FolderId != target.Id))
            if (!await _engine.CopyAsync(item.Model, target)) notCopied++;
        if (notCopied > 0)
            _dialogs.Info(L.T("Not copied yet"), notCopied == 1 && items.Count == 1
                ? L.T("This message is still being moved on the server. Try again in a moment.")
                : L.F("{0} of these messages are still being moved on the server. Try those again in a moment.", notCopied));
    }

    /// <summary>Folders belong to one account, so a move or copy can't mix messages from several.</summary>
    private bool OneAccount(List<MessageItemViewModel> items, bool copy)
    {
        if (items.Count == 0) return false;
        if (items.Select(i => i.Model.AccountId).Distinct().Count() == 1) return true;
        _dialogs.Info(L.T("Messages from several accounts"), copy
            ? L.T("Messages can only be copied to a folder of their own account. Select messages from one account at a time.")
            : L.T("Messages can only be moved to a folder of their own account. Select messages from one account at a time."));
        return false;
    }

    /// <summary>The account's folders, leaving out the one every selected message is already in.</summary>
    private IEnumerable<Folder> FolderChoices(List<MessageItemViewModel> items) =>
        (_foldersByAccount.GetValueOrDefault(items[0].Model.AccountId) ?? [])
        .Where(f => !f.IsVirtual && f.Role != FolderRole.Outbox && items.Any(i => i.Model.FolderId != f.Id))
        .OrderBy(f => f.Role == FolderRole.Other).ThenBy(f => Array.IndexOf(RoleOrder, f.Role)).ThenBy(f => f.Name);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task MoveToJunk() => MoveSelectedToRoleAsync(FolderRole.Junk);

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task NotJunk() => MoveSelectedToRoleAsync(FolderRole.Inbox);

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private void Print() => PrintRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DownloadOffline()
    {
        var items = Targets();
        var saved = 0;
        foreach (var item in items)
            if (await _engine.EnsureBodyAsync(item.Model)) saved++;
        if (saved == 0) return;
        _dialogs.Info(L.T("Available offline"), items.Count == 1
            ? L.T("This message is saved on this computer and opens without a connection.")
            : L.F("{0} of {1} messages are saved on this computer and open without a connection.", saved, items.Count));
    }

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private async Task ViewSource()
    {
        if (SelectedMessage is not { } item) return;
        await using var raw = await OpenRawAsync(item);
        if (raw is null) return;
        using var reader = new StreamReader(raw);
        _dialogs.ShowText(L.F("Source – {0}", item.Subject), await reader.ReadToEndAsync());
    }

    [RelayCommand(CanExecute = nameof(HasSingleSelection))]
    private async Task ViewHeaders()
    {
        if (SelectedMessage is not { } item) return;
        await using var raw = await OpenRawAsync(item);
        if (raw is null) return;
        using var reader = new StreamReader(raw);
        var text = await reader.ReadToEndAsync();
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var i and >= 0 ? i : text.IndexOf("\n\n", StringComparison.Ordinal);
        _dialogs.ShowText(L.F("Header – {0}", item.Subject), end >= 0 ? text[..end] : text);
    }

    // ───────── reading pane menu ─────────

    /// <summary>Properties…: who, when, where and how big, for the shown message.</summary>
    [RelayCommand]
    private void MessageProperties()
    {
        if (SelectedMessage is not { } item || Reader.CurrentMime is not { } mime) return;
        // One person per line, as "Name <address>".
        static string People(MimeKit.InternetAddressList list) =>
            string.Join(Environment.NewLine, list.Mailboxes.Select(m => Addresses.Format(m.Name ?? "", m.Address)));
        PropertiesDialog.Show(null, L.T("Message properties"), item.Subject,
        [
            (L.T("From"), People(mime.From)),
            (L.T("To"), People(mime.To)),
            (L.T("Cc"), People(mime.Cc)),
            (L.T("Reply to"), People(mime.ReplyTo)),
            (L.T("Date"), item.FullDate),
            (L.T("Account"), _accountsById.GetValueOrDefault(item.Model.AccountId)?.Email ?? ""),
            (L.T("Folder"), SelectedFolderName ?? ""),
            (L.T("Size"), FormatBytes(item.Model.Size)),
            (L.T("Attachments"), string.Join(Environment.NewLine, Reader.Attachments.Select(a => $"{a.FileName}  ({a.SizeText})"))),
            ("Message-ID", mime.MessageId ?? ""),
        ]);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB",
    };

    [RelayCommand]
    private async Task AddAccount()
    {
        if (_dialogs.ShowAddAccount()) await LoadAsync();
    }

    [RelayCommand]
    private async Task OpenRules()
    {
        if (_dialogs.ShowRules()) await ReloadAfterRulesAsync();
    }

    /// <summary>
    /// "Create Rule" from a message: the common rules in one click (pick a folder where needed), or the editor
    /// filled in from the message. Afterwards, offers to apply the new rule to mail already in the inbox.
    /// </summary>
    [RelayCommand]
    private async Task CreateRule(string kind)
    {
        if (SelectedMessage?.Model is not { } m) return;
        // Several messages picked: one rule for all their senders (or subjects), a condition listing each.
        var senders = TargetSenders();
        if (senders.Count == 0) senders = [m];
        var who = ListNames(senders.Select(NameOf).ToList());
        var fromSender = new RuleCondition { Field = RuleField.From, Operator = RuleOperator.Is, Values = senders.Select(x => x.FromAddress.Trim()).ToList() };
        var subjects = TargetSubjects();
        if (subjects.Count == 0) subjects = [RuleMatcher.CleanSubject(m.Subject)];
        var subject = subjects.Count == 1 ? subjects[0] : L.F("{0} subjects", subjects.Count);

        if (kind == "custom")
        {
            var draft = new MailRule { Name = L.F("Mail from {0}", who), AccountId = m.AccountId, Conditions = [fromSender] };
            if (_dialogs.ShowRules(draft)) await ReloadAfterRulesAsync();
            return;
        }

        MailRule rule;
        if (kind is "move-sender" or "move-subject")
        {
            var folders = (_foldersByAccount.GetValueOrDefault(m.AccountId) ?? []).Where(f => !f.IsVirtual && f.Role != FolderRole.Outbox);
            var heading = kind == "move-sender" ? L.F("Move mail from {0} to", who)
                : subjects.Count == 1 ? L.F("Move mail about “{0}” to", subject) : L.T("Move mail with these subjects to");
            if (_dialogs.PickFolder(heading, L.T("Create rule"), folders, AccountTitle) is not { } folder) return;
            rule = new MailRule
            {
                Name = kind == "move-sender" ? $"{who} > {folder.Name}" : subjects.Count == 1 ? $"“{subject}” > {folder.Name}" : $"{subject} > {folder.Name}",
                AccountId = m.AccountId,
                Conditions = [kind == "move-sender" ? fromSender : new RuleCondition { Field = RuleField.Subject, Values = subjects }],
                Actions = [new RuleAction { Kind = RuleActionKind.MoveTo, FolderAccountId = folder.AccountId, FolderPath = folder.Path }],
            };
        }
        else if (kind == "read-sender")
        {
            rule = new MailRule { Name = L.F("Mark {0} as read", who), Conditions = [fromSender], Actions = [new RuleAction { Kind = RuleActionKind.MarkRead }] };
        }
        else
        {
            var addresses = ListNames(senders.Select(x => x.FromAddress.Trim()).ToList());
            if (!_dialogs.Confirm(L.F("Delete all mail from {0}?", who), L.F("New mail from {0} will go straight to Trash.", addresses), L.T("Create rule"), DialogKind.Danger)) return;
            rule = new MailRule { Name = L.F("Delete mail from {0}", who), Conditions = [fromSender], Actions = [new RuleAction { Kind = RuleActionKind.Delete }] };
        }

        _settings.Save(_settings.Current with { Rules = [.. _settings.Current.Rules, rule] });
        var existing = await _engine.PreviewRuleAsync(rule);
        if (existing.Count > 0 && _dialogs.Confirm(L.F("Rule “{0}” created", rule.Name),
                existing.Count == 1 ? L.T("1 message already in your inbox matches. Apply the rule to it now?") : L.F("{0} messages already in your inbox match. Apply the rule to them now?", existing.Count),
                L.T("Apply now")))
        {
            foreach (var inbox in _foldersByAccount.Where(kv => rule.AccountId is null || kv.Key == rule.AccountId)
                         .Select(kv => kv.Value.FirstOrDefault(f => f.Role == FolderRole.Inbox)).OfType<Folder>())
                await _engine.RunRulesAsync([rule], inbox);
            await ReloadAfterRulesAsync();
        }
        StatusText = L.F("Rule “{0}” created", rule.Name);
    }

    /// <summary>An account's sidebar name (for folder pickers that list several accounts).</summary>
    private string AccountTitle(long accountId) => _accountsById.GetValueOrDefault(accountId)?.SidebarTitle ?? "";

    /// <summary>"Create Rule for their mail" from a contact card: the rule editor, filled in with the sender.</summary>
    public async Task CreateRuleForSenderAsync(string name, string address)
    {
        var draft = new MailRule
        {
            Name = L.F("Mail from {0}", name.Contains('@') ? address : name),
            Conditions = [new RuleCondition { Field = RuleField.From, Operator = RuleOperator.Is, Values = [address] }],
        };
        if (_dialogs.ShowRules(draft)) await ReloadAfterRulesAsync();
    }

    private async Task ReloadAfterRulesAsync()
    {
        await RefreshCountsAsync();
        await ReloadMessagesAsync(preserveSelection: true);
    }

    [RelayCommand]
    private void OpenSettings()
    {
        if (!_dialogs.ShowSettings()) return;
        // Sender pictures switched on or off: rows on screen look again (off keeps only your own account pictures).
        foreach (var row in Messages) row.ForgetPicture();
    }

    [RelayCommand]
    private async Task OpenAccounts()
    {
        _dialogs.ShowAccounts();
        await LoadAsync();
        // An account picture may have changed: refresh it on your own mail.
        foreach (var row in Messages.Where(r => _accountsById.Values.Any(a => a.Identities.Any(i =>
                     i.Address.Equals(r.Model.FromAddress, StringComparison.OrdinalIgnoreCase)))))
            row.ForgetPicture();
    }

    [RelayCommand]
    private void SyncAccount(SidebarNode? node) => _engine.RequestSync(node?.AccountId is > 0 ? node.AccountId : null, userInitiated: true);

    [RelayCommand]
    private async Task SignInAgain(SidebarNode? node)
    {
        if (node is { AccountId: > 0 } && _accountsById.TryGetValue(node.AccountId, out var account))
            await _dialogs.SignInAgainAsync(account);
    }

    [RelayCommand]
    private async Task RemoveAccount(SidebarNode? node)
    {
        if (node is not { AccountId: > 0 }) return;
        if (!_dialogs.Confirm(L.F("Remove {0}?", node.Title), L.T("Plicbox deletes its local copy and the saved sign-in. Nothing is deleted on the server."),
                L.T("Remove"), DialogKind.Danger))
            return;
        await _engine.RemoveAccountAsync(node.AccountId);
        SelectedMessage = null;
        await LoadAsync();
    }

    // ───────── the empty list ─────────

    [ObservableProperty] private string _listEmptyGlyph = "";
    [ObservableProperty] private string _listEmptyTitle = "";
    [ObservableProperty] private string _listEmptyText = "";
    [ObservableProperty] private string? _listEmptyAction;
    [ObservableProperty] private string _listEmptyActionGlyph = "";
    [ObservableProperty] private System.Windows.Input.ICommand? _listEmptyCommand;

    [RelayCommand] private void ClearSearch() => SearchText = "";

    /// <summary>A search that found nothing in part of the mail: the same words in all of it, Trash and Junk too.</summary>
    [RelayCommand]
    private void SearchAllMail()
    {
        SearchIncludesTrash = true;
        SearchArea = SearchArea.All;
    }

    /// <summary>
    /// What the list says when it has nothing to show (shown only then): a search that found nothing, with a way to
    /// look further or start over, or which kind of folder is empty.
    /// </summary>
    private void UpdateListEmptyState()
    {
        ListEmptyAction = null;
        ListEmptyCommand = null;
        var query = SearchText.Trim();
        if (query.Length > 0)
        {
            ListEmptyGlyph = "";
            ListEmptyTitle = L.F("No messages match “{0}”", query);
            if (IsSearchNarrowed)
            {
                // Only part of the mail was searched: offer the rest before giving up.
                ListEmptyText = EffectiveSearchArea switch
                {
                    SearchArea.Folder => L.F("Only {0} was searched.", SearchFolder!.Name),
                    SearchArea.Account => L.F("Only {0} was searched.", SearchAccount!.SidebarTitle),
                    _ => L.T("Trash and Junk weren't searched."),
                };
                (ListEmptyAction, ListEmptyActionGlyph, ListEmptyCommand) = (L.T("Search all mail"), "", SearchAllMailCommand);
            }
            else
            {
                ListEmptyText = L.T("Check the spelling, or try a name, an address or a word from the subject.");
                (ListEmptyAction, ListEmptyActionGlyph, ListEmptyCommand) = (L.T("Clear search"), "", ClearSearchCommand);
            }
            return;
        }

        var role = FolderOf(SelectedNode)?.Role;
        (ListEmptyGlyph, ListEmptyTitle, ListEmptyText) = (SelectedNode?.Kind, role) switch
        {
            (NodeKind.Unread, _) => ("", L.T("No unread mail"), L.T("You're all caught up.")),
            (NodeKind.Flagged, _) => ("", L.T("Nothing flagged"), L.T("Flag a message to keep it here until it's done.")),
            (NodeKind.Outbox, _) or (_, FolderRole.Outbox) => ("", L.T("Nothing waiting to be sent"), ""),
            (NodeKind.Trash, _) or (_, FolderRole.Trash) => ("", L.T("Trash is empty"), ""),
            (_, FolderRole.Junk) => ("", L.T("No junk"), L.T("Mail that looks like spam lands here.")),
            (_, FolderRole.Drafts) => ("", L.T("No drafts"), L.T("Messages you start and save for later are kept here.")),
            (_, FolderRole.Sent) => ("", L.T("Nothing sent yet"), ""),
            (NodeKind.UnifiedInbox, _) or (_, FolderRole.Inbox) => ("", L.T("Your inbox is empty"), L.T("You're all caught up.")),
            _ => ("", L.T("This folder is empty"), ""),
        };
    }

    // ───────── folder menu (right-click a folder in the sidebar) ─────────

    public Folder? FolderOf(SidebarNode? node) => node is { Kind: NodeKind.Folder }
        ? _foldersByAccount.GetValueOrDefault(node.AccountId)?.FirstOrDefault(f => f.Id == node.FolderId)
        : null;

    public FolderPrefs FolderPrefsOf(Folder folder) => PrefsOf(folder);

    /// <summary>Standard folders (Inbox, Sent, Trash, …) can't be renamed, moved or deleted, as in eM Client.</summary>
    public static bool IsCustom(Folder folder) => folder.Role == FolderRole.Other && !folder.IsVirtual;

    /// <summary>The account's hidden folders, for "Show hidden folder" on the account menu.</summary>
    public IReadOnlyList<Folder> HiddenFolders(long accountId) =>
        SidebarOrder(_foldersByAccount.GetValueOrDefault(accountId) ?? []).Where(f => PrefsOf(f).Visibility == FolderVisibility.Hide).ToList();

    /// <summary>New Folder… on a folder creates a subfolder; on an account (or its "More"), a top-level folder.</summary>
    [RelayCommand]
    private async Task NewFolder(SidebarNode? node)
    {
        if (node is not { AccountId: > 0 }) return;
        var parent = FolderOf(node);
        var where = parent is null ? (_accountsById.GetValueOrDefault(node.AccountId)?.SidebarTitle ?? L.T("this account")) : $"“{parent.Name}”";
        if (_dialogs.AskText(L.T("New folder"), L.F("Name of the new folder in {0}:", where), L.T("Create")) is not { } name) return;
        await RunFolderActionAsync(L.T("Creating the folder"), () => _engine.CreateFolderAsync(node.AccountId, parent?.Path, name));
    }

    [RelayCommand]
    private async Task RenameFolder(SidebarNode? node)
    {
        if (FolderOf(node) is not { } folder || !IsCustom(folder)) return;
        var current = folder.Name.Split('/').Last();
        if (_dialogs.AskText(L.T("Rename folder"), L.F("New name for “{0}”:", current), L.T("Rename"), current) is not { } name || name == current) return;
        var newPath = await RunFolderActionAsync(L.T("Renaming the folder"), () => _engine.RenameFolderAsync(folder, name), folder);
        if (newPath is not null) await MovePrefsAsync(folder, newPath);
    }

    [RelayCommand]
    private async Task MoveFolder(SidebarNode? node)
    {
        if (FolderOf(node) is not { } folder || !IsCustom(folder)) return;
        static bool Inside(string path, string root) =>
            path.Length > root.Length && path.StartsWith(root, StringComparison.Ordinal) && !char.IsLetterOrDigit(path[root.Length]);
        var choices = (_foldersByAccount.GetValueOrDefault(folder.AccountId) ?? [])
            .Where(f => f.Id != folder.Id && !f.IsVirtual && f.Role != FolderRole.Outbox && !Inside(f.Path, folder.Path));
        var top = new Folder { AccountId = folder.AccountId, Path = "", Name = L.T("(Top level)"), Role = FolderRole.Other };
        if (_dialogs.PickFolder(L.F("Move “{0}” into", folder.Name), L.T("Move"), SidebarOrder(choices).Prepend(top)) is not { } target) return;
        var newPath = await RunFolderActionAsync(L.T("Moving the folder"),
            () => _engine.MoveFolderAsync(folder, target.Path.Length == 0 ? null : target.Path), folder);
        if (newPath is not null) await MovePrefsAsync(folder, newPath);
    }

    [RelayCommand]
    private async Task DeleteFolder(SidebarNode? node)
    {
        if (FolderOf(node) is not { } folder || !IsCustom(folder)) return;
        if (!_dialogs.Confirm(L.F("Delete “{0}”?", folder.Name),
                L.T("The folder, its subfolders and every message in them are deleted from the server. This can't be undone."),
                L.T("Delete"), DialogKind.Danger))
            return;
        if (SelectedNode?.FolderId == folder.Id) SelectedNode = SidebarNodes.First(n => n.Kind == NodeKind.UnifiedInbox);
        await RunFolderActionAsync(L.T("Deleting the folder"), async () => { await _engine.DeleteFolderAsync(folder); return ""; }, folder);
    }

    /// <summary>Empty Trash / Empty the junk folder.</summary>
    [RelayCommand]
    private async Task EmptyFolder(SidebarNode? node)
    {
        if (FolderOf(node) is not { Role: FolderRole.Trash or FolderRole.Junk } folder) return;
        var trash = folder.Role == FolderRole.Trash;
        if (!_dialogs.Confirm(trash ? L.T("Empty Trash?") : L.T("Empty the junk folder?"),
                L.F("Everything in {0} is deleted from the server for good.", folder.Name), L.T("Empty"), DialogKind.Danger))
            return;
        if (await RunFolderActionAsync(trash ? L.T("Emptying Trash") : L.T("Emptying the junk folder"), async () => { await _engine.EmptyFolderAsync(folder); return ""; }, folder) is null) return;
        await RefreshAfterFolderChangeAsync(folder);
    }

    /// <summary>
    /// Favorites > Trash > Empty All Trash: every account's Trash, after one clear question. An account that fails
    /// says so; the others are still emptied.
    /// </summary>
    [RelayCommand]
    private async Task EmptyAllTrash()
    {
        var trashes = _foldersByAccount.Values.SelectMany(f => f).Where(f => f.Role == FolderRole.Trash && !f.IsVirtual).ToList();
        if (trashes.Count == 0) return;
        var text = trashes.Count == 1
            ? L.F("Everything in {0} is deleted from the server for good.", trashes[0].Name)
            : L.F("Everything in Trash in all {0} accounts is deleted from the servers for good.", trashes.Count);
        if (!_dialogs.Confirm(L.T("Empty all Trash?"), text, L.T("Empty all"), DialogKind.Danger)) return;
        foreach (var folder in trashes)
        {
            var account = _accountsById.GetValueOrDefault(folder.AccountId)?.SidebarTitle ?? "";
            await RunFolderActionAsync(L.F("Emptying Trash of {0}", account), async () => { await _engine.EmptyFolderAsync(folder); return ""; }, folder);
        }
        await RefreshCountsAsync();
        if (SelectedNode is { Kind: not NodeKind.Group } && SearchText.Length == 0) await ReloadMessagesAsync(preserveSelection: true);
    }

    [RelayCommand]
    private async Task MarkFolderRead(SidebarNode? node)
    {
        if (FolderOf(node) is not { } folder) return;
        if (await RunFolderActionAsync(L.T("Marking messages as read"), async () => { await _engine.MarkFolderReadAsync(folder); return ""; }, folder) is null) return;
        await RefreshAfterFolderChangeAsync(folder);
    }

    [RelayCommand]
    private void CollapseAll() => SetAllExpanded(false);

    [RelayCommand]
    private void ExpandAll() => SetAllExpanded(true);

    private void SetAllExpanded(bool expanded)
    {
        foreach (var group in SidebarNodes.Where(n => n.IsGroup)) group.IsExpanded = expanded;
        UpdateFoldedCounts();
        SidebarView.Refresh();
    }

    public Task SetFolderVisibilityAsync(Folder folder, FolderVisibility visibility)
    {
        if (visibility == FolderVisibility.Hide && SelectedNode?.FolderId == folder.Id)
            SelectedNode = SidebarNodes.First(n => n.Kind == NodeKind.UnifiedInbox);
        return UpdatePrefsAsync(folder, p => p with { Visibility = visibility });
    }

    public Task ToggleFavoriteAsync(Folder folder) => UpdatePrefsAsync(folder, p => p with { Favorite = !p.Favorite });

    public Task SetFolderColorAsync(Folder folder, string? hex) => UpdatePrefsAsync(folder, p => p with { Color = hex });

    [RelayCommand]
    private async Task FolderProperties(SidebarNode? node)
    {
        if (FolderOf(node) is not { } folder) return;
        var counts = await Task.Run(() => _messages.GetCountsAsync());
        var account = _accountsById.GetValueOrDefault(folder.AccountId);
        var kind = folder.Role == FolderRole.Other ? L.T("Folder") : L.F("Special folder ({0})", folder.Name);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        PropertiesDialog.Show(null, L.T("Folder properties"), folder.Name,
        [
            (L.T("Account"), account?.Email ?? ""),
            (L.T("Location on the server"), folder.Path),
            (L.T("Type"), kind + (folder.IsVirtual ? L.T(" (a view of mail kept in other folders)") : "")),
            (L.T("Messages on this computer"), counts.TotalByFolder.GetValueOrDefault(folder.Id).ToString("N0", culture)),
            (L.T("Unread"), counts.UnreadByFolder.GetValueOrDefault(folder.Id).ToString("N0", culture)),
            (L.T("Synced"), folder.IsSynced ? L.T("always") : L.T("only while open")),
        ]);
    }

    /// <summary>Folders with an action running on the server: their sidebar rows show a spinner.</summary>
    private readonly HashSet<long> _busyFolders = [];

    private void SetFolderBusy(long folderId, bool busy)
    {
        if (busy) _busyFolders.Add(folderId);
        else _busyFolders.Remove(folderId);
        foreach (var node in SidebarNodes.Where(n => n.Kind == NodeKind.Folder && n.FolderId == folderId)) node.IsBusy = busy;
    }

    /// <summary>Runs a server-side folder change; shows the error and returns null when it fails.</summary>
    /// <param name="folder">The folder it works on: its sidebar row shows a spinner meanwhile.</param>
    private async Task<string?> RunFolderActionAsync(string doing, Func<Task<string>> action, Folder? folder = null)
    {
        StatusText = $"{doing}…";
        StatusIsError = false;
        if (folder is not null) SetFolderBusy(folder.Id, true);
        try
        {
            return await action();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "{Doing} failed", doing);
            _dialogs.Error(L.F("{0} didn't work: {1}", doing, ex.Message));
            return null;
        }
        finally
        {
            if (folder is not null) SetFolderBusy(folder.Id, false);
            UpdateStatus();
        }
    }

    private async Task RefreshAfterFolderChangeAsync(Folder folder)
    {
        await RefreshCountsAsync();
        if (SelectedNode is { Kind: not NodeKind.Group } && SearchText.Length == 0) await ReloadMessagesAsync(preserveSelection: true);
    }

    private async Task UpdatePrefsAsync(Folder folder, Func<FolderPrefs, FolderPrefs> change)
    {
        var all = new Dictionary<string, FolderPrefs>(_settings.Current.Folders);
        var key = FolderPrefs.Key(folder.AccountId, folder.Path);
        var updated = change(all.GetValueOrDefault(key) ?? new FolderPrefs());
        if (updated.IsDefault) all.Remove(key);
        else all[key] = updated;
        _settings.Save(_settings.Current with { Folders = all });
        await LoadAsync();
    }

    /// <summary>A renamed or moved folder (and its subfolders) keeps its colour, favourite and visibility.</summary>
    private Task MovePrefsAsync(Folder folder, string newPath)
    {
        var prefix = FolderPrefs.Key(folder.AccountId, folder.Path);
        bool Affected(string key) => key == prefix || (key.StartsWith(prefix, StringComparison.Ordinal) && !char.IsLetterOrDigit(key[prefix.Length]));
        var all = _settings.Current.Folders;
        if (!all.Keys.Any(Affected)) return Task.CompletedTask;
        var moved = all.ToDictionary(
            kv => Affected(kv.Key) ? FolderPrefs.Key(folder.AccountId, newPath) + kv.Key[prefix.Length..] : kv.Key,
            kv => kv.Value);
        _settings.Save(_settings.Current with { Folders = moved });
        return Task.CompletedTask;
    }

    // Snooze and Quick Actions land in Phase 2.
    private static bool NotYet() => false;
    [RelayCommand(CanExecute = nameof(NotYet))] private void Snooze() { }
    [RelayCommand(CanExecute = nameof(NotYet))] private void QuickActions() { }
}

/// <summary>A Gmail label shown on the open message (Important stands out in yellow).</summary>
public sealed record LabelChip(string Name, string Raw, bool IsImportant)
{
    /// <summary>Gmail's system labels get their usual names; Starred is the flag, shown already.</summary>
    public static LabelChip? From(string raw) => raw switch
    {
        "\\Starred" => null,
        "\\Important" => new LabelChip(L.T("Important"), raw, true),
        "\\Inbox" => new LabelChip(L.T("Inbox"), raw, false),
        "\\Sent" => new LabelChip(L.T("Sent"), raw, false),
        "\\Draft" => new LabelChip(L.T("Drafts"), raw, false),
        "\\Spam" => new LabelChip(L.T("Junk Email"), raw, false),
        "\\Trash" => new LabelChip(L.T("Trash"), raw, false),
        _ when raw.StartsWith('\\') => null,
        _ => new LabelChip(raw, raw, false),
    };
}
