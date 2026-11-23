using System.Globalization;
using System.IO;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Plicbox.App.Services;
using Plicbox.Core;
using Plicbox.Mail;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Plicbox.App;

/// <summary>
/// Printing a message: the settings on the left, and on the right the pages exactly as they will come out (the message
/// is laid out for paper by a hidden browser view, and its PDF shown page by page). Print sends it to the printer
/// without the browser's own headers, footers and dialogs; "Save as PDF" writes the same pages to a file.
/// </summary>
public sealed partial class PrintWindow : Window
{
    private sealed record Choice<T>(string Label, T Value);

    /// <summary>A sheet size, portrait, in inches.</summary>
    private sealed record Paper(string Label, double Width, double Height, PageMediaSizeName Media);

    private static readonly Paper[] Papers =
    [
        new("A4 (210 × 297 mm)", 8.27, 11.69, PageMediaSizeName.ISOA4),
        new("Letter (8.5 × 11 in)", 8.5, 11, PageMediaSizeName.NorthAmericaLetter),
        new("Legal (8.5 × 14 in)", 8.5, 14, PageMediaSizeName.NorthAmericaLegal),
        new("A5 (148 × 210 mm)", 5.83, 8.27, PageMediaSizeName.ISOA5),
        new("A3 (297 × 420 mm)", 11.69, 16.54, PageMediaSizeName.ISOA3),
    ];

    /// <summary>What the printer offers, read in the background (network printers can take a moment).</summary>
    private sealed record PrinterInfo(PageMediaSizeName? DefaultPaper, IReadOnlySet<PageMediaSizeName> Papers, bool Color, bool Duplex);

    // Chosen last time (for this run of the app): the next print starts from them.
    private static string? _lastPrinter;
    private static bool _lastLandscape;
    private static PageMediaSizeName? _lastPaper;
    private static bool _lastGray;

    private const double MarginTopBottom = 0.55, MarginSides = 0.5; // inches
    private const string SaveAsPdf = "\u0001pdf"; // the printer list's "Save as PDF"

    private readonly string _html;
    private readonly string _title;
    private readonly string? _sender;
    private bool _ready;
    private bool _busy;
    private bool _refreshing;
    private int _previewVersion;
    private WindowsPdf? _pdf;
    private readonly List<PrintPreviewPage> _allPages = [];
    private IReadOnlyList<int>? _selected;
    private readonly DispatcherTimer _relayout;

    private PrintWindow(string html, string title, string? sender)
    {
        InitializeComponent();
        _html = html;
        _title = title;
        _sender = sender;
        Title = L.F("Print – {0}", title);
        Icon = WindowIcons.Print;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1080, area.Width * 0.85);
        Height = Math.Min(900, area.Height * 0.9);

        PagesBox.ItemsSource = new[] { new Choice<bool>(L.T("All"), false), new Choice<bool>(L.T("Custom"), true) };
        PagesBox.SelectedIndex = 0;
        LayoutBox.ItemsSource = new[] { new Choice<bool>(L.T("Portrait"), false), new Choice<bool>(L.T("Landscape"), true) };
        LayoutBox.SelectedIndex = _lastLandscape ? 1 : 0;
        ColorBox.ItemsSource = new[] { new Choice<bool>(L.T("Color"), false), new Choice<bool>(L.T("Black and white"), true) };
        ColorBox.SelectedIndex = _lastGray ? 1 : 0;
        DuplexBox.ItemsSource = new[]
        {
            new Choice<CoreWebView2PrintDuplex>(L.T("Off"), CoreWebView2PrintDuplex.OneSided),
            new Choice<CoreWebView2PrintDuplex>(L.T("Flip on long edge"), CoreWebView2PrintDuplex.TwoSidedLongEdge),
            new Choice<CoreWebView2PrintDuplex>(L.T("Flip on short edge"), CoreWebView2PrintDuplex.TwoSidedShortEdge),
        };
        DuplexBox.SelectedIndex = 0;
        ShowPapers(null);

