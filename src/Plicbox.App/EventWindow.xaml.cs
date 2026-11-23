using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Plicbox.App.Services;
using Plicbox.App.ViewModels;
using Plicbox.Core;
using Plicbox.Dav;
using Plicbox.Mail;

namespace Plicbox.App;

/// <summary>What to open the editor with: an existing event, or the times/title for a new one.</summary>
public sealed record EventEditRequest(CalendarEvent? Existing, DateTime Start, DateTime End, bool AllDay, string? Title, long? CalendarId,
    bool VideoCall = false, string? Guests = null);

/// <summary>Create or edit an event (Google Calendar).</summary>
public partial class EventWindow : Window
{
    private sealed record CalendarChoice(CalendarInfo Info, string Name, string Account, Brush Color);
    private sealed record Choice<T>(string Label, T Value);
    private sealed record AttendeeRow(string Label, string Glyph, Brush GlyphBrush);

    private readonly EventEditRequest _request;
    private readonly CalendarEngine _engine;
    private readonly CalendarViewModel _calendar;
    private readonly Dialogs _dialogs;
    private readonly CalendarEvent? _existing;
    private string? _originalRule;
    private string _originalNotes = "";
    private bool _busy;

    public EventWindow(EventEditRequest request, CalendarEngine engine, CalendarViewModel calendar, Dialogs dialogs, IAccountRepository accounts)
    {
        InitializeComponent();
        (_request, _engine, _calendar, _dialogs, _existing) = (request, engine, calendar, dialogs, request.Existing);
        Services.FreeWindows.Place(this); // centred on the main window, but not kept in front of it
        Icon = WindowIcons.Calendar;
        Title = _existing is null ? L.T("New event") : _existing.Title;

        // Times every 15 minutes, written the way this PC writes times.
        var times = Enumerable.Range(0, 96).Select(i => TimeOnly.FromTimeSpan(TimeSpan.FromMinutes(i * 15)).ToString("t", CultureInfo.CurrentCulture)).ToList();
        StartTime.ItemsSource = times;
        EndTime.ItemsSource = times;

        ReminderBox.ItemsSource = new List<Choice<int?>>
        {
            new(L.T("No reminder"), null), new(L.T("At start"), 0), new(L.T("5 minutes before"), 5), new(L.T("10 minutes before"), 10),
            new(L.T("15 minutes before"), 15), new(L.T("30 minutes before"), 30), new(L.T("1 hour before"), 60), new(L.T("2 hours before"), 120),
            new(L.T("1 day before"), 1440), new(L.T("1 week before"), 10080),
        };

        Loaded += async (_, _) => await LoadAsync(accounts);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private async Task LoadAsync(IAccountRepository accounts)
    {
        var accountNames = (await accounts.GetAllAsync()).ToDictionary(a => a.Id, a => a.SidebarTitle);
        var calendars = (_existing is null ? _calendar.WritableCalendars : _calendar.CalendarsById.Values.ToList())
            .Select(c => new CalendarChoice(c, c.Name,
                c.AccountId is { } accountId ? accountNames.GetValueOrDefault(accountId) ?? "" : L.T("On this computer"), CalendarBrushes.For(c.Color)))
            .ToList();
        CalendarBox.ItemsSource = calendars;
        var calendarId = _existing?.CalendarId ?? _request.CalendarId;
        CalendarBox.SelectedItem = calendars.FirstOrDefault(c => c.Info.Id == calendarId) ?? calendars.FirstOrDefault();
        CalendarBox.IsEnabled = _existing is null; // moving between calendars isn't supported yet

        var e = _existing;
        TitleBox.Text = e?.Title ?? _request.Title ?? "";
        LocationBox.Text = e?.Location ?? "";
        _originalNotes = e?.Description is { Length: > 0 } d ? (d.Contains('<') ? MessageRenderer.HtmlToText(d) : d) : "";
        NotesBox.Text = _originalNotes;
        AllDayBox.IsChecked = _request.AllDay;
        SetWhen(_request.Start, _request.AllDay ? _request.End.AddDays(-1) : _request.End);
        BusyBox.SelectedIndex = e is { IsBusy: false } ? 1 : 0;
        if (e is null && _request.VideoCall) VideoBox.IsChecked = true;
        if (e is null && _request.Guests is { Length: > 0 } guests) GuestsBox.Text = guests;
        var reminder = e is null ? (_request.AllDay ? null : _calendar.Prefs.DefaultReminderMinutes) : e.ReminderMinutes;
        ReminderBox.SelectedItem = ((List<Choice<int?>>)ReminderBox.ItemsSource).FirstOrDefault(r => r.Value == reminder)
                                   ?? ((List<Choice<int?>>)ReminderBox.ItemsSource)[0];

        if (e is not null)
        {
            DeleteButton.Visibility = Visibility.Visible;
            GoogleButton.Visibility = e.HtmlLink is null ? Visibility.Collapsed : Visibility.Visible;
            var me = e.Attendees.FirstOrDefault(a => a.IsSelf);
            GuestsBox.Text = string.Join(", ", e.Attendees.Where(a => !a.IsSelf && !a.IsOrganizer).Select(a => a.Email));
            AttendeeList.ItemsSource = e.Attendees.Select(a => new AttendeeRow(
                (a.IsSelf ? L.T("You") : string.IsNullOrWhiteSpace(a.Name) ? a.Email : a.Name) + (a.IsOrganizer ? " · " + L.T("organiser") : ""),
                a.Response switch { "accepted" => "", "declined" => "", "tentative" => "", _ => "" },
                CalendarBrushes.For(a.Response switch { "accepted" => "#10B981", "declined" => "#C42B1C", "tentative" => "#D97706", _ => "#8C8C8C" })))
                .ToList();
            if (e.MyResponse is { } response)
            {
                RsvpBar.Visibility = Visibility.Visible;
                var organizer = e.Attendees.FirstOrDefault(a => a.IsOrganizer);
                var who = e.OrganizerName ?? organizer?.Name ?? e.OrganizerEmail ?? organizer?.Email ?? L.T("Someone");
                RsvpText.Text = L.F("{0} invited you. Going?", who);
                HighlightResponse(response);
            }
        }
        if (e?.MeetingUrl is { } url)
        {
            VideoBox.Visibility = Visibility.Collapsed;
            MeetingLinkText.Visibility = Visibility.Visible;
            MeetingLink.Inlines.Add(url);
            JoinButton.Visibility = Visibility.Visible;
        }

        // Repeat: presets for the start day, plus the series' own rule when it's something fancier.
        _originalRule = e is null ? null : await SafeRuleAsync(e);
        FillRepeatChoices();

        var editable = e is null || (_calendar.CanEdit(e) && e.MyResponse is null);
        if (!editable)
        {
            foreach (var c in new Control[] { TitleBox, StartDate, StartTime, EndDate, EndTime, AllDayBox, RepeatBox, LocationBox, VideoBox, GuestsBox, BusyBox, NotesBox })
                c.IsEnabled = false;
            ReminderBox.IsEnabled = false;
            SaveButton.Visibility = Visibility.Collapsed;
            CancelButton.Margin = new Thickness(0); // first in the bar now: lines up with the form below
            // Removing an invitation from your own calendar is fine; changing it is the organiser's job.
            DeleteButton.Visibility = _calendar.CanEdit(e!) ? Visibility.Visible : Visibility.Collapsed;
            ReadOnlyText.Visibility = Visibility.Visible;
            ReadOnlyText.Text = e!.MyResponse is not null
                ? L.T("You were invited, so only the organiser can change this event. Answer with Yes, Maybe or No above.")
                : L.T("This calendar is read-only.");
        }
        UpdateDuration();
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    /// <summary>Video calls and guests go through Google: a calendar on this computer has neither.</summary>
    private void Calendar_Changed(object sender, SelectionChangedEventArgs e)
    {
        var online = CalendarBox.SelectedItem is not CalendarChoice { Info.IsLocal: true };
        foreach (var row in new FrameworkElement[] { VideoGlyph, VideoRow, GuestsGlyph, GuestsRow })
            row.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<string?> SafeRuleAsync(CalendarEvent e)
    {
        try { return await _engine.GetSeriesRuleAsync(e); }
        catch (Exception) { return null; } // offline: the repeat box just shows L.T("Does not repeat") for single changes
    }

    private void FillRepeatChoices()
    {
        var start = StartDate.SelectedDate ?? DateTime.Today;
        var choices = new List<Choice<string?>>
        {
            new(L.T("Does not repeat"), null),
            new(L.T("Every day"), Calendars.BuildRule(RepeatKind.Daily, start)),
            new(L.T("Every weekday (Mon–Fri)"), Calendars.BuildRule(RepeatKind.Weekdays, start)),
            new(L.F("Every week on {0}", start.ToString("dddd", CultureInfo.CurrentCulture)), Calendars.BuildRule(RepeatKind.Weekly, start)),
            new(L.F("Every month on day {0}", start.Day), Calendars.BuildRule(RepeatKind.Monthly, start)),
            new(L.F("Every year on {0}", start.ToString("M", CultureInfo.CurrentCulture)), Calendars.BuildRule(RepeatKind.Yearly, start)),
        };
        var current = (RepeatBox.SelectedItem as Choice<string?>)?.Value ?? _originalRule;
        if (current is not null && choices.All(c => c.Value != current))
            choices.Add(new(L.F("{0} (custom)", Calendars.Describe(current, start)), current));
        RepeatBox.ItemsSource = choices;
        RepeatBox.SelectedItem = choices.FirstOrDefault(c => c.Value == current) ?? choices[0];
    }

    // ───────── when ─────────

    private void SetWhen(DateTime start, DateTime end)
    {
        StartDate.SelectedDate = start.Date;
        EndDate.SelectedDate = end.Date;
        StartTime.Text = TimeOnly.FromDateTime(start).ToString("t", CultureInfo.CurrentCulture);
        EndTime.Text = TimeOnly.FromDateTime(end).ToString("t", CultureInfo.CurrentCulture);
    }

    private bool TryReadWhen(out DateTime start, out DateTime end)
    {
        start = end = default;
        if (StartDate.SelectedDate is not { } sd || EndDate.SelectedDate is not { } ed) return false;
        if (AllDayBox.IsChecked == true)
        {
            start = sd.Date;
            end = ed.Date.AddDays(1);
            return end > start;
        }
        if (!TimeOnly.TryParse(StartTime.Text, CultureInfo.CurrentCulture, out var st) || !TimeOnly.TryParse(EndTime.Text, CultureInfo.CurrentCulture, out var et))
            return false;
        start = sd.Date + st.ToTimeSpan();
        end = ed.Date + et.ToTimeSpan();
        return end > start;
    }

    private TimeSpan _length = TimeSpan.FromHours(1);

    private void StartTime_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Moving the start keeps the length, like every calendar app.
        if (StartTime.SelectedItem is string s && TimeOnly.TryParse(s, CultureInfo.CurrentCulture, out var st) && StartDate.SelectedDate is { } sd)
        {
            var end = sd.Date + st.ToTimeSpan() + _length;
            EndDate.SelectedDate = end.Date;
            EndTime.Text = TimeOnly.FromDateTime(end).ToString("t", CultureInfo.CurrentCulture);
        }
        UpdateDuration();
    }

    private void When_Changed(object? sender, RoutedEventArgs e)
    {
        UpdateDuration();
        if (sender == StartDate && RepeatBox.ItemsSource is not null) FillRepeatChoices();
    }

    private void AllDay_Changed(object sender, RoutedEventArgs e)
    {
        var allDay = AllDayBox.IsChecked == true;
        StartTime.Visibility = EndTime.Visibility = allDay ? Visibility.Collapsed : Visibility.Visible;
        UpdateDuration();
    }

    private void UpdateDuration()
    {
        if (!TryReadWhen(out var start, out var end))
        {
            DurationText.Text = L.T("The end has to be after the start.");
            DurationText.Foreground = CalendarBrushes.For("#C42B1C");
            return;
        }
        if (AllDayBox.IsChecked != true) _length = end - start;
        DurationText.Foreground = (Brush)FindResource("MutedTextBrush");
        var span = end - start;
        DurationText.Text = AllDayBox.IsChecked == true
            ? (span.TotalDays <= 1 ? L.T("1 day") : L.F("{0:0} days", span.TotalDays))
            : span.TotalMinutes < 60 ? L.F("{0:0} min", span.TotalMinutes)
            : span.TotalHours < 24 ? L.F("{0:0.#} h", span.TotalHours)
            : L.F("{0:0.#} days", span.TotalDays);
    }

    // ───────── actions ─────────

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (_busy || CalendarBox.SelectedItem is not CalendarChoice cal) return;
        if (!TryReadWhen(out var start, out var end))
        {
            MessageDialog.Show(this, L.T("Check the times"), L.T("The event has to end after it starts."));
            return;
        }

        var draft = new EventDraft
        {
            Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? L.T("(No title)") : TitleBox.Text.Trim(),
            Location = LocationBox.Text.Trim(),
            // Only send notes back when they were edited, so formatting made in Google Calendar survives.
            Description = NotesBox.Text == _originalNotes ? _existing?.Description ?? NotesBox.Text : NotesBox.Text,
            IsAllDay = AllDayBox.IsChecked == true,
            Start = start, End = end,
            StartDate = DateOnly.FromDateTime(start), EndDate = DateOnly.FromDateTime(end),
            TimeZone = _existing?.TimeZone ?? Calendars.LocalIanaZone,
            Recurrence = (RepeatBox.SelectedItem as Choice<string?>)?.Value,
            Guests = GuestsRow.Visibility == Visibility.Visible ? ParseGuests(GuestsBox.Text) : [],
            AddVideoCall = VideoRow.Visibility == Visibility.Visible && VideoBox.Visibility == Visibility.Visible && VideoBox.IsChecked == true,
            ReminderMinutes = (ReminderBox.SelectedItem as Choice<int?>)?.Value,
            IsBusy = BusyBox.SelectedIndex != 1,
        };
        if (draft.Guests.Any(g => !g.Contains('@')))
        {
            MessageDialog.Show(this, L.T("Check the guests"), L.T("Separate addresses with commas, e.g. ana@example.com, bo@example.com."));
            return;
        }

        var wholeSeries = false;
        if (_existing?.SeriesId is not null)
        {
            var ruleChanged = draft.Recurrence != _originalRule;
            var choice = ruleChanged ? false : MessageDialog.Choose(this, L.T("Change a repeating event"),
                L.F("\"{0}\" repeats. Change only this occurrence, or every one?", _existing.Title), L.T("Only this one"), L.T("All events"));
            if (choice is null) return;
            wholeSeries = choice == false;
        }

        await RunAsync(async () =>
        {
            if (_existing is null) await _engine.CreateAsync(cal.Info.Id, draft);
            else await _engine.UpdateAsync(_existing, draft, wholeSeries);
            Close();
        }, L.T("Couldn't save the event"));
    }

    private static List<string> ParseGuests(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(g => MimeKit.MailboxAddress.TryParse(g, out var m) ? m.Address : g)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_existing is null) return;
        if (await _calendar.DeleteEventAsync(_existing)) Close();
    }

