using System.Windows;
using System.Windows.Input;
using Plicbox.App.ViewModels;

namespace Plicbox.App;

/// <summary>New contact, edit, or save someone from your mail: a window of its own, like a new mail or event.</summary>
public partial class ContactWindow : Window
{
    private readonly ContactEditor _editor;
    private bool _saving;

    public ContactWindow(ContactEditor editor)
    {
        InitializeComponent();
        Icon = Services.WindowIcons.Contact;
        DataContext = _editor = editor;
        Owner = Application.Current.MainWindow;
        // Start typing straight away: in the first name for a new contact.
        Loaded += (_, _) =>
        {
            GivenNameBox.Focus();
            GivenNameBox.CaretIndex = GivenNameBox.Text.Length;
        };
        // Ctrl+S saves, Esc closes (as in the event editor).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Save_Click(this, new RoutedEventArgs()); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

    /// <summary>The contact this window edits (to bring it forward instead of opening it twice).</summary>
    public long? ContactId => _editor.Original is { IsSaved: true } c ? c.Id : null;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_saving || _editor.Save is not { } save) return;
        _saving = true;
        try
        {
            if (await save(_editor)) Close();
        }
        finally
        {
            _saving = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
