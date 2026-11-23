using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Plicbox.App.Calendar;
using Plicbox.App.Services;
using Plicbox.App.ViewModels;
using Plicbox.Core;
using Plicbox.Dav;

namespace Plicbox.App;

/// <summary>
/// The calendar module. The views are built in code: they're mostly geometry (events placed by time),
/// which is simpler to express here than in XAML templates.
/// </summary>
public partial class CalendarView : UserControl
{
    private const double SlotHeight = 24;
    private const double RulerWidth = 64;

    private readonly CalendarViewModel _vm;
    private readonly List<DayPanel> _dayPanels = [];
    private ScrollViewer? _gridScroll;
    private double? _scrollOffset;
    private Grid? _gridBody;

    /// <summary>The module switcher at the bottom asked for another module ("mail").</summary>
    public event Action<string>? ModuleRequested;

    /// <summary>Builds the New button's menu (the main window's, with mail, events and more); falls back to the events-only menu.</summary>
    public Func<ContextMenu>? NewMenuFactory { get; set; }

    public CalendarView(CalendarViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.EventsChanged += Rebuild;
        vm.SearchChanged += Rebuild;
        vm.ClockTick += () => { foreach (var p in _dayPanels) p.InvalidateVisual(); };
        vm.PropertyChanged += OnViewModelChanged;
        // Light ⇄ dark: the grid draws its own colours, so build it again.
        Theme.Changed += () => { Rebuild(); BuildMiniMonth(); };
    }

