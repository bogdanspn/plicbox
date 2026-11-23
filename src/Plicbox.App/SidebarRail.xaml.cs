using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Plicbox.App;

/// <summary>
/// The sidebar folded down to a strip of icons (dragged narrow, or ‹ at the end of the module tabs; as in eM Client):
/// the module's lists or folders as icons with their names as tooltips, and the module tabs stacked underneath, the
/// last of them › to open it again.
/// </summary>
public partial class SidebarRail : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(nameof(ItemsSource), typeof(IEnumerable), typeof(SidebarRail));
    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(nameof(SelectedItem), typeof(object), typeof(SidebarRail),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public IEnumerable? ItemsSource { get => (IEnumerable?)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
    public object? SelectedItem { get => GetValue(SelectedItemProperty); set => SetValue(SelectedItemProperty, value); }

    /// <summary>An icon was clicked (also when it's already the selected one), with the row it sits in.</summary>
    public event Action<object, FrameworkElement>? ItemClicked;

    /// <summary>A row's Tag while a menu from it is open: it keeps its hover tint.</summary>
    public const string MenuOpen = "menu-open";

    public static readonly DependencyProperty MoreHasSelectionProperty = DependencyProperty.Register(nameof(MoreHasSelection), typeof(bool), typeof(SidebarRail));
    public static readonly DependencyProperty MoreHasUnreadProperty = DependencyProperty.Register(nameof(MoreHasUnread), typeof(bool), typeof(SidebarRail));

    /// <summary>What's open is among the icons that didn't fit: ⋯ lights up as that icon would.</summary>
    public bool MoreHasSelection { get => (bool)GetValue(MoreHasSelectionProperty); private set => SetValue(MoreHasSelectionProperty, value); }
    /// <summary>An account under ⋯ has unread mail: ⋯ carries its blue dot.</summary>
    public bool MoreHasUnread { get => (bool)GetValue(MoreHasUnreadProperty); private set => SetValue(MoreHasUnreadProperty, value); }

    public SidebarRail()
    {
        InitializeComponent();
        SizeChanged += (_, _) => QueueFit();
        IsVisibleChanged += (_, _) => QueueFit();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == SelectedItemProperty) QueueFit();
    }

    // ─── More icons than room (many accounts, a short window): the ones that don't fit go under ⋯ ───

    private readonly List<object> _hidden = [];
    private readonly List<RailEntry> _watched = [];
    private bool _fitQueued;

    private void Items_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The strip never scrolls (whatever doesn't fit is under ⋯), also when the list brings a selected icon into view.
        if (e.VerticalOffset > 0 && e.OriginalSource is ScrollViewer viewer) viewer.ScrollToTop();
        if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0) QueueFit();
    }

    private void Items_PreviewMouseWheel(object sender, MouseWheelEventArgs e) => e.Handled = true;

    private void QueueFit()
    {
        if (_fitQueued) return;
        _fitQueued = true;
        Dispatcher.BeginInvoke(() => { _fitQueued = false; Fit(); }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Shows the icons that fit whole, then ⋯ straight after the last of them; everything further down is cut off and
    /// listed in ⋯'s menu instead.
    /// </summary>
    private void Fit()
    {
        foreach (var entry in _watched) entry.PropertyChanged -= Watched_PropertyChanged;
        _watched.Clear();
        _hidden.Clear();
        var room = Items.ActualHeight;
        var rows = Items.Items.Cast<object>()
            .Select(item => (Item: item, Row: Items.ItemContainerGenerator.ContainerFromItem(item) as FrameworkElement))
            .ToList();
        double Bottom(FrameworkElement row) => row.TransformToAncestor(Items).Transform(new Point(0, row.ActualHeight)).Y;
        var overflows = IsVisible && room > 0 && rows.Any(r => r.Row is not { IsVisible: true } || Bottom(r.Row) > room + 0.5);
        if (!overflows)
        {
            Items.Clip = null;
            MoreButton.Visibility = Visibility.Collapsed;
            MoreHasSelection = MoreHasUnread = false;
            return;
        }

        // Room for ⋯ under the last whole icon.
        var cut = 0.0;
        foreach (var (item, row) in rows)
        {
            if (row is { IsVisible: true } && Bottom(row) <= room - MoreHeight + 0.5) cut = Bottom(row);
            else _hidden.Add(item);
        }
        Items.Clip = new RectangleGeometry(new Rect(0, 0, Items.ActualWidth, cut));
        MoreButton.Margin = new Thickness(0, cut, 0, 0);
        MoreButton.Visibility = Visibility.Visible;
        MoreHasSelection = SelectedItem is { } selected && _hidden.Contains(selected);
        _watched.AddRange(_hidden.OfType<RailEntry>());
        foreach (var entry in _watched) entry.PropertyChanged += Watched_PropertyChanged;
        MoreHasUnread = _watched.Any(e => e.HasUnread);
    }

    private const double MoreHeight = 48;

    private void Watched_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RailEntry.HasUnread)) MoreHasUnread = _watched.Any(w => w.HasUnread);
    }

    /// <summary>
    /// ⋯: the icons that didn't fit, each with its name. A list or folder opens as if its icon were clicked; an account
    /// opens its own menu, from ⋯.
    /// </summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = PlacementMode.Right, HorizontalOffset = 2 };
        foreach (var item in _hidden.ToList())
        {
            var entry = new MenuItem { Header = TitleOf(item), Icon = SmallIcon(item) };
            if (ReferenceEquals(item, SelectedItem)) entry.FontWeight = FontWeights.SemiBold;
            entry.Click += (_, _) =>
            {
                if (item is not RailEntry { IsAccount: true }) SelectedItem = item;
                // After this menu has closed, so an account's own menu can open in its place.
                Dispatcher.BeginInvoke(() => ItemClicked?.Invoke(item, MoreButton), System.Windows.Threading.DispatcherPriority.Input);
            };
            menu.Items.Add(entry);
        }
        MoreButton.Tag = MenuOpen;
        menu.Closed += (_, _) => MoreButton.ClearValue(TagProperty);
        menu.IsOpen = true;
    }

    private static string TitleOf(object item) => item switch
    {
        RailEntry entry => entry.Title,
        _ => item.GetType().GetProperty("Title")?.GetValue(item) as string ?? item.ToString() ?? "",
    };

    /// <summary>The icon as the rail shows it, at menu size: an account's picture or initial, else the list's own icon.</summary>
    private static FrameworkElement SmallIcon(object item)
    {
        var (glyph, picture, color, initial) = item switch
        {
            RailEntry { IsAccount: true, HasGlyphIcon: false } a => ("", a.Picture, a.Color, a.Initial),
            RailEntry r => (r.Glyph, null, null, ""),
            ViewModels.TaskListEntry { HasAccountIcon: true } t => ("", t.AccountPicture, t.AccountColor, t.AccountInitial),
            _ => (item.GetType().GetProperty("Glyph")?.GetValue(item) as string ?? "", (ImageSource?)null, (Brush?)null, ""),
        };
        if (picture is not null)
            return new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill } };
        if (color is not null)
            return new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = color,
                Child = new TextBlock { Text = initial, Foreground = Brushes.White, FontSize = 10, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
        return new GlyphIcon { Glyph = glyph, FontSize = 16 };
    }

    // An account isn't selected when pressed (only its menu opens): otherwise it would flash white, and the icon for
    // what's on screen lose its tint, until the selection is put back.
    private void Items_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Items, e.OriginalSource as DependencyObject) is ListBoxItem { DataContext: RailEntry { IsAccount: true } })
            e.Handled = true;
    }

    private void Items_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Items, e.OriginalSource as DependencyObject) is ListBoxItem row)
            ItemClicked?.Invoke(row.DataContext, row);
    }
}

