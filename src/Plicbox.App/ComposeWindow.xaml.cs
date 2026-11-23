using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Plicbox.Core;
using Plicbox.Mail;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using MimeKit;

namespace Plicbox.App;

public partial class ComposeWindow : Window
{
    // The editor page is served from memory: NavigateToString is capped at ~2 MB, which a few pictures exceed.
    private const string PageUri = "https://compose.plicbox.invalid/";
    private const long MaxPictureBytes = 10 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private ComposeDraft _draft;
    private readonly MailEngine _engine;
    private readonly IAccountRepository _accounts;
    private readonly ObservableCollection<AttachmentEntry> _attachments = [];
    private readonly DispatcherTimer _autosave = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly TaskCompletionSource _editorReady = new();
    private string _page = "";
    private bool _sent, _sending, _saving, _closing, _closeConfirmed;
    /// <summary>Account whose Drafts folder holds a copy of this message; null while it isn't saved anywhere.</summary>
    private long? _draftAccountId;
    /// <summary>The saved copy is one the user asked for (continued from Drafts, or saved by hand), not just an autosave.</summary>
    private bool _keepDraft;
    /// <summary>Everything the user can change, as of the last save (or as first shown): closing asks only if it differs.</summary>
    private string? _savedSnapshot;
    private Identity? _shownIdentity;

    /// <param name="Warning">Why it may not get through, or not open at the other end (null when nothing to say).</param>
    public sealed record AttachmentEntry(string Name, long Size, string? Path, MimeEntity? Forwarded, OutgoingWarning? Warning)
    {
        public string SizeText => Size > 0 ? FormatSize(Size) : "";
        public bool IsCaution => Warning?.Risk == AttachmentRisk.Caution;
        public bool IsDanger => Warning?.Risk == AttachmentRisk.Danger;
    }

    /// <summary>One choice in the From list: an account's own address or one of its aliases.</summary>
    public sealed record Identity(Account Account, MailAlias Alias)
    {
        public string Label => Alias.ToString();
    }

    private sealed record EditorState(string Html, string Text, bool Plain, string Importance, bool Receipt);

    /// <summary>Message-ID of the saved draft this window is editing (null until saved).</summary>
    public string? DraftMessageId => _draft.MessageId;

    private readonly RecipientSuggestions _toSuggestions;

    /// <summary>Dev screenshots: types a few lines at the top of the message, pressing Enter between them.</summary>
    internal Task TypeLinesAsync(params string[] lines) => Editor.ExecuteScriptAsync(
        "(() => { const e = document.getElementById('editor'); e.focus(); const r = document.createRange(); r.setStart(e, 0); r.collapse(true);" +
        " const s = getSelection(); s.removeAllRanges(); s.addRange(r);" +
        string.Concat(lines.Select((l, i) => (i > 0 ? " document.execCommand('insertParagraph');" : "") + $" document.execCommand('insertText', false, {System.Text.Json.JsonSerializer.Serialize(l)});")) +
        " })()");

