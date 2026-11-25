using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Plicbox.App.Services;
using Plicbox.Core;
using Plicbox.Mail;

namespace Plicbox.App;

public partial class SettingsWindow : Window
{
    private sealed record ViewChoice(string Key, string Label);
    private sealed record Choice<T>(T Value, string Label);

    private readonly ISettingsStore _settings;
    private readonly IAccountRepository _accounts;
    private readonly ITrustedSenders _trusted;
    private readonly BackupService _backups;
    // Edited here, saved with Save & Close.
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _pictureSenders = [];
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _blockedSenders = [];

    private readonly ThreatListUpdater _threatLists;

    public SettingsWindow(ISettingsStore settings, IAccountRepository accounts, ITrustedSenders trusted, Updates updates, BackupService backups,
        Plicbox.Storage.Database db, IBodyStore bodies, ThreatListUpdater threatLists)
    {
        _threatLists = threatLists;
        InitializeComponent();
        Icon = WindowIcons.Settings;
        (_settings, _accounts, _trusted, _backups) = (settings, accounts, trusted, backups);
        UpdatesSection.DataContext = updates;
        DefaultAppSection.Visibility = Updates.IsInstalled ? Visibility.Visible : Visibility.Collapsed; // a dev build isn't registered
        VersionText.Text = L.F("Plicbox {0}", Updates.Version);
        // A dev build doesn't update: the installed Plicbox does that.
        CheckUpdates.IsEnabled = AutoUpdate.IsEnabled = Updates.IsInstalled;
        PictureSenders.ItemsSource = _pictureSenders;
        BlockedSenders.ItemsSource = _blockedSenders;
        Loaded += async (_, _) => await LoadAsync();
        Loaded += async (_, _) => await ShowSpaceAsync(db.FilePath, (bodies as Plicbox.Storage.FileBodyStore)?.Root);
    }