    private CalendarPrefs Prefs => _vm.Prefs;
    private double HourHeight => Math.Max(44, 60.0 / Prefs.SlotMinutes * SlotHeight);
    private static CultureInfo Culture => CultureInfo.CurrentCulture;

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CalendarViewModel.MiniMonth) or nameof(CalendarViewModel.BusyDays) or nameof(CalendarViewModel.Anchor))
            BuildMiniMonth();
        if (e.PropertyName == nameof(CalendarViewModel.Mode)) _scrollOffset = null;
    }

    private void Rebuild()
    {
        // Keep the user's scroll position across redraws, but only one they could actually have scrolled to
        // (a grid built while the calendar was hidden never scrolled and would pin the view to midnight).
        if (_gridScroll is { IsVisible: true, ExtentHeight: > 0 }) _scrollOffset = _gridScroll.VerticalOffset;
        _dayPanels.Clear();
        _gridScroll = null;
        _gridBody = null;
        ViewHost.Content = _vm.IsSearching ? BuildSearchResults() : _vm.Mode switch
        {
            CalendarViewMode.Month => BuildMonth(_vm.Days),
            CalendarViewMode.Agenda => BuildAgenda(_vm.Days),
            _ => BuildTimeGrid(_vm.Days),
        };
        BuildMiniMonth();
    }

    // ═════════════════════════════ time grid (day / work week / week) ═════════════════════════════

    private UIElement BuildTimeGrid(IReadOnlyList<DateOnly> days)
    {
        var events = _vm.Events; // declined invitations stay visible, faded and struck through
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        // Day headers: big date number (a filled circle for today) and the weekday, on a band of their own (a line
        // above, the columns' lines through it), so they read as the grid's head rather than part of the title.
        var header = DayColumns(days.Count, scrollbarGutter: true);
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            // Today as the selected view tab is drawn: its text in blue and a bar along the bottom.
            var isToday = day == CalendarViewModel.Today;
            var link = (Brush)FindResource("LinkBrush");
            var dayText = Prefs.WorkDays.Contains(day.DayOfWeek) ? (Brush)FindResource("TextBrush") : (Brush)FindResource("MutedTextBrush");
            var number = new TextBlock
            {
                Text = day.Day.ToString(Culture), FontSize = 18, FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isToday ? link : (Brush)FindResource("TextBrush"), VerticalAlignment = VerticalAlignment.Center,
            };
            var weekday = new TextBlock
            {
                Text = day.ToString("ddd", Culture), FontSize = 15, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Foreground = isToday ? link : dayText, FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
            };
            var cell = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 9, 0, 9) };
            cell.Children.Add(number);
            cell.Children.Add(weekday);
            var columnContent = new Grid { Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = day.ToString("D", Culture) };
            columnContent.Children.Add(cell);
            if (isToday) columnContent.Children.Add(new Border { Height = 3, Background = link, VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(1.5, 1.5, 0, 0) });
            columnContent.MouseLeftButtonUp += (_, _) => _vm.GoTo(day, CalendarViewMode.Day);
            columnContent.MouseEnter += (_, _) => columnContent.Background = (Brush)FindResource("ListHoverBrush");
            columnContent.MouseLeave += (_, _) => columnContent.Background = Brushes.Transparent;
            columnContent.ContextMenu = DayMenu(day, null);
            var column = new Border { Child = columnContent, BorderBrush = (Brush)FindResource("DividerBrush"), BorderThickness = new Thickness(1, 0, 0, 0) };
            Grid.SetColumn(column, i + 1);
            header.Children.Add(column);
        }
        root.Children.Add(new Border
        {
            Child = header, Background = (Brush)FindResource("AltRowBrush"),
            BorderBrush = (Brush)FindResource("DividerBrush"), BorderThickness = new Thickness(0, 1, 0, 0),
        });

        // All-day strip: all-day and multi-day events as bars across their days.
        var spans = CalendarLayout.LayoutSpans(events, days[0], days.Count);
        // Work week skips days, so map day indexes through the list of shown days.
        if (days.Count != days[^1].DayNumber - days[0].DayNumber + 1)
            spans = CalendarLayout.LayoutSpans(events, days[0], days[^1].DayNumber - days[0].DayNumber + 1)
                .Select(s => MapToShownDays(s, days)).Where(s => s is not null).Select(s => s!).ToList();
        var lanes = spans.Count == 0 ? 0 : spans.Max(s => s.Lane) + 1;
        var allDay = DayColumns(days.Count, scrollbarGutter: true);
        allDay.MinHeight = 28;
        for (var l = 0; l < Math.Max(1, lanes); l++) allDay.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        var label = new TextBlock { Text = "All-day", FontSize = 12, Foreground = (Brush)FindResource("MutedTextBrush"), Margin = new Thickness(8, 5, 0, 0) };
        Grid.SetRowSpan(label, Math.Max(1, lanes));
        allDay.Children.Add(label);
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var cell = new Border
            {
                Background = days.Count > 1 && day == _vm.Anchor ? SelectedDayTint : Brushes.Transparent,
                BorderBrush = (Brush)FindResource("DividerBrush"), BorderThickness = new Thickness(1, 0, 0, 0),
            };
            HoverBackground(cell, SlotTint);
            cell.ContextMenu = DayMenu(day, null);
            cell.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount == 2) _vm.OpenEditor(null, day.ToDateTime(TimeOnly.MinValue), day.AddDays(1).ToDateTime(TimeOnly.MinValue), allDay: true);
            };
            Grid.SetColumn(cell, i + 1);
            Grid.SetRowSpan(cell, Math.Max(1, lanes));
            allDay.Children.Add(cell);
        }
        foreach (var span in spans)
        {
            var bar = MakeSpanBar(span.Event, span.ContinuesBefore, span.ContinuesAfter);
            Grid.SetColumn(bar, span.FirstDay + 1);
            Grid.SetColumnSpan(bar, span.Days);
            Grid.SetRow(bar, span.Lane);
            allDay.Children.Add(bar);
        }
        var allDayBorder = new Border
        {
            Child = allDay, BorderBrush = (Brush)FindResource("DividerBrush"), BorderThickness = new Thickness(0, 1, 0, 1),
        };
        Grid.SetRow(allDayBorder, 1);
        root.Children.Add(allDayBorder);

        // Scrolling hours: ruler + one DayPanel per day.
        var body = DayColumns(days.Count, scrollbarGutter: false);
        body.Height = HourHeight * 24;
        body.Children.Add(BuildRuler());
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            var panel = new DayPanel
            {
                Day = day, HourHeight = HourHeight, SlotMinutes = Prefs.SlotMinutes,
                WorkStartHour = Prefs.WorkDayStartHour, WorkEndHour = Prefs.WorkDayEndHour, IsWorkDay = Prefs.WorkDays.Contains(day.DayOfWeek),
                IsSelected = days.Count > 1 && day == _vm.Anchor,
            };
            foreach (var p in CalendarLayout.LayoutDay(events, day))
            {
                var block = MakeTimedBlock(p.Event, p.Start, p.End);
                DayPanel.SetStartMinute(block, (p.Start - day.ToDateTime(TimeOnly.MinValue)).TotalMinutes);
                DayPanel.SetEndMinute(block, Math.Max((p.End - day.ToDateTime(TimeOnly.MinValue)).TotalMinutes,
                    (p.Start - day.ToDateTime(TimeOnly.MinValue)).TotalMinutes + CalendarLayout.MinVisible.TotalMinutes));
                DayPanel.SetColumn(block, p.Column);
                DayPanel.SetColumns(block, p.Columns);
                panel.Children.Add(block);
            }
            HookCreateSelection(panel);
            Grid.SetColumn(panel, i + 1);
            body.Children.Add(panel);
            _dayPanels.Add(panel);
        }

        _gridBody = body;
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Visible, Focusable = false };
        _gridScroll = scroll;
        var todayShown = days.Contains(CalendarViewModel.Today);
        var firstHour = todayShown && DateTime.Now.Hour > Prefs.WorkDayStartHour + 1 ? DateTime.Now.Hour - 2 : Prefs.WorkDayStartHour - 0.5;
        var target = _scrollOffset ?? Math.Max(0, firstHour * HourHeight);
        // The extent only exists once the grid has been laid out while visible (not in Loaded, and not while the
        // calendar module is collapsed), so scroll on the first change that brings a real extent.
        void ApplyInitialScroll(object? sender, ScrollChangedEventArgs e)
        {
            if (scroll.ExtentHeight <= 0 || scroll.ViewportHeight <= 0) return;
            scroll.ScrollChanged -= ApplyInitialScroll;
            scroll.ScrollToVerticalOffset(target);
        }
        scroll.ScrollChanged += ApplyInitialScroll;
        // The day header and all-day rows line up with the hour grid: they leave exactly the room its scrollbar takes
        // (none when the scrollbar floats over the grid).
        void SyncGutters()
        {
            var room = new GridLength(Math.Max(0, scroll.ActualWidth - scroll.ViewportWidth));
            header.ColumnDefinitions[^1].Width = room;
            allDay.ColumnDefinitions[^1].Width = room;
        }
        scroll.ScrollChanged += (_, _) => SyncGutters();
        scroll.SizeChanged += (_, _) => SyncGutters();
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);
        return root;
    }

    private static SpanPlacement? MapToShownDays(SpanPlacement s, IReadOnlyList<DateOnly> days)
    {
        var first = days[0];
        var covered = Enumerable.Range(s.FirstDay, s.Days).Select(i => first.AddDays(i)).ToHashSet();
        var shown = days.Select((d, i) => (d, i)).Where(x => covered.Contains(x.d)).Select(x => x.i).ToList();
        return shown.Count == 0 ? null : s with { FirstDay = shown[0], Days = shown[^1] - shown[0] + 1 };
    }

    /// <summary>A grid with the ruler column plus one star column per day (and room for the scrollbar in fixed rows).</summary>
    private static Grid DayColumns(int days, bool scrollbarGutter)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(RulerWidth) });
        for (var i = 0; i < days; i++) g.ColumnDefinitions.Add(new ColumnDefinition());
        if (scrollbarGutter) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) }); // sized to the grid's scrollbar (SyncGutters)
        return g;
    }

    /// <summary>Hour labels in eM Client's style: a big hour with small minutes ("08 ⁰⁰").</summary>
    private UIElement BuildRuler()
    {
        var canvas = new Canvas { Height = HourHeight * 24, Background = Theme.Brush("SurfaceBrush") };
        var muted = (Brush)FindResource("MutedTextBrush");
        for (var h = 1; h < 24; h++)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock { Text = h.ToString("00", Culture), FontSize = 17, Foreground = muted });
            label.Children.Add(new TextBlock { Text = "00", FontSize = 9, Foreground = muted, Margin = new Thickness(2, 3, 0, 0) });
            Canvas.SetLeft(label, 12);
            Canvas.SetTop(label, h * HourHeight - 11);
            canvas.Children.Add(label);
        }
        return canvas;
    }

    // ───────── event visuals ─────────

    private string ColorOf(CalendarEvent e) => e.Color ?? _vm.CalendarOf(e)?.Color ?? "#4285F4";

    private Border MakeTimedBlock(CalendarEvent e, DateTime start, DateTime end)
    {
        var color = ColorOf(e);
        var declined = e.MyResponse == "declined";
        var unanswered = e.MyResponse is "needsAction" or "tentative";
        var text = CalendarBrushes.Shade(color);

        var title = new TextBlock
        {
            Text = e.Title, FontWeight = FontWeights.SemiBold, FontSize = 12.5, Foreground = text,
            TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxHeight = 34,
            TextDecorations = declined ? TextDecorations.Strikethrough : null,
        };
        var details = new TextBlock
        {
            Text = $"{start.ToString("t", Culture)} – {end.ToString("t", Culture)}" + (e.Location is { Length: > 0 } l ? $" · {l}" : ""),
            FontSize = 11.5, Foreground = text, Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var compact = end - start < TimeSpan.FromMinutes(45);
        if (compact)
        {
            // Short events get one line: "Standup 09:30".
            title.TextWrapping = TextWrapping.NoWrap;
            title.FontSize = 12;
            title.Inlines.Add(new System.Windows.Documents.Run("  " + start.ToString("t", Culture)) { FontWeight = FontWeights.Normal });
            details.Visibility = Visibility.Collapsed;
        }
        var stack = new DockPanel { LastChildFill = true };
        if (e.MeetingUrl is not null)
        {
            var cam = new TextBlock { Style = (Style)FindResource("Icon"), Text = "", FontSize = 12, Foreground = text, Margin = new Thickness(4, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(cam, Dock.Right);
            stack.Children.Add(cam);
        }
        var inner = new StackPanel();
        inner.Children.Add(title);
        inner.Children.Add(details);
        stack.Children.Add(inner);

        // A soft block in the calendar's colour (no side stripe): busy time fuller, "free" time lighter; an unanswered
        // invitation is only outlined; declined ones fade out.
        var block = new Border
        {
            Child = stack, CornerRadius = new CornerRadius(6), Padding = compact ? new Thickness(7, 0, 5, 0) : new Thickness(8, 4, 6, 4),
            BorderThickness = unanswered ? new Thickness(1) : new Thickness(0),
            BorderBrush = CalendarBrushes.For(color),
            Background = unanswered ? Theme.Brush("SurfaceBrush") : CalendarBrushes.Tint(color, e.IsBusy ? 0.32 : 0.16),
            Opacity = declined ? 0.55 : 1,
            Tag = e, ToolTip = Describe(e), ContextMenu = BuildEventMenu(e), Cursor = Cursors.Hand,
        };
        // A little deeper while the mouse is over it.
        HoverBackground(block, unanswered ? CalendarBrushes.Tint(color, 0.1) : CalendarBrushes.Tint(color, e.IsBusy ? 0.44 : 0.26));
        HookEventDrag(block, e);
        return block;
    }

    private Border MakeSpanBar(CalendarEvent e, bool continuesBefore, bool continuesAfter)
    {
        var color = ColorOf(e);
        var bar = new Border
        {
            Background = CalendarBrushes.For(color), Margin = new Thickness(continuesBefore ? 0 : 3, 2, continuesAfter ? 0 : 3, 2),
            CornerRadius = new CornerRadius(continuesBefore ? 0 : 4, continuesAfter ? 0 : 4, continuesAfter ? 0 : 4, continuesBefore ? 0 : 4),
            Padding = new Thickness(8, 0, 6, 0), Tag = e, ToolTip = Describe(e), ContextMenu = BuildEventMenu(e), Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = (continuesBefore ? "◂ " : "") + e.Title, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
            },
            Opacity = e.MyResponse == "declined" ? 0.55 : 1,
        };
        bar.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount == 2) { _vm.OpenCommand.Execute(e); args.Handled = true; }
        };
        HoverBackground(bar, Darker(color));
        return bar;
    }

    private string Describe(CalendarEvent e)
    {
        var when = e.IsAllDay
            ? (e.EndDate.DayNumber - e.StartDate.DayNumber <= 1 ? e.StartDate.ToString("D", Culture)
                : $"{e.StartDate.ToString("d", Culture)} – {e.EndDate.AddDays(-1).ToString("d", Culture)}")
            : $"{e.Start.LocalDateTime.ToString("g", Culture)} – {e.End.LocalDateTime.ToString(e.Start.Date == e.End.Date ? "t" : "g", Culture)}";
        var lines = new List<string> { e.Title, when };
        if (e.Location is { Length: > 0 } l) lines.Add(l);
        if (_vm.CalendarOf(e) is { } cal) lines.Add(cal.Name);
        if (e.IsRecurring) lines.Add(L.T("Repeats"));
        if (e.MyResponse is { } r) lines.Add(r switch { "accepted" => L.T("You're going"), "declined" => L.T("You declined"), "tentative" => L.T("You said maybe"), _ => L.T("Not answered yet") });
        return string.Join("\n", lines);
    }

    private ContextMenu BuildEventMenu(CalendarEvent e)
    {
        var menu = new ContextMenu();
        MenuItem Item(string header, string glyph, Action action, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, Icon = new TextBlock { Style = (Style)FindResource("Icon"), Text = glyph, FontSize = 16 } };
            mi.Click += (_, _) => action();
            return mi;
        }
        menu.Items.Add(Item(L.T("Open"), "", () => _vm.OpenCommand.Execute(e)));
        if (e.MeetingUrl is not null) menu.Items.Add(Item(L.T("Join video call"), "", () => _vm.JoinCommand.Execute(e)));
        if (e.MyResponse is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Accept"), "", () => _vm.AcceptCommand.Execute(e), e.MyResponse != "accepted"));
            menu.Items.Add(Item(L.T("Maybe"), "", () => _vm.TentativeCommand.Execute(e), e.MyResponse != "tentative"));
            menu.Items.Add(Item(L.T("Decline"), "", () => _vm.DeclineCommand.Execute(e), e.MyResponse != "declined"));
        }
        menu.Items.Add(new Separator());
        if (e.HtmlLink is not null) menu.Items.Add(Item(L.T("Open in Google Calendar"), "", () => _vm.OpenInGoogleCommand.Execute(e)));
        menu.Items.Add(Item(L.T("Delete"), "", () => _vm.DeleteCommand.Execute(e), _vm.CanEdit(e)));
        return menu;
    }

    // ───────── create by dragging across empty time ─────────

    private sealed class Selection
    {
        public required DayPanel Panel { get; init; }
        public required Border Box { get; init; }
        public double Anchor { get; init; }
        public bool Moved { get; set; }
    }

    private Selection? _selection;
    private Popup? _quickCreate;

    private double Snap(double minute) => Math.Round(minute / Prefs.SlotMinutes) * Prefs.SlotMinutes;
    private double SnapDown(double minute) => Math.Floor(minute / Prefs.SlotMinutes) * Prefs.SlotMinutes;

    private void HookCreateSelection(DayPanel panel)
    {
        // Hover: the slot under the mouse lights up with its time, so what a click makes is clear beforehand.
        var ghostTime = new TextBlock { FontSize = 11.5, Foreground = (Brush)FindResource("LinkBrush"), Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var ghost = new Border
        {
            Background = Frozen(Color.FromArgb(0x16, 0x1A, 0x73, 0xE8)), IsHitTestVisible = false,
            Visibility = Visibility.Collapsed, Child = ghostTime,
        };
        DayPanel.SetFillsSlot(ghost, true);
        panel.Children.Add(ghost);
        panel.MouseMove += (_, e) =>
        {
            if (_selection is not null || _drag is not null || !ReferenceEquals(e.OriginalSource, panel) || e.LeftButton == MouseButtonState.Pressed)
            {
                ghost.Visibility = Visibility.Collapsed;
                return;
            }
            var minute = SnapDown(panel.YToMinute(e.GetPosition(panel).Y));
            DayPanel.SetStartMinute(ghost, minute);
            DayPanel.SetEndMinute(ghost, minute + Prefs.SlotMinutes);
            ghostTime.Text = "+ " + panel.Day.ToDateTime(TimeOnly.MinValue).AddMinutes(minute).ToString("t", Culture);
            ghost.Visibility = Visibility.Visible;
        };
        panel.MouseLeave += (_, _) => ghost.Visibility = Visibility.Collapsed;
        // Right-click on empty time: a new event right there, and the usual ways around.
        panel.ContextMenu = new ContextMenu();
        panel.ContextMenuOpening += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, panel)) return; // events have their own menu
            var minute = SnapDown(panel.YToMinute(Mouse.GetPosition(panel).Y));
            panel.ContextMenu = DayMenu(panel.Day, panel.Day.ToDateTime(TimeOnly.MinValue).AddMinutes(minute));
        };
        panel.MouseLeftButtonDown += (_, e) =>
        {
            if (!ReferenceEquals(e.OriginalSource, panel)) return; // clicks on events are theirs
            ghost.Visibility = Visibility.Collapsed;
            CloseQuickCreate();
            var minute = SnapDown(panel.YToMinute(e.GetPosition(panel).Y));
            if (e.ClickCount == 2)
            {
                var start = panel.Day.ToDateTime(TimeOnly.MinValue).AddMinutes(minute);
                _vm.OpenEditor(null, start, start.AddMinutes(Prefs.DefaultDurationMinutes), allDay: false);
                return;
            }
            var box = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0x55, 0x1A, 0x73, 0xE8)), BorderBrush = (Brush)FindResource("LinkBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5), IsHitTestVisible = false,
            };
            DayPanel.SetStartMinute(box, minute);
            DayPanel.SetEndMinute(box, minute + Prefs.SlotMinutes);
            panel.Children.Add(box);
            _selection = new Selection { Panel = panel, Box = box, Anchor = minute };
            panel.CaptureMouse();
            e.Handled = true;
        };
        panel.MouseMove += (_, e) =>
        {
            if (_selection is not { } sel || sel.Panel != panel || e.LeftButton != MouseButtonState.Pressed) return;
            var minute = Snap(panel.YToMinute(e.GetPosition(panel).Y));
            var (start, end) = minute >= sel.Anchor + Prefs.SlotMinutes ? (sel.Anchor, minute)
                : minute <= sel.Anchor ? (minute, sel.Anchor + Prefs.SlotMinutes)
                : (sel.Anchor, sel.Anchor + Prefs.SlotMinutes);
            sel.Moved |= Math.Abs(end - start - Prefs.SlotMinutes) > 0.1 || start != sel.Anchor;
            DayPanel.SetStartMinute(sel.Box, start);
            DayPanel.SetEndMinute(sel.Box, Math.Min(end, 24 * 60));
        };
        panel.MouseLeftButtonUp += (_, _) =>
        {
            if (_selection is not { } sel || sel.Panel != panel) return;
            panel.ReleaseMouseCapture();
            var start = DayPanel.GetStartMinute(sel.Box);
            var end = sel.Moved ? DayPanel.GetEndMinute(sel.Box) : start + Prefs.DefaultDurationMinutes;
            DayPanel.SetEndMinute(sel.Box, Math.Min(end, 24 * 60));
            var day = panel.Day.ToDateTime(TimeOnly.MinValue);
            ShowQuickCreate(sel.Box, day.AddMinutes(start), day.AddMinutes(end));
        };
    }

    /// <summary>A small card at the selection: type a title and press Enter, or open the full editor.</summary>
    private void ShowQuickCreate(Border anchor, DateTime start, DateTime end)
    {
        var calendars = _vm.WritableCalendars;
        if (calendars.Count == 0)
        {
            RemoveSelection();
            _vm.OpenEditor(null, start, end, allDay: false);
            return;
        }

        var title = new TextBox { FontSize = 15, Padding = new Thickness(4, 5, 4, 5), Tag = L.T("Add a title") };
        var calendarBox = new ComboBox { ItemsSource = calendars, SelectedIndex = 0, Margin = new Thickness(0, 10, 0, 0), MinWidth = 240 };
        calendarBox.ItemTemplate = CalendarChoiceTemplate();
        var save = new Button { Content = L.T("Save"), Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(16, 5, 16, 5), IsDefault = true };
        var more = new Button { Content = L.T("More options"), Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(0, 0, 8, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(more);
        buttons.Children.Add(save);

        var when = new TextBlock
        {
            Text = $"{start.ToString("dddd, d. MMMM", Culture)} · {start.ToString("t", Culture)} – {end.ToString("t", Culture)}",
            Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 12, Margin = new Thickness(0, 0, 0, 8),
        };
        var card = new StackPanel { Width = 320 };
        card.Children.Add(when);
        card.Children.Add(title);
        card.Children.Add(calendarBox);
        card.Children.Add(buttons);

        var popup = new Popup
        {
            PlacementTarget = anchor, Placement = PlacementMode.Right, HorizontalOffset = 8, StaysOpen = false, AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = new Border
            {
                Child = card, Background = Theme.Brush("SurfaceBrush"), Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(10),
                BorderBrush = Theme.Brush("StrongDividerBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 14, 14),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.2 },
            },
        };
        popup.SetResourceReference(TextElement.FontFamilyProperty, "UiFont"); // a popup doesn't take the window's font
        async Task CreateAsync()
        {
            if (string.IsNullOrWhiteSpace(title.Text) || calendarBox.SelectedItem is not CalendarInfo cal) return;
            popup.IsOpen = false;
            await _vm.CreateQuickAsync(cal.Id, title.Text.Trim(), start, end, allDay: false);
        }
        save.Click += async (_, _) => await CreateAsync();
        title.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await CreateAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; popup.IsOpen = false; }
        };
        more.Click += (_, _) =>
        {
            popup.IsOpen = false;
            _vm.OpenEditor(null, start, end, allDay: false, title.Text, (calendarBox.SelectedItem as CalendarInfo)?.Id);
        };
        popup.Closed += (_, _) => RemoveSelection();
        _quickCreate = popup;
        popup.IsOpen = true;
        title.Focus();
    }

    private DataTemplate CalendarChoiceTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var dot = new FrameworkElementFactory(typeof(Border));
        dot.SetValue(WidthProperty, 12.0);
        dot.SetValue(HeightProperty, 12.0);
        dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        dot.SetValue(MarginProperty, new Thickness(0, 0, 10, 0));
        dot.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding(nameof(CalendarInfo.Color)) { Converter = new HexToBrushConverter() });
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(CalendarInfo.Name)));
        panel.AppendChild(dot);
        panel.AppendChild(name);
        return new DataTemplate { VisualTree = panel };
    }

    private void CloseQuickCreate()
    {
        if (_quickCreate is { IsOpen: true }) _quickCreate.IsOpen = false;
        RemoveSelection();
    }

    private void RemoveSelection()
    {
        if (_selection is { } sel) sel.Panel.Children.Remove(sel.Box);
        _selection = null;
    }

    // ───────── move and resize by dragging ─────────

    private sealed class Drag
    {
        public required Border Block { get; init; }
        public required CalendarEvent Event { get; init; }
        public required bool Resize { get; init; }
        public required Point Origin { get; init; }
        public required DayPanel OriginPanel { get; init; }
        public required double OriginStart { get; init; }
        public required double OriginEnd { get; init; }
        public DayPanel Panel { get; set; } = null!;
        public bool Started { get; set; }
        public double MinuteDelta { get; set; }
        public int DayDelta { get; set; }
    }

    private Drag? _drag;

    private void HookEventDrag(Border block, CalendarEvent e)
    {
        var editable = _vm.CanEdit(e) && e.MyResponse is null;
        block.MouseMove += (_, args) =>
        {
            if (_drag is null && editable)
                block.Cursor = args.GetPosition(block).Y > block.ActualHeight - 6 ? Cursors.SizeNS : Cursors.Hand;
        };
        block.MouseLeftButtonDown += (_, args) =>
        {
            CloseQuickCreate();
            if (args.ClickCount == 2)
            {
                _vm.OpenCommand.Execute(e);
                args.Handled = true;
                return;
            }
            if (!editable || _gridBody is null || block.Parent is not DayPanel panel) { args.Handled = true; return; }
            _drag = new Drag
            {
                Block = block, Event = e, Resize = args.GetPosition(block).Y > block.ActualHeight - 6, Origin = args.GetPosition(_gridBody),
                OriginPanel = panel, Panel = panel, OriginStart = DayPanel.GetStartMinute(block), OriginEnd = DayPanel.GetEndMinute(block),
            };
            block.CaptureMouse();
            args.Handled = true;
        };
        block.MouseMove += (_, args) =>
        {
            if (_drag is not { } d || d.Block != block || _gridBody is null) return;
            var pos = args.GetPosition(_gridBody);
            if (!d.Started && (pos - d.Origin).Length < 4) return;
            if (!d.Started)
            {
                d.Started = true;
                block.Opacity = 0.85;
                DayPanel.SetColumn(block, 0);
                DayPanel.SetColumns(block, 1);
                Panel.SetZIndex(block, 100);
            }
            d.MinuteDelta = Snap((pos.Y - d.Origin.Y) / HourHeight * 60);
            if (d.Resize)
            {
                DayPanel.SetEndMinute(block, Math.Clamp(d.OriginEnd + d.MinuteDelta, d.OriginStart + Prefs.SlotMinutes, 24 * 60));
                return;
            }
            // Moving: follow the mouse into another day's column too.
            var target = _dayPanels.FirstOrDefault(p => { var x = p.TranslatePoint(new Point(0, 0), _gridBody).X; return pos.X >= x && pos.X < x + p.ActualWidth; }) ?? d.Panel;
            if (target != d.Panel)
            {
                d.Panel.Children.Remove(block);
                target.Children.Add(block);
                d.Panel = target;
                block.CaptureMouse();
            }
            d.DayDelta = target.Day.DayNumber - d.OriginPanel.Day.DayNumber;
            var length = d.OriginEnd - d.OriginStart;
            var start = Math.Clamp(d.OriginStart + d.MinuteDelta, 0, 24 * 60 - length);
            DayPanel.SetStartMinute(block, start);
            DayPanel.SetEndMinute(block, start + length);
            d.MinuteDelta = start - d.OriginStart;
        };
        block.MouseLeftButtonUp += async (_, _) =>
        {
            if (_drag is not { } d || d.Block != block) return;
            block.ReleaseMouseCapture();
            _drag = null;
            if (!d.Started || (d.MinuteDelta == 0 && d.DayDelta == 0)) { Rebuild(); return; }
            var shift = TimeSpan.FromMinutes(d.MinuteDelta) + TimeSpan.FromDays(d.DayDelta);
            var newStart = d.Event.Start.LocalDateTime + (d.Resize ? TimeSpan.Zero : shift);
            var newEnd = d.Event.End.LocalDateTime + shift;
            await _vm.MoveAsync(d.Event, newStart, newEnd);
        };
    }

    // ═════════════════════════════ month ═════════════════════════════

    private UIElement BuildMonth(IReadOnlyList<DateOnly> days)
    {
        var month = _vm.Anchor.Month;
        var showWeeks = Prefs.ShowWeekNumbers;
        var grid = new Grid { Margin = new Thickness(8, 0, 16, 12) };
        if (showWeeks) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        for (var c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var r = 0; r < 6; r++) grid.RowDefinitions.Add(new RowDefinition());
        var offset = showWeeks ? 1 : 0;
        var muted = (Brush)FindResource("MutedTextBrush");
        var divider = (Brush)FindResource("DividerBrush");

        for (var c = 0; c < 7; c++)
        {
            var name = new TextBlock { Text = days[c].ToString("dddd", Culture), Foreground = muted, Margin = new Thickness(8, 0, 0, 6), FontSize = 13 };
            Grid.SetColumn(name, c + offset);
            grid.Children.Add(name);
        }

        var byDay = new Dictionary<DateOnly, List<CalendarEvent>>();
        foreach (var e in _vm.Events.OrderByDescending(CalendarLayout.IsSpanning).ThenBy(e => e.Start))
            for (var d = CalendarLayout.StartDay(e); d < CalendarLayout.EndDay(e); d = d.AddDays(1))
            {
                if (d < days[0] || d > days[^1]) continue;
                if (!byDay.TryGetValue(d, out var list)) byDay[d] = list = [];
                list.Add(e);
            }

        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i];
            int row = i / 7 + 1, col = i % 7;
            if (showWeeks && col == 0)
            {
                var kw = new TextBlock { Text = Calendars.IsoWeek(day).ToString(Culture), Foreground = muted, FontSize = 11, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetRow(kw, row);
                grid.Children.Add(kw);
            }

            // Today: its number in blue and a bar along the top of the cell, as the week view's header has below.
            var isToday = day == CalendarViewModel.Today;
            var number = new Border
            {
                CornerRadius = new CornerRadius(4), MinWidth = 22, Height = 22, Padding = new Thickness(5, 0, 5, 0), HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent, Margin = new Thickness(4, isToday ? 1 : 4, 0, 2), Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = day.Day == 1 ? day.ToString("d. MMM", Culture) : day.Day.ToString(Culture), FontSize = 13,
                    FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = isToday ? (Brush)FindResource("LinkBrush") : day.Month == month ? (Brush)FindResource("TextBrush") : muted,
                },
            };
            number.MouseLeftButtonUp += (_, _) => _vm.GoTo(day, CalendarViewMode.Day);
            HoverBackground(number, HoverTint);

            var chips = new StackPanel { Margin = new Thickness(3, 0, 3, 2) };
            var dayEvents = byDay.GetValueOrDefault(day) ?? [];
            const int maxChips = 4;
            foreach (var e in dayEvents.Take(dayEvents.Count > maxChips ? maxChips - 1 : maxChips)) chips.Children.Add(MakeMonthChip(e));
            if (dayEvents.Count > maxChips)
            {
                var more = new TextBlock { Text = $"+{dayEvents.Count - (maxChips - 1)} more", Foreground = (Brush)FindResource("LinkBrush"), FontSize = 12, Margin = new Thickness(6, 1, 0, 0), Cursor = Cursors.Hand };
                more.MouseLeftButtonUp += (_, _) => _vm.GoTo(day, CalendarViewMode.Day);
                chips.Children.Add(more);
            }

            var panel = new DockPanel { LastChildFill = true };
            if (isToday)
            {
                var bar = new Border { Height = 3, Background = (Brush)FindResource("LinkBrush") };
                DockPanel.SetDock(bar, Dock.Top);
                panel.Children.Add(bar);
            }
            DockPanel.SetDock(number, Dock.Top);
            panel.Children.Add(number);
            panel.Children.Add(chips);
            var cell = new Border
            {
                Child = panel, BorderBrush = divider, BorderThickness = new Thickness(col == 0 ? 1 : 0, row == 1 ? 1 : 0, 1, 1), ClipToBounds = true,
                Background = day == _vm.Anchor ? SelectedDayTint
                    : day.Month != month ? OtherMonthFill
                    : Prefs.WorkDays.Contains(day.DayOfWeek) ? Theme.Brush("SurfaceBrush") : WeekendFill,
            };
            HoverBackground(cell, day.Month != month ? OtherMonthHover : SlotTint);
            cell.ContextMenu = DayMenu(day, null);
            cell.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2 || e.OriginalSource is not (Border or DockPanel or StackPanel)) return;
                var start = day.ToDateTime(new TimeOnly(Math.Clamp(Prefs.WorkDayStartHour + 1, 0, 23), 0));
                _vm.OpenEditor(null, start, start.AddMinutes(Prefs.DefaultDurationMinutes), allDay: false);
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col + offset);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private FrameworkElement MakeMonthChip(CalendarEvent e)
    {
        var color = ColorOf(e);
        FrameworkElement chip;
        if (CalendarLayout.IsSpanning(e))
        {
            chip = new Border
            {
                Background = CalendarBrushes.For(color), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 1, 0, 1),
                Child = new TextBlock { Text = e.Title, Foreground = Brushes.White, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
            };
        }
        else
        {
            var row = new DockPanel { Margin = new Thickness(2, 1, 0, 1), Background = Brushes.Transparent };
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = CalendarBrushes.For(color), Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var time = new TextBlock { Text = e.Start.LocalDateTime.ToString("t", Culture), Foreground = (Brush)FindResource("MutedTextBrush"), FontSize = 12, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            DockPanel.SetDock(time, Dock.Left);
            row.Children.Add(dot);
            row.Children.Add(time);
            row.Children.Add(new TextBlock { Text = e.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
            chip = row;
        }
        if (chip is Border spanning) HoverBackground(spanning, Darker(color));
        else if (chip is DockPanel line)
        {
            line.MouseEnter += (_, _) => line.Background = HoverTint;
            line.MouseLeave += (_, _) => line.Background = Brushes.Transparent;
        }
        chip.Cursor = Cursors.Hand;
        chip.ToolTip = Describe(e);
        chip.ContextMenu = BuildEventMenu(e);
        chip.Opacity = e.MyResponse == "declined" ? 0.55 : 1;
        chip.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount == 2) { _vm.OpenCommand.Execute(e); args.Handled = true; }
        };
        return chip;
    }

    // ═════════════════════════════ agenda ═════════════════════════════

    private UIElement BuildAgenda(IReadOnlyList<DateOnly> days)
    {
        // A day per block: its date on the left (number, weekday, month), its events on the right, a line between days.
        var list = new StackPanel { Margin = new Thickness(24, 4, 24, 24) };
        var muted = (Brush)FindResource("MutedTextBrush");
        var accent = (Brush)FindResource("LinkBrush"); // today, in the calendar's blue (as in the other views)
        var hover = (Brush)FindResource("ChromeHoverBrush");
        var any = false;
        foreach (var day in days)
        {
            var dayEvents = _vm.Events
                .Where(e => CalendarLayout.StartDay(e) <= day && CalendarLayout.EndDay(e) > day)
                .OrderByDescending(CalendarLayout.IsSpanning).ThenBy(e => e.Start).ToList();
            if (dayEvents.Count == 0) continue;
            if (any) list.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("DividerBrush"), Margin = new Thickness(0, 10, 0, 10) });
            any = true;

            var isToday = day == CalendarViewModel.Today;
            var block = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
            block.ColumnDefinitions.Add(new ColumnDefinition());
            var date = new StackPanel { Margin = new Thickness(2, 2, 12, 0) };
            var number = new TextBlock { Text = day.Day.ToString(Culture), FontSize = 28, FontWeight = FontWeights.Light, LineHeight = 32, LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
            var weekday = new TextBlock
            {
                Text = isToday ? L.T("Today") : day == CalendarViewModel.Today.AddDays(1) ? L.T("Tomorrow") : day.ToString("dddd", Culture),
                FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (isToday) number.Foreground = weekday.Foreground = accent;
            date.Children.Add(number);
            date.Children.Add(weekday);
            var month = day.ToString(day.Year == CalendarViewModel.Today.Year ? "MMMM" : "MMMM yyyy", Culture);
            if (isToday || day == CalendarViewModel.Today.AddDays(1)) month = $"{day.ToString("ddd", Culture)}, {month}";
            date.Children.Add(new TextBlock { Text = month, FontSize = 12, Foreground = muted, TextTrimming = TextTrimming.CharacterEllipsis });
            block.Children.Add(date);
            var rows = new StackPanel();
            Grid.SetColumn(rows, 1);
            block.Children.Add(rows);
            list.Children.Add(block);

            foreach (var e in dayEvents)
            {
                var row = new Grid();
                var rowBox = new Border
                {
                    Child = row, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = Brushes.Transparent,
                    Cursor = Cursors.Hand, ToolTip = Describe(e), ContextMenu = BuildEventMenu(e),
                };
                rowBox.MouseEnter += (_, _) => rowBox.Background = hover;
                rowBox.MouseLeave += (_, _) => rowBox.Background = Brushes.Transparent;
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var when = CalendarLayout.IsSpanning(e) ? L.T("All day")
                    : $"{Max(e.Start.LocalDateTime, day.ToDateTime(TimeOnly.MinValue)).ToString("t", Culture)} – {e.End.LocalDateTime.ToString("t", Culture)}";
                row.Children.Add(new TextBlock { Text = when, Foreground = muted, VerticalAlignment = VerticalAlignment.Center });
                var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = CalendarBrushes.For(ColorOf(e)), Margin = new Thickness(0, 2, 12, 2) };
                Grid.SetColumn(bar, 1);
                row.Children.Add(bar);
                var text = new StackPanel();
                text.Children.Add(new TextBlock
                {
                    Text = e.Title, FontSize = 14.5, TextTrimming = TextTrimming.CharacterEllipsis,
                    TextDecorations = e.MyResponse == "declined" ? TextDecorations.Strikethrough : null,
                });
                var sub = string.Join(" · ", new[]
                {
                    e.Location,
                    e.Attendees.Count > 1 ? L.F("{0} people", e.Attendees.Count) : null,
                    _vm.CalendarOf(e)?.Name,
                }.Where(s => !string.IsNullOrWhiteSpace(s)));
                if (sub.Length > 0) text.Children.Add(new TextBlock { Text = sub, Foreground = muted, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
                Grid.SetColumn(text, 2);
                row.Children.Add(text);
                if (e.MeetingUrl is not null)
                {
                    var join = new Button { Content = L.T("Join"), Style = (Style)FindResource("PrimaryButton"), Padding = new Thickness(12, 3, 12, 3), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                    join.Click += (_, _) => _vm.JoinCommand.Execute(e);
                    Grid.SetColumn(join, 3);
                    row.Children.Add(join);
                }
                rowBox.MouseLeftButtonDown += (_, args) =>
                {
                    if (args.ClickCount == 2) { _vm.OpenCommand.Execute(e); args.Handled = true; }
                };
                rows.Children.Add(rowBox);
            }
        }
        if (!any)
            list.Children.Add(new TextBlock { Text = L.T("Nothing planned in these 30 days."), Foreground = muted, FontSize = 15, Margin = new Thickness(0, 40, 0, 0) });
        return new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    // ═════════════════════════════ search results ═════════════════════════════

    /// <summary>
    /// Matches in two parts: what's coming (soonest first) and what's past (most recent first), so the
    /// event you're most likely after is near the top either way.
    /// </summary>
    private UIElement BuildSearchResults()
    {
        var list = new StackPanel { Margin = new Thickness(24, 0, 24, 24), MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        var muted = (Brush)FindResource("MutedTextBrush");

        // Nothing found: the heading, and the empty state in the middle of the whole pane with Clear search under it,
        // as in contacts and tasks. (The heading's own Clear search is only there beside results.)
        var nothing = _vm.SearchResults.Count == 0 && !_vm.SearchStatus.Contains('…') && !_vm.SearchFailed;
        var head = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        if (!nothing)
        {
            var clear = new Button { Style = (Style)FindResource("ToolbarButton"), Tag = "", Content = L.T("Clear search"), Command = _vm.ClearSearchCommand };
            DockPanel.SetDock(clear, Dock.Right);
            head.Children.Add(clear);
        }
        var heading = new StackPanel();
        heading.Children.Add(new TextBlock { Text = L.F("Search: “{0}”", _vm.SearchText.Trim()), FontSize = 17, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock { Text = _vm.SearchStatus, Foreground = muted, FontSize = 12.5, Margin = new Thickness(0, 2, 0, 0) });
        head.Children.Add(heading);
        list.Children.Add(head);
        if (nothing)
        {
            var pane = new Grid();
            pane.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            pane.RowDefinitions.Add(new RowDefinition());
            pane.Children.Add(list);
            var empty = new EmptyState
            {
                Title = L.F("No events match “{0}”", _vm.SearchText.Trim()),
                Text = L.T("Check the spelling, or try a shorter word."),
                ActionText = L.T("Clear search"),
                ActionCommand = _vm.ClearSearchCommand,
            };
            Grid.SetRow(empty, 1);
            pane.Children.Add(empty);
            return pane;
        }

        var today = CalendarViewModel.Today;
        var upcoming = _vm.SearchResults.Where(e => CalendarLayout.EndDay(e) > today).OrderBy(e => e.Start).ToList();
        var past = _vm.SearchResults.Where(e => CalendarLayout.EndDay(e) <= today).OrderByDescending(e => e.Start).ToList();

        void Section(string title, List<CalendarEvent> events)
        {
            if (events.Count == 0) return;
            list.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = muted, Margin = new Thickness(0, 18, 0, 4) });
            foreach (var e in events) list.Children.Add(SearchRow(e, muted));
        }
        Section(L.T("Upcoming"), upcoming);
        Section(L.T("Earlier"), past);
        return new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private Grid SearchRow(CalendarEvent e, Brush muted)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand, ToolTip = Describe(e), ContextMenu = BuildEventMenu(e) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var start = e.Start.LocalDateTime;
        var dot = Culture.DateTimeFormat.ShortDatePattern.Contains("d.", StringComparison.Ordinal) ? "." : "";
        var date = CalendarLayout.StartDay(e).ToString(CalendarLayout.StartDay(e).Year == CalendarViewModel.Today.Year ? $"ddd d{dot} MMM" : $"ddd d{dot} MMM yyyy", Culture);
        var when = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        when.Children.Add(new TextBlock { Text = date });
        when.Children.Add(new TextBlock { Text = CalendarLayout.IsSpanning(e) ? L.T("All day") : $"{start.ToString("t", Culture)} – {e.End.LocalDateTime.ToString("t", Culture)}", Foreground = muted, FontSize = 12.5 });
        row.Children.Add(when);

        var bar = new Border { Width = 4, CornerRadius = new CornerRadius(2), Background = CalendarBrushes.For(ColorOf(e)), Margin = new Thickness(0, 3, 12, 3) };
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = e.Title, FontSize = 14.5, TextTrimming = TextTrimming.CharacterEllipsis,
            TextDecorations = e.MyResponse == "declined" ? TextDecorations.Strikethrough : null,
        });
        var sub = string.Join(" · ", new[] { e.Location, e.Attendees.Count > 1 ? L.F("{0} people", e.Attendees.Count) : null, _vm.CalendarOf(e)?.Name }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        if (sub.Length > 0) text.Children.Add(new TextBlock { Text = sub, Foreground = muted, FontSize = 12.5, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 2);
        row.Children.Add(text);

        var go = new Button
        {
            Style = (Style)FindResource("ToolbarButton"), Tag = "", ToolTip = "Show in calendar", Padding = new Thickness(8, 5, 8, 5),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0),
        };
        go.Click += (_, _) => _vm.ShowInCalendar(e);
        Grid.SetColumn(go, 3);
        row.Children.Add(go);

        row.MouseEnter += (_, _) => row.Background = (Brush)FindResource("SelectionBrush");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonDown += (_, args) =>
        {
            if (args.ClickCount == 2) { _vm.OpenCommand.Execute(e); args.Handled = true; }
        };
        return row;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    // ═════════════════════════════ mini month ═════════════════════════════

    private void BuildMiniMonth()
    {
        var first = _vm.MiniMonth;
        var start = CalendarLayout.WeekStart(first, Prefs.FirstDayOfWeek);
        var shown = _vm.Mode is CalendarViewMode.Month or CalendarViewMode.Agenda ? [] : _vm.Days.ToHashSet();
        var muted = (Brush)FindResource("MutedTextBrush");
        var text = (Brush)FindResource("TextBrush");
        var link = (Brush)FindResource("LinkBrush");
        var showWeeks = Prefs.ShowWeekNumbers;
        var collapsed = Prefs.MiniMonthCollapsed;

        // Heading: the month (opens it), back · this month · forward, and the fold arrow.
        var root = new StackPanel();
        var head = new DockPanel();
        Button Small(string glyph, string tip, Action click)
        {
            var b = new Button { Style = (Style)FindResource("ToolbarButton"), Tag = glyph, Padding = new Thickness(6, 4, 6, 4), ToolTip = tip, FontSize = 12 };
            b.Click += (_, _) => click();
            DockPanel.SetDock(b, Dock.Right);
            return b;
        }
        head.Children.Add(Small(collapsed ? "\uE70E" : "\uE70D", collapsed ? L.T("Show the month") : L.T("Fold the month away"), () => _vm.ToggleMiniMonthCommand.Execute(null)));
        if (!collapsed)
        {
            head.Children.Add(Small("\uE76C", L.T("Next month"), () => _vm.NextMiniMonthCommand.Execute(null)));
            head.Children.Add(Small("\uE8D1", L.T("This month"), () => _vm.MiniMonth = new DateOnly(CalendarViewModel.Today.Year, CalendarViewModel.Today.Month, 1)));
            head.Children.Add(Small("\uE76B", L.T("Previous month"), () => _vm.PreviousMiniMonthCommand.Execute(null)));
        }
        var title = new TextBlock
        {
            Text = first.ToString("MMMM yyyy", Culture), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand, ToolTip = L.T("Show this month"), Margin = new Thickness(4, 0, 0, 0), Background = Brushes.Transparent,
        };
        title.MouseLeftButtonUp += (_, _) => _vm.GoTo(first, CalendarViewMode.Month);
        title.MouseEnter += (_, _) => title.Foreground = link;
        title.MouseLeave += (_, _) => title.Foreground = text;
        head.Children.Add(title);
        root.Children.Add(head);
        MiniMonthHost.Child = root;
        if (collapsed) return;

        const double row = 30, circle = 26, face = 23; // a day's square: clear of the event dot under it
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        if (showWeeks) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        for (var c = 0; c < 7; c++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
        for (var r = 0; r < 6; r++) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(row) });
        var offset = showWeeks ? 1 : 0;
        for (var c = 0; c < 7; c++)
        {
            var d = start.AddDays(c);
            var name = new TextBlock
            {
                Text = d.ToString("ddd", Culture)[..1].ToUpper(Culture), FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center, Foreground = muted, FontWeight = FontWeights.SemiBold,
            };
            Grid.SetColumn(name, c + offset);
            grid.Children.Add(name);
        }

        // The days on screen in the main view: one band per week row under the numbers, white like the sidebar's
        // selected item and rounded like the app's other boxes, so today's blue square stands out on it.
        for (var r = 0; r < 6; r++)
        {
            var cols = Enumerable.Range(0, 7).Where(c => shown.Contains(start.AddDays(r * 7 + c))).ToList();
            if (cols.Count == 0) continue;
            var band = new Border { Background = (Brush)FindResource("SidebarSelectedBrush"), CornerRadius = new CornerRadius(6), Height = face, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(1, 1, 1, 0) };
            Grid.SetRow(band, r + 1);
            Grid.SetColumn(band, cols[0] + offset);
            Grid.SetColumnSpan(band, cols[^1] - cols[0] + 1);
            grid.Children.Add(band);
        }

        var hover = HoverTint;
        var otherMonth = Theme.IsDark ? Frozen(Color.FromRgb(0x6A, 0x6A, 0x6A)) : Frozen(Color.FromRgb(0xB4, 0xB4, 0xB4));
        for (var i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            int r = i / 7 + 1, col = i % 7;
            if (showWeeks && col == 0)
            {
                var weekStart = day;
                var kw = new TextBlock
                {
                    Text = Calendars.IsoWeek(day).ToString(Culture), Foreground = muted, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 0, 0), Cursor = Cursors.Hand, ToolTip = L.T("Show this week"),
                    Background = Brushes.Transparent,
                };
                kw.MouseEnter += (_, _) => kw.Foreground = link;
                kw.MouseLeave += (_, _) => kw.Foreground = muted;
                kw.MouseLeftButtonUp += (_, _) => _vm.GoTo(weekStart, CalendarViewMode.Week);
                Grid.SetRow(kw, r);
                grid.Children.Add(kw);
            }
            var isToday = day == CalendarViewModel.Today;
            var inMonth = day.Month == first.Month;
            var number = new TextBlock
            {
                Text = day.Day.ToString(Culture), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = isToday ? link : !inMonth ? otherMonth : Prefs.WorkDays.Contains(day.DayOfWeek) ? text : muted,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
            };
            // Today: a soft blue square with a blue number; the picked day: a thin blue outline.
            var todayFill = (Brush)FindResource("SelectionBrush");
            var square = new Border
            {
                Width = circle, Height = face, CornerRadius = new CornerRadius(6), Child = number, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 0, 0), Background = isToday ? todayFill : Brushes.Transparent,
                BorderBrush = link, BorderThickness = new Thickness(day == _vm.Anchor && !isToday ? 1.5 : 0),
            };
            // Something on that day: a small dot under the number.
            var dot = new Border
            {
                Width = 4, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 1), Background = inMonth ? (Brush)FindResource("AccentBrush") : otherMonth,
                Visibility = _vm.BusyDays.Contains(day) ? Visibility.Visible : Visibility.Hidden,
            };
            var cell = new Grid
            {
                Background = Brushes.Transparent, Cursor = Cursors.Hand,
                ToolTip = day.ToString("D", Culture) + (_vm.BusyDays.Contains(day) ? "\n" + L.T("Has events") : ""),
            };
            cell.Children.Add(square);
            cell.Children.Add(dot);
            if (!isToday)
            {
                cell.MouseEnter += (_, _) => square.Background = hover;
                cell.MouseLeave += (_, _) => square.Background = Brushes.Transparent;
            }
            cell.MouseLeftButtonUp += (_, _) => _vm.GoTo(day);
            cell.ContextMenu = DayMenu(day, null);
            Grid.SetRow(cell, r);
            Grid.SetColumn(cell, col + offset);
            grid.Children.Add(cell);
        }
        root.Children.Add(grid);
    }

    // ═════════════════════════════ toolbar and sidebar handlers ═════════════════════════════

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        // The accent button has a chevron on its right edge: that part opens the menu, the rest makes an event.
        var x = Mouse.GetPosition(NewButton).X;
        if (x > NewButton.ActualWidth - 34 && (NewMenuFactory?.Invoke() ?? NewButton.ContextMenu) is { } menu)
        {
            menu.PlacementTarget = NewButton;
            menu.Placement = PlacementMode.Bottom;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
        else
        {
            _vm.NewEventCommand.Execute(null);
        }
    }

    private void UpNext_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is UpNextItem item) _vm.OpenCommand.Execute(item.Event);
    }

    // ───────── sidebar menus (built when opened, so they always match the calendar) ─────────

    // ───────── hover and right-click on empty space ─────────

    // Each in a light and a dark version (Theme.IsDark when the view is built).
    private static Brush Pick(byte[] light, byte[] dark) => Theme.IsDark ? Frozen(Color.FromRgb(dark[0], dark[1], dark[2])) : Frozen(Color.FromRgb(light[0], light[1], light[2]));
    private static Brush HoverTint => Pick([0xE6, 0xEE, 0xFA], [0x2E, 0x37, 0x47]);
    private static Brush SlotTint => Pick([0xF5, 0xF8, 0xFD], [0x2C, 0x30, 0x37]);
    private static Brush OtherMonthFill => Pick([0xFA, 0xFA, 0xFA], [0x23, 0x23, 0x23]);
    private static Brush OtherMonthHover => Pick([0xF2, 0xF5, 0xFA], [0x2A, 0x2E, 0x34]);
    private static Brush WeekendFill => Pick([0xF8, 0xF8, 0xF9], [0x24, 0x24, 0x25]);
    /// <summary>The picked day (the view's anchor): its column, all-day cell or month cell.</summary>
    private static Brush SelectedDayTint => Pick([0xEE, 0xF4, 0xFD], [0x26, 0x31, 0x40]);

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static readonly DependencyProperty NormalBackgroundProperty =
        DependencyProperty.RegisterAttached("NormalBackground", typeof(Brush), typeof(CalendarView));

    /// <summary>Swaps the background while the mouse is over it, and back.</summary>
    private static void HoverBackground(Border element, Brush hover)
    {
        element.MouseEnter += (_, _) =>
        {
            element.SetValue(NormalBackgroundProperty, element.Background);
            element.Background = hover;
        };
        element.MouseLeave += (_, _) => element.Background = (Brush?)element.GetValue(NormalBackgroundProperty) ?? element.Background;
    }

    /// <summary>The calendar colour a shade darker (bars and chips on hover).</summary>
    private static Brush Darker(string hex)
    {
        var c = CalendarBrushes.For(hex).Color;
        return Frozen(Color.FromRgb((byte)(c.R * 0.86), (byte)(c.G * 0.86), (byte)(c.B * 0.86)));
    }

    /// <summary>Right-click on a day (or a time in it): create something there, or move around.</summary>
    private ContextMenu DayMenu(DateOnly day, DateTime? at)
    {
        var start = at ?? day.ToDateTime(new TimeOnly(Math.Clamp(Prefs.WorkDayStartHour + 1, 0, 23), 0));
        var end = start.AddMinutes(Prefs.DefaultDurationMinutes);
        var time = at?.ToString("t", Culture);
        var menu = new ContextMenu();
        menu.Items.Add(MenuEntry(time is null ? L.T("New Event…") : L.F("New Event at {0}…", time), "\uE710", () => _vm.OpenEditor(null, start, end, allDay: false)));
        menu.Items.Add(MenuEntry(time is null ? L.T("New Online Meeting…") : L.F("New Online Meeting at {0}…", time), "\uE714", () => _vm.OpenEditor(null, start, end, allDay: false, videoCall: true)));
        menu.Items.Add(MenuEntry(L.T("New All-Day Event…"), "\uE8BF",
            () => _vm.OpenEditor(null, day.ToDateTime(TimeOnly.MinValue), day.AddDays(1).ToDateTime(TimeOnly.MinValue), allDay: true)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(L.F("Show {0}", day.ToString("dddd", Culture)), "\uE787", () => _vm.GoTo(day, CalendarViewMode.Day), _vm.Mode != CalendarViewMode.Day));
        menu.Items.Add(MenuEntry(L.T("Show Week"), "\uE8C0", () => _vm.GoTo(day, CalendarViewMode.Week), _vm.Mode != CalendarViewMode.Week));
        menu.Items.Add(MenuEntry(L.T("Go to Today"), "\uE8D1", () => _vm.GoTodayCommand.Execute(null)));
        return menu;
    }

    private static MenuItem MenuEntry(string header, string? glyph, Action? click, bool enabled = true, object? icon = null)
    {
        var item = new MenuItem
        {
            Header = header, IsEnabled = enabled,
            Icon = icon ?? (glyph is null ? null : new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 }),
        };
        if (click is not null) item.Click += (_, _) => click();
        return item;
    }

    private MenuItem HolidaysMenu(long accountId)
    {
        var menu = MenuEntry(L.T("Add holidays"), "\uE909", null);
        var have = _vm.CalendarsById.Values.Where(c => c.AccountId == accountId).Select(c => c.RemoteId).ToHashSet();
        foreach (var h in HolidayCalendar.Common)
        {
            var added = have.Any(id => id.EndsWith($".{h.Key}#holiday@group.v.calendar.google.com", StringComparison.Ordinal));
            menu.Items.Add(MenuEntry(h.Country, added ? "\uE73E" : null, () => _ = _vm.AddHolidaysAsync(accountId, h), enabled: !added));
        }
        return menu;
    }

    private void Calendar_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarItemViewModel item, ContextMenu: { } menu }) return;
        var cal = item.Info;
        menu.Items.Clear();
        menu.Items.Add(MenuEntry(L.T("New Event…"), "\uE710", () => _vm.NewEventIn(item), cal.CanEdit));
        menu.Items.Add(new Separator());

        // Fluent menus don't draw IsChecked, so a tick icon marks the state.
        menu.Items.Add(MenuEntry(L.T("Show"), item.IsVisible ? "\uE73E" : null, () => item.IsVisible = !item.IsVisible));
        menu.Items.Add(MenuEntry(L.T("Show Only This Calendar"), "\uE890", () => _vm.ShowOnlyCommand.Execute(item)));
        menu.Items.Add(MenuEntry(L.T("Show All Calendars"), "\uE8A9", () => _vm.ShowAllCommand.Execute(null)));

        var colors = MenuEntry(L.T("Calendar Color"), "\uE790", null);
        foreach (var (name, hex) in CalendarViewModel.Palette)
        {
            var swatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = CalendarBrushes.For(hex) };
            var entry = MenuEntry(name, null, () => _ = _vm.SetColorAsync(item, hex), icon: swatch);
            entry.FontWeight = string.Equals(cal.Color, hex, StringComparison.OrdinalIgnoreCase) ? FontWeights.SemiBold : FontWeights.Normal;
            colors.Items.Add(entry);
        }
        menu.Items.Add(colors);
        menu.Items.Add(new Separator());

        menu.Items.Add(MenuEntry(L.T("Rename…"), "\uE8AC", () => _ = _vm.RenameCalendarAsync(item)));
        var delete = MenuEntry(item.IsOwned ? L.T("Delete Calendar…") : L.T("Remove from List"), "\uE74D", () => _ = _vm.DeleteCalendarAsync(item), !cal.IsPrimary);
        if (cal.IsPrimary)
        {
            delete.ToolTip = L.T("An account's main calendar can't be deleted.");
            ToolTipService.SetShowOnDisabled(delete, true);
        }
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(L.T("Import Events…"), "", () => _ = _vm.ImportEventsAsync(item), cal.CanEdit));
        menu.Items.Add(MenuEntry(L.T("Export…"), "", () => _ = _vm.ExportCalendarAsync(item)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(L.T("New Calendar…"), "\uE787", () => _ = _vm.NewCalendarAsync(cal.AccountId)));
        if (cal.AccountId is { } accountId) menu.Items.Add(HolidaysMenu(accountId)); // Google's holiday calendars
    }

    /// <summary>Dev screenshots: opens the first calendar's context menu.</summary>
    /// <summary>Dev aid (screenshots): the right-click menu of an empty slot at 10:00 on the first shown day.</summary>
    internal ContextMenu? OpenSlotMenuForPreview()
    {
        if (_dayPanels.FirstOrDefault() is not { } panel) return null;
        var menu = DayMenu(panel.Day, panel.Day.ToDateTime(new TimeOnly(10, 0)));
        menu.PlacementTarget = panel;
        menu.IsOpen = true;
        return menu;
    }

    internal ContextMenu? OpenCalendarMenuForPreview()
    {
        var box = FindVisual<CheckBox>(this, b => b.DataContext is CalendarItemViewModel);
        if (box?.ContextMenu is not { } menu) return null;
        Calendar_ContextMenuOpening(box, null!);
        menu.PlacementTarget = box;
        menu.IsOpen = true;
        return menu;
    }

    private static T? FindVisual<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t && match(t)) return t;
            if (FindVisual(child, match) is { } found) return found;
        }
        return null;
    }

    private void Account_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarGroupViewModel group, ContextMenu: { } menu }) return;
        menu.Items.Clear();
        menu.Items.Add(MenuEntry(L.T("New Calendar…"), "\uE787", () => _ = _vm.NewCalendarAsync(group.AccountId)));
        if (group.AccountId is not { } accountId)
        {
            // On this computer: nothing to sync, no Google holidays.
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry(L.T("Show All Calendars on This Computer"), "", () => _ = _vm.SetAccountVisibleAsync(null, true)));
            menu.Items.Add(MenuEntry(L.T("Hide All Calendars on This Computer"), "", () => _ = _vm.SetAccountVisibleAsync(null, false)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MenuEntry(L.T("Calendar Settings…"), "", () => _vm.OpenCalendarSettingsCommand.Execute(null)));
            return;
        }
        menu.Items.Add(HolidaysMenu(accountId));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(L.T("Show All Calendars of This Account"), "\uE8A9", () => _ = _vm.SetAccountVisibleAsync(group.AccountId, true)));
        menu.Items.Add(MenuEntry(L.T("Hide All Calendars of This Account"), "\uED1A", () => _ = _vm.SetAccountVisibleAsync(group.AccountId, false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry(L.T("Refresh"), "\uE72C", () => _vm.RefreshAccount(accountId)));
        menu.Items.Add(MenuEntry(L.T("Calendar Settings…"), "\uE713", () => _vm.OpenCalendarSettingsCommand.Execute(null)));
    }

    private void MailModule_Click(object sender, RoutedEventArgs e) => ModuleRequested?.Invoke("mail");
    private void ContactsModule_Click(object sender, RoutedEventArgs e) => ModuleRequested?.Invoke("contacts");
    private void TasksModule_Click(object sender, RoutedEventArgs e) => ModuleRequested?.Invoke("tasks");

    /// <summary>For the calendar's own module bar: the "Calendar" button must look selected whenever the view is shown.</summary>
    /// <summary>Ctrl+F while the calendar is shown.</summary>
    public void FocusSearch() => CalendarSearch.FocusBox();

    public void OnShown()
    {
        // Clicking Mail here checked the Mail button of this bar; coming back must show Calendar as current again.
        CalendarModuleButton.IsChecked = true;
        Rebuild();
        Focus();
    }
}
