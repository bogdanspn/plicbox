using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Plicbox.App;

/// <summary>Properties of a message or a folder: labels on the left, values on the right, each value selectable to copy.</summary>
public partial class PropertiesDialog : Window
{
    private PropertiesDialog(string kind, string title, IEnumerable<(string Label, string Value)> fields)
    {
        InitializeComponent();
        Title = title;
        KindText.Text = kind;
        TitleText.Text = title;
        foreach (var (label, value) in fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
        {
            var row = Fields.RowDefinitions.Count;
            Fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock
            {
                Text = label, Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
                Margin = new Thickness(0, 4, 18, 4), VerticalAlignment = VerticalAlignment.Top,
            };
            var text = new TextBox { Text = value, Style = (Style)FindResource("SelectableText"), Margin = new Thickness(0, 4, 0, 4) };
            Grid.SetRow(name, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            Fields.Children.Add(name);
            Fields.Children.Add(text);
        }
    }

    /// <param name="kind">What it is ("Message properties"), above the title.</param>
    /// <param name="fields">Label and value; empty values are left out. Several values go one per line.</param>
    public static void Show(Window? owner, string kind, string title, IEnumerable<(string Label, string Value)> fields)
    {
        if (MessageDialog.Unattended) return; // dev screenshot runs
        var dialog = new PropertiesDialog(kind, title, fields);
        owner ??= Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && owner != dialog) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        dialog.ShowDialog();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