    /// <summary>Settings > Mail: how much room the mail kept on this computer takes.</summary>
    private async Task ShowSpaceAsync(string dbFile, string? bodiesRoot)
    {
        static long Size(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        var (messages, database) = await Task.Run(() =>
        {
            long total = 0;
            if (bodiesRoot is not null && Directory.Exists(bodiesRoot))
                foreach (var file in new DirectoryInfo(bodiesRoot).EnumerateFiles("*.eml", SearchOption.AllDirectories)) total += file.Length;
            return (total, Size(dbFile) + Size(dbFile + "-wal"));
        });
        OfflineMessages.Text = Bytes(messages);
        OfflineDatabase.Text = Bytes(database);
        OfflineTotal.Text = Bytes(messages + database);
        OfflineBarMessages.Width = new GridLength(messages, GridUnitType.Star);
        OfflineBarDatabase.Width = new GridLength(database, GridUnitType.Star);
        // Nothing yet: an empty bar rather than one filled by nothing.
        OfflineBar.ColumnDefinitions[2].Width = new GridLength(messages + database == 0 ? 1 : 0, GridUnitType.Star);
    }

    /// <summary>"38,214 sites and pages listed, checked 2 hours ago."</summary>
    private string ThreatListStatus(bool enabled)
    {
        if (!enabled) return "";
        var count = Plicbox.Mail.ThreatLists.Current.Count;
        if (count == 0 || _threatLists.LastChecked is not { } checkedAt) return L.T("The lists haven't been downloaded yet.");
        var ago = DateTimeOffset.UtcNow - checkedAt;
        var when = ago.TotalMinutes < 60 ? L.T("less than an hour ago")
            : ago.TotalMinutes < 120 ? L.T("an hour ago")
            : ago.TotalHours < 48 ? L.F("{0} hours ago", (int)ago.TotalHours)
            : L.F("{0} days ago", (int)ago.TotalDays);
        return L.F("{0:N0} sites and pages listed, checked {1}.", count, when);
    }

    private static string Bytes(long n) => n switch
    {
        >= 1L << 30 => L.F("{0:0.0} GB", n / (double)(1L << 30)),
        >= 1L << 20 => L.F("{0:0} MB", n / (double)(1L << 20)),
        _ => L.F("{0:0} KB", Math.Max(1, n / 1024.0)),
    };

    private async Task LoadAsync()
    {
        var s = _settings.Current;
        MinimizeToTray.IsChecked = s.MinimizeToTray;
        CloseToTray.IsChecked = s.CloseToTray;
        RunOnStartup.IsChecked = s.RunOnStartup;
        AutoUpdate.IsChecked = s.AutoUpdate;
        NotifyNewMail.IsChecked = s.NotifyNewMail;
        SenderPictures.IsChecked = s.SenderPictures;
        ThreatLists.IsChecked = s.ThreatLists;
        ThreatListsStatus.Text = ThreatListStatus(s.ThreatLists);
        SyncInterval.Text = s.SyncIntervalMinutes.ToString();

        var choices = new List<ViewChoice> { new("unified", L.T("All Inboxes")) };
        choices.AddRange((await _accounts.GetAllAsync()).Select(a => new ViewChoice($"inbox:{a.Id}", L.F("Inbox – {0}", a.SidebarTitle))));
        StartupView.ItemsSource = choices;
        StartupView.SelectedItem = choices.FirstOrDefault(c => c.Key == s.StartupView) ?? choices[0];

        LoadCalendar(s.Calendar);
        LoadAppearance(s);
        LoadBackup(s);
        Fill(LanguageBox, [new("system", L.T("Same as Windows")), .. L.Languages.Select(l => new Choice<string>(l.Code, l.Name))], s.Language);
        Fill(MarkReadBox, [new(0.0, L.T("As soon as I open them")), new(1.5, L.T("After 1.5 seconds")), new(5.0, L.T("After 5 seconds")),
                           new(-1.0, L.T("Never (I mark them myself)"))], s.MarkReadAfterSeconds);
        Fill(NoticesBox, [new(true, L.T("Compact (one row)")), new(false, L.T("Full sentences"))], s.CompactNotices);
        Fill(UndoSendBox, [new(0, L.T("Off (sends at once)")), new(5, L.T("5 seconds")), new(10, L.T("10 seconds")), new(20, L.T("20 seconds")),
                           new(30, L.T("30 seconds")), new(60, L.T("1 minute")), new(120, L.T("2 minutes")), new(300, L.T("5 minutes"))], s.UndoSendSeconds);

        foreach (var entry in _trusted.All()) _pictureSenders.Add(entry);
        foreach (var entry in s.BlockedSenders.Order(StringComparer.OrdinalIgnoreCase)) _blockedSenders.Add(entry);
        (s.BlockedToTrash ? BlockedToTrash : BlockedToJunk).IsChecked = true;
    }

    private void LoadCalendar(CalendarPrefs c)
    {
        var names = CultureInfo.CurrentCulture.DateTimeFormat;
        // Days in the order the user's week runs, starting with Monday for most of Europe.
        var week = Enumerable.Range(0, 7).Select(i => (DayOfWeek)(((int)names.FirstDayOfWeek + i) % 7)).ToList();

        Fill(FirstDay, week.Select(d => new Choice<DayOfWeek>(d, names.GetDayName(d))), c.FirstDayOfWeek);
        WorkDaysPanel.Children.Clear();
        foreach (var d in week)
            WorkDaysPanel.Children.Add(new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = names.GetAbbreviatedDayName(d), Tag = d, IsChecked = c.WorkDays.Contains(d),
                Width = 46, Height = 32, Padding = new Thickness(0), Margin = new Thickness(0, 0, 6, 0), ToolTip = names.GetDayName(d),
            });

        string Hour(int h) => new DateTime(2000, 1, 1, h % 24, 0, 0).ToString("t", CultureInfo.CurrentCulture);
        Fill(WorkStart, Enumerable.Range(0, 24).Select(h => new Choice<int>(h, Hour(h))), c.WorkDayStartHour);
        Fill(WorkEnd, Enumerable.Range(1, 24).Select(h => new Choice<int>(h, h == 24 ? L.T("Midnight") : Hour(h))), c.WorkDayEndHour);
        Fill(SlotSize, [new(15, L.T("15 minutes")), new(30, L.T("30 minutes")), new(60, L.T("1 hour"))], c.SlotMinutes);
        Fill(DefaultLength, [new(15, L.T("15 minutes")), new(30, L.T("30 minutes")), new(45, L.T("45 minutes")), new(60, L.T("1 hour")),
                             new(90, L.T("1½ hours")), new(120, L.T("2 hours"))], c.DefaultDurationMinutes);
        Fill<int?>(DefaultReminder, [new(null, L.T("No reminder")), new(0, L.T("At start")), new(5, L.T("5 minutes before")), new(10, L.T("10 minutes before")),
                                     new(15, L.T("15 minutes before")), new(30, L.T("30 minutes before")), new(60, L.T("1 hour before"))], c.DefaultReminderMinutes);
        ShowWeekNumbers.IsChecked = c.ShowWeekNumbers;
        NotifyReminders.IsChecked = c.NotifyReminders;
    }