    private async void Rsvp_Click(object sender, RoutedEventArgs e)
    {
        if (_existing is null || (sender as Button)?.Tag is not string response) return;
        await RunAsync(async () =>
        {
            await _calendar.RespondAsync(_existing, response);
            HighlightResponse(response);
        }, L.T("Couldn't send your answer"));
    }

    private void HighlightResponse(string response)
    {
        foreach (var b in new[] { YesButton, MaybeButton, NoButton })
        {
            // ClearValue, not null: a null Style would also switch off the app's theme for the button.
            if ((string)b.Tag == response) b.Style = (Style)FindResource("PrimaryButton");
            else b.ClearValue(StyleProperty);
        }
    }

    private void Join_Click(object sender, RoutedEventArgs e)
    {
        if (_existing is not null) _calendar.JoinCommand.Execute(_existing);
    }

    private void OpenInGoogle_Click(object sender, RoutedEventArgs e)
    {
        if (_existing is not null) _calendar.OpenInGoogleCommand.Execute(_existing);
    }

    private async Task RunAsync(Func<Task> action, string failure)
    {
        _busy = true;
        SaveButton.IsEnabled = false;
        SavingSpinner.Visibility = Visibility.Visible;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, failure, ex.Message, DialogKind.Warning);
        }
        finally
        {
            _busy = false;
            SaveButton.IsEnabled = true;
            SavingSpinner.Visibility = Visibility.Collapsed;
        }
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await SaveAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
