using Plicbox.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Plicbox.App;

/// <summary>The search box at the right end of a module's toolbar, always shown.</summary>
public partial class ToolbarSearch : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(ToolbarSearch),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((ToolbarSearch)d).OnTextChanged((string?)e.NewValue ?? "")));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(ToolbarSearch),
        new PropertyMetadata(L.T("Search"), (d, e) => ((ToolbarSearch)d).Input.Tag = L.F("{0} (Ctrl+F)", e.NewValue)));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

    public static readonly DependencyProperty OptionsActiveProperty = DependencyProperty.Register(nameof(OptionsActive), typeof(bool), typeof(ToolbarSearch),
        new PropertyMetadata(false, (d, _) => ((ToolbarSearch)d).UpdateOptionsLook()));

    /// <summary>Width of the box.</summary>
    public double BoxWidth { get => Box.Width; set => Box.Width = value; }

    /// <summary>The scope button at the right end of the box can be used (elsewhere it's shown greyed out, so the box looks
    /// the same in every module); clicking it raises <see cref="OptionsClick"/>.</summary>
    public bool HasOptions
    {
        get => OptionsButton.IsEnabled;
        set => OptionsButton.IsEnabled = value;
    }

    /// <summary>The search is narrowed (not everything): the scope button takes the accent colour.</summary>
    public bool OptionsActive { get => (bool)GetValue(OptionsActiveProperty); set => SetValue(OptionsActiveProperty, value); }

    public event RoutedEventHandler? OptionsClick;

    private void Options_Click(object sender, RoutedEventArgs e) => OptionsClick?.Invoke(OptionsButton, e);

    private void UpdateOptionsLook()
    {
        if (OptionsActive) OptionsButton.SetResourceReference(ForegroundProperty, "LinkBrush");
        else OptionsButton.ClearValue(ForegroundProperty);
    }

    public ToolbarSearch()
    {
        InitializeComponent();
        Input.Tag = L.F("{0} (Ctrl+F)", Placeholder);
    }

    /// <summary>Puts the cursor in the box, selecting what's there (Ctrl+F).</summary>
    public void FocusBox() =>
        Dispatcher.BeginInvoke(() => { Input.Focus(); Input.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);

    private void OnTextChanged(string value)
    {
        if (Input.Text != value) Input.Text = value;
        ClearButton.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Input_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (Text != Input.Text) Text = Input.Text;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        Text = "";
        Input.Focus();
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Text = "";
        Keyboard.ClearFocus();
    }
}