    // ───────── Appearance page: previewed live, put back on Cancel ─────────

    private string _accent = "#F7931E";
    private bool _appearanceLoaded;

    private void LoadAppearance(AppSettings s)
    {
        _accent = s.AccentColor;
        Fill(ThemeBox, [new(Theme.System, L.T("Match Windows")), new("light", L.T("Light")), new("dark", L.T("Dark"))], s.Theme);
        DarkMessages.IsChecked = s.DarkMessages;
        AccentHighlights.IsChecked = s.AccentHighlights;
        BuildSwatches();
        UiFontBox.ItemsSource = Appearance.Fonts;
        UiFontBox.SelectedItem = Appearance.Fonts.Contains(s.UiFont ?? "") ? s.UiFont : Appearance.DefaultFont;
        Fill(MessageZoomBox, Appearance.MessageSizes.Select(m => new Choice<double>(m.Zoom, m.Label)), s.MessageZoom);
        MessageFontBox.ItemsSource = Appearance.MessageFonts;
        MessageFontBox.SelectedItem = Appearance.MessageFonts.Contains(s.MessageFont ?? "") ? s.MessageFont : Appearance.DefaultFont;
        Fill(MessageTextSizeBox, Appearance.MessageTextSizes.Select(m => new Choice<double>(m.Size, m.Label)), s.MessageTextSize);
        ShowMessageTextSample();
        _readingPane = s.ReadingPane;
        BuildReadingPaneChoices();
        _appearanceLoaded = true;
        // Closed without saving: back to what was saved.
        Closed += (_, _) => { if (DialogResult != true) Appearance.Apply(_settings.Current); };
    }

