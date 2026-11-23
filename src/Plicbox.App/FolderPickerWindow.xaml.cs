using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Plicbox.Core;

namespace Plicbox.App;

/// <summary>Pick a folder (Move to Folder…, Copy to Folder…, a rule's target), grouped under its account.</summary>
public partial class FolderPickerWindow : Window
{
    public sealed record Choice(Folder Folder, string Name, string Glyph, string Account);

    private readonly ICollectionView _view;

    /// <param name="accountName">Names the account above its folders; without it the list isn't grouped.</param>
    public FolderPickerWindow(string heading, string action, IEnumerable<Folder> folders, Func<long, string>? accountName = null)
    {
        InitializeComponent();
        Title = heading;
        Heading.Text = heading;
        OkButton.Content = action;
        Icon = Services.WindowIcons.Folder;
        var choices = folders.Select(f => new Choice(f, f.Name, Glyphs.ForRole(f.Role), accountName?.Invoke(f.AccountId) ?? "")).ToList();
        _view = CollectionViewSource.GetDefaultView(choices);
        if (accountName is not null) _view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(Choice.Account)));
        FolderList.ItemsSource = _view;
        // Short lists don't need a filter.
        if (choices.Count < 10) FilterBox.Visibility = Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            if (FilterBox.IsVisible) FilterBox.Focus();
            else FolderList.Focus();
        };
    }

    public Folder? Selected => (FolderList.SelectedItem as Choice)?.Folder;

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = FilterBox.Text.Trim();
        _view.Filter = text.Length == 0 ? null : o => o is Choice c
            && (c.Name.Contains(text, StringComparison.CurrentCultureIgnoreCase) || c.Folder.Path.Contains(text, StringComparison.CurrentCultureIgnoreCase));
        // One match left (or the first of several): ready to confirm with Enter.
        if (FolderList.SelectedItem is null || !_view.Contains(FolderList.SelectedItem))
            FolderList.SelectedItem = _view.Cast<object>().FirstOrDefault();
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Up)) return;
        // Arrow keys move through the list while typing.
        var items = _view.Cast<object>().ToList();
        if (items.Count == 0) return;
        var i = items.IndexOf(FolderList.SelectedItem!);
        FolderList.SelectedItem = items[Math.Clamp(i + (e.Key == Key.Down ? 1 : -1), 0, items.Count - 1)];
        FolderList.ScrollIntoView(FolderList.SelectedItem);
        e.Handled = true;
    }

    private void FolderList_SelectionChanged(object sender, SelectionChangedEventArgs e) => OkButton.IsEnabled = Selected is not null;

    private void FolderList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Selected is not null) DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