        _relayout = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Relayout(), Dispatcher) { IsEnabled = false };
        Pages.SizeChanged += (_, _) => { _relayout.Stop(); _relayout.Start(); };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control && PrintButton.IsEnabled) Print_Click(this, e);
        };
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) =>
        {
            _previewVersion++;
            _pdf?.Dispose();
            _pdf = null;
            Browser.Dispose(); // its browser process isn't needed once the window is gone
        };
    }

    private async Task StartAsync()
    {
        _ = LoadPrintersAsync();
        try
        {
            await MailWebView.InitAsync(Browser);
            Browser.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                _ready = true;
                await RefreshPreviewAsync();
            };
            MailWebView.Show(Browser, _html, _sender);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Opening the print window failed");
            ShowStatus(L.T("The message couldn't be prepared for printing."), error: true);
            PreparingPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ─── Printers ────────────────────────────────────────────────────────────────────────────────

    private async Task LoadPrintersAsync()
    {
        var (names, preferred) = await Task.Run(() =>
        {
            try
            {
                using var server = new LocalPrintServer();
                var list = new List<string>();
                foreach (var queue in server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]))
                    using (queue) list.Add(queue.FullName);
                string? fallback = null;
                try
                {
                    using var standard = LocalPrintServer.GetDefaultPrintQueue();
                    fallback = standard.FullName;
                }
                catch (PrintQueueException) { }
                list.Sort(StringComparer.CurrentCultureIgnoreCase);
                return (list, fallback);
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "Couldn't list the printers");
                return ([], null);
            }
        });
        var choices = new List<Choice<string>> { new(L.T("Save as PDF"), SaveAsPdf) };
        choices.AddRange(names.Select(n => new Choice<string>(n, n)));
        PrinterBox.ItemsSource = choices;
        var start = _lastPrinter is { } last && choices.Any(c => c.Value == last) ? last : preferred ?? choices[^1].Value;
        PrinterBox.SelectedItem = choices.FirstOrDefault(c => c.Value == start) ?? choices[0];
    }

    private string? PrinterName => (PrinterBox.SelectedItem as Choice<string>)?.Value;
    private bool ToPdf => PrinterName == SaveAsPdf;

    private async void Printer_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (PrinterName is not { } name) return;
        _lastPrinter = name;
        PrintIcon.Text = ToPdf ? "" : "";
        PrintText.Text = ToPdf ? L.T("Save") : L.T("Print");
        CopiesPanel.Visibility = ToPdf ? Visibility.Collapsed : Visibility.Visible;
        ColorPanel.Visibility = ToPdf ? Visibility.Collapsed : Visibility.Visible;
        DuplexPanel.Visibility = Visibility.Collapsed;
        if (ToPdf)
        {
            ShowPapers(null);
            UpdatePreviewColor();
            return;
        }
        var info = await Task.Run(() => Describe(name));
        if (PrinterName != name || !IsLoaded) return; // another printer was picked meanwhile
        ShowPapers(info);
        ColorPanel.Visibility = info is { Color: false } ? Visibility.Collapsed : Visibility.Visible;
        DuplexPanel.Visibility = info is { Duplex: true } ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreviewColor();
    }

    private static PrinterInfo? Describe(string name)
    {
        try
        {
            using var server = new LocalPrintServer();
            foreach (var queue in server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]))
            {
                using (queue)
                {
                    if (queue.FullName != name) continue;
                    var caps = queue.GetPrintCapabilities();
                    var papers = caps.PageMediaSizeCapability.Where(m => m.PageMediaSizeName is not null).Select(m => m.PageMediaSizeName!.Value).ToHashSet();
                    return new PrinterInfo(queue.DefaultPrintTicket?.PageMediaSize?.PageMediaSizeName, papers,
                        caps.OutputColorCapability.Count == 0 || caps.OutputColorCapability.Contains(OutputColor.Color),
                        caps.DuplexingCapability.Contains(Duplexing.TwoSidedLongEdge));
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Couldn't read what printer {Printer} offers", name);
        }
        return null;
    }

    /// <summary>The paper sizes the printer takes (all of them for a PDF), starting from the last one used, else the printer's own.</summary>
    private void ShowPapers(PrinterInfo? info)
    {
        var current = (PaperBox.SelectedItem as Choice<Paper>)?.Value.Media;
        var offered = Papers.Where(p => info is null || info.Papers.Count == 0 || info.Papers.Contains(p.Media)).ToList();
        if (offered.Count == 0) offered = [.. Papers];
        var choices = offered.Select(p => new Choice<Paper>(p.Label, p)).ToList();
        var regional = RegionInfo.CurrentRegion.IsMetric ? PageMediaSizeName.ISOA4 : PageMediaSizeName.NorthAmericaLetter;
        var want = _lastPaper ?? info?.DefaultPaper ?? current ?? regional;
        PaperBox.SelectionChanged -= Paper_Changed;
        PaperBox.ItemsSource = choices;
        PaperBox.SelectedItem = choices.FirstOrDefault(c => c.Value.Media == want) ?? choices.FirstOrDefault(c => c.Value.Media == regional) ?? choices[0];
        PaperBox.SelectionChanged += Paper_Changed;
        if ((PaperBox.SelectedItem as Choice<Paper>)?.Value.Media != current && current is not null) _ = RefreshPreviewAsync();
    }

    // ─── Settings ────────────────────────────────────────────────────────────────────────────────

    private bool Landscape => (LayoutBox.SelectedItem as Choice<bool>)?.Value == true;
    private bool Gray => !ToPdf && ColorPanel.Visibility == Visibility.Visible && (ColorBox.SelectedItem as Choice<bool>)?.Value == true;
    private Paper SelectedPaper => (PaperBox.SelectedItem as Choice<Paper>)?.Value ?? Papers[0];

    private int Copies => int.TryParse(CopiesBox.Text, out var n) ? Math.Clamp(n, 1, 99) : 1;

    private void FewerCopies_Click(object sender, RoutedEventArgs e) => CopiesBox.Text = Math.Max(1, Copies - 1).ToString(CultureInfo.CurrentCulture);
    private void MoreCopies_Click(object sender, RoutedEventArgs e) => CopiesBox.Text = Math.Min(99, Copies + 1).ToString(CultureInfo.CurrentCulture);
    private void Copies_PreviewTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);
    private void Copies_TextChanged(object sender, TextChangedEventArgs e) => UpdateSummary();

    private void Layout_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _lastLandscape = Landscape;
        _ = RefreshPreviewAsync();
    }

    private void Paper_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || PaperBox.SelectedItem is not Choice<Paper> paper) return;
        _lastPaper = paper.Value.Media;
        _ = RefreshPreviewAsync();
    }

    private void Color_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        _lastGray = (ColorBox.SelectedItem as Choice<bool>)?.Value == true;
        UpdatePreviewColor();
    }

    private void Pages_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized) return;
        var custom = (PagesBox.SelectedItem as Choice<bool>)?.Value == true;
        PageRangeBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (custom)
        {
            if (string.IsNullOrWhiteSpace(PageRangeBox.Text) && _allPages.Count > 0) PageRangeBox.Text = $"1-{_allPages.Count}";
            PageRangeBox.Focus();
            PageRangeBox.SelectAll();
        }
        ApplyPageSelection();
    }

    private void PageRange_Changed(object sender, TextChangedEventArgs e) => ApplyPageSelection();

    private CoreWebView2PrintSettings PrintSettings(bool final)
    {
        var settings = Browser.CoreWebView2.Environment.CreatePrintSettings();
        var paper = SelectedPaper;
        settings.Orientation = Landscape ? CoreWebView2PrintOrientation.Landscape : CoreWebView2PrintOrientation.Portrait;
        settings.MediaSize = CoreWebView2PrintMediaSize.Custom;
        settings.PageWidth = paper.Width;
        settings.PageHeight = paper.Height;
        settings.MarginTop = settings.MarginBottom = MarginTopBottom;
        settings.MarginLeft = settings.MarginRight = MarginSides;
        settings.ShouldPrintBackgrounds = true;
        settings.ShouldPrintHeaderAndFooter = false;
        settings.ScaleFactor = 1;
        if (!final) return settings;
        if (_selected is { } pages && pages.Count < _allPages.Count) settings.PageRanges = PageRanges.Format(pages);
        if (ToPdf) return settings;
        settings.PrinterName = PrinterName;
        settings.Copies = Copies;
        settings.Collation = CoreWebView2PrintCollation.Collated;
        settings.ColorMode = Gray ? CoreWebView2PrintColorMode.Grayscale : CoreWebView2PrintColorMode.Color;
        if (DuplexPanel.Visibility == Visibility.Visible && DuplexBox.SelectedItem is Choice<CoreWebView2PrintDuplex> duplex) settings.Duplex = duplex.Value;
        return settings;
    }

    // ─── Preview ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Lays the message out again for the chosen paper and shows the pages (the old ones stay until the new are in).</summary>
    private async Task RefreshPreviewAsync()
    {
        if (!_ready || !IsLoaded) return;
        var version = ++_previewVersion;
        _refreshing = true;
        PrintButton.IsEnabled = false;
        ShowStatus(_allPages.Count == 0 ? "" : L.T("Updating preview…"), busy: _allPages.Count > 0);
        try
        {
            using var bytes = new MemoryStream();
            using (var stream = await Browser.CoreWebView2.PrintToPdfStreamAsync(PrintSettings(final: false)))
                await stream.CopyToAsync(bytes);
            if (version != _previewVersion) return;
            var pdf = await WindowsPdf.LoadAsync(bytes.ToArray());
            if (version != _previewVersion) { pdf.Dispose(); return; }

            _pdf?.Dispose(); // pages of the old one still in view stop asking for pictures (RenderPageAsync)
            _pdf = pdf;
            _allPages.Clear();
            for (uint i = 0; i < pdf.PageCount; i++)
            {
                var (width, height) = pdf.PageSize(i);
                _allPages.Add(new PrintPreviewPage(this, pdf, i, width, height) { Gray = Gray });
            }
            PreparingPanel.Visibility = Visibility.Collapsed;
            _refreshing = false;
            ShowStatus("");
            ApplyPageSelection();
        }
        catch (Exception ex)
        {
            if (version != _previewVersion) return;
            _refreshing = false;
            Serilog.Log.Error(ex, "Making the print preview failed");
            PreparingPanel.Visibility = Visibility.Collapsed;
            ShowStatus(L.T("The preview couldn't be made."), error: true);
        }
    }

    /// <summary>Shows the chosen pages (all, or the ones typed) and says how many will be printed.</summary>
    private void ApplyPageSelection()
    {
        if (_allPages.Count == 0) return;
        var custom = (PagesBox.SelectedItem as Choice<bool>)?.Value == true;
        _selected = custom ? PageRanges.Parse(PageRangeBox.Text, _allPages.Count) : [.. Enumerable.Range(1, _allPages.Count)];
        // Pages that can't be told from the text: all of them stay in view, faded, until it can.
        if (_selected is null)
            ShowStatus(_allPages.Count == 1 ? L.T("This message has only one page.") : L.F("Type pages from 1 to {0}, like 1-3, 5.", _allPages.Count), error: true);
        else if (!_busy && !_refreshing) ShowStatus("");
        PrintButton.IsEnabled = !_busy && !_refreshing && _selected is not null;
        Pages.Opacity = _selected is null ? 0.4 : 1;
        var shown = (_selected ?? [.. Enumerable.Range(1, _allPages.Count)]).Select(n => _allPages[n - 1]).ToList();
        foreach (var page in shown) page.Caption = L.F("Page {0} of {1}", page.Index + 1, _allPages.Count);
        Relayout(shown);
        Pages.ItemsSource = shown;
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (_allPages.Count == 0) { SheetsText.Text = ""; return; }
        if (_selected is not { Count: var count }) { SheetsText.Text = ""; return; }
        var pages = count == 1 ? L.T("1 page") : L.F("{0} pages", count);
        SheetsText.Text = !ToPdf && Copies > 1 ? L.F("{0} × {1} copies", pages, Copies) : pages;
    }

    /// <summary>Page size on screen: a whole page in view (scrolling goes from page to page), never bigger than the paper itself.</summary>
    private void Relayout(IEnumerable<PrintPreviewPage>? pages = null)
    {
        _relayout.Stop();
        var width = Math.Max(160, Pages.ActualWidth - 72);
        var height = Math.Max(200, Pages.ActualHeight - 28 - 12 - 16 - 30); // the padding, the gap and the caption under a page
        foreach (var page in pages ?? (Pages.ItemsSource as IEnumerable<PrintPreviewPage>) ?? [])
        {
            var scale = Math.Min(1, Math.Min(width / page.PaperWidth, height / page.PaperHeight));
            page.Width = Math.Round(page.PaperWidth * scale);
            page.Height = Math.Round(page.PaperHeight * scale);
            page.RenderAgainIfBlurry();
        }
    }

    private void UpdatePreviewColor()
    {
        foreach (var page in _allPages) page.Gray = Gray;
    }

    internal double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private void PageImage_Loaded(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as PrintPreviewPage)?.Realize();
    private void PageImage_Unloaded(object sender, RoutedEventArgs e) => ((sender as FrameworkElement)?.DataContext as PrintPreviewPage)?.Release();

    // ─── Print ───────────────────────────────────────────────────────────────────────────────────

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || !_ready || _selected is null) return;
        var settings = PrintSettings(final: true);
        if (ToPdf)
        {
            var dialog = new SaveFileDialog
            {
                FileName = Dialogs.SafeFileName(_title) + ".pdf",
                Filter = L.T("PDF document (*.pdf)") + "|*.pdf",
                DefaultExt = ".pdf",
            };
            if (dialog.ShowDialog(this) != true) return;
            SetBusy(true, L.T("Saving…"));
            var saved = false;
            try { saved = await Browser.CoreWebView2.PrintToPdfAsync(dialog.FileName, settings); }
            catch (Exception ex) { Serilog.Log.Error(ex, "Saving the PDF failed"); }
            SetBusy(false);
            if (saved) Close();
            else ShowStatus(L.T("The PDF couldn't be saved."), error: true);
            return;
        }

        SetBusy(true, L.T("Sending to the printer…"));
        var status = CoreWebView2PrintStatus.OtherError;
        try { status = await Browser.CoreWebView2.PrintAsync(settings); }
        catch (Exception ex) { Serilog.Log.Error(ex, "Printing failed"); }
        SetBusy(false);
        switch (status)
        {
            case CoreWebView2PrintStatus.Succeeded:
                Close();
                break;
            case CoreWebView2PrintStatus.PrinterUnavailable:
                ShowStatus(L.T("The printer isn't available. Check that it's on and connected."), error: true);
                break;
            default:
                ShowStatus(L.T("Printing failed."), error: true);
                break;
        }
    }

    private void SetBusy(bool busy, string text = "")
    {
        _busy = busy;
        PrintButton.IsEnabled = !busy && !_refreshing && _selected is not null;
        PrinterBox.IsEnabled = !busy;
        ShowStatus(text, busy: busy);
    }

    private void ShowStatus(string text, bool error = false, bool busy = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(error ? "DangerTextBrush" : "MutedTextBrush");
        BusySpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Opens the print window for a message as it's shown (its pictures as allowed there).</summary>
    /// <param name="renderedHtml">The message as rendered for reading.</param>
    public static PrintWindow Open(Window? owner, string renderedHtml, string subject, IEnumerable<(string Label, string Value)> header, string? sender)
    {
        var title = string.IsNullOrWhiteSpace(subject) ? L.T("(no subject)") : subject;
        var window = new PrintWindow(MessageRenderer.ForPrint(renderedHtml, title, header), title, sender);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.Show();
        return window;
    }

    // ─── Dev aids (--screenshot-print) ───────────────────────────────────────────────────────────

    internal int DevPageCount => _allPages.Count;
    internal bool DevReady => _allPages.Count > 0 && !_busy && !_refreshing;
    internal void DevChoose(bool landscape, bool gray, string? pages)
    {
        LayoutBox.SelectedIndex = landscape ? 1 : 0;
        ColorBox.SelectedIndex = gray ? 1 : 0;
        PagesBox.SelectedIndex = pages is null ? 0 : 1;
        if (pages is not null) PageRangeBox.Text = pages;
    }
    internal async Task<bool> DevSavePdfAsync(string path) => await Browser.CoreWebView2.PrintToPdfAsync(path, PrintSettings(final: true));
    internal string DevPrinters => string.Join(" | ", (PrinterBox.ItemsSource as IEnumerable<Choice<string>>)?.Select(c => c.Label) ?? []);

    /// <summary>One page of the preview: its picture is made when it scrolls into view and dropped when it leaves.</summary>
    internal async Task<BitmapSource?> RenderPageAsync(WindowsPdf pdf, uint index, int pixelWidth)
    {
        if (!ReferenceEquals(pdf, _pdf)) return null;
        if (await pdf.RenderAsync(index, (uint)pixelWidth) is not { } picture) return null;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(picture);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}

/// <summary>A page in the print preview.</summary>
public sealed partial class PrintPreviewPage(PrintWindow owner, WindowsPdf pdf, uint index, double paperWidth, double paperHeight) : ObservableObject
{
    public uint Index => index;
    /// <summary>Size on paper, in 1/96 inch.</summary>
    public double PaperWidth => paperWidth;
    public double PaperHeight => paperHeight;

    [ObservableProperty] private double _width;
    [ObservableProperty] private double _height;
    [ObservableProperty] private string _caption = "";
    [ObservableProperty] private ImageSource? _image;

    private BitmapSource? _rendered;
    private int _renderedWidth;
    private int _views; // shown by this many containers (a new list can show the page before the old one lets go)
    private bool Realized => _views > 0;
    private bool _rendering;
    private bool _gray;

    public bool Gray
    {
        get => _gray;
        set
        {
            if (_gray == value) return;
            _gray = value;
            Show();
        }
    }

    public void Realize()
    {
        _views++;
        _ = RenderAsync();
    }

    public void Release()
    {
        if (_views > 0) _views--;
        if (_views > 0) return;
        _rendered = null;
        _renderedWidth = 0;
        Image = null;
    }

    /// <summary>The preview was made bigger than the picture was made for: make it again, sharp.</summary>
    public void RenderAgainIfBlurry()
    {
        if (Realized && _renderedWidth > 0 && PixelWidth > _renderedWidth * 1.1) _ = RenderAsync();
    }

    private int PixelWidth => (int)Math.Ceiling(Width * owner.PixelsPerDip);

    private async Task RenderAsync()
    {
        if (_rendering || Width <= 0) return;
        if (_rendered is not null && PixelWidth <= _renderedWidth * 1.1) return;
        _rendering = true;
        try
        {
            var width = PixelWidth;
            var bitmap = await owner.RenderPageAsync(pdf, index, width);
            if (bitmap is null || !Realized) return;
            _rendered = bitmap;
            _renderedWidth = width;
            Show();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Showing page {Page} of the print preview failed", index + 1);
        }
        finally
        {
            _rendering = false;
        }
    }

    private void Show()
    {
        if (_rendered is null) { Image = null; return; }
        if (!_gray) { Image = _rendered; return; }
        var gray = new FormatConvertedBitmap(_rendered, PixelFormats.Gray8, null, 0);
        gray.Freeze();
        Image = gray;
    }
}