    private void BuildSwatches()
    {
        AccentSwatches.Children.Clear();
        foreach (var swatch in Appearance.Accents)
        {
            var colour = Appearance.Resolve(swatch.Hex);
            var isWindows = swatch.Hex == Appearance.WindowsAccent;
            var tick = (0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B) / 255 > 0.6 ? Brushes.Black : Brushes.White;
            var picked = string.Equals(swatch.Hex, _accent, StringComparison.OrdinalIgnoreCase);
            var dot = new Grid { Width = 34, Height = 34 };
            dot.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Fill = new SolidColorBrush(colour), Stroke = picked ? new SolidColorBrush(colour) : null, StrokeThickness = 0,
            });
            if (picked)
            {
                // The chosen one: a white ring inside and a tick.
                dot.Children.Add(new System.Windows.Shapes.Ellipse { Stroke = tick, StrokeThickness = 2, Margin = new Thickness(3) });
                dot.Children.Add(new TextBlock
                {
                    Text = "\uE73E", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = tick,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
            }
            else if (isWindows)
            {
                // L.T("Match Windows"): a small screen on the swatch, in Windows' current accent.
                dot.Children.Add(new TextBlock
                {
                    Text = "\uE7F4", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 15, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var button = new Button
            {
                Content = dot, ToolTip = isWindows ? L.T("Match Windows (follows your Windows accent colour)") : swatch.Name,
                Margin = new Thickness(0, 0, isWindows ? 22 : 10, 0), Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Template = SwatchTemplate,
            };
            button.Click += (_, _) =>
            {
                _accent = swatch.Hex;
                BuildSwatches();
                PreviewAppearance();
            };
            AccentSwatches.Children.Add(button);
        }
    }

    // ───────── Reading pane: four small pictures of the window ─────────

    private string _readingPane = "bottom";

    private static readonly (string Key, string Label, string Hint)[] ReadingPanes =
    [
        ("bottom", L.T("Bottom"), L.T("Bottom: the message under the list, which keeps all its columns.")),
        ("right", L.T("Right"), L.T("Right: the message beside the list, which shows each message on two lines. Good on wide screens.")),
        ("full", L.T("Fill screen"), L.T("Fill screen: the list uses all the room; a click opens the message in its place, and Back (or Esc) returns to the list.")),
        ("popout", L.T("Popout only"), L.T("Popout only: no reading pane; double-click or Enter opens the message in its own window.")),
    ];

    /// <summary>Dev aid (--screenshot-dialogs): the Reading pane choices scrolled into view.</summary>
    internal void DevShowReadingPane() => ReadingPaneChoices.BringIntoView();
    /// <summary>Dev aid (--screenshot-dialogs): the message text sample scrolled into view.</summary>
    internal void DevShowTextSample() => MessageTextSample.BringIntoView(new Rect(0, 0, 10, 160));
    /// <summary>Dev aid (--screenshot-dialogs): a few entries in the Senders lists (closed unsaved, so never kept).</summary>
    internal void DevFillSenders()
    {
        foreach (var entry in new[] { "news@shop.example", "@bakery.example", "team@travel.example", "hello@studio.example" }) _pictureSenders.Add(entry);
        foreach (var entry in new[] { "@spam.example", "offers@deals.example" }) _blockedSenders.Add(entry);
    }
    /// <summary>Dev aid (--screenshot-dialogs): the Offline mail table scrolled into view.</summary>
    internal void DevShowOfflineSpace() => OfflineTotal.BringIntoView(new Rect(0, 0, 10, 80));

    /// <summary>Dev screenshots: the phishing and malware lists, at the end of the Mail page.</summary>
    internal void DevShowThreatLists() => ThreatListsStatus.BringIntoView(new Rect(0, 0, 10, 60));

    private void BuildReadingPaneChoices()
    {
        ReadingPaneChoices.Children.Clear();
        foreach (var (key, label, hint) in ReadingPanes)
        {
            var picked = key == _readingPane;
            if (picked) ReadingPaneHint.Text = hint;
            // The picture fills its quarter of the row; a thin ring in the accent colour marks the one chosen.
            var frame = new Border
            {
                Child = new Viewbox { Child = ReadingPanePicture(key) }, Padding = new Thickness(4), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1.5), BorderBrush = picked ? (Brush)FindResource("AccentBrush") : Brushes.Transparent,
            };
            var text = new TextBlock
            {
                Text = label, Margin = new Thickness(5.5, 6, 0, 0), // under the picture's left edge
                Foreground = (Brush)FindResource(picked ? "TextBrush" : "SecondaryTextBrush"), FontWeight = picked ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var stack = new StackPanel();
            stack.Children.Add(frame);
            stack.Children.Add(text);
            var button = new Button
            {
                Content = stack, Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Template = SwatchTemplate, ToolTip = hint,
            };
            button.MouseEnter += (_, _) => { if (!picked) frame.BorderBrush = (Brush)FindResource("DividerBrush"); };
            button.MouseLeave += (_, _) => { if (!picked) frame.BorderBrush = Brushes.Transparent; };
            button.Click += (_, _) =>
            {
                _readingPane = key;
                BuildReadingPaneChoices();
            };
            ReadingPaneChoices.Children.Add(button);
        }
    }

    /// <summary>
    /// The main window in miniature, kept light: sidebar, toolbar, the list (white) and where the message goes (in the
    /// accent colour).
    /// </summary>
    private FrameworkElement ReadingPanePicture(string key)
    {
        var canvas = new Canvas { Width = 128, Height = 78, ClipToBounds = true };
        void Box(double x, double y, double w, double h, Brush fill, double radius = 1, bool shadow = false, Brush? stroke = null)
        {
            var box = new Border { Width = w, Height = h, Background = fill, CornerRadius = new CornerRadius(radius) };
            if (stroke is not null) { box.BorderBrush = stroke; box.BorderThickness = new Thickness(1); }
            if (shadow) box.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 8, ShadowDepth = 2, Direction = 270, Opacity = 0.18 };
            Canvas.SetLeft(box, x);
            Canvas.SetTop(box, y);
            canvas.Children.Add(box);
        }
        var chrome = (Brush)FindResource("PanelBackdropBrush");
        var edge = (Brush)FindResource("DividerBrush");
        var surface = (Brush)FindResource("SurfaceBrush");
        // A shade darker than the window around it, or the sidebar disappears into it.
        var sidebar = (Brush)FindResource("StrongDividerBrush");
        var message = (Brush)FindResource("AccentBrush");
        Box(0, 0, 128, 78, chrome, 3, stroke: edge);   // the window
        Box(30, 7, 92, 6, surface);                    // toolbar
        Box(6, 7, 19, 64, sidebar);                    // sidebar
        switch (key)
        {
            case "right":
                Box(30, 18, 34, 54, surface);
                Box(68, 18, 54, 54, message, 1.5);
                break;
            case "full":
                Box(30, 18, 92, 54, message, 1.5);
                break;
            case "popout":
                Box(30, 18, 92, 54, surface);
                Box(44, 26, 64, 38, message, 2, shadow: true);
                break;
            default:
                Box(30, 18, 92, 22, surface);
                Box(30, 44, 92, 28, message, 1.5);
                break;
        }
        return canvas;
    }

    private static readonly ControlTemplate SwatchTemplate = BuildSwatchTemplate();

    private static ControlTemplate BuildSwatchTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = presenter;
        return template;
    }

    private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_appearanceLoaded) return;
        ShowMessageTextSample();
        PreviewAppearance();
    }

    private void AppearanceToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_appearanceLoaded) PreviewAppearance();
    }

    private void PreviewAppearance()
    {
        Appearance.Apply(Picked(ThemeBox, Theme.System), _accent, UiFontBox.SelectedItem as string, Picked(MessageZoomBox, 1.0),
            MessageFontBox.SelectedItem as string, Picked(MessageTextSizeBox, MessageRenderer.DefaultTextSize),
            AccentHighlights.IsChecked == true, DarkMessages.IsChecked == true);
        BuildReadingPaneChoices(); // its pictures are drawn in the accent colour and the mode's greys
    }

    /// <summary>
    /// The sample message in the chosen font, at the size it will really be: the text size times the zoom (CSS pixels
    /// are WPF's units too).
    /// </summary>
    private void ShowMessageTextSample()
    {
        MessageTextSample.FontFamily = new FontFamily((MessageFontBox.SelectedItem as string ?? Appearance.DefaultFont) + ", Segoe UI");
        MessageTextSample.FontSize = Picked(MessageTextSizeBox, MessageRenderer.DefaultTextSize) * Picked(MessageZoomBox, 1.0);
    }

    private static void Fill<T>(ComboBox box, IEnumerable<Choice<T>> items, T selected)
    {
        var list = items.ToList();
        // Keep a value set elsewhere (or by hand in settings.json) visible rather than silently replacing it.
        if (!list.Any(i => EqualityComparer<T>.Default.Equals(i.Value, selected)))
            list.Add(new Choice<T>(selected, selected?.ToString() ?? L.T("None")));
        box.ItemsSource = list;
        box.SelectedItem = list.First(i => EqualityComparer<T>.Default.Equals(i.Value, selected));
    }

    private static T Picked<T>(ComboBox box, T fallback) => box.SelectedItem is Choice<T> c ? c.Value : fallback;

    /// <summary>The page to open on: "general", "mail" or "calendar".</summary>
    public string? StartPage
    {
        set => (value switch { "appearance" => NavAppearance, "mail" => NavMail, "calendar" => NavCalendar, "senders" => NavSenders, "backup" => NavBackup, _ => NavGeneral }).IsChecked = true;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (GeneralPage is null) return; // fires while the window is still being built
        GeneralPage.Visibility = NavGeneral.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = NavAppearance.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MailPage.Visibility = NavMail.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        CalendarPage.Visibility = NavCalendar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        SendersPage.Visibility = NavSenders.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BackupPage.Visibility = NavBackup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageScroll.ScrollToTop();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(SyncInterval.Text, out var minutes) || minutes is < 1 or > 1440)
        {
            NavMail.IsChecked = true;
            ErrorText.Text = L.T("Enter a sync interval between 1 and 1440 minutes.");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }

        var calendar = _settings.Current.Calendar;
        var workStart = Picked(WorkStart, calendar.WorkDayStartHour);
        var workEnd = Picked(WorkEnd, calendar.WorkDayEndHour);
        if (workEnd <= workStart)
        {
            NavCalendar.IsChecked = true;
            ErrorText.Text = L.T("Working hours must end after they start.");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        calendar = calendar with
        {
            FirstDayOfWeek = Picked(FirstDay, calendar.FirstDayOfWeek),
            WorkDays = WorkDaysPanel.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Where(b => b.IsChecked == true).Select(b => (DayOfWeek)b.Tag).ToList(),
            WorkDayStartHour = workStart,
            WorkDayEndHour = workEnd,
            SlotMinutes = Picked(SlotSize, calendar.SlotMinutes),
            DefaultDurationMinutes = Picked(DefaultLength, calendar.DefaultDurationMinutes),
            DefaultReminderMinutes = Picked(DefaultReminder, calendar.DefaultReminderMinutes),
            ShowWeekNumbers = ShowWeekNumbers.IsChecked == true,
            NotifyReminders = NotifyReminders.IsChecked == true,
        };

        var updated = _settings.Current with
        {
            BlockedSenders = _blockedSenders.ToList(),
            BlockedToTrash = BlockedToTrash.IsChecked == true,
            Calendar = calendar,
            MinimizeToTray = MinimizeToTray.IsChecked == true,
            CloseToTray = CloseToTray.IsChecked == true,
            RunOnStartup = RunOnStartup.IsChecked == true,
            AutoUpdate = AutoUpdate.IsChecked == true,
            NotifyNewMail = NotifyNewMail.IsChecked == true,
            SenderPictures = SenderPictures.IsChecked == true,
            ThreatLists = ThreatLists.IsChecked == true,
            SyncIntervalMinutes = minutes,
            Language = Picked(LanguageBox, _settings.Current.Language),
            StartupView = (StartupView.SelectedItem as ViewChoice)?.Key ?? "unified",
            Theme = Picked(ThemeBox, Theme.System),
            AccentColor = _accent,
            AccentHighlights = AccentHighlights.IsChecked == true,
            DarkMessages = DarkMessages.IsChecked == true,
            UiFont = UiFontBox.SelectedItem as string is { } font && font != Appearance.DefaultFont ? font : null,
            MessageZoom = Picked(MessageZoomBox, 1.0),
            ReadingPane = _readingPane,
            MessageFont = MessageFontBox.SelectedItem as string is { } messageFont && messageFont != Appearance.DefaultFont ? messageFont : null,
            MessageTextSize = Picked(MessageTextSizeBox, MessageRenderer.DefaultTextSize),
            MarkReadAfterSeconds = Picked(MarkReadBox, 1.5),
            CompactNotices = Picked(NoticesBox, true),
            UndoSendSeconds = Picked(UndoSendBox, 30),
            BackupDaily = BackupDaily.IsChecked == true,
            BackupFolder = string.Equals(_backupFolder, Plicbox.Storage.Backups.DefaultFolder, StringComparison.OrdinalIgnoreCase) ? null : _backupFolder,
        };
        try
        {
            StartupRegistration.Set(updated.RunOnStartup);
        }
        catch (Exception ex)
        {
            NavGeneral.IsChecked = true;
            ErrorText.Text = L.F("Couldn't change the startup setting: {0}", ex.Message);
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        var languageChanged = updated.Language != _settings.Current.Language;
        _settings.Save(updated);
        if (languageChanged)
            MessageDialog.Show(this, L.T("Language changed"), L.T("Plicbox shows the new language after it restarts."));
        // The pictures list lives in the database: apply what changed.
        var before = _trusted.All().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var gone in before.Where(b => !_pictureSenders.Contains(b, StringComparer.OrdinalIgnoreCase))) _trusted.Untrust(gone);
        foreach (var added in _pictureSenders.Where(a => !before.Contains(a))) _trusted.Trust(added);
        DialogResult = true;
    }

    private void DefaultApp_Click(object sender, RoutedEventArgs e) => DefaultMailApp.OpenWindowsSettings();

    // ───────── Backup page ─────────

    private string _backupFolder = "";

    private void LoadBackup(AppSettings s)
    {
        BackupDaily.IsChecked = s.BackupDaily;
        BackupFolder.Text = _backupFolder = _backups.Folder;
        ShowBackupStatus();
        void Changed() => Dispatcher.BeginInvoke(ShowBackupStatus);
        _backups.Changed += Changed;
        Closed += (_, _) => _backups.Changed -= Changed;
    }

    private void ShowBackupStatus()
    {
        BackUpNow.IsEnabled = !_backups.IsRunning;
        BackupStatus.Text = _backups.IsRunning ? L.T("Backing up…")
            : _backups.LastError is { } error ? L.F("The last backup didn't work: {0}", error)
            : _settings.Current.LastBackup is { } last ? L.F("Last backup: {0:g}", last.LocalDateTime)
            : L.T("Not backed up yet");
    }

    private void ChooseBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L.T("Where should backups go?"),
            InitialDirectory = System.IO.Directory.Exists(_backupFolder) ? _backupFolder : "",
        };
        if (dialog.ShowDialog(this) == true) BackupFolder.Text = _backupFolder = dialog.FolderName;
    }

    private async void BackUpNow_Click(object sender, RoutedEventArgs e)
    {
        try { await _backups.BackUpNowAsync(_backupFolder); }
        catch (Exception) { } // the status line says why
        ShowBackupStatus();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("Choose a backup"),
            Filter = L.T("Plicbox backups") + "|*.zip",
            InitialDirectory = System.IO.Directory.Exists(_backupFolder) ? _backupFolder : "",
        };
        if (dialog.ShowDialog(this) != true) return;

        Plicbox.Storage.BackupSummary summary;
        try { summary = _backups.PrepareRestore(dialog.FileName); }
        catch (Exception ex)
        {
            MessageDialog.Show(this, L.T("Couldn't restore"), ex.Message, DialogKind.Warning);
            return;
        }
        var what = L.F("This backup from {0:g} has {1} accounts, {2} calendars with {3} events, {4} contacts and {5} tasks.",
            summary.Made, summary.Accounts, summary.Calendars, summary.Events, summary.Contacts, summary.Tasks);
        if (!MessageDialog.Ask(this, L.T("Restore this backup?"),
                what + " " + L.T("Everything on this computer is replaced by it, and Plicbox restarts. Mail downloads again from your servers."),
                L.T("Restore and restart"), L.T("Cancel"), DialogKind.Warning))
        {
            _backups.CancelRestore();
            return;
        }
        App.Restart();
    }

    // ───────── Senders page ─────────

    private void AddPictureSender_Click(object sender, RoutedEventArgs e) => AddEntry(NewPictureSender, _pictureSenders, PictureSenders);
    private void AddBlockedSender_Click(object sender, RoutedEventArgs e) => AddEntry(NewBlockedSender, _blockedSenders, BlockedSenders);

    private void NewPictureSender_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true; // Enter adds here instead of saving the whole window
        AddEntry(NewPictureSender, _pictureSenders, PictureSenders);
    }

    private void NewBlockedSender_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        e.Handled = true;
        AddEntry(NewBlockedSender, _blockedSenders, BlockedSenders);
    }

    private void AddEntry(TextBox box, System.Collections.ObjectModel.ObservableCollection<string> list, ListBox view)
    {
        if (Addresses.NormalizeEntry(box.Text) is not { } entry)
        {
            ErrorText.Text = L.T("Enter an address like ana@shop.example, or a domain like @shop.example.");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        if (!list.Contains(entry, StringComparer.OrdinalIgnoreCase)) list.Add(entry);
        view.ScrollIntoView(entry);
        box.Text = "";
    }

    private void RemoveSender_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not string entry) return;
        _pictureSenders.Remove(entry);
        _blockedSenders.Remove(entry);
    }
}
