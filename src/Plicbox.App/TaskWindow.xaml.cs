using System.Windows;
using System.Windows.Input;
using Plicbox.App.ViewModels;

namespace Plicbox.App;

/// <summary>A new task: a window of its own, like a new mail, event or contact.</summary>
public partial class TaskWindow : Window
{
    private readonly TaskEditor _editor;
    private bool _saving;

    public TaskWindow(TaskEditor editor)
    {
        InitializeComponent();
        Icon = Services.WindowIcons.Tasks;
        DataContext = _editor = editor;
        Owner = Application.Current.MainWindow;
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
        // Ctrl+S or Ctrl+Enter saves, Esc closes (as in the other editors).
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.S or Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Save_Click(this, new RoutedEventArgs()); }
            else if (e.Key == Key.Enter && e.OriginalSource == TitleBox) { e.Handled = true; Save_Click(this, new RoutedEventArgs()); }
            else if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        };
    }

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
