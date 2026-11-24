using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Plicbox.Core;
using Plicbox.Mail;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace Plicbox.App.Services;

/// <summary>
/// Sets up a WebView2 for showing mail: no scripts, no dev tools, no autofill, and the message can never navigate
/// the view itself. Links the user clicks open in the default browser, after a phishing check; hovering a link
/// shows where it really goes, rated (see <see cref="LinkSafety"/>), in a bubble kept inside the view.
/// </summary>
public static partial class MailWebView
{
    // NavigateToString is capped at ~2 MB; larger messages are rendered from a temp file instead.
    private const int MaxInlineHtml = 1_500_000;

    /// <summary>Per view: the shown message's links (with their text) and sender, and the hover bubble.</summary>
    private sealed class LinkContext
    {
        public IReadOnlyDictionary<string, string> Links = new Dictionary<string, string>();
        public string? Sender;
        public Popup? Bubble;
        /// <summary>The bar is sliding away (a new hover takes it back).</summary>
        public bool Hiding;
    }

    private static readonly ConditionalWeakTable<CoreWebView2, LinkContext> Contexts = new();

    public static async Task InitAsync(WebView2 view)
    {
        var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(App.OwnDir, "WebView2"));
        await view.EnsureCoreWebView2Async(env);
        var core = view.CoreWebView2;
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreDevToolsEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        // The browser's own link bubble can hang outside the window; ours stays in the view and rates the link.
        settings.IsStatusBarEnabled = false;

