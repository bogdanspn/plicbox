using Plicbox.Core;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Plicbox.App;

public enum DialogKind { Question, Info, Warning, Danger }

/// <summary>The app's own confirmation/notice dialog, replacing the stock Windows message box.</summary>
public partial class MessageDialog : Window
{
    private MessageDialog(string title, string message, string primary, string? secondary, DialogKind kind)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = primary;
        if (secondary is null) SecondaryButton.Visibility = Visibility.Collapsed;
        else SecondaryButton.Content = secondary;

        var (glyph, color) = kind switch
        {
            DialogKind.Info => ("", "#3B82F6"),
            DialogKind.Warning => ("", "#D97706"),
            DialogKind.Danger => ("", "#C42B1C"),
            _ => ("", "#3B82F6"),
        };
        Glyph.Text = glyph;
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        IconTile.Background = brush;
        // Destructive actions get a red button so they can't be mistaken for the safe choice.
        if (kind == DialogKind.Danger) PrimaryButton.Background = brush;
    }

    /// <summary>Dev screenshot runs: nobody is there to click, so dialogs are skipped (answered with Cancel) and logged.</summary>
    public static bool Unattended { get; set; }

    /// <summary>Asks the user; true when they chose the primary action.</summary>
    public static bool Ask(Window? owner, string title, string message, string primary, string? secondary = "Cancel",
        DialogKind kind = DialogKind.Question)
    {
        if (Unattended)
        {
            Serilog.Log.Information("Dialog skipped (unattended run): {Title} – {Message}", title, message);
            return false;
        }
        // The default (a parameter default can't be translated) in the app's language.
        if (secondary == "Cancel") secondary = L.T("Cancel");
        return Create(owner, title, message, primary, secondary, kind).ShowDialog() == true;
    }

    /// <summary>Two actions plus Cancel: true = primary, false = secondary, null = cancelled (Cancel, Esc or closed).</summary>
    public static bool? Choose(Window? owner, string title, string message, string primary, string secondary,
        DialogKind kind = DialogKind.Question, string? cancel = null)
    {
        if (Unattended) return null;
        var dialog = Create(owner, title, message, primary, secondary, kind);
        dialog.SecondaryButton.IsCancel = false;
        dialog.CancelButton.Visibility = Visibility.Visible;
        if (cancel is not null) dialog.CancelButton.Content = cancel;
        bool? choice = null;
        dialog.PrimaryButton.Click += (_, _) => choice = true;
        dialog.SecondaryButton.Click += (_, _) => choice = false;
        dialog.ShowDialog();
        return choice;
    }

    /// <summary>Asks, with a box to tick besides: null when cancelled, otherwise whether it was ticked.</summary>
    public static bool? AskWithOption(string title, string message, string primary, string option, string note, DialogKind kind)
    {
        if (Unattended) return null;
        var dialog = Create(null, title, message, primary, L.T("Cancel"), kind);
        dialog.ShowOption(option, note);
        return dialog.ShowDialog() == true ? dialog.Option.IsChecked == true : null;
    }

    private void ShowOption(string option, string note)
    {
        (OptionText.Text, OptionNote.Text) = (option, note);
        Option.Visibility = Visibility.Visible;
    }

    private static MessageDialog Create(Window? owner, string title, string message, string primary, string? secondary, DialogKind kind)
    {
        var dialog = new MessageDialog(title, message, primary, secondary, kind);
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && owner != dialog) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return dialog;
    }

    /// <summary>Dev screenshots only: a dialog that can be shown without blocking.</summary>
    internal static MessageDialog CreateForPreview(string title, string message, string primary, string? secondary, DialogKind kind) =>
        new(title, message, primary, secondary, kind);

    /// <summary>Tells the user something; one OK button.</summary>
    public static void Show(Window? owner, string title, string message, DialogKind kind = DialogKind.Info) =>
        Ask(owner, title, message, L.T("OK"), null, kind);

    private void Primary_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void Secondary_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