    /// <summary>Dev aid (PLICBOX_PICTURE_TEST): a big picture put in, picked, sized to Medium; what a draft and a send carry.</summary>
    internal async Task DevPictureTestAsync(string picture, string dir)
    {
        Topmost = true; // screen captures: nothing else in front
        Activate();
        await RunScriptAsync("insertImage", "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(picture)));
        await Task.Delay(800);
        string Js(string script) => script;
        async Task<string> Eval(string script) => await Editor.ExecuteScriptAsync(script);
        Serilog.Log.Information("PRINTTEST picture inserted: {Info}", await Eval(
            "(() => { const i = document.querySelector('#editor img'); return `${i.naturalWidth}x${i.naturalHeight} shown ${Math.round(i.getBoundingClientRect().width)} width=${i.getAttribute('width')}`; })()"));
        await Eval(Js("(() => { const i = document.querySelector('#editor img'); i.dispatchEvent(new MouseEvent('mousedown', { bubbles: true, cancelable: true })); })()"));
        await Task.Delay(500);
        WindowCapture.SavePng(this, Path.Combine(dir, "picture-picked.png"));
        await Eval("document.querySelector('#picbar button[data-fit=\"0.5\"]').click()");
        await Task.Delay(500);
        WindowCapture.SavePng(this, Path.Combine(dir, "picture-medium.png"));
        var draft = await ReadEditorAsync();
        var sent = await ReadEditorAsync(forSending: true);
        Serilog.Log.Information("PRINTTEST picture after Medium: {Info}; draft html {Draft} chars, sent html {Sent} chars", await Eval(
            "(() => { const i = document.querySelector('#editor img'); return `shown ${Math.round(i.getBoundingClientRect().width)} width=${i.getAttribute('width')}`; })()"),
            draft?.Html.Length, sent?.Html.Length);
        await Eval("document.getElementById('editor').dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))");
        await Task.Delay(300);
        Serilog.Log.Information("PRINTTEST picture after Esc: frame hidden {Hidden}", await Eval("document.getElementById('picframe').hidden"));
        Topmost = false;
    }

    // ── Dev aids for the website's video (--record-site): type like a person, then show sending ──

    internal Task DevEditorReady => _editorReady.Task;

    internal FrameworkElement DevSendButton => SendButton;

    /// <summary>Puts the caret at the very top of the message, above the quoted original.</summary>
    internal Task DevCaretToStartAsync() => Editor.ExecuteScriptAsync(
        "(() => { const e = document.getElementById('editor'); e.focus(); const r = document.createRange(); r.setStart(e, 0); r.collapse(true);" +
        " const s = getSelection(); s.removeAllRanges(); s.addRange(r); })()");

    /// <summary>Types text where the caret is, as the keyboard would.</summary>
    internal Task DevTypeAsync(string text) => Editor.ExecuteScriptAsync(
        $"document.execCommand('insertText', false, {System.Text.Json.JsonSerializer.Serialize(text)})");

    internal Task DevNewParagraphAsync() => Editor.ExecuteScriptAsync("document.execCommand('insertParagraph')");

    /// <summary>What the window shows while a message goes out (the demo has no server to send it to).</summary>
    internal void DevShowSending()
    {
        SendButton.IsEnabled = false;
        StatusText.Text = L.T("Sending…");
        SendingSpinner.Visibility = Visibility.Visible;
    }

    /// <summary>Dev aid: the From line as chosen.</summary>
    internal string? DevFrom => (FromBox.SelectedItem as Identity)?.Label;

    /// <summary>Dev screenshots: types into To and returns the suggestion popup.</summary>
    internal async Task<System.Windows.Controls.Primitives.Popup> PreviewSuggestionsAsync(string text)
    {
        ToBox.Focus();
        ToBox.Text = text;
        ToBox.CaretIndex = text.Length;
        await _toSuggestions.RefreshAsync();
        await Task.Delay(800);
        return _toSuggestions.Popup;
    }

    public ComposeWindow(ComposeDraft draft, Account account, MailEngine engine, IAccountRepository accounts, Services.AddressBook addressBook)
    {
        InitializeComponent();
        // Suggestions from your contacts and everyone you write to, as you type an address.
        _toSuggestions = RecipientSuggestions.Attach(ToBox, addressBook);
        RecipientSuggestions.Attach(CcBox, addressBook);
        RecipientSuggestions.Attach(BccBox, addressBook);
        Icon = Services.WindowIcons.Message; // the plain envelope, as for reading a message
        (_draft, _engine, _accounts) = (draft, engine, accounts);
        Owner = Application.Current.MainWindow;

        ToBox.Text = draft.To;
        CcBox.Text = draft.Cc;
        BccBox.Text = draft.Bcc;
        ReplyToBox.Text = draft.ReplyTo;
        SubjectBox.Text = draft.Subject;
        if (draft.Cc.Length > 0) CcRow.Visibility = Visibility.Visible;
        if (draft.Bcc.Length > 0) CcRow.Visibility = BccRow.Visibility = Visibility.Visible;
        if (draft.ReplyTo.Length > 0) ReplyToRow.Visibility = Visibility.Visible;
        foreach (var entity in draft.ForwardedAttachments)
        {
            var name = entity.ContentDisposition?.FileName ?? (entity as MimePart)?.FileName ?? "attachment";
            _attachments.Add(new AttachmentEntry(name, 0, null, entity, OutgoingAttachments.Check(name, null, null)));
        }
        AttachmentList.ItemsSource = _attachments;
        _attachments.CollectionChanged += (_, _) => ShowAttachmentWarnings();
        ShowAttachmentWarnings();
        if (draft.MessageId is not null)
        {
            _draftAccountId = account.Id; // continuing a message from Drafts
            _keepDraft = true;
        }

        Loaded += async (_, _) => await InitAsync(account);
        Closing += OnClosing;
        PreviewDragOver += OnPreviewDragOver;
        PreviewDrop += OnPreviewDrop;
        _dropHintTimer.Tick += (_, _) => HideDropHint();
        _autosave.Tick += async (_, _) => await SaveDraftAsync(quiet: true);
    }

    private async Task InitAsync(Account account)
    {
        var identities = (await _accounts.GetAllAsync())
            .SelectMany(a => a.Identities.Select(alias => new Identity(a, alias)))
            .ToList();
        FromBox.ItemsSource = identities;
        FromBox.SelectedItem =
            identities.FirstOrDefault(i => i.Account.Id == account.Id && i.Alias.Address.Equals(_draft.FromAddress, StringComparison.OrdinalIgnoreCase))
            ?? identities.FirstOrDefault(i => i.Account.Id == account.Id)
            ?? identities.FirstOrDefault();
        _shownIdentity = FromBox.SelectedItem as Identity;
        FromBox.SelectionChanged += async (_, _) => await OnFromChangedAsync();

        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(App.OwnDir, "WebView2"));
        await Editor.EnsureCoreWebView2Async(env);
        var core = Editor.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsWebMessageEnabled = true; // toolbar buttons that need the window (attach, save, print, ...)
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // no Ctrl+P/Ctrl+F browser UI inside the editor
        // The editor page never navigates (e.g. a link clicked or a file dropped onto it).
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri != PageUri && !e.Uri.StartsWith("about:", StringComparison.Ordinal)) e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.AddWebResourceRequestedFilter(PageUri + "*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            var response = e.Request.Uri == PageUri
                ? core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(_page)), 200, "OK", "Content-Type: text/html; charset=utf-8")
                : core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            e.Response = response;
        };
        core.NavigationCompleted += (_, _) => _editorReady.TrySetResult();
        core.WebMessageReceived += OnWebMessage;

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var state = JsonSerializer.Serialize(new
        {
            importance = _draft.Importance switch { MessageImportance.High => "high", MessageImportance.Low => "low", _ => "normal" },
            receipt = _draft.ReadReceipt,
            plain = _draft.PlainText,
        });
        // The quoted original's web pictures: shown if you allowed them for that message, placeholders otherwise.
        var body = _draft.ShowRemotePictures ? _draft.BodyHtml : Composing.HoldRemotePictures(_draft.BodyHtml);
        _page = EditorTemplate.Value.Replace("{{NONCE}}", nonce).Replace("{{STATE}}", state)
            .Replace("{{REMOTE_IMAGES}}", _draft.ShowRemotePictures ? " https: http:" : "")
            .Replace("{{SCROLLBAR_CSS}}", MessageRenderer.ScrollbarCss).Replace("{{BODY}}", body);
        core.Navigate(PageUri);

        if (ToBox.Text.Length == 0) ToBox.Focus();
        else Editor.Focus();

        await _editorReady.Task;
        if (await ReadEditorAsync() is { } shown) _savedSnapshot = Snapshot(shown);
        _autosave.Start();
    }

    // Ctrl+Enter sends and Ctrl+S saves from anywhere, including inside the editor (WebView2 forwards accelerator keys).
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    Send_Click(this, new RoutedEventArgs());
                    return;
                case Key.S:
                    e.Handled = true;
                    SaveDraft_Click(this, new RoutedEventArgs());
                    return;
                case Key.P:
                    e.Handled = true;
                    _ = PrintAsync();
                    return;
            }
        }
        base.OnPreviewKeyDown(e);
    }

    // ───────────────────────── editor ─────────────────────────

    /// <param name="forSending">Pictures shown smaller than they are go out smaller (a draft keeps them whole).</param>
    private async Task<EditorState?> ReadEditorAsync(bool forSending = false)
    {
        if (Editor.CoreWebView2 is null || !_editorReady.Task.IsCompleted) return null;
        var json = await Editor.ExecuteScriptAsync($"window.plicbox ? window.plicbox.state({(forSending ? "true" : "false")}) : null");
        return JsonSerializer.Deserialize<EditorState>(json, Json);
    }

    private Task RunScriptAsync(string function, params object?[] args)
    {
        if (Editor.CoreWebView2 is null) return Task.CompletedTask;
        var list = string.Join(", ", args.Select(a => JsonSerializer.Serialize(a)));
        return Editor.ExecuteScriptAsync($"window.plicbox.{function}({list})");
    }

    private string Snapshot(EditorState s) => string.Join('\u0001',
        (FromBox.SelectedItem as Identity)?.Account.Id, (FromBox.SelectedItem as Identity)?.Label,
        ToBox.Text, CcBox.Text, BccBox.Text, ReplyToBox.Text, SubjectBox.Text,
        string.Join('|', _attachments.Select(a => a.Path ?? a.Name)),
        s.Plain, s.Plain ? s.Text : s.Html, s.Importance, s.Receipt);

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? type;
        try { type = JsonDocument.Parse(e.WebMessageAsJson).RootElement.GetProperty("type").GetString(); }
        catch (Exception) { return; }
        try
        {
            switch (type)
            {
                case "attach": Attach_Click(this, new RoutedEventArgs()); break;
                case "files": AddFiles(e.AdditionalObjects.OfType<CoreWebView2File>().Select(f => f.Path)); break;
                case "tooBigForText":
                    StatusText.Text = L.F("Pictures over {0} are attached instead of put in the text", $"{MaxPictureBytes / (1024 * 1024)} MB");
                    break;
                case "image": await InsertPictureAsync(); break;
                case "signature": await InsertSignatureAsync(); break;
                case "save": SaveDraft_Click(this, new RoutedEventArgs()); break;
                case "print": await PrintAsync(); break;
                case "replyTo": ShowReplyTo(); break;
                case "plainText": await SwitchToPlainTextAsync(); break;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Compose action {Action} failed", type);
        }
    }

    private async Task InsertPictureAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = L.T("Insert picture"),
            Filter = L.T("Pictures") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp;*.svg|" + L.T("All files") + "|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        var file = new FileInfo(dialog.FileName);
        if (file.Length > MaxPictureBytes)
        {
            MessageDialog.Show(this, L.T("That picture is too big to put in the text"),
                L.F("{0} is {1}. Attach it instead, or use a picture under {2}.", file.Name, FormatSize(file.Length), FormatSize(MaxPictureBytes)));
            return;
        }
        var type = file.Extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "image/png",
        };
        var uri = $"data:{type};base64,{Convert.ToBase64String(await File.ReadAllBytesAsync(file.FullName))}";
        Editor.Focus();
        await RunScriptAsync("insertImage", uri);
    }

    private async Task InsertSignatureAsync()
    {
        if (FromBox.SelectedItem is not Identity id) return;
        var block = Composing.SignatureBlock(id.Account);
        if (block.Length == 0)
        {
            MessageDialog.Show(this, L.T("No signature yet"), L.F("Add a signature for {0} under Accounts, and it can be inserted here.", id.Account.Email));
            return;
        }
        Editor.Focus();
        await RunScriptAsync("setSignature", block, true);
    }

    /// <summary>Switching to another account's address swaps in that account's signature, like eM Client.</summary>
    private async Task OnFromChangedAsync()
    {
        var previous = _shownIdentity;
        _shownIdentity = FromBox.SelectedItem as Identity;
        if (_shownIdentity is null || previous is null || previous.Account.Id == _shownIdentity.Account.Id || !_editorReady.Task.IsCompleted) return;
        await RunScriptAsync("setSignature", Composing.SignatureBlock(_shownIdentity.Account), false);
    }

    private async Task SwitchToPlainTextAsync()
    {
        if (!MessageDialog.Ask(this, L.T("Switch to plain text?"), L.T("Formatting, pictures and tables in the text will be removed."), L.T("Switch"), L.T("Cancel")))
            return;
        await RunScriptAsync("setPlain", true);
    }

    private void ShowReplyTo()
    {
        ReplyToRow.Visibility = Visibility.Visible;
        ReplyToBox.Focus();
    }

    private void RemoveReplyTo_Click(object sender, RoutedEventArgs e)
    {
        ReplyToBox.Text = "";
        ReplyToRow.Visibility = Visibility.Collapsed;
    }

    private async Task PrintAsync()
    {
        if (Editor.CoreWebView2 is null || !_editorReady.Task.IsCompleted) return;
        var from = (FromBox.SelectedItem as Identity)?.Label ?? "";
        var header = new StringBuilder($"<h1>{Enc(SubjectBox.Text)}</h1><div>{Enc(L.F("From: {0}", from))}</div>");
        if (ToBox.Text.Length > 0) header.Append($"<div>{Enc(L.F("To: {0}", ToBox.Text))}</div>");
        if (CcBox.Text.Length > 0) header.Append($"<div>{Enc(L.F("Cc: {0}", CcBox.Text))}</div>");
        await RunScriptAsync("setPrintHeader", header.ToString());
        Editor.CoreWebView2.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
    }

    // ───────────────────────── send, save, discard ─────────────────────────

    private MimeMessage BuildMessage(Identity identity, EditorState s, bool asDraft)
    {
        var draft = _draft with
        {
            FromAddress = identity.Alias.Address,
            ReplyTo = ReplyToBox.Text,
            ForwardedAttachments = _attachments.Where(a => a.Forwarded is not null).Select(a => a.Forwarded!).ToList(),
            Importance = s.Importance switch { "high" => MessageImportance.High, "low" => MessageImportance.Low, _ => MessageImportance.Normal },
            ReadReceipt = s.Receipt,
            PlainText = s.Plain,
            // The sent message gets its own Message-ID; the draft's is only used to find and replace saved copies.
            MessageId = asDraft ? _draft.MessageId : null,
        };
        return Composing.ToMimeMessage(identity.Account, draft, ToBox.Text, CcBox.Text, BccBox.Text, SubjectBox.Text,
            s.Plain ? s.Text : s.Html, _attachments.Where(a => a.Path is not null).Select(a => a.Path!), identity.Alias, asDraft);
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync(null);

    /// <summary>Send later: the ready-made times, or any other picked in a small window.</summary>
    private void SendLater_Click(object sender, RoutedEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = SendLaterButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (label, at) in SendLaterPrompt.Suggestions(DateTime.Now))
        {
            var item = new System.Windows.Controls.MenuItem { Header = label };
            item.Click += async (_, _) => await SendAsync(at);
            menu.Items.Add(item);
        }
        menu.Items.Add(new System.Windows.Controls.Separator());
        var pick = new System.Windows.Controls.MenuItem { Header = L.T("Pick a date and time…") };
        pick.Click += async (_, _) =>
        {
            if (SendLaterPrompt.Ask(this) is { } at) await SendAsync(at);
        };
        menu.Items.Add(pick);
        menu.IsOpen = true;
    }

    /// <param name="at">Send later; null: now (after the seconds Undo send allows).</param>
    private async Task SendAsync(DateTime? at)
    {
        if (_sending || FromBox.SelectedItem is not Identity { Account: var account } identity) return;
        if (string.IsNullOrWhiteSpace(SubjectBox.Text)
            && !MessageDialog.Ask(this, L.T("Send without a subject?"), L.T("A subject helps people find your message later."), L.T("Send anyway"), L.T("Add a subject")))
            return;
        // Attachments mail services refuse: the message would likely bounce, so say so once more before it goes.
        var refused = AttachmentWarnings().Where(w => w.Risk == AttachmentRisk.Danger).Select(w => w.Text).ToList();
        if (refused.Count > 0
            && !MessageDialog.Ask(this, L.T("This message may not get through"), string.Join("\n\n", refused), L.T("Send anyway"), L.T("Keep editing"), DialogKind.Warning))
            return;

        _sending = true;
        SendButton.IsEnabled = false;
        StatusText.Text = L.T("Sending…");
        SendingSpinner.Visibility = Visibility.Visible;
        try
        {
            var state = await ReadEditorAsync(forSending: true) ?? throw new InvalidOperationException(L.T("The editor isn't ready yet."));
            var message = BuildMessage(identity, state, asDraft: false);

            await _engine.SendAsync(account, message, at is { } later ? new DateTimeOffset(later) : null);
            _sent = true;
            _autosave.Stop();
            if (_draftAccountId is { } draftAccount && _draft.MessageId is { } draftId) DeleteDraftInBackground(draftAccount, draftId);
            Close();
        }
        catch (FormatException ex)
        {
            StatusText.Text = "";
            MessageDialog.Show(this, L.T("Check the recipients"), ex.Message);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Sending failed");
            StatusText.Text = "";
            MessageDialog.Show(this, L.T("Your message wasn't sent"), ex.Message, DialogKind.Warning); // couldn't even be kept in the Outbox
        }
        finally
        {
            _sending = false;
            SendButton.IsEnabled = true;
            SendingSpinner.Visibility = Visibility.Collapsed;
        }
    }

    private async void SaveDraft_Click(object sender, RoutedEventArgs e) => await SaveDraftAsync(quiet: false);

    /// <summary>Saves to the From account's Drafts folder, replacing the previous copy.</summary>
    /// <param name="quiet">Autosave: skipped when nothing changed, and failures only show in the status text.</param>
    private async Task<bool> SaveDraftAsync(bool quiet)
    {
        if (_saving || _sending || _sent || FromBox.SelectedItem is not Identity identity) return false;
        _saving = true;
        try
        {
            if (await ReadEditorAsync() is not { } state) return false;
            var snapshot = Snapshot(state);
            if (quiet && snapshot == _savedSnapshot) return true;

            StatusText.Text = L.T("Saving draft…");
            var message = BuildMessage(identity, state, asDraft: true);
            if (!await _engine.SaveDraftAsync(identity.Account.Id, message))
            {
                StatusText.Text = L.T("Draft not saved");
                if (!quiet)
                    MessageDialog.Show(this, L.T("Couldn't save the draft"),
                        L.F("No Drafts folder was found for {0}. You can choose one under Accounts.", identity.Account.Email), DialogKind.Warning);
                return false;
            }

            _draft = _draft with { MessageId = message.MessageId };
            if (_sent)
            {
                DeleteDraftInBackground(identity.Account.Id, message.MessageId); // finished after the message went out
                return true;
            }
            // Sending from another account now: its Drafts folder has the copy, the old one goes.
            if (_draftAccountId is { } old && old != identity.Account.Id) DeleteDraftInBackground(old, message.MessageId);
            _draftAccountId = identity.Account.Id;
            _keepDraft |= !quiet;
            _savedSnapshot = snapshot;
            StatusText.Text = L.F("Draft saved at {0:t}", DateTime.Now);
            return true;
        }
        catch (FormatException ex)
        {
            StatusText.Text = L.T("Draft not saved");
            if (!quiet) MessageDialog.Show(this, L.T("Couldn't save the draft"), ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Saving the draft failed");
            StatusText.Text = L.T("Draft not saved");
            if (!quiet)
                MessageDialog.Show(this, L.T("Couldn't save the draft"), ex.Message, DialogKind.Warning);
            return false;
        }
        finally
        {
            _saving = false;
        }
    }

    private void DeleteDraftInBackground(long accountId, string? messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return;
        _ = Task.Run(async () =>
        {
            try { await _engine.DeleteDraftAsync(accountId, messageId); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Removing the draft from Drafts failed"); }
        });
    }

    private async Task<bool> HasUnsavedChangesAsync() =>
        await ReadEditorAsync() is { } state && Snapshot(state) != _savedSnapshot;

    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_sending) return;
        var saved = _draftAccountId is not null;
        if (saved || await HasUnsavedChangesAsync())
        {
            var message = saved ? L.T("The copy in Drafts will be deleted too.") : L.T("What you've written will be lost.");
            if (!MessageDialog.Ask(this, L.T("Discard this message?"), message, L.T("Discard"), L.T("Keep editing"), DialogKind.Danger)) return;
        }
        if (_draftAccountId is { } account && _draft.MessageId is { } id) DeleteDraftInBackground(account, id);
        CloseWithoutPrompt();
    }

    internal void CloseWithoutPrompt()
    {
        _closeConfirmed = true;
        Close();
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_sent || _closeConfirmed || !_editorReady.Task.IsCompleted)
        {
            _autosave.Stop();
            return;
        }
        e.Cancel = true;
        if (_sending || _saving || _closing) return;
        _closing = true;
        _autosave.Stop();
        try
        {
            if (await HasUnsavedChangesAsync())
            {
                var choice = MessageDialog.Choose(this, L.T("Save this message as a draft?"), L.T("You can finish it later from the Drafts folder."),
                    L.T("Save draft"), L.T("Discard"));
                if (choice is null) return;
                if (choice == true)
                {
                    if (!await SaveDraftAsync(quiet: false)) return;
                }
                else if (!_keepDraft && _draftAccountId is { } account && _draft.MessageId is { } id)
                {
                    DeleteDraftInBackground(account, id); // only ever autosaved: nothing the user wanted to keep
                }
            }
            _closeConfirmed = true;
            // Closing again from inside this handler isn't allowed; let it finish first.
            _ = Dispatcher.BeginInvoke(Close);
        }
        finally
        {
            _closing = false;
            if (!_closeConfirmed) _autosave.Start();
        }
    }

    // ───────────────────────── header fields, attachments ─────────────────────────

    private void Attach_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Title = L.T("Attach files") };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }

    // Files dropped anywhere outside the text (toolbar, header fields, attachments) are attached, pictures too; the
    // editor page decides for drops on the text. Caught on the way down (Preview), since the header text boxes would
    // otherwise take the drop and refuse it for not being text.
    private readonly DispatcherTimer _dropHintTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
        DropHint.Visibility = Visibility.Visible;
        // No dependable "drag left" event (moving onto a text box or the editor counts as leaving): the hint goes once
        // dragover stops coming.
        _dropHintTimer.Stop();
        _dropHintTimer.Start();
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        HideDropHint();
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        AddFiles(files);
    }

    private void HideDropHint()
    {
        _dropHintTimer.Stop();
        DropHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>Attaches the files that exist and aren't attached already (folders are skipped).</summary>
    private void AddFiles(IEnumerable<string> paths)
    {
        foreach (var path in paths.Where(File.Exists))
        {
            if (_attachments.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            _attachments.Add(new AttachmentEntry(Path.GetFileName(path), new FileInfo(path).Length, path, null, OutgoingAttachments.Check(path)));
        }
    }

    /// <summary>Each attachment that may not get through, and all of them together when they're too big, under the list.</summary>
    private List<OutgoingWarning> AttachmentWarnings()
    {
        var warnings = _attachments.Where(a => a.Warning is not null)
            .Select(a => a.Warning! with { Text = $"{a.Name}: {a.Warning!.Text}" }).ToList();
        if (OutgoingAttachments.CheckTotal(_attachments.Sum(a => a.Size)) is { } total) warnings.Add(total);
        return warnings;
    }

    private void ShowAttachmentWarnings()
    {
        var warnings = AttachmentWarnings();
        AttachmentWarningList.ItemsSource = warnings;
        AttachmentWarningList.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is AttachmentEntry entry) _attachments.Remove(entry);
    }

    private void ToggleCc_Click(object sender, RoutedEventArgs e)
    {
        var show = CcRow.Visibility != Visibility.Visible || BccRow.Visibility != Visibility.Visible;
        CcRow.Visibility = BccRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Subject_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        Title = string.IsNullOrWhiteSpace(SubjectBox.Text) ? L.T("New message") : SubjectBox.Text;

    private static string Enc(string s) => WebUtility.HtmlEncode(s);

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / (1024.0 * 1024):0.0} MB",
    };

    private static readonly Lazy<string> EditorTemplate = new(() =>
    {
        using var stream = typeof(ComposeWindow).Assembly.GetManifestResourceStream("Plicbox.App.Resources.compose.html")!;
        using var reader = new StreamReader(stream);
        return Localize(reader.ReadToEnd());
    });

    /// <summary>
    /// The editor page in the app's language: [[…]] in its HTML, T('…') in its script, whose translations go in as
    /// {{STRINGS}}. Done to the bare template, before a message body is put in.
    /// </summary>
    private static string Localize(string page)
    {
        var strings = ScriptText().Matches(page).Select(m => m.Groups[1].Value).Distinct().ToDictionary(s => s, L.T);
        return HtmlText().Replace(page, m => WebUtility.HtmlEncode(L.T(m.Groups[1].Value)))
            .Replace("{{STRINGS}}", JsonSerializer.Serialize(strings));
    }

    // No quotes or brackets inside, so script like [['high', …]] is never taken for one.
    [System.Text.RegularExpressions.GeneratedRegex(@"\[\[([^\[\]'""<>]+?)\]\]")]
    private static partial System.Text.RegularExpressions.Regex HtmlText();

    [System.Text.RegularExpressions.GeneratedRegex(@"\bT\('([^'\\]*)'")]
    private static partial System.Text.RegularExpressions.Regex ScriptText();
}
