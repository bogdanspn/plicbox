using Plicbox.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Plicbox.App.ViewModels;

namespace Plicbox.App;

/// <summary>The contacts module: smart lists, people, and a card for the selected person.</summary>
public partial class ContactsView : UserControl
{
    private readonly ContactsViewModel _vm;

    /// <summary>The module bar asked for another module ("mail", "calendar").</summary>
    public event Action<string>? ModuleRequested;

    /// <summary>Builds the New button's full menu (the main window's).</summary>
    public Func<ContextMenu>? NewMenuFactory { get; set; }

    public ContactsView(ContactsViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ContactsViewModel.GroupByCompany) or nameof(ContactsViewModel.People))
        {
            // Companies: one heading per organisation.
            var view = CollectionViewSource.GetDefaultView(_vm.People);
            view.GroupDescriptions.Clear();
            if (_vm.GroupByCompany) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(PersonViewModel.CompanyName)));
        }
        if (e.PropertyName == nameof(ContactsViewModel.Selected) && _vm.Selected is { } person)
            PeopleList.ScrollIntoView(person);
    }

    public void OnShown()
    {
        ContactsModuleButton.IsChecked = true;
        Focus();
    }

    public void FocusSearch() => PeopleSearch.FocusBox();

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        // Like Mail and Calendar: the chevron opens everything that can be created, the rest makes a contact.
        if (Mouse.GetPosition(NewButton).X > NewButton.ActualWidth - 34 && NewMenuFactory?.Invoke() is { } menu)
        {
            menu.PlacementTarget = NewButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        else
        {
            _vm.NewContactCommand.Execute(null);
        }
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } p || sender is not FrameworkElement button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        MenuItem Item(string header, string glyph, Action click, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = header, IsEnabled = enabled,
                Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 },
            };
            item.Click += (_, _) => click();
            return item;
        }
        if (!p.IsSaved) menu.Items.Add(Item(L.T("Save as Contact…"), "", () => _vm.EditCommand.Execute(null)));
        if (p.Email is { } email)
        {
            menu.Items.Add(Item(L.T("Copy Address"), "", () => Clipboard.SetText(email)));
            menu.Items.Add(Item(L.T("Show All Mail"), "", () => _vm.ShowAllMailCommand.Execute(null)));
            menu.Items.Add(Item(L.T("Create Rule for Their Mail…"), "", () => _vm.CreateRuleCommand.Execute(null)));
        }
        if (p.IsSaved)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Delete Contact"), "", () => _vm.DeleteCommand.Execute(null)));
        }
        menu.IsOpen = true;
    }

    /// <summary>The toolbar's "…": contacts in and out as .vcf files (from another address book, or a copy to keep).</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement button) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
        MenuItem Item(string header, string glyph, Func<Task> click)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 },
            };
            item.Click += async (_, _) => await click();
            return item;
        }
        menu.Items.Add(Item(L.T("Import Contacts…"), "", _vm.ImportContactsAsync));
        menu.Items.Add(Item(L.T("Export Contacts…"), "", _vm.ExportContactsAsync));
        menu.IsOpen = true;
    }

    /// <summary>Right-click on a person: the row becomes the selection and gets everything you can do with them.</summary>
    private void PeopleList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if ((e.OriginalSource as DependencyObject) is { } source && ItemsControl.ContainerFromElement(PeopleList, source) is ListBoxItem { DataContext: PersonViewModel hit })
            _vm.Selected = hit;
        if (_vm.Selected is not { } p) { e.Handled = true; return; }
        var menu = PeopleList.ContextMenu!;
        menu.Items.Clear();
        MenuItem Item(string header, string glyph, Action click, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = header, IsEnabled = enabled,
                Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 },
            };
            item.Click += (_, _) => click();
            return item;
        }
        var email = p.Email;
        menu.Items.Add(Item(L.T("Email"), "\uE715", () => _vm.EmailCommand.Execute(null), email is not null));
        menu.Items.Add(Item(L.T("Meeting…"), "\uE787", () => _vm.MeetingCommand.Execute(null), email is not null));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(p.IsSaved ? L.T("Edit…") : L.T("Save as Contact…"), "\uE70F", () => _vm.EditCommand.Execute(null)));
        menu.Items.Add(Item(p.IsFavorite ? L.T("Remove from Favorites") : L.T("Add to Favorites"), p.IsFavorite ? "\uE735" : "\uE734", () => _vm.ToggleFavoriteCommand.Execute(null)));
        if (email is not null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Copy Address"), "\uE8C8", () => Clipboard.SetText(email)));
            menu.Items.Add(Item(L.T("Show All Mail"), "\uE721", () => _vm.ShowAllMailCommand.Execute(null)));
            menu.Items.Add(Item(L.T("Create Rule for Their Mail…"), "\uE8B1", () => _vm.CreateRuleCommand.Execute(null)));
        }
        if (p.IsSaved)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Delete Contact"), "\uE74D", () => _vm.DeleteCommand.Execute(null)));
        }
    }

    private void MailModule_Click(object sender, RoutedEventArgs e) => ModuleRequested?.Invoke("mail");
    private void CalendarModule_Click(object sender, RoutedEventArgs e) => ModuleRequested?.Invoke("calendar");
    private void TasksModule_Click(object sender, RoutedEventArgs e) => ModuleRequested?.Invoke("tasks");
}
