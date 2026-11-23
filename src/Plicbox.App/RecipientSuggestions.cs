using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Plicbox.App.Services;
using Plicbox.Core;

namespace Plicbox.App;

/// <summary>
/// Suggestions under a To/Cc/Bcc box as you type: the part after the last comma is matched against everyone you
/// write to. ↑/↓ choose, Enter or Tab take it (and add ", " for the next one), Esc closes.
/// </summary>
internal sealed class RecipientSuggestions
{
    private readonly TextBox _box;
    private readonly AddressBook _book;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private int _version;
    private bool _applying;

    public static RecipientSuggestions Attach(TextBox box, AddressBook book) => new(box, book);

    /// <summary>Dev screenshots: the suggestion popup.</summary>
    internal Popup Popup => _popup;

    /// <summary>Dev screenshots: text set in code moves the caret only after TextChanged, so look again.</summary>
    internal Task RefreshAsync() => UpdateAsync();

    private RecipientSuggestions(TextBox box, AddressBook book)
    {
        (_box, _book) = (box, book);
        _list = new ListBox
        {
            BorderThickness = new Thickness(0), Background = Brushes.Transparent, MaxHeight = 320, Focusable = false,
            ItemTemplate = Template(), ItemContainerStyle = RowStyle(),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.PreviewMouseLeftButtonUp += (_, _) => Accept();
        _popup = new Popup
        {
            PlacementTarget = box, Placement = PlacementMode.Bottom, AllowsTransparency = true, StaysOpen = false, Focusable = false,
            Child = new Border
            {
                Child = _list, Background = Services.Theme.Brush("SurfaceBrush"), BorderBrush = Services.Theme.Brush("StrongDividerBrush"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(4), Margin = new Thickness(0, 2, 8, 8),
                MinWidth = 360, Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.16 },
            },
        };
        _popup.SetResourceReference(TextElement.FontFamilyProperty, "UiFont"); // a popup doesn't take the window's font
        box.TextChanged += async (_, _) => await UpdateAsync();
        box.PreviewKeyDown += OnKey;
        box.LostKeyboardFocus += (_, e) =>
        {
            if (e.NewFocus is not DependencyObject d || !IsInside(d)) _popup.IsOpen = false;
        };
    }

    private bool IsInside(DependencyObject d)
    {
        for (var x = d; x is not null; x = VisualTreeHelper.GetParent(x) ?? LogicalTreeHelper.GetParent(x))
            if (x == _list) return true;
        return false;
    }

    /// <summary>The address being typed: after the last comma or semicolon before the cursor.</summary>
    private (int Start, string Text) CurrentToken()
    {
        var caret = _box.CaretIndex;
        var before = _box.Text[..caret];
        var start = Math.Max(before.LastIndexOf(','), before.LastIndexOf(';')) + 1;
        return (start, before[start..].TrimStart());
    }

    private async Task UpdateAsync()
    {
        if (_applying) return;
        var version = ++_version;
        var (_, token) = CurrentToken();
        if (token.Length < 1 || token.Contains('<'))
        {
            _popup.IsOpen = false;
            return;
        }
        var already = Addresses.Parse(_box.Text).Select(a => a.Address).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = await _book.SuggestAsync(token, already);
        if (version != _version) return; // typed on meanwhile
        _list.ItemsSource = found;
        _list.SelectedIndex = found.Count > 0 ? 0 : -1;
        _popup.IsOpen = found.Count > 0 && (_box.IsKeyboardFocused || _box.IsFocused);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (!_popup.IsOpen) return;
        switch (e.Key)
        {
            case Key.Down:
                _list.SelectedIndex = Math.Min(_list.Items.Count - 1, _list.SelectedIndex + 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                _list.SelectedIndex = Math.Max(0, _list.SelectedIndex - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter or Key.Tab when _list.SelectedItem is not null:
                Accept();
                e.Handled = true;
                break;
            case Key.Escape:
                _popup.IsOpen = false;
                e.Handled = true;
                break;
        }
    }

    private void Accept()
    {
        if (_list.SelectedItem is not AddressSuggestion s) return;
        var (start, _) = CurrentToken();
        var caret = _box.CaretIndex;
        var after = _box.Text[caret..];
        var nextComma = after.IndexOfAny([',', ';']);
        var rest = nextComma >= 0 ? after[nextComma..].TrimStart(',', ';', ' ') : "";
        var prefix = _box.Text[..start].TrimEnd();
        var inserted = (prefix.Length > 0 ? prefix + " " : "") + s.Formatted + ", ";
        _applying = true;
        _box.Text = inserted + rest;
        _box.CaretIndex = inserted.Length;
        _applying = false;
        _popup.IsOpen = false;
        _box.Focus();
    }

    // Rows highlight in the app's own selection colour, like the message list.
    private static Style RowStyle()
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(4, 2, 4, 2));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("ListHoverBrush"), "Bd"));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Border.BackgroundProperty, Application.Current.FindResource("SelectionBrush"), "Bd"));
        template.Triggers.Add(hover);
        template.Triggers.Add(selected);
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        return style;
    }

    private static DataTemplate Template()
    {
        // Name on top, address below; a small person icon for saved contacts.
        var root = new FrameworkElementFactory(typeof(DockPanel));
        root.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 4, 6, 4));
        var icon = new FrameworkElementFactory(typeof(TextBlock));
        icon.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"));
        icon.SetValue(TextBlock.TextProperty, "");
        icon.SetValue(TextBlock.FontSizeProperty, 14.0);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "HintBrush");
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 3, 10, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Top);
        icon.SetValue(DockPanel.DockProperty, Dock.Left);
        root.AppendChild(icon);
        var lines = new FrameworkElementFactory(typeof(StackPanel));
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AddressSuggestion.Display)));
        name.SetValue(TextBlock.FontSizeProperty, 14.0);
        lines.AppendChild(name);
        var address = new FrameworkElementFactory(typeof(TextBlock));
        address.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(AddressSuggestion.Address)));
        address.SetValue(TextBlock.FontSizeProperty, 12.5);
        address.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        lines.AppendChild(address);
        root.AppendChild(lines);
        return new DataTemplate { VisualTree = root };
    }
}