        var context = Contexts.GetOrCreateValue(core);
        core.StatusBarTextChanged += (_, _) => OnHover(view, context, core.StatusBarText);
        core.NavigationStarting += OnNavigationStarting;
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            Open(context, args.Uri);
        };
        view.Unloaded += (_, _) => HideBubble(context);
        if (Window.GetWindow(view) is { } window)
        {
            window.Deactivated += (_, _) => HideBubble(context);
            window.LocationChanged += (_, _) => HideBubble(context);
        }
        view.ZoomFactor = Appearance.MessageZoom; // Settings > Appearance > Message text
    }

    /// <param name="senderAddress">The message's From address, for judging its links.</param>
    public static void Show(WebView2 view, string html, string? senderAddress = null)
    {
        if (view.CoreWebView2 is { } core && Contexts.TryGetValue(core, out var context))
        {
            HideBubble(context);
            context.Sender = senderAddress;
            context.Links = new Dictionary<string, string>();
            _ = Task.Run(() => LinkSafety.ExtractLinks(html)).ContinueWith(t => context.Links = t.Result, TaskScheduler.Default);
        }
        // A message shown dark (dark mode, a plain message) on a dark backing, so switching to it doesn't flash white.
        var backing = MessageRenderer.IsDarkPage(html) ? Theme.Color("SurfaceBrush") : Colors.White;
        view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(backing.R, backing.G, backing.B);
        if (VisualTreeHelper.GetParent(view) is Panel { Parent: Border frame }) frame.Background = new SolidColorBrush(backing);
        // The file of the big message shown before (if any) isn't needed once the next one is on its way.
        if (RenderFiles.TryGetValue(view, out var previous))
        {
            RenderFiles.Remove(view);
            TryDelete(previous.Path);
        }
        if (html.Length <= MaxInlineHtml)
        {
            view.NavigateToString(html);
            return;
        }
        var path = Path.Combine(App.OwnDir, $"render-{Guid.NewGuid():N}.html");
        Directory.CreateDirectory(App.OwnDir);
        File.WriteAllText(path, html);
        RenderFiles.Add(view, new RenderFile(path));
        view.CoreWebView2.Navigate(new Uri(path).AbsoluteUri);
    }

    private sealed record RenderFile(string Path);

    /// <summary>Per view: the temporary file its current (big) message is shown from.</summary>
    private static readonly ConditionalWeakTable<WebView2, RenderFile> RenderFiles = new();

    /// <summary>Removes render files left by an earlier run (each window deletes its own as it moves on).</summary>
    public static void CleanUpRenderFiles()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(App.OwnDir, "render-*.html")) TryDelete(file);
        }
        catch (IOException) { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (e.Uri.StartsWith("data:", StringComparison.Ordinal) || e.Uri.StartsWith("about:", StringComparison.Ordinal)
            || e.Uri.StartsWith("file:", StringComparison.Ordinal) && e.Uri.Contains("/render-", StringComparison.Ordinal))
            return;

        e.Cancel = true;
        if (!e.IsUserInitiated) return;
        if (sender is CoreWebView2 core && Contexts.TryGetValue(core, out var context)) Open(context, e.Uri);
        else OpenExternal(e.Uri);
    }

    /// <summary>Opens a link from the message shown in <paramref name="view"/> (the reading pane's "Open Link").</summary>
    public static void OpenLink(WebView2 view, string uri)
    {
        if (view.CoreWebView2 is { } core && Contexts.TryGetValue(core, out var context)) Open(context, uri);
        else OpenExternal(uri);
    }

    /// <summary>Opens a web or mailto link from a message in the default browser or mail handler (other schemes are ignored).</summary>
    public static void OpenLink(string uri) => OpenExternal(uri);

    // A link that looks like phishing asks first; everything else opens straight away (the hover bubble already said).
    private static void Open(LinkContext context, string uri)
    {
        HideBubble(context);
        var verdict = Judge(context, uri);
        var title = verdict.Listed switch
        {
            ThreatKind.Phishing => L.T("This link goes to a reported phishing site"),
            ThreatKind.Malware => L.T("This link goes to a site that spreads malware"),
            _ => L.T("This link looks like phishing"),
        };
        if (verdict.Risk == LinkRisk.Danger
            && !MessageDialog.Ask(null, title,
                string.Join("\n", verdict.Reasons) + "\n\n" + L.F("It goes to {0}. Only open it if you're sure who sent this.", verdict.Host),
                L.T("Open anyway"), L.T("Don't open"), DialogKind.Danger))
            return;
        OpenExternal(uri);
    }

    private static LinkVerdict Judge(LinkContext context, string uri)
    {
        var links = context.Links;
        var text = links.TryGetValue(uri, out var t) ? t
            : Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && links.TryGetValue(parsed.AbsoluteUri, out var t2) ? t2 : null;
        return LinkSafety.Assess(uri, text, context.Sender);
    }

    private static void OpenExternal(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;
        if (parsed.Scheme is not ("http" or "https" or "mailto")) return;
        Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
    }

    // ───────── hover bubble ─────────

    private static void OnHover(WebView2 view, LinkContext context, string text)
    {
        // Only for the window in front: the bar is a window of its own, and over another app it would cover that app.
        if (string.IsNullOrWhiteSpace(text) || !view.IsVisible || Window.GetWindow(view) is not { IsActive: true })
        {
            HideBubble(context);
            return;
        }
        ShowBubble(view, context, text);
    }

    private static void ShowBubble(WebView2 view, LinkContext context, string text)
    {
        var verdict = Judge(context, text);
        var bubble = context.Bubble ??= new Popup
        {
            AllowsTransparency = true, Placement = PlacementMode.Relative, PlacementTarget = view, Focusable = false, StaysOpen = true,
        };
        bubble.SetResourceReference(System.Windows.Documents.TextElement.FontFamilyProperty, "UiFont"); // a popup doesn't take the window's font
        var wasShowing = bubble.IsOpen && !context.Hiding;
        context.Hiding = false;

        // A bar across the bottom of the message, like a browser's status line; at the top while the pointer is down
        // there, so it never covers the link you're pointing at.
        // Out to the window's own right and bottom edges (past the narrow resize margin around the message), so it sits
        // flush in the corner; the window's rounded corner is followed when it isn't maximized.
        var window = Window.GetWindow(view);
        var origin = window is null ? new Point(0, 0) : view.TranslatePoint(new Point(0, 0), window);
        var client = window?.Content as FrameworkElement;
        var rounded = window is { WindowState: not WindowState.Maximized };
        // Windows 11 draws its thin window border over the edge of a chrome-less window's content; the bar (a window of
        // its own) stops inside it, so the border runs on unbroken around the corner.
        var inset = rounded && window is not null && client is not null ? SystemBorderInset(window, client) : default;
        var toRight = client is null ? view.ActualWidth : Math.Max(view.ActualWidth, client.ActualWidth - origin.X - inset.Right);
        var toBottom = client is null ? view.ActualHeight : Math.Max(view.ActualHeight, client.ActualHeight - origin.Y - inset.Bottom);
        var atLeftEdge = origin.X < 1;
        // Always out to the window's edge, over the message's scrollbar too (it only shows while the pointer is on a link).

        // Beside a pane (not at the window's edge) it starts one pixel early, so its edge lies on the pane's divider line
        // instead of leaving a sliver of white beside it.
        var lead = atLeftEdge ? 0 : 1;
        var width = Math.Max(200, Math.Round(toRight + lead));
        var bar = BuildBar(verdict, width);
        var atTop = CursorIn(view).Y > view.ActualHeight - 48;
        // A line on the inner sides, none against the window's edges.
        bar.BorderThickness = atTop ? new Thickness(atLeftEdge ? 0 : 1, 0, 0, 1) : new Thickness(atLeftEdge ? 0 : 1, 1, 0, 0);
        var corner = Math.Max(0, 8 - inset.Bottom); // the window's rounding, just inside its border
        bar.CornerRadius = !atTop && rounded ? new CornerRadius(0, 0, corner, atLeftEdge ? corner : 0) : new CornerRadius(0);
        bar.Measure(new Size(width, double.PositiveInfinity));
        var height = Math.Ceiling(bar.DesiredSize.Height);
        bar.Height = height;
        bubble.Child = bar;
        bubble.HorizontalOffset = -lead;
        bubble.VerticalOffset = atTop ? 0 : Math.Max(0, toBottom - height);
        bubble.IsOpen = true;
        NotTopmost(bar);
        if (!wasShowing) Slide(bar, atTop ? -height : height, 0, null);
    }

    /// <summary>
    /// WPF makes every popup an always-on-top window; this one goes back to the ordinary layer, so another app brought
    /// to the front covers it like it covers the rest of Plicbox.
    /// </summary>
    private static void NotTopmost(Visual child)
    {
        if (PresentationSource.FromVisual(child) is System.Windows.Interop.HwndSource source)
            SetWindowPos(source.Handle, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Slides the bar in from (or out past) its edge.</summary>
    private static void Slide(FrameworkElement bar, double from, double to, Action? done)
    {
        var shift = new TranslateTransform(0, from);
        bar.RenderTransform = shift;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(from, to, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        if (done is not null) animation.Completed += (_, _) => done();
        shift.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    /// <summary>Dev screenshots: shows the hover bubble for a link as if it were hovered; returns the bubble.</summary>
    internal static Popup? PreviewBubble(WebView2 view, string url, string? text, string? sender)
    {
        if (view.CoreWebView2 is not { } core || !Contexts.TryGetValue(core, out var context)) return null;
        context.Sender = sender;
        context.Links = text is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [url] = text };
        ShowBubble(view, context, url);
        return context.Bubble;
    }

    private static void HideBubble(LinkContext context)
    {
        if (context.Bubble is not { IsOpen: true } bubble || context.Hiding) return;
        if (bubble.Child is not FrameworkElement bar)
        {
            bubble.IsOpen = false;
            return;
        }
        // Slides back out over its edge, then closes (unless a new hover took it back meanwhile).
        context.Hiding = true;
        var atTop = bar is Border { BorderThickness.Bottom: > 0 };
        var height = bar.ActualHeight;
        Slide(bar, 0, atTop ? -height : height, () =>
        {
            if (context.Hiding) bubble.IsOpen = false;
            context.Hiding = false;
        });
    }

    /// <summary>
    /// One line: what kind of link it is (colour and words), its site, for risky links the main reason, then the address
    /// shortened (no tracking query, long paths cut).
    /// </summary>
    private static Border BuildBar(LinkVerdict v, double width)
    {
        // Colour says it before the words do: grey link, green own site, amber careful, red phishing.
        var dark = Theme.IsDark;
        var (back, border, accent, glyph) = v.Risk switch
        {
            LinkRisk.Safe => dark ? ("#1D3326", "#2F5A3C", "#7BD88F", "\uE73E") : ("#EEF7F0", "#BFE0C7", "#1E7B34", "\uE73E"),
            LinkRisk.Caution => dark ? ("#3A321A", "#7A6527", "#F2C46B", "\uE7BA") : ("#FFF5E1", "#F0CD86", "#8A5300", "\uE7BA"),
            LinkRisk.Danger => dark ? ("#3E2224", "#7A3438", "#FF8A80", "\uEA39") : ("#FDECEC", "#EFADAD", "#B42318", "\uEA39"),
            _ => dark ? ("#2E2E2E", "#454545", "#C4C4C4", "\uE71B") : ("#F7F7F7", "#D9D9D9", "#5B5B5B", "\uE71B"),
        };
        Brush B(string hex) => (Brush)new BrushConverter().ConvertFromString(hex)!;
        var muted = Theme.Brush("MutedTextBrush");

        var line = new TextBlock { FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        line.Inlines.Add(new System.Windows.Documents.Run(v.Headline) { FontWeight = FontWeights.SemiBold, Foreground = B(accent) });
        line.Inlines.Add(new System.Windows.Documents.Run(":  ") { Foreground = B(accent) });
        line.Inlines.Add(new System.Windows.Documents.Run(v.Host) { FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("TextBrush") });
        if (v.Destination is { } dest)
            line.Inlines.Add(new System.Windows.Documents.Run($"  >  {dest}") { Foreground = Theme.Brush("TextBrush") });
        if (v.Risk is LinkRisk.Caution or LinkRisk.Danger && v.Reasons.FirstOrDefault() is { } reason)
            line.Inlines.Add(new System.Windows.Documents.Run($", {reason}") { Foreground = Theme.Brush("SecondaryTextBrush") });
        line.Inlines.Add(new System.Windows.Documents.Run($"   {ShortUrl(v.Url)}") { Foreground = muted });
        line.ToolTip = null;

        var row = new DockPanel();
        var icon = new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 13, Foreground = B(accent),
            Margin = new Thickness(0, 0, 9, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(line);

        var bar = new Border
        {
            Child = row, Background = B(back), BorderBrush = B(border), Width = width, Padding = new Thickness(14, 6, 14, 6), IsHitTestVisible = false,
        };
        System.Windows.Documents.TextElement.SetFontFamily(bar, (FontFamily)Application.Current.Resources["UiFont"]);
        return bar;
    }

    /// <summary>"www.site.com/email/click/…": the site and the start of the path, without the query (usually tracking).</summary>
    internal static string ShortUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return url.Length > 70 ? url[..69] + "…" : url;
        var path = u.AbsolutePath.TrimEnd('/');
        if (path.Length > 40) path = path[..39] + "…";
        return u.Host + path + (u.Query.Length > 1 && !path.EndsWith('…') ? "?…" : "");
    }

    private static Point CursorIn(FrameworkElement element)
    {
        if (!GetCursorPos(out var p)) return new Point(-1, -1);
        try { return element.PointFromScreen(new Point(p.X, p.Y)); }
        catch (InvalidOperationException) { return new Point(-1, -1); }
    }

    /// <summary>
    /// How far the system's window border reaches into the content at the right and bottom (in DIPs): zero for a
    /// window with a normal frame, where the border sits outside the content.
    /// </summary>
    private static Thickness SystemBorderInset(Window window, FrameworkElement client)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out NativeRect frame, Marshal.SizeOf<NativeRect>()) != 0) return default;
            var border = DwmGetWindowAttribute(hwnd, DWMWA_VISIBLE_FRAME_BORDER_THICKNESS, out uint thickness, sizeof(uint)) == 0 ? (int)thickness : 0;
            if (border == 0) return default; // Windows 10: no border drawn over the content
            var end = client.PointToScreen(new Point(client.ActualWidth, client.ActualHeight));
            var dpi = VisualTreeHelper.GetDpi(client);
            var right = Math.Max(0, end.X - (frame.Right - border)) / dpi.DpiScaleX;
            var bottom = Math.Max(0, end.Y - (frame.Bottom - border)) / dpi.DpiScaleY;
            return new Thickness(0, 0, right, bottom);
        }
        catch (InvalidOperationException)
        {
            return default;
        }
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_VISIBLE_FRAME_BORDER_THICKNESS = 37;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out NativeRect value, int size);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out uint value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);
}