/// <summary>An icon in a rail: a favorite, or an account (its picture, else its initial on its colour; its folders or calendars in a menu).</summary>
public sealed partial class RailEntry : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public required string Title { get; init; }
    public string Glyph { get; init; } = "";
    /// <summary>The rail group: "" for favorites, set for accounts (a line above them).</summary>
    public string Group { get; init; } = "";
    public bool IsAccount { get; init; }
    public string Initial { get; init; } = "";
    public Brush? Color { get; init; }
    /// <summary>The account's own picture: shown instead of the initial when there is one.</summary>
    public ImageSource? Picture { get; init; }
    public bool HasPicture => Picture is not null;
    /// <summary>A group with a menu but no account (calendars on this computer): its icon instead of an initial.</summary>
    public bool HasGlyphIcon => IsAccount && Glyph.Length > 0;
    public object? Target { get; init; }

    /// <summary>Unread mail waiting (All Inboxes, Unread, anywhere in an account but Trash and Junk): a blue dot on the icon.</summary>
    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty] private bool _hasUnread;
}

/// <summary>The rail row's background for the ring around the unread dot: the rail's own colour where the row has none.</summary>
public sealed class RowBackgroundConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) =>
        value is SolidColorBrush { Color.A: > 0 } brush ? brush : (Brush)Application.Current.FindResource("ChromeBrush");

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RailTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Glyph { get; set; }
    public DataTemplate? Entry { get; set; }
    public DataTemplate? List { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => item switch
    {
        RailEntry => Entry,
        ViewModels.TaskListEntry => List,
        _ => Glyph,
    };
}
