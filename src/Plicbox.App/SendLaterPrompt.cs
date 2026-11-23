using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Plicbox.App.Services;
using Plicbox.Core;

namespace Plicbox.App;

/// <summary>Send later: the ready-made times for the menu, and a small window to pick any other.</summary>
public static class SendLaterPrompt
{
    /// <summary>"Later today" (before five), "Tomorrow morning", and "Monday morning" when tomorrow isn't Monday.</summary>
    public static IReadOnlyList<(string Label, DateTime At)> Suggestions(DateTime now)
    {
        var list = new List<(string, DateTime)>();
        var culture = CultureInfo.CurrentCulture;
        if (now.Hour < 17) list.Add((L.F("Later today, {0}", now.Date.AddHours(18).ToString("t", culture)), now.Date.AddHours(18)));
        var tomorrow = now.Date.AddDays(1).AddHours(8);
        list.Add((L.F("Tomorrow morning, {0}", tomorrow.ToString("ddd t", culture)), tomorrow));
        var toMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        var monday = now.Date.AddDays(toMonday == 0 ? 7 : toMonday).AddHours(8);
        if (monday.Date != tomorrow.Date) list.Add((L.F("Monday morning, {0}", monday.ToString("ddd d MMM, t", culture)), monday));
        return list;
    }

    /// <summary>Asks for a date and time in the future; null when cancelled.</summary>
    public static DateTime? Ask(Window owner)
    {
        var start = DateTime.Now.Date.AddDays(1).AddHours(8);
        var date = new DatePicker { SelectedDate = start.Date, DisplayDateStart = DateTime.Today, Width = 170, Margin = new Thickness(0, 0, 10, 0) };
        // Every half hour, in the computer's own time format; another time can be typed.
        var times = Enumerable.Range(0, 48).Select(i => DateTime.Today.AddMinutes(30 * i).ToString("t", CultureInfo.CurrentCulture)).ToList();
        var time = new ComboBox { ItemsSource = times, IsEditable = true, Text = start.ToString("t", CultureInfo.CurrentCulture), Width = 110 };
        var error = new TextBlock { Foreground = (System.Windows.Media.Brush)Application.Current.Resources["DangerTextBrush"], Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var ok = new Button { Content = L.T("Schedule"), IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 5, 12, 5) };
        var cancel = new Button { Content = L.T("Cancel"), IsCancel = true, MinWidth = 90, Padding = new Thickness(12, 5, 12, 5) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        row.Children.Add(date);
        row.Children.Add(time);
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = L.T("When should it go?"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(row);
        panel.Children.Add(error);
        panel.Children.Add(new TextBlock
        {
            Text = L.T("It waits in the Outbox until then. Plicbox sends it, so it needs to be running at that time (in the tray is enough); otherwise it goes as soon as Plicbox starts."),
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 12, 0, 0),
            Foreground = (System.Windows.Media.Brush)Application.Current.Resources["MutedTextBrush"],
        });
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = L.T("Send later"), Icon = WindowIcons.SendLater, Content = panel, Owner = owner, Width = 420, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = (System.Windows.Media.FontFamily)Application.Current.Resources["UiFont"], FontSize = 14,
        };
        DateTime? picked = null;
        ok.Click += (_, _) =>
        {
            if (date.SelectedDate is not { } day || !DateTime.TryParse(time.Text, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var clock))
            {
                error.Text = L.T("Enter a date and a time.");
                error.Visibility = Visibility.Visible;
                return;
            }
            var at = day.Date + clock.TimeOfDay;
            if (at <= DateTime.Now)
            {
                error.Text = L.T("Pick a time that's still to come.");
                error.Visibility = Visibility.Visible;
                return;
            }
            picked = at;
            window.DialogResult = true;
        };
        return window.ShowDialog() == true ? picked : null;
    }
}
