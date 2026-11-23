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

/// <summary>The tasks module: smart lists and your lists, the tasks, and the selected task's details.</summary>
public partial class TasksView : UserControl
{
    private readonly TasksViewModel _vm;
    private GridLength _detailWidth = new(420);

    /// <summary>The module bar asked for another module ("mail", "calendar", "contacts").</summary>
    public event Action<string>? ModuleRequested;

    /// <summary>Builds the New button's full menu (the main window's).</summary>
    public Func<ContextMenu>? NewMenuFactory { get; set; }

    public TasksView(TasksViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        vm.PropertyChanged += OnViewModelChanged;
        // The sidebar: smart lists first, then a heading per account over its lists.
        CollectionViewSource.GetDefaultView(vm.Lists).GroupDescriptions.Add(new PropertyGroupDescription(nameof(TaskListEntry.Group)));
        ShowDetail(false);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TasksViewModel.Rows):
                var view = CollectionViewSource.GetDefaultView(_vm.Rows);
                view.GroupDescriptions.Clear();
                if (_vm.Rows.Any(r => r.Group.Length > 0)) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TaskRow.Group)));
                break;
            case nameof(TasksViewModel.Editor):
                ShowDetail(_vm.Editor is not null);
                break;
            case nameof(TasksViewModel.Selected) when _vm.Selected is { } row:
                TaskList.ScrollIntoView(row);
                break;
        }
    }

    /// <summary>The detail pane takes room only while a task is open.</summary>
    private void ShowDetail(bool show)
    {
        if (!show && DetailColumn.Width.Value > 0) _detailWidth = DetailColumn.Width;
        DetailColumn.Width = show ? _detailWidth : new GridLength(0);
        DetailColumn.MinWidth = show ? 320 : 0;
        DetailSplitter.Visibility = DetailPane.Visibility = DetailLine.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    public void OnShown()
    {
        TasksModuleButton.IsChecked = true;
        Dispatcher.BeginInvoke(() => QuickBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    public void FocusSearch() => TaskSearch.FocusBox();

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        // Like the other modules: the chevron opens everything that can be created, the rest makes a task.
        if (Mouse.GetPosition(NewButton).X > NewButton.ActualWidth - 34 && NewMenuFactory?.Invoke() is { } menu)
        {
            menu.PlacementTarget = NewButton;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        else
        {
            _vm.NewTaskCommand.Execute(null);
        }
    }

    private void EditorCheck_Click(object sender, RoutedEventArgs e) => _vm.ToggleEditorDone();

    private void TaskList_KeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.Selected is not { } row || e.OriginalSource is TextBox) return;
        if (e.Key == Key.Space) { e.Handled = true; _vm.ToggleCompleteCommand.Execute(row); }
        else if (e.Key == Key.Delete) { e.Handled = true; _vm.DeleteCommand.Execute(null); }
    }

    // ───────── context menus ─────────

    private static MenuItem Item(string header, string? glyph, Action click, bool enabled = true, string? gesture = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, InputGestureText = gesture ?? "" };
        if (glyph is not null) item.Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 };
        item.Click += (_, _) => click();
        return item;
    }

    private void TaskList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = TaskList.ContextMenu!;
        menu.Items.Clear();
        // The row under the mouse becomes the selection (as in the mail list).
        if ((e.OriginalSource as DependencyObject) is { } source && ItemsControl.ContainerFromElement(TaskList, source) is ListBoxItem { DataContext: TaskRow hit })
            _vm.Selected = hit;
        if (_vm.Selected is not { } row) { e.Handled = true; return; }

        var today = DateOnly.FromDateTime(DateTime.Today);
        menu.Items.Add(Item(row.IsCompleted ? L.T("Mark as Not Done") : L.T("Mark as Done"), "", () => _vm.ToggleCompleteCommand.Execute(row), gesture: L.T("Space")));
        menu.Items.Add(new Separator());
        var due = Item(L.T("Due"), "", () => { });
        due.Items.Add(Item(L.T("Today"), null, () => _ = _vm.SetDueAsync(row, today)));
        due.Items.Add(Item(L.T("Tomorrow"), null, () => _ = _vm.SetDueAsync(row, today.AddDays(1))));
        due.Items.Add(Item(L.T("Next Week"), null, () => _ = _vm.SetDueAsync(row, today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 6) % 7 + 1))));
        due.Items.Add(new Separator());
        due.Items.Add(Item(L.T("No Date"), null, () => _ = _vm.SetDueAsync(row, null), row.Model.Due is not null));
        menu.Items.Add(due);
        var targets = _vm.MoveTargets(row).ToList();
        var move = Item(L.T("Move to"), "", () => { }, targets.Count > 0);
        foreach (var target in targets)
            move.Items.Add(Item(target.Group.Length == 0 || target.Group == L.T("Lists") ? target.Title : $"{target.Title} · {target.Group}", "", () => _ = _vm.MoveRowAsync(row, target.List!.Id)));
        menu.Items.Add(move);
        if (row.HasEmail) menu.Items.Add(Item(L.T("Open Email"), "", () => _ = _vm.OpenRowEmailAsync(row)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("Delete"), "", () => _vm.DeleteCommand.Execute(null), gesture: L.T("Del")));
    }

    /// <summary>The toolbar's Due: the same choices as the right-click menu.</summary>
    private void Due_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } row) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var menu = new ContextMenu { PlacementTarget = DueButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(Item(L.T("Today"), "\uE706", () => _ = _vm.SetDueAsync(row, today)));
        menu.Items.Add(Item(L.T("Tomorrow"), "\uE787", () => _ = _vm.SetDueAsync(row, today.AddDays(1))));
        menu.Items.Add(Item(L.T("Next Week"), "\uE8C0", () => _ = _vm.SetDueAsync(row, today.AddDays(((int)DayOfWeek.Monday - (int)today.DayOfWeek + 6) % 7 + 1))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L.T("No Date"), "\uE711", () => _ = _vm.SetDueAsync(row, null), row.Model.Due is not null));
        menu.IsOpen = true;
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected is not { } row) return;
        var menu = new ContextMenu { PlacementTarget = MoveButton, Placement = PlacementMode.Bottom };
        foreach (var target in _vm.MoveTargets(row))
            menu.Items.Add(Item(target.Group.Length == 0 || target.Group == L.T("Lists") ? target.Title : $"{target.Title} · {target.Group}", "\uE8FD", () => _ = _vm.MoveRowAsync(row, target.List!.Id)));
        if (menu.Items.Count == 0) menu.Items.Add(Item(L.T("No other lists"), null, () => { }, false));
        menu.IsOpen = true;
    }

    private void Lists_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var menu = ListsBox.ContextMenu!;
        menu.Items.Clear();
        var entry = (e.OriginalSource as DependencyObject) is { } source && ItemsControl.ContainerFromElement(ListsBox, source) is ListBoxItem { DataContext: TaskListEntry hit }
            ? hit : null;
        if (entry?.List is not null)
        {
            menu.Items.Add(Item(L.T("Rename List…"), "", () => _ = _vm.RenameListAsync(entry)));
            menu.Items.Add(Item(L.T("Delete Completed Tasks"), "", () => _ = _vm.DeleteCompletedAsync(entry), _vm.HasCompleted(entry)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item(L.T("Delete List…"), "", () => _ = _vm.DeleteListAsync(entry)));
            menu.Items.Add(new Separator());
        }
        else if (entry?.Kind == TaskView.Completed)
        {
            menu.Items.Add(Item(L.T("Delete All Completed Tasks"), "", () => _ = _vm.DeleteCompletedAsync(entry), _vm.HasCompleted(entry)));
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(Item(L.T("New List…"), "", () => _vm.NewListCommand.Execute(null)));
    }

    private void MailModule_Click(object sender, RoutedEventArgs e) => ModuleRequested?.Invoke("mail");
    private void CalendarModule_Click(object sender, RoutedEventArgs e) => ModuleRequested?.Invoke("calendar");
    private void ContactsModule_Click(object sender, RoutedEventArgs e) => ModuleRequested?.Invoke("contacts");
}
