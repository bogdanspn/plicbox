using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Plicbox.App.Services;
using Plicbox.App.ViewModels;
using Plicbox.Core;
using Microsoft.Web.WebView2.Core;

namespace Plicbox.App;

/// <summary>Makes a message-menu item whose action runs once the menu has closed.</summary>
public delegate MenuItem MenuMaker(string header, string? glyph, Action run, bool enabled = true, string? gesture = null);

/// <summary>
/// One message on screen, for the reading pane and the message window alike: the header with its people menus, chips,
/// notices and attachments, and the message itself with its own menu (link, copy, find, the window's actions, format, zoom).
/// </summary>
public partial class ReadingView : UserControl
{
    private MessageReader? _reader;
    private bool _initStarted;

    public ReadingView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) => Attach(e.NewValue as MessageReader);
        Loaded += OnLoaded;
    }

    /// <summary>The message view is ready (WebView2 started).</summary>
    public bool IsReady { get; private set; }

    /// <summary>The window's actions on the message (Reply, Flag, Delete, …), placed in the message menu after Find.</summary>
    public Func<MenuMaker, IEnumerable<object>>? MessageMenu { get; set; }

    /// <summary>Called as the message menu opens (the window refreshes what its actions can do).</summary>
    public event Action? MessageMenuOpening;

    private void Attach(MessageReader? reader)
    {
        if (_reader is not null) _reader.PropertyChanged -= OnReaderPropertyChanged;
        _reader = reader;
        if (reader is null) return;
        reader.PropertyChanged += OnReaderPropertyChanged;
        ShowBody();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initStarted) return;
        _initStarted = true;
        try
        {
            await MailWebView.InitAsync(Body);
            Body.CoreWebView2.ContextMenuRequested += Body_ContextMenuRequested;
            IsReady = true;
            ShowBody();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "WebView2 initialisation failed");
        }
    }

    private void OnReaderPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MessageReader.BodyHtml)) ShowBody();
    }

    private void ShowBody()
    {
        if (IsReady && _reader?.BodyHtml is { } html) MailWebView.Show(Body, html, _reader.SelectedMessage?.Model.FromAddress);
    }

    /// <summary>Print…: the message in a print window of its own, with who and when at the top of the page.</summary>
    /// <summary>Dev aid (--screenshot-print): the keyboard into the message body.</summary>
    internal void DevFocusBody() => Body.Focus();

    public void Print()
    {
        if (_reader is not { BodyHtml: { } html, SelectedMessage: { } item, CurrentMime: { } mime }) return;
        static string People(MimeKit.InternetAddressList list) =>
            string.Join(", ", list.Mailboxes.Select(m => Addresses.Format(m.Name ?? "", m.Address)));
        PrintWindow.Open(Window.GetWindow(this), html, item.Subject,
        [
            (L.T("From"), People(mime.From)),
            (L.T("To"), People(mime.To)),
            (L.T("Cc"), People(mime.Cc)),
            (L.T("Date"), item.FullDate),
        ], item.Model.FromAddress);
    }

    // ───────── people in the header ─────────

    private void Person_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HeaderPerson person } element) return;
        var menu = PersonMenu(person);
        menu.PlacementTarget = element;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void Person_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: HeaderPerson person } element) { e.Handled = true; return; }
        element.ContextMenu = PersonMenu(person);
    }

    /// <summary>What you can do with someone named in the header (sender or recipient).</summary>
    private ContextMenu PersonMenu(HeaderPerson person)
    {
        MenuItem Item(string header, string glyph, Action click, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, Icon = Glyph(glyph) };
            item.Click += (_, _) => click();
            return item;
        }
        var menu = new ContextMenu();
        if (person.Address is not { } address)
        {
            menu.Items.Add(Item(L.T("No address for this name (older mail)"), "\uE946", () => { }, enabled: false));
            return menu;
        }
        var host = MessageReader.Host;
        var name = person.Name;
        var first = name.Contains('@') ? name : name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? name;
        var message = _reader?.SelectedMessage?.Model;
        var isSender = message is not null && string.Equals(message.FromAddress, address, StringComparison.OrdinalIgnoreCase);
        var formatted = Addresses.Format(name.Contains('@') ? "" : name, address);
        menu.Items.Add(Item(L.F("Email {0}…", first), "\uE715", () => host?.ComposeTo(formatted), host is not null));
        menu.Items.Add(Item(L.T("New Meeting…"), "\uE787", () => host?.NewMeeting(name.Contains('@') ? address : first, address), host is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Show Contact"), "\uE77B", () => host?.ShowContact(address), host is not null));
        menu.Items.Add(Item(L.T("All Mail with Them"), "\uE721", () => host?.ShowAllMail(address), host is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Copy Address"), "\uE8C8", () => Clipboard.SetText(address)));
        menu.Items.Add(Item(L.T("Copy Name and Address"), "\uE8C8", () => Clipboard.SetText(formatted)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Create Rule for Their Mail…"), "\uE8B1", () => host?.CreateRule(name, address), host is not null));
        if (isSender && message is not null) menu.Items.Add(Item(L.F("Block {0}…", first), "\uE733", () => host?.BlockSender(message), host is not null));
        return menu;
    }

    // ───────── the message's menu (replaces the browser's own, like eM Client's message menu) ─────────

    private void Body_ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        e.Handled = true;
        var target = e.ContextMenuTarget;
        Action? chosen = null;

        MenuItem Run(string header, string? glyph, Action run, bool enabled = true, string? gesture = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "", Icon = glyph is null ? null : Glyph(glyph) };
            item.Click += (_, _) => chosen = run;
            return item;
        }

        MessageMenuOpening?.Invoke();
        var plain = _reader?.ShowingPlainText == true;
        var menu = new ContextMenu { DataContext = DataContext, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        if (target.Kind == CoreWebView2ContextMenuTargetKind.Image)
        {
            // Chromium leaves out the address of big embedded pictures (long data: URLs); then it's read from the
            // picture under the pointer. A web picture that wasn't downloaded (pictures blocked) has nothing to copy yet.
            var known = target.HasSourceUri && target.SourceUri.Length > 0 ? target.SourceUri : null;
            var at = e.Location;
            var available = known is null || known.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || _reader?.PicturesAllowed == true;
            async Task<string?> Source() => known ?? await PictureSourceAtAsync(at);
            menu.Items.Add(Run(L.T("Copy Image"), "", () => _ = CopyImageAsync(Source), available));
            menu.Items.Add(Run(L.T("Save Image As…"), "", () => _ = SaveImageAsync(Source), available));
            menu.Items.Add(new Separator());
        }
        if (target.HasLinkUri)
        {
            var link = target.LinkUri;
            menu.Items.Add(Run(L.T("Open Link"), "\uE8A7", () => MailWebView.OpenLink(Body, link)));
            menu.Items.Add(Run(L.T("Copy Link Address"), "\uE71B", () => Clipboard.SetText(link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? link[7..] : link)));
            menu.Items.Add(new Separator());
        }
        var selection = target.HasSelection ? target.SelectionText : "";
        menu.Items.Add(Run(L.T("Copy"), "\uE8C8", () => Clipboard.SetText(selection), selection.Length > 0, L.T("Ctrl+C")));
        menu.Items.Add(Run(L.T("Select All"), null, () => _ = SelectAllAsync(), gesture: L.T("Ctrl+A")));
        menu.Items.Add(Run(L.T("Find…"), "\uE721", () => _ = FindAsync(selection), gesture: "F4"));
        menu.Items.Add(new Separator());
        if (MessageMenu?.Invoke(Run) is { } actions)
        {
            foreach (var action in actions) menu.Items.Add(action);
            menu.Items.Add(new Separator());
        }
        var format = Submenu(L.T("Format"), "\uE943");
        format.Items.Add(Run("HTML", plain ? null : "\uE73E", () => _reader?.ShowAsPlainTextCommand.Execute(false)));
        format.Items.Add(Run(L.T("Plain Text"), plain ? "\uE73E" : null, () => _reader?.ShowAsPlainTextCommand.Execute(true)));
        menu.Items.Add(format);
        var zoom = Submenu(L.F("Zoom ({0:0}%)", Body.ZoomFactor * 100), "\uE71E");
        zoom.Items.Add(Run(L.T("Zoom In"), "\uE8A3", () => SetZoom(Body.ZoomFactor * 1.1), gesture: L.T("Ctrl+Wheel")));
        zoom.Items.Add(Run(L.T("Zoom Out"), "\uE71F", () => SetZoom(Body.ZoomFactor / 1.1)));
        zoom.Items.Add(Run(L.T("Actual Size (100%)"), null, () => SetZoom(1)));
        menu.Items.Add(zoom);

        menu.Closed += (_, _) =>
        {
            deferral.Complete();
            // Our own actions run after the browser has its answer, so dialogs don't open while it waits.
            if (chosen is { } action) Dispatcher.BeginInvoke(action);
        };
        menu.IsOpen = true;
    }

    private async Task CopyImageAsync(Func<Task<string?>> source)
    {
        if (await PictureAsync(source) is not { } picture) return;
        if (!MessagePictures.Copy(picture.Bytes))
            MessageDialog.Show(Window.GetWindow(this), L.T("Couldn't copy the picture"),
                L.T("This kind of picture can't go on the clipboard. Use Save Image As… instead."), DialogKind.Warning);
    }

    private async Task SaveImageAsync(Func<Task<string?>> source)
    {
        if (await PictureAsync(source) is not { } picture) return;
        try { MessagePictures.SaveAs(picture.Bytes, picture.Type, picture.Source, Window.GetWindow(this)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageDialog.Show(Window.GetWindow(this), L.T("Couldn't save the picture"), ex.Message, DialogKind.Warning);
        }
    }

    private async Task<(byte[] Bytes, string Type, string Source)?> PictureAsync(Func<Task<string?>> findSource)
    {
        try
        {
            if (IsReady && await findSource() is { } source && await MessagePictures.GetAsync(Body.CoreWebView2, source) is { } picture)
                return (picture.Bytes, picture.Type, source);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Getting a picture from the message failed");
        }
        MessageDialog.Show(Window.GetWindow(this), L.T("The picture isn't available"),
            L.T("It couldn't be read from the message. If it's a web picture, it may be gone from the sender's server."), DialogKind.Warning);
        return null;
    }

    /// <summary>
    /// The src of the picture under a point of the view (scripts are off, so the page is asked through DevTools).
    /// The menu's point is in view units; the page counts in CSS pixels, which zoom (and on some setups, display
    /// scaling) changes, so each reading is tried until one lands on a picture.
    /// </summary>
    private async Task<string?> PictureSourceAtAsync(System.Drawing.Point at)
    {
        var core = Body.CoreWebView2;
        var dpi = VisualTreeHelper.GetDpi(Body).DpiScaleX;
        foreach (var scale in new[] { Body.ZoomFactor, Body.ZoomFactor * dpi, 1.0, dpi }.Distinct())
        {
            try
            {
                using var hit = System.Text.Json.JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.getNodeForLocation",
                    System.Text.Json.JsonSerializer.Serialize(new { x = (int)Math.Round(at.X / scale), y = (int)Math.Round(at.Y / scale) })));
                var backendNodeId = hit.RootElement.GetProperty("backendNodeId").GetInt32();
                using var described = System.Text.Json.JsonDocument.Parse(await core.CallDevToolsProtocolMethodAsync("DOM.describeNode",
                    System.Text.Json.JsonSerializer.Serialize(new { backendNodeId })));
                var node = described.RootElement.GetProperty("node");
                if (!string.Equals(node.GetProperty("nodeName").GetString(), "IMG", StringComparison.OrdinalIgnoreCase)
                    || !node.TryGetProperty("attributes", out var attributes)) continue;
                var pairs = attributes.EnumerateArray().Select(a => a.GetString() ?? "").ToList();
                for (var i = 0; i + 1 < pairs.Count; i += 2)
                    if (pairs[i].Equals("src", StringComparison.OrdinalIgnoreCase)) return pairs[i + 1];
            }
            catch (Exception ex)
            {
                Serilog.Log.Debug(ex, "No picture found at {X},{Y} (scale {Scale})", at.X, at.Y, scale);
            }
        }
        return null;
    }

    private static GlyphIcon Glyph(string glyph) => new() { Glyph = glyph, FontSize = 16 };

    private MenuItem Submenu(string header, string glyph) => new() { Header = header, Icon = Glyph(glyph) };

    public void SetZoom(double factor) => Body.ZoomFactor = Math.Clamp(factor, 0.5, 3);

    /// <summary>Scripts are off in the message, so Select All runs the browser's own editing command (as Ctrl+A would).</summary>
    public async Task SelectAllAsync()
    {
        if (!IsReady) return;
        try
        {
            Body.Focus();
            await Body.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                """{"type":"rawKeyDown","key":"a","code":"KeyA","windowsVirtualKeyCode":65,"modifiers":2,"commands":["selectAll"]}""");
            await Body.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                """{"type":"keyUp","key":"a","code":"KeyA","windowsVirtualKeyCode":65,"modifiers":2}""");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Select all in message failed");
        }
    }

    /// <summary>Find… (F4): the browser's find bar over the message, starting with the selected text if any.</summary>
    public async Task FindAsync(string term = "")
    {
        if (!IsReady || _reader?.SelectedMessage is null) return;
        try
        {
            var options = Body.CoreWebView2.Environment.CreateFindOptions();
            options.FindTerm = term;
            options.ShouldHighlightAllMatches = true;
            Body.Focus();
            await Body.CoreWebView2.Find.StartAsync(options);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Find in message failed");
        }
    }

    // ───────── resizing ─────────

    /// <summary>
    /// The message view is a separate native window that moves a frame or two behind the rest while the window is
    /// resized (worst from the left or top edge, where everything shifts). A still image of it stands in until the
    /// drag ends, so the resize stays smooth.
    /// </summary>
    /// <param name="stillSizing">Whether the drag is still going once the picture is ready.</param>
    public async Task FreezeAsync(Func<bool> stillSizing)
    {
        if (!IsReady || !Body.IsVisible || Body.ActualWidth < 1 || Body.CoreWebView2 is null) return;
        try
        {
            using var stream = new MemoryStream();
            await Body.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            if (!stillSizing()) return;
            stream.Position = 0;
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.StreamSource = stream;
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            BodySnapshot.Source = image;
            BodySnapshot.Width = Body.ActualWidth;
            BodySnapshot.Height = Body.ActualHeight;
            BodySnapshot.Visibility = Visibility.Visible;
            Body.Visibility = Visibility.Hidden;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "Couldn't snapshot the message for resizing");
        }
    }

    public void Unfreeze()
    {
        Body.Visibility = Visibility.Visible;
        BodySnapshot.Visibility = Visibility.Collapsed;
        BodySnapshot.Source = null;
    }

    /// <summary>
    /// In a chrome-less window the message view reaches the window's right and bottom edges, and as its own native
    /// window it would swallow the mouse there: no resize cursor. A gap the width of the resize border keeps those
    /// edges the window's (not needed when maximized).
    /// </summary>
    public void SetEdgeGap(bool gap) => Body.Margin = gap ? new Thickness(0, 0, 6, 6) : new Thickness(0);
}
