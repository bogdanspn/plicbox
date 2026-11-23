using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Plicbox.App.Services;
using Plicbox.App.ViewModels;
using Plicbox.Core;
using Plicbox.Mail;
using MimeKit;

namespace Plicbox.App;

/// <summary>
/// A message opened in its own window (Open / double-click). It shows the message exactly as the reading pane does
/// (the same view), with reply, forward, flag, delete and print for this message.
/// </summary>
public partial class MessageWindow : Window
{
    private readonly MessageItemViewModel _item;
    private readonly MimeMessage _mime;
    private readonly Account _account;
    private readonly MessageReader _reader;
    private readonly MainViewModel _main;
    private readonly MessageRenderer _renderer;
    private readonly IBodyStore _bodies;
    private readonly Dialogs _dialogs;

    public MessageWindow(MessageItemViewModel item, MimeMessage mime, Account account, bool allowRemoteContent,
        MessageReader reader, MainViewModel main, MessageRenderer renderer, IBodyStore bodies, Dialogs dialogs)
    {
        InitializeComponent();
        (_item, _mime, _account, _reader, _main, _renderer, _bodies, _dialogs) = (item, mime, account, reader, main, renderer, bodies, dialogs);
        Owner = Application.Current.MainWindow;
        Icon = Services.WindowIcons.Message; // the same plain glyphs as the other popups
        Title = item.Subject;

        reader.ReplyAction = () => Compose(ComposeMode.Reply);
        reader.ReplyAllAction = () => Compose(ComposeMode.ReplyAll);
        reader.ForwardAction = () => Compose(ComposeMode.Forward);
        reader.Status += text => main.StatusText = text;
        Reading.DataContext = reader;
        Reading.MessageMenu = MenuItems;
        _ = reader.ShowAsync(item, allowRemoteContent);

        item.PropertyChanged += OnItemChanged;
        Closed += (_, _) => item.PropertyChanged -= OnItemChanged;
        UpdateFlagButton();

        // F4 finds in the message, as in the main window.
        PreviewKeyDown += (_, e) =>
        {
            // Ctrl+P: our print window, not the browser's preview inside the message body.
            if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                Reading.Print();
                return;
            }
            if (e.Key != Key.F4 || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            _ = Reading.FindAsync();
        };
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessageItemViewModel.IsFlagged)) UpdateFlagButton();
    }

    private void UpdateFlagButton() => FlagButton.Content = _item.IsFlagged ? L.T("Unflag") : L.T("Flag");

    private void Reply_Click(object sender, RoutedEventArgs e) => Compose(ComposeMode.Reply);
    private void ReplyAll_Click(object sender, RoutedEventArgs e) => Compose(ComposeMode.ReplyAll);
    private void Forward_Click(object sender, RoutedEventArgs e) => Compose(ComposeMode.Forward);
    private void Flag_Click(object sender, RoutedEventArgs e) => _ = _main.UpdateFlagsAsync(_item, isFlagged: !_item.IsFlagged);
    private void Delete_Click(object sender, RoutedEventArgs e) => _ = DeleteAsync();
    private void Print_Click(object sender, RoutedEventArgs e) => Reading.Print();

    private void Compose(ComposeMode mode) =>
        _dialogs.Compose(Composing.Build(mode, _mime, _account, _renderer) with { ShowRemotePictures = _reader.PicturesAllowed }, _account);

    private async Task DeleteAsync()
    {
        if (await _main.DeleteMessageAsync(_item)) Close();
    }

    /// <summary>This message's actions in its message menu (between Find and Format).</summary>
    private IEnumerable<object> MenuItems(MenuMaker run)
    {
        yield return run(L.T("Reply"), "\uE97A", () => Compose(ComposeMode.Reply));
        yield return run(L.T("Reply All"), "\uE8C2", () => Compose(ComposeMode.ReplyAll));
        var forward = new MenuItem { Header = L.T("Forward"), Icon = Glyph("\uEE35") };
        forward.Items.Add(run(L.T("Forward"), "\uEE35", () => Compose(ComposeMode.Forward)));
        forward.Items.Add(run(L.T("Forward as Attachment…"), "\uE723", () => Compose(ComposeMode.ForwardAsAttachment)));
        forward.Items.Add(run(L.T("Forward as Original…"), "\uE8A5", () => Compose(ComposeMode.ForwardAsOriginal)));
        yield return forward;
        yield return run(L.T("Resend…"), "\uE8EE", () => Compose(ComposeMode.Resend));
        yield return new Separator();
        yield return run(_item.IsFlagged ? L.T("Remove Flag") : L.T("Flag"), "\uE7C1", () => _ = _main.UpdateFlagsAsync(_item, isFlagged: !_item.IsFlagged));
        yield return _item.IsRead
            ? run(L.T("Mark as Unread"), "\uE715", () => _ = _main.UpdateFlagsAsync(_item, isRead: false))
            : run(L.T("Mark as Read"), "\uE8C3", () => _ = _main.UpdateFlagsAsync(_item, isRead: true));
        yield return new Separator();
        yield return run(L.T("Save As…"), "\uE74E", SaveAs);
        yield return run(L.T("Save Attachments…"), "\uE896", () => _reader.SaveAttachmentsCommand.Execute(null), _reader.Attachments.Count > 0);
        yield return run(L.T("Delete"), "\uE74D", () => _ = DeleteAsync());
        yield return new Separator();
        yield return run(L.T("Print…"), "\uE749", Reading.Print);
        yield return new Separator();
        yield return run(L.T("View Mail Header…"), "\uF0E3", () => ShowRaw(headerOnly: true));
        yield return run(L.T("View Mail Source…"), "\uE943", () => ShowRaw(headerOnly: false));
    }

    private static GlyphIcon Glyph(string glyph) => new() { Glyph = glyph, FontSize = 16 };

    private void SaveAs()
    {
        using var raw = _bodies.Open(_item.Model.Id);
        if (raw is not null) _dialogs.SaveMessage(raw, _item.Model.Subject);
    }

    private void ShowRaw(bool headerOnly)
    {
        using var raw = _bodies.Open(_item.Model.Id);
        if (raw is null) return;
        using var reader = new StreamReader(raw);
        var text = reader.ReadToEnd();
        if (!headerOnly)
        {
            _dialogs.ShowText(L.F("Source – {0}", _item.Subject), text);
            return;
        }
        var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) is var i and >= 0 ? i : text.IndexOf("\n\n", StringComparison.Ordinal);
        _dialogs.ShowText(L.F("Header – {0}", _item.Subject), end >= 0 ? text[..end] : text);
    }
}
