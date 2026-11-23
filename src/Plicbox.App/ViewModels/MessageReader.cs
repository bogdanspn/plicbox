using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plicbox.App.Services;
using Plicbox.Core;
using Plicbox.Mail;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace Plicbox.App.ViewModels;

/// <summary>What a shown message offers beyond itself: the people in it (menus) and blocking its sender.</summary>
public interface IReaderHost
{
    void ComposeTo(string formattedAddress);
    void NewMeeting(string name, string address);
    void ShowContact(string address);
    void ShowAllMail(string address);
    void CreateRule(string name, string address);
    void BlockSender(MessageSummary message);
}

/// <summary>
/// One message on screen: its body (with the privacy rules), the sender warning, notices (tracking, pictures,
/// unsubscribe), Gmail labels and attachments. The reading pane and every message window each have one, so both show
/// exactly the same things.
/// </summary>
public sealed partial class MessageReader(
    IBodyStore bodies, MailEngine engine, MessageRenderer renderer, ITrustedSenders trusted, IMessageRepository messages,
    IAccountRepository accounts, IFolderRepository folders, Dialogs dialogs, ISettingsStore settings, Plicbox.Dav.CalendarEngine calendar,
    PeopleData people, ILogger<MessageReader> log) : ObservableObject
{
    /// <summary>The main window, for the people menus and blocking (set once at start).</summary>
    public static IReaderHost? Host { get; set; }

    // Senders unsubscribed from this session, shared by every reader.
    private static readonly HashSet<string> Unsubscribed = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private Account? _account;
    private bool _inJunk;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection))] private MessageItemViewModel? _selectedMessage;
    public bool HasSelection => SelectedMessage is not null;
    /// <summary>The header's folder chip: the folder, and its account when the list mixes accounts ("Trash, you@work.example").</summary>
    [ObservableProperty] private string? _selectedFolderName;
    /// <summary>The message's own folder name (Gmail labels with the same name aren't shown twice).</summary>
    private string? _folderName;
    /// <summary>Shown from a view that mixes accounts (All Inboxes, Trash…): the folder chip names the account too.</summary>
    public bool ShowAccount { get; set; }

    public ObservableCollection<AttachmentItem> Attachments { get; } = [];
    /// <summary>Gmail: the message's other labels (Important, Inbox, …), as chips in the header.</summary>
    public ObservableCollection<LabelChip> Labels { get; } = [];

    [ObservableProperty] private string? _bodyHtml;
    [ObservableProperty] private bool _hasRemoteContent;
    /// <summary>"SendGrid email tracking" etc. when tracking pixels were removed from the message.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasBlockedTracking), nameof(BlockedTrackingText))] private string? _blockedTracking;
    public bool HasBlockedTracking => BlockedTracking is not null;
    public string BlockedTrackingText => BlockedTracking is { } who ? L.F("{0} was blocked to preserve privacy.", who) : "";

    /// <summary>Settings → Mail → Reading: the notices as one row of chips (read for each message shown).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FullNotices), nameof(ShowWarningBanner), nameof(ShowWarningChip))] private bool _compactNotices = true;
    public bool FullNotices => !CompactNotices;
    // The sender warning: a banner with the reasons, or (compact) a chip leading the notices row, reasons on hover.
    public bool ShowWarningBanner => HasSenderWarning && !CompactNotices;
    public bool ShowWarningChip => HasSenderWarning && CompactNotices;

    /// <summary>The message's way to unsubscribe, when it offers one and isn't junk or suspicious.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUnsubscribe))] private UnsubscribeOption? _unsubscribe;
    /// <summary>Shown instead of the link once you've unsubscribed from this sender.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanUnsubscribe))] private string? _unsubscribeDone;
    public bool CanUnsubscribe => Unsubscribe is not null && UnsubscribeDone is null;

    /// <summary>Signs the sender may be forged (failed SPF/DKIM/DMARC, look-alike domain, …).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSenderWarning), nameof(SenderWarningIsDanger), nameof(SenderWarningTitle), nameof(SenderWarningText),
        nameof(ShowWarningBanner), nameof(ShowWarningChip))]
    private SenderVerdict? _senderWarning;
    public bool HasSenderWarning => SenderWarning is { Risk: not SenderRisk.None };
    public bool SenderWarningIsDanger => SenderWarning?.Risk == SenderRisk.Danger;
    public string SenderWarningTitle => SenderWarning?.Title
        ?? (SenderWarningIsDanger ? L.T("This message may not be from who it says it is") : L.T("Be careful with this message"));
    public string SenderWarningText => SenderWarning is { } v ? string.Join(" ", v.Reasons) : ""; // sentences, on one line

    /// <summary>"first message from this address", after the sender's address in the header (null when it isn't).</summary>
    [ObservableProperty] private string? _firstContactNote;

    // Who you correspond with, rebuilt when the people data or your addresses change (shared by every reader).
    private static (int Version, string Own, KnownCorrespondents Known)? _known;

    private bool _watching;

    /// <summary>
    /// Settings → Mail → Reading changed: the message on screen follows at once. Held weakly, so a closed message
    /// window's reader can go.
    /// </summary>
    private void WatchSettings()
    {
        if (_watching) return;
        _watching = true;
        var me = new WeakReference<MessageReader>(this);
        var ui = System.Windows.Application.Current?.Dispatcher;
        Action<AppSettings>? handler = null;
        handler = s =>
        {
            if (!me.TryGetTarget(out var reader)) { settings.Changed -= handler; return; }
            ui?.BeginInvoke(() => reader.CompactNotices = s.CompactNotices);
        };
        settings.Changed += handler;
        // Settings → Appearance → Message text (also its live preview): the message on screen shows again in it.
        Action? textChanged = null;
        textChanged = () =>
        {
            if (!me.TryGetTarget(out var reader)) { Appearance.MessageTextChanged -= textChanged; return; }
            ui?.BeginInvoke(() => { if (reader.ShownId is not null) _ = reader.ReloadAsync(); });
        };
        Appearance.MessageTextChanged += textChanged;
    }

    /// <summary>The loaded message (null while downloading).</summary>
    public MimeMessage? CurrentMime { get; private set; }
    /// <summary>The message whose body is shown (or being loaded).</summary>
    public long? ShownId { get; private set; }

    // For this message only; reset when another one is shown.
    public bool AllowRemoteContent { get; set; }
    public bool TrackingAllowed { get; set; }
    /// <summary>Whether the message's web pictures are shown (allowed for it, or its sender is trusted).</summary>
    public bool PicturesAllowed => SelectedMessage is { } m && (AllowRemoteContent || trusted.IsTrusted(m.Model.FromAddress));
    /// <summary>Format › Plain Text.</summary>
    public bool ShowingPlainText { get; private set; }

    /// <summary>The header's Reply button.</summary>
    public Action? ReplyAction { get; set; }
    public Action? ReplyAllAction { get; set; }
    public Action? ForwardAction { get; set; }

    /// <summary>Replying to all would reach more than the sender (others in To/Cc besides you): the header offers it too.</summary>
    [ObservableProperty] private bool _canReplyAll;
    /// <summary>A short note for the status line ("Saved 3 attachments…").</summary>
    public event Action<string>? Status;

    /// <summary>Shows a message (another one: pictures, tracking and plain text back to their defaults).</summary>
    /// <param name="allowRemoteContent">Pictures already allowed for it (the reading pane's choice, carried into a window).</param>
    public Task ShowAsync(MessageItemViewModel? item, bool allowRemoteContent = false)
    {
        AllowRemoteContent = allowRemoteContent;
        TrackingAllowed = ShowingPlainText = false;
        return LoadAsync(item);
    }

    /// <summary>The same message as a fresh row object (the list was rebuilt): only the header follows.</summary>
    public void Rebind(MessageItemViewModel item)
    {
        if (item.Model.Id == ShownId) SelectedMessage = item;
    }

    private Task ReloadAsync() => LoadAsync(SelectedMessage);

    private async Task LoadAsync(MessageItemViewModel? item)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        SelectedMessage = item;
        HasRemoteContent = false;
        BlockedTracking = null;
        SenderWarning = null;
        FirstContactNote = null;
        Unsubscribe = null;
        UnsubscribeDone = null;
        Labels.Clear();
        Attachments.Clear();
        CurrentMime = null;
        ShownId = item?.Model.Id;
        CompactNotices = settings.Current.CompactNotices;
        WatchSettings();
        CanReplyAll = false;
        if (item is null) { BodyHtml = null; SelectedFolderName = _folderName = null; return; }

        var clock = MainViewModel.PerfTrace ? System.Diagnostics.Stopwatch.StartNew() : null;
        try
        {
            // SQLite's "async" calls run synchronously: off the UI thread, like everything below that only computes.
            var (account, folder) = await Task.Run(async () => (await accounts.GetAsync(item.Model.AccountId, cts.Token),
                (await folders.GetByAccountAsync(item.Model.AccountId, cts.Token)).FirstOrDefault(f => f.Id == item.Model.FolderId)), cts.Token);
            if (cts.IsCancellationRequested) return;
            _account = account;
            _folderName = folder?.Name;
            SelectedFolderName = ShowAccount && folder is not null && account is not null ? $"{folder.Name} / {account.SidebarTitle}" : folder?.Name;
            _inJunk = folder?.Role == FolderRole.Junk;

            var stream = bodies.Open(item.Model.Id);
            if (stream is null)
            {
                BodyHtml = MessageRenderer.Placeholder(L.T("Downloading message…"), busy: true);
                if (!await engine.EnsureBodyAsync(item.Model, cts.Token))
                {
                    if (!cts.IsCancellationRequested)
                        BodyHtml = MessageRenderer.Placeholder(L.T("This message isn't available right now. It will download on the next sync."));
                    return;
                }
                stream = bodies.Open(item.Model.Id);
                if (stream is null || cts.IsCancellationRequested) { stream?.Dispose(); return; }
            }

            var allowRemote = AllowRemoteContent || trusted.IsTrusted(item.Model.FromAddress);
            var allowTracking = TrackingAllowed;
            var plain = ShowingPlainText;
            var identities = account?.Identities.Select(i => i.Address).ToList();
            var inJunk = _inJunk;
            // Parsing, cleaning the HTML and the checks all read the whole message: done in the background, together.
            var shown = await Task.Run(async () =>
            {
                MimeMessage mime;
                await using (stream) mime = await MimeMessage.LoadAsync(stream, cts.Token);
                var key = RecentRenders.Key.For(item.Model, allowRemote, allowTracking, plain);
                var rendered = RecentRenders.Get(key);
                if (rendered is null)
                {
                    rendered = plain
                        ? new RenderedMessage(MessageRenderer.RenderPlainText(mime), HasRemoteContent: false)
                        : renderer.Render(mime, allowRemote, allowTracking);
                    RecentRenders.Add(key, rendered);
                }
                var warning = SenderCheck.Check(mime, identities);
                // Unsubscribing from spam only confirms the address is real: offered for ordinary mail only.
                var unsubscribe = warning is { Risk: not SenderRisk.None } || inJunk ? null : UnsubscribeOption.From(mime);
                var attachments = mime.Attachments.OfType<MimePart>()
                    .Select(part => new AttachmentItem(part.FileName ?? L.T("attachment"), FormatSize(part), part)).ToList();
                var mine = identities?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
                var from = mime.From.Mailboxes.Select(m => m.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var others = mime.To.Mailboxes.Concat(mime.Cc.Mailboxes)
                    .Count(m => !mine.Contains(m.Address) && !from.Contains(m.Address));
                return (Mime: mime, Rendered: rendered, Warning: warning, Unsubscribe: unsubscribe, Attachments: attachments, OthersToReply: others);
            }, CancellationToken.None); // always runs, so the file is always closed; a newer message just ignores the result
            if (cts.IsCancellationRequested) return;

            CurrentMime = shown.Mime;
            CanReplyAll = shown.OthersToReply > 0;
            BodyHtml = shown.Rendered.Html;
            HasRemoteContent = shown.Rendered.HasRemoteContent && !allowRemote;
            BlockedTracking = shown.Rendered.BlockedTracking;
            SenderWarning = shown.Warning;
            Unsubscribe = shown.Unsubscribe;
            if (Unsubscribe is not null && Unsubscribed.Contains(item.Model.FromAddress)) UnsubscribeDone = L.T("You've asked to stop receiving these messages.");
            _ = LoadLabelsAsync(item, cts.Token);
            if (folder?.Role is not (FolderRole.Sent or FolderRole.Drafts or FolderRole.Outbox)) _ = CheckFamiliarityAsync(item, cts.Token);
            foreach (var attachment in shown.Attachments) Attachments.Add(attachment);
            if (clock is not null)
                log.LogInformation("PERF   message {Id} ready to show: {Ms} ms ({Kb} KB of HTML)", item.Model.Id, clock.ElapsedMilliseconds, shown.Rendered.Html.Length / 1024);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.LogError(ex, "Showing message {Id} failed", item.Model.Id);
            BodyHtml = MessageRenderer.Placeholder(L.T("This message couldn't be displayed."));
        }
    }

    /// <summary>
    /// Is the sender someone you know, an impostor of someone you know, or new? Worked out after the message is on
    /// screen (the people data may need reading first) and added to the sender warning.
    /// </summary>
    private async Task CheckFamiliarityAsync(MessageItemViewModel item, CancellationToken ct)
    {
        try
        {
            var verdict = await Task.Run(async () =>
            {
                var snapshot = await people.GetAsync(ct);
                var own = (await accounts.GetAllAsync(ct)).SelectMany(a => a.Identities).ToList();
                var ownKey = string.Join("|", own.Select(o => $"{o.Name}<{o.Address}>"));
                var known = _known is { } k && k.Version == snapshot.Version && k.Own == ownKey ? k.Known
                    : (_known = (snapshot.Version, ownKey, new KnownCorrespondents(snapshot.Contacts, snapshot.Stats, own))).Value.Known;
                return known.Check(item.Model.FromAddress, item.Model.FromName, item.Model.Date);
            }, ct);
            if (ct.IsCancellationRequested || ShownId != item.Model.Id) return;

            FirstContactNote = verdict.FirstMessage ? L.T("first message from this address") : null;
            if (verdict.Risk == SenderRisk.None) return;
            var current = SenderWarning;
            SenderWarning = new SenderVerdict(
                current is null || verdict.Risk > current.Risk ? verdict.Risk : current.Risk,
                [.. current?.Reasons ?? [], .. verdict.Reasons],
                current?.Title);
            Unsubscribe = null; // unsubscribing only tells an impostor the address is read
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Checking whether the sender of message {Id} is known failed", item.Model.Id);
        }
    }

    // ───────── notices ─────────

    [RelayCommand]
    private Task AllowTracking()
    {
        // For this message only, like eM Client's "Allow email tracking".
        TrackingAllowed = true;
        return ReloadAsync();
    }

    [RelayCommand]
    private Task AlwaysShowFromSender()
    {
        if (SelectedMessage is not { } item || string.IsNullOrWhiteSpace(item.Model.FromAddress)) return Task.CompletedTask;
        trusted.Trust(item.Model.FromAddress);
        return ReloadAsync();
    }

    [RelayCommand]
    private Task ShowRemoteContent()
    {
        AllowRemoteContent = true; // this message only
        return ReloadAsync();
    }

    [RelayCommand]
    private Task ShowAsPlainText(bool plain)
    {
        if (ShowingPlainText == plain) return Task.CompletedTask;
        ShowingPlainText = plain;
        return ReloadAsync();
    }

    [RelayCommand] private void Reply() => ReplyAction?.Invoke();
    [RelayCommand] private void ReplyAll() => ReplyAllAction?.Invoke();
    [RelayCommand] private void Forward() => ForwardAction?.Invoke();

    [RelayCommand]
    private async Task UnsubscribeFromSender()
    {
        if (SelectedMessage is not { } item || Unsubscribe is not { } way) return;
        // Like eM Client: the message's own unsubscribe link, opened in the browser; the header's address otherwise.
        var how = way.Page is not null ? L.T("Their unsubscribe page opens in your browser.") : L.T("Plicbox sends them an unsubscribe email from your account.");
        if (!dialogs.Confirm(L.F("Unsubscribe from {0}?", item.Sender), L.T("You'll stop getting these messages, though it can take a few days.") + " " + how, L.T("Unsubscribe"))) return;
        try
        {
            if (way.Page is { } page)
            {
                MailWebView.OpenLink(page.AbsoluteUri);
                UnsubscribeDone = L.T("Finish on the page that opened in your browser.");
            }
            else if (_account is { } account && way.BuildMail(new MailboxAddress(account.DisplayName, account.Email)) is { } mail)
            {
                await engine.SendAsync(account, mail, sendAt: DateTimeOffset.Now); // nothing to undo
                UnsubscribeDone = L.T("Unsubscribe email sent.");
            }
            else
            {
                dialogs.Error(L.T("The sender's unsubscribe address didn't work. Try the unsubscribe link inside the message."));
                return;
            }
            Unsubscribed.Add(item.Model.FromAddress);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Unsubscribing failed");
            dialogs.Error(L.F("Couldn't unsubscribe: {0}. Try the unsubscribe link inside the message.", ex.Message));
        }
    }

    // ───────── Gmail labels ─────────

    /// <summary>Gmail labels besides the folder it's in, fetched live (and the list's Important dot kept right).</summary>
    private async Task LoadLabelsAsync(MessageItemViewModel item, CancellationToken ct)
    {
        if (_account?.Provider != ProviderKind.Gmail) return;
        IReadOnlyList<string> labels;
        try { labels = await engine.GetLabelsAsync(item.Model, ct); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { log.LogDebug(ex, "Couldn't read labels"); return; }
        if (ct.IsCancellationRequested || SelectedMessage?.Model.Id != item.Model.Id) return;
        var important = labels.Contains("\\Important");
        if (item.IsImportant != important)
        {
            item.IsImportant = important;
            await messages.SetImportantAsync(item.Model.Id, important, CancellationToken.None);
        }
        var here = _folderName;
        foreach (var chip in labels.Select(LabelChip.From).OfType<LabelChip>()
                     .Where(c => !string.Equals(c.Name, here, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(c => c.IsImportant).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            Labels.Add(chip);
    }

    [RelayCommand]
    private async Task RemoveLabel(LabelChip chip)
    {
        if (SelectedMessage is not { } item) return;
        try
        {
            await engine.RemoveLabelAsync(item.Model, chip.Raw);
            Labels.Remove(chip);
            if (chip.IsImportant)
            {
                item.IsImportant = false;
                await messages.SetImportantAsync(item.Model.Id, false);
            }
        }
        catch (Exception ex)
        {
            dialogs.Error(L.F("Couldn't remove the label: {0}", ex.Message));
        }
    }

    // ───────── attachments ─────────

    [RelayCommand]
    private void OpenAttachment(AttachmentItem item)
    {
        try { dialogs.OpenAttachment(item.Part); }
        catch (Exception ex) { dialogs.Error(L.F("Couldn't open {0}: {1}", item.FileName, ex.Message)); }
    }

    /// <summary>An invitation or event file: its events go into a calendar (the account's own, or one on this computer).</summary>
    [RelayCommand]
    private async Task AddToCalendar(AttachmentItem item)
    {
        try
        {
            using var content = new System.IO.MemoryStream();
            if (item.Part.Content is null) return;
            await item.Part.Content.DecodeToAsync(content);
            var charset = item.Part.ContentType.CharsetEncoding ?? System.Text.Encoding.UTF8;
            var events = Plicbox.Dav.IcsFormat.Read(charset.GetString(content.ToArray()));
            if (events.Count == 0)
            {
                dialogs.Info(L.T("Nothing to add"), L.T("That file has no events in it."));
                return;
            }
            var target = await calendar.CalendarForInvitationAsync(_account?.Id);
            var first = events[0].Event;
            var when = first.IsAllDay ? first.StartDate.ToString("D", System.Globalization.CultureInfo.CurrentCulture)
                : first.Start.LocalDateTime.ToString("f", System.Globalization.CultureInfo.CurrentCulture);
            var what = events.Count == 1 ? L.F("“{0}” ({1}) goes into “{2}”.", first.Title, when, target.Name)
                : L.F("{0} events go into “{1}”.", events.Count, target.Name);
            if (!dialogs.Confirm(L.T("Add to calendar?"), what, L.T("Add"))) return;
            await calendar.ImportAsync(target.Id, events);
            Status?.Invoke(L.F("Added to “{0}”", target.Name));
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Adding an attachment to the calendar failed");
            dialogs.Error(L.F("Couldn't add it to the calendar: {0}", ex.Message));
        }
    }

    [RelayCommand]
    private void SaveAttachment(AttachmentItem item)
    {
        try { dialogs.SaveAttachment(item.Part); }
        catch (Exception ex) { dialogs.Error(L.F("Couldn't save {0}: {1}", item.FileName, ex.Message)); }
    }

    [RelayCommand]
    private void SaveAttachments()
    {
        if (Attachments.Count == 0) return;
        try
        {
            if (dialogs.SaveAttachments(Attachments.Select(a => a.Part).ToList()) is { } folder)
                Status?.Invoke(Attachments.Count == 1 ? L.F("Saved 1 attachment to {0}", folder) : L.F("Saved {0} attachments to {1}", Attachments.Count, folder));
        }
        catch (Exception ex)
        {
            dialogs.Error(L.F("Saving the attachments didn't work: {0}", ex.Message));
        }
    }

    private static string FormatSize(MimePart part)
    {
        // Encoded size overstates base64 content by about a third; good enough for a hint.
        if (part.Content?.Stream is not { CanSeek: true } s) return "";
        var bytes = part.ContentTransferEncoding == ContentEncoding.Base64 ? s.Length * 3 / 4 : s.Length;
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
            _ => $"{bytes / (1024.0 * 1024):0.0} MB",
        };
    }
}
