using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Plicbox.App.Services;
using Plicbox.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Plicbox.Core;
using Microsoft.Web.WebView2.Core;

namespace Plicbox.App;

public partial class MainWindow : Window, IReaderHost
{
    private readonly MainViewModel _vm;
    private readonly ISettingsStore _settings;
    private readonly TrayService _tray;

    private readonly CalendarView _calendarView;
    private readonly CalendarViewModel _calendar;
    private readonly ContactsView _contactsView;
    private readonly ContactsViewModel _contacts;
    private readonly TasksView _tasksView;
    private readonly TasksViewModel _tasks;
    private string _module = "mail";
    private bool _calendarShown => _module == "calendar";

    public MainWindow(MainViewModel vm, ISettingsStore settings, TrayService tray, CalendarViewModel calendar, ContactsViewModel contacts, TasksViewModel tasks,
        Updates updates)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        (_settings, _tray) = (settings, tray);
        UpdateNote.DataContext = updates;
        Title = TrayService.AppName; // the taskbar's name; a dev build says so
        if (!Updates.IsInstalled) DevChip.Visibility = Visibility.Visible;

        _calendar = calendar;
        _calendarView = new CalendarView(calendar) { NewMenuFactory = BuildNewMenu };
        _calendarView.ModuleRequested += ShowModule;
        CalendarHost.Children.Add(_calendarView);

        _contacts = contacts;
        _contactsView = new ContactsView(contacts) { NewMenuFactory = BuildNewMenu };
        _contactsView.ModuleRequested += ShowModule;
        ContactsHost.Children.Add(_contactsView);
        _tasks = tasks;
        _tasksView = new TasksView(tasks) { NewMenuFactory = BuildNewMenu };
        _tasksView.ModuleRequested += ShowModule;
        TasksHost.Children.Add(_tasksView);
        tasks.OpenMessageRequested = id => vm.OpenMessageWindowAsync(id);
        // The contact card reaches into mail and calendar.
        contacts.ComposeRequested = to => vm.ComposeTo(to);
        contacts.OpenMessageRequested = id => vm.OpenMessageWindowAsync(id);
        contacts.ShowMailRequested = address =>
        {
            ShowModule("mail");
            vm.SearchText = address;
        };
        contacts.MeetingRequested = (name, address) => calendar.NewMeetingWith(name, address);
        contacts.CreateRuleRequested = (name, address) => _ = vm.CreateRuleForSenderAsync(name, address);
        // Reminders always pop up: unlike new mail, they matter even while you're looking at the app.
        calendar.ReminderDue += (title, text) => _tray.Notify(title, text);
        Icon = AppIcon.Window;
        vm.NewMailArrived += OnNewMail;
        Activated += (_, _) =>
        {
            if (_unseenMail == 0) return;
            _unseenMail = 0;
            ShowUnseenMail();
        };
        vm.PrintRequested += () => Reading.Print();
        // The reading pane: the people menus come back here, the message menu gets the mail actions.
        MessageReader.Host = this;
        Reading.MessageMenu = MessageMenuItems;
        Reading.MessageMenuOpening += vm.RefreshMenuState;
        vm.IsListAtTop = () => FindChild<ScrollViewer>(MessageList) is not { } sv || sv.VerticalOffset < 0.5;
        vm.ScrollToTopRequested += () =>
        {
            if (FindChild<ScrollViewer>(MessageList) is { } sv) sv.ScrollToTop();
        };
        vm.SelectionRestoreRequested += rows =>
        {
            foreach (var row in rows.Where(r => !MessageList.SelectedItems.Contains(r))) MessageList.SelectedItems.Add(row);
        };
        MessageList.SelectionChanged += (_, _) =>
            _vm.SetSelectedMessages(MessageList.SelectedItems.OfType<MessageItemViewModel>().ToList());
        MessageList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(MessageList_ScrollChanged));
        // Settings > Appearance > Reading pane, and Fill screen's open message.
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ReadingLayout)) ApplyReadingLayout();
            else if (e.PropertyName == nameof(MainViewModel.IsReadingOpen))
            {
                ApplyReadingLayout();
                if (!_vm.IsReadingOpen) FocusListAfterReading();
            }
            else if (e.PropertyName is nameof(MainViewModel.SortColumn) or nameof(MainViewModel.SortDescending)) ShowCompactSort();
        };
        ApplyReadingLayout();
        StateChanged += (_, _) => OnStateChanged();
        Loaded += OnLoaded;
        Loaded += (_, _) => FitToolbar();
        ToolbarRow.SizeChanged += (_, _) => FitToolbar();
        LeftTools.SizeChanged += (_, _) => FitToolbar(); // the sync status beside Refresh changes length
        Closing += OnClosing;
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Find, (_, _) =>
        {
            if (_module == "calendar") _calendarView.FocusSearch();
            else if (_module == "contacts") _contactsView.FocusSearch();
            else if (_module == "tasks") _tasksView.FocusSearch();
            else MailSearch.FocusBox();
        }));
        // The search box opening or closing changes the room left for the actions.
        MailSearch.SizeChanged += (_, _) => FitToolbar();
        // F4 finds in the open message (eM Client's shortcut); Ctrl+F stays the mailbox search.
        PreviewKeyDown += (_, e) =>
        {
            // Fill screen: Esc goes from the open message back to the list (not while typing in a box).
            if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && _module == "mail" && _vm.IsReadingOpen
                && Keyboard.FocusedElement is not TextBox)
            {
                e.Handled = true;
                _vm.CloseReadingCommand.Execute(null);
                return;
            }
            // Ctrl+1 … Ctrl+4: mail, calendar, contacts, tasks, as in eM Client and Outlook.
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4)
            {
                e.Handled = true;
                ShowModule(e.Key switch { Key.D1 => "mail", Key.D2 => "calendar", Key.D3 => "contacts", _ => "tasks" });
                return;
            }
            // Ctrl+P: our print window. Handled here too when the message body has the keyboard, or the browser
            // would open its own print preview inside the reading pane.
            if (e.Key == Key.P && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                if (_module == "mail" && _vm.PrintCommand.CanExecute(null)) _vm.PrintCommand.Execute(null);
                return;
            }
            // Ctrl+N: a new item of the module you're in. Mail writes from the sidebar's account, else the default one.
            if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
            {
                e.Handled = true;
                switch (_module)
                {
                    case "calendar": _calendar.NewEventCommand.Execute(null); break;
                    case "contacts": _contacts.NewContact(); break;
                    case "tasks": _tasks.NewTaskCommand.Execute(null); break;
                    default: _vm.NewCommand.Execute(null); break;
                }
                return;
            }
            if (e.Key != Key.F4 || Keyboard.Modifiers != ModifierKeys.None) return;
            e.Handled = true;
            _ = Reading.FindAsync();
        };

        SyncSidebarWidths();

        tray.OpenRequested += ShowFromTray;
        tray.SyncRequested += () => vm.RefreshCommand.Execute(null);
        tray.ExitRequested += App.ExitApp;
        tray.Initialize();
    }

    /// <summary>
    /// One left-column width for every module, kept between runs: dragging the edge in any module moves it in all.
    /// Let go narrower than <see cref="FoldBelow"/>, or click ‹ at the end of the module tabs, and it folds into a strip of
    /// icons (as in eM Client); › there or dragging it wide again opens it at the last width (never narrower than
    /// <see cref="SidebarMinWidth"/>).
    /// </summary>
    private void SyncSidebarWidths()
    {
        var columns = new[] { SidebarColumn, _calendarView.SidebarColumn, _contactsView.SidebarColumn, _tasksView.SidebarColumn };
        var sidebars = new (Grid Panel, SidebarRail Rail, System.Windows.Controls.Primitives.UniformGrid Tabs, Button Fold)[]
        {
            (SidebarPanel, Rail, ModuleTabs, FoldButton),
            (_calendarView.SidebarPanel, _calendarView.Rail, _calendarView.ModuleTabs, _calendarView.FoldButton),
            (_contactsView.SidebarPanel, _contactsView.Rail, _contactsView.ModuleTabs, _contactsView.FoldButton),
            (_tasksView.SidebarPanel, _tasksView.Rail, _tasksView.ModuleTabs, _tasksView.FoldButton),
        };
        var isFolded = false;
        void Apply(double width, bool folded)
        {
            width = folded ? RailWidth : Math.Clamp(width, SidebarMinWidth, SidebarColumn.MaxWidth);
            foreach (var column in columns) column.Width = new GridLength(width);
            isFolded = folded;
            foreach (var (panel, rail, tabs, fold) in sidebars)
            {
                fold.Content = folded ? "" : ""; // › opens, ‹ folds
                fold.ToolTip = folded ? L.T("Expand sidebar") : L.T("Collapse sidebar");
                panel.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
                rail.Visibility = folded ? Visibility.Visible : Visibility.Collapsed;
                // The module tabs: across the foot of the sidebar, or stacked under the icons.
                tabs.Rows = folded ? 0 : 1;
                tabs.Columns = folded ? 1 : 0;
            }
        }
        void Save(AppSettings settings)
        {
            if (settings != _settings.Current) _settings.Save(settings);
        }
        Apply(_settings.Current.SidebarWidth, _settings.Current.SidebarCollapsed);
        _devFoldSidebar = folded => Apply(_settings.Current.SidebarWidth, folded);
        foreach (var (splitter, column) in new[] { (SidebarSplitter, SidebarColumn), (_calendarView.SidebarSplitter, _calendarView.SidebarColumn),
                     (_contactsView.SidebarSplitter, _contactsView.SidebarColumn), (_tasksView.SidebarSplitter, _tasksView.SidebarColumn) })
            splitter.DragCompleted += (_, _) =>
            {
                var width = Math.Round(column.ActualWidth);
                var folded = width < FoldBelow;
                Apply(width, folded);
                Save(folded
                    ? _settings.Current with { SidebarCollapsed = true }
                    : _settings.Current with { SidebarCollapsed = false, SidebarWidth = Math.Clamp(width, SidebarMinWidth, SidebarColumn.MaxWidth) });
            };
        foreach (var (_, _, _, fold) in sidebars)
            fold.Click += (_, _) =>
            {
                Apply(_settings.Current.SidebarWidth, !isFolded);
                Save(_settings.Current with { SidebarCollapsed = isFolded });
            };
        SetUpMailRail();
        SetUpCalendarRail();
        SetUpTasksRail();
    }

    private const double RailWidth = 56, FoldBelow = 150, SidebarMinWidth = 200;

    /// <summary>A date heading in the message list was clicked: its group folds away, or opens again.</summary>
    private void MessageGroup_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.ToggleButton { DataContext: System.Windows.Data.CollectionViewGroup { Name: { } name } } header) return;
        var key = name.ToString() ?? "";
        if (header.IsChecked == true) GroupOpenConverter.Folded.Remove(key);
        else GroupOpenConverter.Folded.Add(key);
    }

    /// <summary>Dev aid (--screenshot-print with PLICBOX_RAIL): fold or open the sidebar without saving it.</summary>
    private Action<bool>? _devFoldSidebar;

    // ─── The folded mail sidebar: favorites, then each account (its folders in a menu) ───

    private readonly System.Collections.ObjectModel.ObservableCollection<RailEntry> _railEntries = [];

    private void SetUpMailRail()
    {
        var view = new System.Windows.Data.CollectionViewSource { Source = _railEntries }.View;
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(RailEntry.Group)));
        Rail.ItemsSource = view;
        Rail.AllowDrop = true;
        Rail.DragOver += Rail_DragOver;
        Rail.DragLeave += Rail_DragLeave;
        Rail.Drop += Rail_Drop;
        void Rebuild()
        {
            _railEntries.Clear();
            var favorites = _vm.SidebarNodes.FirstOrDefault(n => n.IsGroup && n.Parent is null && n.AccountId == 0);
            foreach (var node in _vm.SidebarNodes.Where(n => !n.IsGroup && n.Parent == favorites && (!n.ShowOnlyIfUnread || n.Count > 0)))
            {
                var entry = new RailEntry { Title = node.Title, Glyph = node.Glyph, Target = node };
                // New mail in any inbox; Unread also has what rules filed into other folders.
                // The Outbox: something waiting to go.
                if (node.Kind is NodeKind.UnifiedInbox or NodeKind.Unread or NodeKind.Outbox) ShowUnread(entry, node, nameof(SidebarNode.Count), () => node.Count > 0);
                _railEntries.Add(entry);
            }
            foreach (var account in _vm.SidebarNodes.Where(n => n.IsGroup && n.Parent is null && n.AccountId != 0))
            {
                var entry = AccountEntry(account.AccountId, account.Title, account);
                ShowUnread(entry, account, nameof(SidebarNode.HasUnread), () => account.HasUnread); // in any of its folders
                _railEntries.Add(entry);
            }
            ShowRailSelection();
        }
        // The sidebar is rebuilt row by row: once for the lot.
        var queued = false;
        _vm.SidebarNodes.CollectionChanged += (_, _) =>
        {
            if (queued) return;
            queued = true;
            Dispatcher.BeginInvoke(() => { queued = false; Rebuild(); }, System.Windows.Threading.DispatcherPriority.Background);
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedNode)) ShowRailSelection();
        };
        Rail.ItemClicked += (item, row) =>
        {
            if (item is not RailEntry { Target: SidebarNode node } entry) return;
            if (!entry.IsAccount)
            {
                _vm.SelectedNode = node;
                return;
            }
            ShowRailSelection(); // the account lights up only while one of its folders is open
            AccountFoldersMenu(entry, node, row).IsOpen = true;
        };
        Rebuild();
    }

    /// <summary>The icon's blue dot follows the row's unread mail as it comes in and is read.</summary>
    private static void ShowUnread(RailEntry entry, SidebarNode node, string property, Func<bool> hasUnread)
    {
        entry.HasUnread = hasUnread();
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == property) entry.HasUnread = hasUnread();
        };
    }

    /// <summary>The icon for what's open: the favorite itself, or the account a folder belongs to.</summary>
    private void ShowRailSelection()
    {
        var node = _vm.SelectedNode;
        Rail.SelectedItem = _railEntries.FirstOrDefault(e => !e.IsAccount && e.Target == node)
            ?? (node is { Kind: NodeKind.Folder, IsFavoriteShortcut: false }
                ? _railEntries.FirstOrDefault(e => e.IsAccount && ((SidebarNode)e.Target!).AccountId == node.AccountId)
                : null);
    }

    /// <summary>
    /// A menu beside an account's icon in a folded sidebar: the account on top (its picture, or the person icon), and
    /// the icon keeps its hover tint while the menu is open, so it's clear which account it belongs to.
    /// </summary>
    private ContextMenu AccountRailMenu(RailEntry account, FrameworkElement row)
    {
        var menu = new ContextMenu { PlacementTarget = row, Placement = System.Windows.Controls.Primitives.PlacementMode.Right, HorizontalOffset = 2 };
        FrameworkElement picture = account.Picture is { } image
            ? new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Fill = new ImageBrush(image) { Stretch = Stretch.UniformToFill } }
            : new GlyphIcon { Glyph = account.Glyph is { Length: > 0 } glyph ? glyph : Glyphs.Person, FontSize = 16 };
        // Not a command: shown in full colour, but nothing happens on it.
        menu.Items.Add(new MenuItem { Header = account.Title, Icon = picture, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false, Focusable = false });
        menu.Items.Add(new Separator());
        row.Tag = SidebarRail.MenuOpen;
        menu.Closed += (_, _) => row.ClearValue(TagProperty);
        return menu;
    }

    /// <summary>An account's folders, from its icon in the folded sidebar: laid out as in the open sidebar, the main
    /// folders first and the rest under More.</summary>
    private ContextMenu AccountFoldersMenu(RailEntry entry, SidebarNode account, FrameworkElement row)
    {
        var menu = AccountRailMenu(entry, row);
        // The count as the open sidebar shows it: unread in blue, Drafts' total muted.
        object Header(string title, int count, bool countIsTotal, FontWeight weight)
        {
            var panel = new DockPanel { LastChildFill = true };
            if (count > 0)
            {
                var badge = new TextBlock
                {
                    Text = count.ToString(System.Globalization.CultureInfo.CurrentCulture),
                    FontSize = 13, Margin = new Thickness(25, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    FontWeight = countIsTotal ? FontWeights.Normal : FontWeights.SemiBold,
                };
                badge.SetResourceReference(TextBlock.ForegroundProperty, countIsTotal ? "MutedTextBrush" : "CountBrush");
                DockPanel.SetDock(badge, Dock.Right);
                panel.Children.Add(badge);
            }
            panel.Children.Add(new TextBlock { Text = title, FontWeight = weight, VerticalAlignment = VerticalAlignment.Center });
            return panel;
        }
        MenuItem FolderItem(SidebarNode folder)
        {
            var item = new MenuItem
            {
                Header = Header(folder.Title, folder.Count, folder.CountIsTotal, folder == _vm.SelectedNode ? FontWeights.SemiBold : FontWeights.Normal),
                Icon = new GlyphIcon { Glyph = folder.Glyph, FontSize = 16 },
            };
            item.Click += (_, _) =>
            {
                // Its group opens in the full sidebar too, so it's shown selected there when the sidebar opens again.
                for (var parent = folder.Parent; parent is not null; parent = parent.Parent) parent.IsExpanded = true;
                _vm.SidebarView.Refresh();
                _vm.SelectedNode = folder;
            };
            return item;
        }
        var folders = _vm.SidebarNodes.Where(n => n.Kind == NodeKind.Folder && n.AccountId == account.AccountId && !n.IsFavoriteShortcut).ToList();
        foreach (var folder in folders.Where(f => f.Parent == account)) menu.Items.Add(FolderItem(folder));
        var others = folders.Where(f => f.Parent != account).ToList();
        if (others.Count > 0)
        {
            // Like a folded More in the open sidebar: the unread of the folders in it, so mail a rule filed there shows.
            var unread = others.Where(f => !f.CountIsTotal).Sum(f => f.Count);
            var more = new MenuItem { Header = Header(L.T("More"), unread, false, FontWeights.Normal), Icon = new GlyphIcon { Glyph = Glyphs.More, FontSize = 16 } };
            foreach (var folder in others) more.Items.Add(FolderItem(folder));
            menu.Items.Add(more);
        }
        return menu;
    }

    /// <summary>An account in a folded sidebar: its picture when it has one, else its initial on its colour.</summary>
    private RailEntry AccountEntry(long accountId, string title, object target, string group = "accounts") => new()
    {
        Title = title, Group = group, IsAccount = true, Target = target,
        Initial = title.Length > 0 ? char.ToUpper(title[0], System.Globalization.CultureInfo.CurrentCulture).ToString() : "?",
        Color = AccountBrush(_vm.AccountColor(accountId)),
        Picture = _vm.AccountPicture(accountId),
    };

    // ─── The folded calendar sidebar: each account, its calendars (shown or hidden) in a menu ───

    private void SetUpCalendarRail()
    {
        var rail = _calendarView.Rail;
        var entries = new System.Collections.ObjectModel.ObservableCollection<RailEntry>();
        rail.ItemsSource = entries;
        void Rebuild()
        {
            entries.Clear();
            foreach (var group in _calendar.Groups)
                entries.Add(group.AccountId is { } accountId
                    ? AccountEntry(accountId, group.Title, group, group: "")
                    : new RailEntry { Title = group.Title, Group = "", IsAccount = true, Target = group, Glyph = Glyphs.Computer }); // this computer's: a PC
        }
        // New calendars, or accounts changed (picture, colour, name: the mail sidebar is rebuilt then): once for the lot.
        var queued = false;
        void Queue()
        {
            if (queued) return;
            queued = true;
            Dispatcher.BeginInvoke(() => { queued = false; Rebuild(); }, System.Windows.Threading.DispatcherPriority.Background);
        }
        _calendar.Groups.CollectionChanged += (_, _) => Queue();
        _vm.SidebarNodes.CollectionChanged += (_, _) => Queue();
        rail.ItemClicked += (item, row) =>
        {
            if (item is not RailEntry { Target: CalendarGroupViewModel group } account) return;
            var menu = AccountRailMenu(account, row);
            foreach (var calendar in group.Calendars)
            {
                // Ticked = shown, as the checkboxes in the open sidebar; the menu stays open to tick several.
                var entry = new MenuItem
                {
                    Header = calendar.Name, IsCheckable = true, StaysOpenOnClick = true,
                    Icon = new System.Windows.Shapes.Ellipse { Width = 10, Height = 10, Fill = calendar.Color },
                };
                entry.SetBinding(MenuItem.IsCheckedProperty, new System.Windows.Data.Binding(nameof(CalendarItemViewModel.IsVisible)) { Source = calendar, Mode = System.Windows.Data.BindingMode.TwoWay });
                menu.Items.Add(entry);
            }
            menu.IsOpen = true;
        };
        Rebuild();
    }

    // ─── The folded tasks sidebar: an account's lists show the account, as in Mail and Calendar ───

    private void SetUpTasksRail()
    {
        void Decorate()
        {
            foreach (var entry in _tasks.Lists)
            {
                if (entry.List?.AccountId is not { } accountId || _vm.AccountColor(accountId) is not { } color)
                {
                    (entry.AccountColor, entry.AccountPicture) = (null, null);
                    continue;
                }
                var name = _vm.AccountList.FirstOrDefault(a => a.Id == accountId)?.SidebarTitle ?? entry.Group;
                entry.AccountInitial = name.Length > 0 ? char.ToUpper(name[0], System.Globalization.CultureInfo.CurrentCulture).ToString() : "?";
                entry.AccountColor = AccountBrush(color);
                entry.AccountPicture = _vm.AccountPicture(accountId);
            }
        }
        // New lists, or accounts changed (picture, colour, name: the mail sidebar is rebuilt then): once for the lot.
        var queued = false;
        void Queue()
        {
            if (queued) return;
            queued = true;
            Dispatcher.BeginInvoke(() => { queued = false; Decorate(); }, System.Windows.Threading.DispatcherPriority.Background);
        }
        _tasks.Lists.CollectionChanged += (_, _) => Queue();
        _vm.SidebarNodes.CollectionChanged += (_, _) => Queue();
        Decorate();
    }

    private static Brush AccountBrush(string? hex)
    {
        try
        {
            if (hex is { Length: > 0 } && ColorConverter.ConvertFromString(hex) is Color color) return new SolidColorBrush(color);
        }
        catch (FormatException) { }
        return (Brush)Application.Current.FindResource("AccentBrush");
    }

    /// <summary>Switches the main area between the mail, calendar, contacts and tasks modules.</summary>
    public void ShowModule(string module)
    {
        if (_module == "tasks" && module != "tasks") _ = _tasks.FlushEditorAsync(); // nothing typed is lost
        _module = module;
        _contacts.IsShown = module == "contacts";
        _tasks.IsShown = module == "tasks";
        CalendarHost.Visibility = module == "calendar" ? Visibility.Visible : Visibility.Collapsed;
        ContactsHost.Visibility = module == "contacts" ? Visibility.Visible : Visibility.Collapsed;
        TasksHost.Visibility = module == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        MailArea.Visibility = ToolbarRow.Visibility = ToolbarLine.Visibility = module == "mail" ? Visibility.Visible : Visibility.Collapsed;
        ModuleTitle.Text = module switch { "calendar" => L.T("Calendar"), "contacts" => L.T("Contacts"), "tasks" => L.T("Tasks"), _ => L.T("Mail") };
        switch (module)
        {
            case "calendar":
                _calendarView.OnShown();
                break;
            case "contacts":
                _contactsView.OnShown();
                _contacts.IsShown = true;
                _ = _contacts.EnsureFreshAsync(); // instant when nothing changed since the last visit
                break;
            case "tasks":
                _tasksView.OnShown();
                _ = _tasks.EnsureFreshAsync();
                break;
            default:
                MailModuleButton.IsChecked = true;
                break;
        }
    }

    private void CalendarModule_Click(object sender, RoutedEventArgs e) => ShowModule("calendar");
    private void ContactsModule_Click(object sender, RoutedEventArgs e) => ShowModule("contacts");
    private void TasksModule_Click(object sender, RoutedEventArgs e) => ShowModule("tasks");

    /// <summary>Create Task on a message: a new task titled after it, linked back to the email.</summary>
    // ───────── people in a shown message (reading pane and message windows) ─────────

    void IReaderHost.ComposeTo(string formattedAddress) => _vm.ComposeTo(formattedAddress);
    void IReaderHost.NewMeeting(string name, string address) => _calendar.NewMeetingWith(name, address);

    void IReaderHost.ShowContact(string address)
    {
        Bring();
        ShowModule("contacts");
        _ = _contacts.ShowPersonAsync(address);
    }

    void IReaderHost.ShowAllMail(string address)
    {
        Bring();
        ShowModule("mail");
        _vm.SearchText = address;
    }

    void IReaderHost.CreateRule(string name, string address) => _ = _vm.CreateRuleForSenderAsync(name, address);
    void IReaderHost.BlockSender(MessageSummary message) => _ = _vm.BlockSenderAsync(message, "sender");

    /// <summary>A message window asked for something shown here: bring the main window forward.</summary>
    private void Bring()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void CreateTask_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedMessage?.Model is { } message) _ = _tasks.CreateFromMessageAsync(message);
    }

    /// <summary>The New button's full menu (both modules): anything that can be created, wherever you are.</summary>
    private ContextMenu BuildNewMenu()
    {
        static MenuItem Entry(string header, string glyph, Action? click, string? gesture = null, bool enabled = true)
        {
            var item = new MenuItem
            {
                Header = header, InputGestureText = gesture ?? "", IsEnabled = enabled,
                Icon = new TextBlock { Text = glyph, FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16 },
            };
            if (click is not null) item.Click += (_, _) => click();
            return item;
        }

        var menu = new ContextMenu();
        // With several accounts, Mail opens a list to pick the sender; Ctrl+N's pick (the sidebar's account, else the default) is marked.
        var accounts = _vm.AccountList;
        if (accounts.Count < 2)
        {
            menu.Items.Add(Entry(L.T("Mail…"), "\uE715", () => _vm.NewCommand.Execute(null), _module == "mail" ? L.T("Ctrl+N") : null));
        }
        else
        {
            var mail = Entry(L.T("Mail"), "\uE715", null);
            var usual = _vm.ComposeAccount;
            foreach (var account in accounts)
            {
                var from = new MenuItem
                {
                    Header = account.SidebarTitle,
                    InputGestureText = _module == "mail" && account.Id == usual?.Id ? L.T("Ctrl+N") : "",
                };
                from.Click += (_, _) => _vm.NewFromCommand.Execute(account);
                mail.Items.Add(from);
            }
            menu.Items.Add(mail);
        }
        menu.Items.Add(Entry(L.T("Event…"), "\uE787", () => _calendar.NewEventCommand.Execute(null), _calendarShown ? L.T("Ctrl+N") : null));
        menu.Items.Add(Entry(L.T("All-Day Event…"), "\uE8BF", () => _calendar.NewAllDayEventCommand.Execute(null)));
        menu.Items.Add(Entry(L.T("Online Meeting…"), "\uE714", () => _calendar.NewOnlineMeetingCommand.Execute(null)));
        menu.Items.Add(new Separator());

        // A new calendar belongs to an account or to this computer: pick where right here when there's a choice.
        var places = _calendar.Groups.Select(g => (g.Title, g.AccountId)).ToList();
        if (!_calendar.Groups.Any(g => g.IsLocal)) places.Add((L.T("On this computer"), null));
        if (places.Count == 1)
        {
            menu.Items.Add(Entry(L.T("Calendar…"), "\uE8F4", () => _ = _calendar.NewCalendarAsync(places[0].AccountId)));
        }
        else
        {
            var calendars = Entry(L.T("Calendar"), "\uE8F4", null);
            foreach (var (title, accountId) in places)
            {
                var forAccount = new MenuItem { Header = title };
                forAccount.Click += (_, _) => _ = _calendar.NewCalendarAsync(accountId);
                calendars.Items.Add(forAccount);
            }
            menu.Items.Add(calendars);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Entry(L.T("Contact…"), "\uE77B", () =>
        {
            ShowModule("contacts");
            _contacts.NewContact();
        }, _module == "contacts" ? L.T("Ctrl+N") : null));
        menu.Items.Add(Entry(L.T("Task…"), "", () => _tasks.NewTaskCommand.Execute(null), _module == "tasks" ? L.T("Ctrl+N") : null));
        return menu;
    }

    private void MailNew_Click(object sender, RoutedEventArgs e)
    {
        // Like the calendar's: the chevron part opens the menu, the rest starts a mail.
        if (Mouse.GetPosition(MailNewButton).X > MailNewButton.ActualWidth - 34)
        {
            var menu = BuildNewMenu();
            menu.PlacementTarget = MailNewButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }
        else
        {
            _vm.NewCommand.Execute(null);
        }
    }

    /// <summary>Brings the window back from the tray (or from behind other windows).</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;  // reliable way to win the foreground when another app has it
        Topmost = false;
        Focus();
    }

    /// <summary>Started at sign-in: stay in the tray (or the taskbar) instead of popping up.</summary>
    public void SendToBackground()
    {
        if (_settings.Current.CloseToTray || _settings.Current.MinimizeToTray) Hide();
        else WindowState = WindowState.Minimized;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.IsExiting || !_settings.Current.CloseToTray || ScreenshotPath is not null || SiteScreenshotDir is not null || SiteVideoPath is not null) return;
        e.Cancel = true; // keep syncing in the background; Exit from the menu or tray quits for real
        Hide();
    }

    private void OnNewMail(string title, string text, int count)
    {
        if (IsVisible && IsActive) return; // you're looking at it
        _unseenMail += count;
        ShowUnseenMail();
        if (_settings.Current.NotifyNewMail) _tray.Notify(title, text);
    }

    /// <summary>New mail that came while you were elsewhere (rules may have filed some of it away from the inbox).</summary>
    private int _unseenMail;

    /// <summary>
    /// The count on the taskbar button, and a dot on the tray icon for when the window is tucked away there; both go
    /// as soon as you come back to Plicbox.
    /// </summary>
    private void ShowUnseenMail()
    {
        TaskbarItemInfo ??= new System.Windows.Shell.TaskbarItemInfo();
        TaskbarItemInfo.Overlay = _unseenMail > 0 ? NewMailBadge.Overlay(_unseenMail) : null;
        TaskbarItemInfo.Description = _unseenMail switch
        {
            0 => "",
            1 => L.T("1 new message"),
            var n => L.F("{0} new messages", n),
        };
        _tray.SetNewMail(_unseenMail > 0);
    }

    /// <summary>Dev aid: when set, the window saves a screenshot after loading and exits.</summary>
    public string? ScreenshotPath { get; set; }

    /// <summary>Dev aid: also open a reply to the selected message and save a screenshot of the compose window.</summary>
    public string? ComposeScreenshotPath { get; set; }

    /// <summary>Dev aid: also save screenshots of the Accounts window and the Add-account dialog into this folder.</summary>
    public string? DialogScreenshotDir { get; set; }

    /// <summary>Dev aid (--dev-db … --dev-memory): memory after mail loads and after Contacts opens, plus timings, written here; then exit.</summary>
    public string? MemoryReport { get; set; }

    private async Task MeasureMemoryAsync(string path)
    {
        var lines = new List<string>();
        void Note(string what)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using var p = Process.GetCurrentProcess();
            lines.Add($"{what,-34} managed {GC.GetTotalMemory(false) / 1048576.0,7:0.0} MB   private {p.PrivateMemorySize64 / 1048576.0,7:0.0} MB   working set {p.WorkingSet64 / 1048576.0,7:0.0} MB");
        }
        for (var i = 0; i < 40 && _vm.Messages.Count == 0; i++) await Task.Delay(250);
        await Task.Delay(6000); // past the background warm-up, like a real session
        Note("mail loaded (contacts warmed)");
        var sw = Stopwatch.StartNew();
        ShowModule("contacts");
        await _contacts.EnsureFreshAsync();
        lines.Add($"contacts first open: {sw.ElapsedMilliseconds} ms, {_contacts.People.Count} in list, {_contacts.Lists[0].Count} people");
        await Task.Delay(1500);
        Note("contacts shown");
        sw.Restart();
        ShowModule("mail");
        ShowModule("contacts");
        await _contacts.EnsureFreshAsync();
        lines.Add($"contacts second open: {sw.ElapsedMilliseconds} ms");
        foreach (var person in _contacts.People.Take(5).ToList())
        {
            sw.Restart();
            _contacts.Selected = person;
            for (var i = 0; i < 100 && (_contacts.Card?.Conversations.Count ?? 0) == 0; i++) await Task.Delay(20);
            lines.Add($"card {person.Name,-30} conversations in {sw.ElapsedMilliseconds} ms");
        }
        sw.Restart();
        _contacts.SearchText = "an";
        lines.Add($"search 'an': {sw.ElapsedMilliseconds} ms, {_contacts.People.Count} hits");
        await Task.Delay(1000);
        Note("after cards and search");
        System.IO.File.WriteAllLines(path, lines);
        App.ExitApp();
    }

    /// <summary>
    /// --demo --measure file (release builds too): what release.ps1 records for each version. How long until the mail
    /// list is up, then memory after a message is open and every module was shown once: Plicbox itself and the
    /// WebView2 processes that show messages. Written as JSON; then exit.
    /// </summary>
    public string? MeasureReport { get; set; }

    private async Task MeasureForReleaseAsync(string path)
    {
        using var self = Process.GetCurrentProcess();
        for (var i = 0; i < 200 && _vm.Messages.Count == 0; i++) await Task.Delay(50);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        var startup = (DateTime.Now - self.StartTime).TotalMilliseconds;

        _vm.SelectedMessage = _vm.Messages.FirstOrDefault(m => !m.IsThreadChild);
        await Task.Delay(2500);
        foreach (var module in new[] { "calendar", "contacts", "tasks", "mail" })
        {
            ShowModule(module);
            await Task.Delay(1500);
        }
        await Task.Delay(5000); // past the background warm-up, like a real session

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        self.Refresh();
        long webViewPrivate = 0;
        var webViewProcesses = 0;
        if (Reading.Body.CoreWebView2 is { } core)
            foreach (var info in core.Environment.GetProcessInfos())
            {
                try
                {
                    using var p = Process.GetProcessById(info.ProcessId);
                    webViewPrivate += p.PrivateMemorySize64;
                    webViewProcesses++;
                }
                catch (ArgumentException) { } // ended meanwhile
            }
        static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);
        var report = new
        {
            startupMs = (int)startup,
            managedMb = Mb(GC.GetTotalMemory(false)),
            appPrivateMb = Mb(self.PrivateMemorySize64),
            appWorkingSetMb = Mb(self.WorkingSet64),
            webViewPrivateMb = Mb(webViewPrivate),
            webViewProcesses,
        };
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(report));
        App.ExitApp();
    }

    /// <summary>Dev aid (--dev-perf): click through every folder, log how long each switch takes until the list is drawn, exit.</summary>
    public bool PerfRun { get; set; }

    /// <summary>Dev aid (--screenshot-site): the website's pictures of each module (mail, calendar, contacts, tasks) into this folder, then exit.</summary>
    public string? SiteScreenshotDir { get; set; }

    /// <summary>
    /// The website's screenshots (site/img): one calm, representative view per module, without the DEV tag. Run once
    /// light and once with --dark; see site/README.md.
    /// </summary>
    private async Task CaptureSiteAsync(string dir)
    {
        await StageSiteMailAsync();
        WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "mail.png"));

        ShowModule("calendar");
        ((CalendarViewModel)_calendarView.DataContext).Mode = CalendarViewMode.Week;
        await Task.Delay(1500);
        WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "calendar.png"));

        // Contacts and tasks show their lists (Favorites, Today) in the sidebar, so it opens again for them.
        _devFoldSidebar?.Invoke(false);
        ShowModule("contacts");
        await Task.Delay(2000);
        _contacts.SelectedList = _contacts.Lists.First(l => l.Kind == PeopleList.Favorites);
        await Task.Delay(400);
        _contacts.Selected = _contacts.People.FirstOrDefault(p => p.Name.StartsWith("Ana", StringComparison.Ordinal));
        await Task.Delay(1800);
        WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "contacts.png"));

        ShowModule("tasks");
        await _tasks.LoadAsync();
        await Task.Delay(800);
        _tasks.SelectedList = _tasks.Lists.First(l => l.Kind == TaskView.Today);
        await Task.Delay(300);
        _tasks.Selected = _tasks.Rows.FirstOrDefault(r => r.Title.StartsWith("Plan the weekend", StringComparison.Ordinal));
        await Task.Delay(900);
        WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "tasks.png"));

        Application.Current.Shutdown();
    }

    /// <summary>
    /// The website's mail view: sidebar folded to icons (not saved), Ana's conversation open, the list from the top.
    /// A few demo messages make room, so the open conversation stays in view when a new message pushes the list down.
    /// </summary>
    private async Task StageSiteMailAsync()
    {
        DevChip.Visibility = Visibility.Collapsed;
        _devFoldSidebar?.Invoke(true);
        for (var i = 0; i < 40 && _vm.Messages.Count == 0; i++) await Task.Delay(250);
        await Task.Delay(800);
        _vm.NewMailArrived -= OnNewMail; // no tray notification on the desktop for staged arrivals
        await _vm.DevRemoveAsync("Your account has been limited", "Q4 planning notes", "New release from Lumen Atlas: Night Transit",
            "Finish setting up your account to unlock member benefits");
        await Task.Delay(400);
        if (_vm.Messages.FirstOrDefault(m => m.IsThread && m.Subject.Contains("Weekend", StringComparison.Ordinal)) is { } thread)
            _vm.SelectedMessage = thread;
        await Task.Delay(300);
        FindChild<ScrollViewer>(MessageList)?.ScrollToTop();
        await Task.Delay(2500);
    }

    /// <summary>Dev aid (--record-site): the website's videos (mail, calendar, contacts, tasks) go into this folder, then Plicbox exits.</summary>
    public string? SiteVideoPath { get; set; }

    /// <summary>
    /// The website's videos, one small everyday action per module, with a drawn pointer. Needs ffmpeg (on PATH or
    /// PLICBOX_FFMPEG). Pauses inside a recording are timed on the video's own clock (rec.DelayAsync), which runs
    /// behind the wall clock: each step then shows at the right moment on video, after the pointer has got there.
    /// </summary>
    private async Task RecordSiteAsync(string dir)
    {
        var ffmpeg = Environment.GetEnvironmentVariable("PLICBOX_FFMPEG") ?? "ffmpeg";
        var random = new Random(7); // the same typing rhythm every time
        Activate(); // typing shows a caret only in the active window
        await StageSiteMailAsync();
        await using (var rec = await SiteRecorder.StartAsync(this, ffmpeg, System.IO.Path.Combine(dir, "mail.mp4")))
            await RecordMailAsync(rec, random);

        ShowModule("calendar");
        ((CalendarViewModel)_calendarView.DataContext).Mode = CalendarViewMode.Week;
        await Task.Delay(1500);
        await using (var rec = await SiteRecorder.StartAsync(this, ffmpeg, System.IO.Path.Combine(dir, "calendar.mp4")))
            await RecordCalendarAsync(rec, random);

        _devFoldSidebar?.Invoke(false); // contacts and tasks are about their lists
        ShowModule("contacts");
        await Task.Delay(2000);
        _contacts.SelectedList = _contacts.Lists.First(l => l.Kind == PeopleList.All);
        await Task.Delay(400);
        _contacts.Selected = _contacts.People.FirstOrDefault(p => p.Name.StartsWith("Ana", StringComparison.Ordinal));
        await Task.Delay(1500);
        await using (var rec = await SiteRecorder.StartAsync(this, ffmpeg, System.IO.Path.Combine(dir, "contacts.mp4")))
            await RecordContactsAsync(rec);

        ShowModule("tasks");
        await _tasks.LoadAsync();
        await Task.Delay(800);
        _tasks.SelectedList = _tasks.Lists.First(l => l.Kind == TaskView.Today);
        await Task.Delay(300);
        _tasks.Selected = null;
        await Task.Delay(1200);
        await using (var rec = await SiteRecorder.StartAsync(this, ffmpeg, System.IO.Path.Combine(dir, "tasks.mp4")))
            await RecordTasksAsync(rec, random);

        _devFoldSidebar?.Invoke(true); // back to the calm mail view, behind the Settings window
        ShowModule("mail");
        await Task.Delay(1200);
        await using (var rec = await SiteRecorder.StartAsync(this, ffmpeg, System.IO.Path.Combine(dir, "settings.mp4")))
            await RecordSettingsAsync(rec);

        Application.Current.Shutdown();
    }

    /// <summary>
    /// Settings: the menu button is clicked and Settings opens over the inbox, then each page in turn. (The menu itself
    /// is a popup the recorder doesn't film, so the window simply opens after the click.)
    /// </summary>
    private async Task RecordSettingsAsync(SiteRecorder rec)
    {
        await rec.DelayAsync(0.9);
        var menu = rec.CentreOf(MenuButton);
        rec.ShowPointer(new Point(menu.X + 520, menu.Y + 380));
        await rec.MoveAsync(menu, 0.9);
        await rec.DelayAsync(0.12);
        await rec.ClickAsync();

        if (App.Services?.GetService(typeof(SettingsWindow)) is not SettingsWindow settings) return;
        settings.Owner = this;
        settings.Show();
        await Task.Delay(700); // drawn and laid out (the app's own time, not the video's)
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        rec.OpenPopup(settings, (int)((ActualWidth - settings.ActualWidth) * scale / 2), (int)((ActualHeight - settings.ActualHeight) * scale / 2) + 20);
        await rec.DelayAsync(1.4);

        // Each page for a moment: the pointer goes down the list on the left and clicks it.
        foreach (var page in new[] { settings.NavAppearance, settings.NavMail, settings.NavCalendar, settings.NavSenders })
        {
            var at = rec.CentreOf(page, onPopup: true);
            await rec.MoveAsync(new Point(at.X - 30, at.Y), 0.55);
            await rec.DelayAsync(0.1);
            await rec.ClickAsync();
            page.IsChecked = true;
            await rec.DelayAsync(1.6);
        }
        rec.HidePointer();
        await rec.DelayAsync(0.6);
        await rec.ClosePopupAsync();
        settings.Close();
        await rec.DelayAsync(1.0);
    }

    /// <summary>Clicks into a text box and types into it a key at a time, the way a person would.</summary>
    private static async Task TypeIntoAsync(SiteRecorder rec, TextBox box, string text, Random random)
    {
        var at = rec.CentreOf(box);
        rec.ShowPointer(new Point(at.X + 240, at.Y + 330));
        await rec.MoveAsync(at, 0.9);
        await rec.DelayAsync(0.12);
        await rec.ClickAsync();
        box.Focus();
        rec.HidePointer(); // like Windows does while you type
        await rec.DelayAsync(0.3);
        foreach (var c in text)
        {
            box.Text += c;
            box.CaretIndex = box.Text.Length;
            await rec.DelayAsync((c == ' ' ? random.Next(70, 120) : random.Next(28, 75)) / 1000.0);
        }
        await rec.DelayAsync(0.5);
    }

    /// <summary>The list or menu entry showing this item (a ListBoxItem whose data it is), or null if it isn't on screen.</summary>
    private static FrameworkElement? ContainerOf(DependencyObject root, object item)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ListBoxItem { DataContext: var data } found && ReferenceEquals(data, item)) return found;
            if (ContainerOf(child, item) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Calendar: "Coffee with Mia Friday 3pm" typed into Quick add, and there it is in the week.</summary>
    private async Task RecordCalendarAsync(SiteRecorder rec, Random random)
    {
        await rec.DelayAsync(1.0);
        var calendar = (CalendarViewModel)_calendarView.DataContext;
        await TypeIntoAsync(rec, _calendarView.QuickAdd, "Coffee with Mia Friday 3pm", random);
        // Real Quick add asks Google to read the words; the demo has no Google account, so the event it would make is
        // saved here instead: Friday of this week, 15:00–16:00, in the default calendar.
        calendar.QuickAddText = "";
        if (App.Services?.GetService(typeof(ICalendarRepository)) is ICalendarRepository events && calendar.WritableCalendars.FirstOrDefault() is { } into)
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var friday = today.AddDays(4 - ((int)today.DayOfWeek + 6) % 7);
            var start = new DateTimeOffset(friday.ToDateTime(new TimeOnly(15, 0)));
            await events.UpsertEventAsync(new CalendarEvent
            {
                CalendarId = into.Id, RemoteId = "demo-quick-add", Title = "Coffee with Mia", Start = start, End = start.AddHours(1),
                Updated = DateTimeOffset.UtcNow, TimeZone = TimeZoneInfo.Local.Id,
            });
            await calendar.ReloadAsync();
        }
        await rec.DelayAsync(3.5);
    }

    /// <summary>Contacts: a click on Keep in touch, then on the person you've lost touch with, whose card opens.</summary>
    private async Task RecordContactsAsync(SiteRecorder rec)
    {
        await rec.DelayAsync(1.0);
        var list = _contacts.Lists.First(l => l.Kind == PeopleList.KeepInTouch);
        if (ContainerOf(_contactsView, list) is { } entry)
        {
            var at = rec.CentreOf(entry);
            rec.ShowPointer(new Point(at.X + 520, at.Y + 420));
            await rec.MoveAsync(new Point(at.X - 60, at.Y), 1.0);
            await rec.DelayAsync(0.14);
            await rec.ClickAsync();
            _contacts.SelectedList = list;
            await rec.DelayAsync(1.0);
        }
        if (_contacts.People.FirstOrDefault() is { } person && ContainerOf(_contactsView.PeopleList, person) is { } row)
        {
            var at = rec.CentreOf(row);
            await rec.MoveAsync(new Point(at.X - 80, at.Y), 0.8);
            await rec.DelayAsync(0.14);
            await rec.ClickAsync();
            _contacts.Selected = person;
            await rec.DelayAsync(0.6);
            rec.HidePointer();
        }
        await rec.DelayAsync(3.2);
    }

    /// <summary>Tasks: one typed into Quick add, then ticked off; it stays struck through a moment, then leaves.</summary>
    private async Task RecordTasksAsync(SiteRecorder rec, Random random)
    {
        const string title = "Book train tickets to Munich";
        await rec.DelayAsync(1.0);
        await TypeIntoAsync(rec, _tasksView.QuickBox, title, random);
        await _tasks.AddQuickCommand.ExecuteAsync(null);
        await rec.DelayAsync(1.6);
        if (_tasks.Rows.FirstOrDefault(r => r.Title == title) is { } added
            && ContainerOf(_tasksView.TaskList, added) is { } row && FindChild<Button>(row) is { } tick)
        {
            var at = rec.CentreOf(tick);
            rec.ShowPointer(new Point(at.X + 420, at.Y + 300));
            await rec.MoveAsync(at, 0.9);
            await rec.DelayAsync(0.14);
            await rec.ClickAsync();
            await _tasks.ToggleCompleteCommand.ExecuteAsync(added);
            await rec.DelayAsync(0.8);
            rec.HidePointer();
        }
        await rec.DelayAsync(3.5);
    }

    /// <summary>Mail, the website's main video: new mail comes in, then you reply to Ana and send it.</summary>
    private async Task RecordMailAsync(SiteRecorder rec, Random random)
    {
        var original = _vm.SelectedMessage?.Model;
        {
            await rec.DelayAsync(1.3);

            // New mail arrives at the top of the list.
            await _vm.SimulateNewMailAsync("Photos from Saturday", "Mia Chen", "mia@work.example", "The good ones from the ridge are attached.");
            FindChild<ScrollViewer>(MessageList)?.ScrollToTop();
            await rec.DelayAsync(1.7);

            // The pointer comes up from the reading pane and clicks Reply.
            var reply = rec.CentreOf(ReplyButton);
            rec.ShowPointer(new Point(reply.X + 380, reply.Y + 760));
            await rec.MoveAsync(reply, 1.0);
            await rec.DelayAsync(0.14);
            await rec.ClickAsync();
            _vm.ReplyCommand.Execute(null);
            ComposeWindow? compose = null;
            for (var i = 0; i < 100 && compose is null; i++)
            {
                await Task.Delay(30);
                compose = Application.Current.Windows.OfType<ComposeWindow>().FirstOrDefault();
            }
            if (compose is not null)
            {
                compose.Width = 820;
                compose.Height = 600;
                await compose.DevEditorReady;
                await Task.Delay(300); // the editor draws its first picture (real time: it's the app catching up)
                rec.HidePointer(); // like Windows does while you type
                rec.OpenPopup(compose, (int)(ActualWidth * VisualTreeHelper.GetDpi(this).DpiScaleX - 1230) / 2 + 40, 250);
                await rec.DelayAsync(0.7);

                // The reply, typed a key at a time.
                await compose.DevCaretToStartAsync();
                string[] lines = ["Sounds perfect, see you at 9!", "I'll print the map, just in case."];
                for (var l = 0; l < lines.Length; l++)
                {
                    if (l > 0) { await compose.DevNewParagraphAsync(); await rec.DelayAsync(0.26); }
                    foreach (var c in lines[l])
                    {
                        await compose.DevTypeAsync(c.ToString());
                        await rec.DelayAsync((c == ' ' ? random.Next(70, 120) : random.Next(28, 75)) / 1000.0);
                    }
                }
                await rec.DelayAsync(0.7);

                // Send.
                var send = rec.CentreOf(compose.DevSendButton, onPopup: true);
                rec.ShowPointer(new Point(send.X + 260, send.Y + 300));
                await rec.MoveAsync(send, 0.8);
                await rec.DelayAsync(0.14);
                await rec.ClickAsync();
                compose.DevShowSending();
                await rec.DelayAsync(1.0);
                await rec.ClosePopupAsync();
                compose.CloseWithoutPrompt();
                rec.HidePointer();
                if (original is not null) await _vm.DevAddSentReplyAsync(original, lines[0]);
            }
            await rec.DelayAsync(3.2);
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Dev aid: PLICBOX_TRASH_DROP=<dir> drops one message of each account (from All Inboxes) on Favorites > Trash, as a
        // drag would, logs where each went, captures the Trash view and the folded rail, then exits.
        if (Environment.GetEnvironmentVariable("PLICBOX_TRASH_DROP") is { Length: > 0 } dropDir)
        {
            System.IO.Directory.CreateDirectory(dropDir);
            await Task.Delay(2500);
            _vm.SelectedNode = _vm.SidebarNodes.First(n => n.Kind == NodeKind.UnifiedInbox);
            await Task.Delay(1500);
            var picked = _vm.Messages.Where(m => !m.IsThreadChild && !m.IsThread).GroupBy(m => m.Model.AccountId).Select(g => g.First()).ToList();
            var trash = _vm.SidebarNodes.First(n => n.Kind == NodeKind.Trash);
            Serilog.Log.Information("TRASHTEST picked {Picked}; can drop: {Can}", string.Join(" | ", picked.Select(m => $"{m.Model.Subject} (account {m.Model.AccountId})")), _vm.CanMoveTo(picked, trash));
            await _vm.MoveToFolderAsync(picked, trash);
            await Task.Delay(1500);
            _vm.SelectedNode = trash;
            await Task.Delay(1500);
            foreach (var m in _vm.Messages)
                Serilog.Log.Information("TRASHTEST in Trash view: {Subject}, account {Account}, folder {Folder}", m.Model.Subject, m.Model.AccountId,
                    _vm.FolderOf(_vm.SidebarNodes.FirstOrDefault(n => n.Kind == NodeKind.Folder && n.FolderId == m.Model.FolderId))?.Role);
            Serilog.Log.Information("TRASHTEST can drop Trash rows on Trash again: {Can}", _vm.CanMoveTo(_vm.Messages.ToList(), trash));
            WindowCapture.SavePng(this, System.IO.Path.Combine(dropDir, "trash-view.png"));
            _devFoldSidebar?.Invoke(true);
            await Task.Delay(1000);
            WindowCapture.SavePng(this, System.IO.Path.Combine(dropDir, "trash-rail.png"));
            App.ExitApp();
            return;
        }
        // Dev aid (screenshots): PLICBOX_READING=right|full|full-open|popout sets the reading pane layout for this run.
        if (Environment.GetEnvironmentVariable("PLICBOX_READING") is { Length: > 0 } reading)
        {
            _vm.ReadingLayout = reading.Replace("-open", "");
            if (reading.EndsWith("-open", StringComparison.Ordinal))
                _ = Dispatcher.BeginInvoke(async () =>
                {
                    for (var i = 0; i < 40 && _vm.SelectedMessage is null; i++) await Task.Delay(100);
                    _vm.OpenInPlaceCommand.Execute(null);
                }, System.Windows.Threading.DispatcherPriority.Background);
        }
        if (SiteScreenshotDir is not null) { await CaptureSiteAsync(SiteScreenshotDir); return; }
        if (SiteVideoPath is not null) { await RecordSiteAsync(SiteVideoPath); return; }
        if (ScreenshotPath is not null) await CaptureAndExitAsync(ScreenshotPath);
        if (PerfRun) await MeasureFolderSwitchingAsync();
        if (MemoryReport is not null) await MeasureMemoryAsync(MemoryReport);
        if (MeasureReport is not null) await MeasureForReleaseAsync(MeasureReport);
        if (ScrollbarReport is not null) await InspectScrollbarsAsync(ScrollbarReport);
        if (PrintScreenshotDir is not null) await CapturePrintAsync(PrintScreenshotDir);
    }

    /// <summary>Dev aid (--dev-db … --dev-scrollbars): where the gutter lists' scrollbars are and what covers them.</summary>
    public string? ScrollbarReport { get; set; }

    /// <summary>Dev aid (--screenshot-print dir): Print… on a message, the print window captured, then closed with Esc.</summary>
    public string? PrintScreenshotDir { get; set; }

    private async Task CapturePrintAsync(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        await Task.Delay(2500);
        _vm.SelectedNode = _vm.SidebarNodes.First(n => n.Kind == NodeKind.Folder && n.Role == FolderRole.Inbox);
        await Task.Delay(1500);
        // PLICBOX_PRINT_PICK=n: the n-th message instead of the newest (a long one, to see several pages).
        var pick = int.TryParse(Environment.GetEnvironmentVariable("PLICBOX_PRINT_PICK"), out var n) ? n : 0;
        _vm.SelectedMessage = _vm.Messages.Where(m => !m.IsThreadChild).Skip(pick).FirstOrDefault();
        if (Environment.GetEnvironmentVariable("PLICBOX_NEW_FROM") is not null) // New: which account it's written from
        {
            await Task.Delay(1500);
            async Task<string?> NewFrom(string where)
            {
                _vm.NewCommand.Execute(null);
                await Task.Delay(2500);
                var compose = Application.Current.Windows.OfType<ComposeWindow>().LastOrDefault();
                var from = compose?.DevFrom;
                Serilog.Log.Information("PRINTTEST new from {Where}: {From}", where, from);
                compose?.CloseWithoutPrompt();
                await Task.Delay(500);
                return from;
            }
            _vm.SelectedNode = _vm.SidebarNodes.First(n => n.Kind == NodeKind.UnifiedInbox);
            await Task.Delay(1500);
            var workId = _vm.SidebarNodes.Where(n => n.IsGroup && n.AccountId != 0).Last().AccountId;
            _vm.SelectedMessage = _vm.Messages.FirstOrDefault(m => m.Model.AccountId == workId);
            await Task.Delay(800);
            await NewFrom($"All Inboxes, a message of account {workId} selected");
            _vm.SelectedNode = _vm.SidebarNodes.First(n => n is { Kind: NodeKind.Folder, Role: FolderRole.Inbox } && n.AccountId == workId);
            await Task.Delay(1500);
            await NewFrom($"account {workId}'s Inbox");
            App.ExitApp();
            return;
        }
        if (Environment.GetEnvironmentVariable("PLICBOX_GROUPS") is not null) // date headings fold, and stay folded over a reload
        {
            await Task.Delay(1500);
            var headers = FindAll<System.Windows.Controls.Primitives.ToggleButton>(MessageList).Where(t => t.Name == "Header").ToList();
            var yesterday = headers.FirstOrDefault(h => (h.DataContext as System.Windows.Data.CollectionViewGroup)?.Name?.ToString() == L.T("Yesterday"));
            Serilog.Log.Information("PRINTTEST groups: {Names}", string.Join(", ", headers.Select(h => (h.DataContext as System.Windows.Data.CollectionViewGroup)?.Name)));
            if (yesterday is not null) yesterday.IsChecked = false;
            await Task.Delay(600);
            WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "groups-folded.png"));
            await _vm.LoadAsync(); // the list built again
            await Task.Delay(1200);
            WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "groups-after-reload.png"));
            Serilog.Log.Information("PRINTTEST groups after reload: folded {Folded}", string.Join(", ", GroupOpenConverter.Folded));
        }
        if (Environment.GetEnvironmentVariable("PLICBOX_RAIL_PRESS") is not null) // a real press on an account icon in the folded sidebar
        {
            _devFoldSidebar?.Invoke(true);
            await Task.Delay(1000);
            var before = Rail.SelectedItem as RailEntry;
            var entry = _railEntries.Last(e => e.IsAccount); // not the one whose folder is open
            var row = (FrameworkElement)Rail.Items.ItemContainerGenerator.ContainerFromItem(entry);
            Activate();
            GetCursorPos(out var was);
            var centre = row.PointToScreen(new Point(row.ActualWidth / 2, row.ActualHeight / 2));
            SetCursorPos((int)centre.X, (int)centre.Y);
            await Task.Delay(300);
            mouse_event(0x0002, 0, 0, 0, 0); // left down
            await Task.Delay(250);
            Serilog.Log.Information("PRINTTEST rail press: selected before {Before}, while held {Held}", before?.Title, (Rail.SelectedItem as RailEntry)?.Title);
            mouse_event(0x0004, 0, 0, 0, 0); // left up
            await Task.Delay(600);
            Serilog.Log.Information("PRINTTEST rail release: selected {After}, row tag {Tag}", (Rail.SelectedItem as RailEntry)?.Title, row.Tag);
            WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "rail-press.png"));
            keybd_event(0x1B, 0, 0, 0); // Esc closes the menu
            keybd_event(0x1B, 0, 2, 0);
            SetCursorPos(was.X, was.Y);
            await Task.Delay(400);
            Serilog.Log.Information("PRINTTEST rail menu closed: row tag {Tag}", row.Tag);
            App.ExitApp();
            return;
        }
        if (Environment.GetEnvironmentVariable("PLICBOX_SEARCH_SHOTS") is not null) // the search box in every module, and its button hovered
        {
            _vm.SearchText = "invoice from the travel agency last spring";
            if (Environment.GetEnvironmentVariable("PLICBOX_RAIL") is not null) _devFoldSidebar?.Invoke(true); // folded into icons
            if (Environment.GetEnvironmentVariable("PLICBOX_RAIL_PICTURE") is { } picture) // the first account with a picture of its own
            {
                var first = _vm.SidebarNodes.First(n => n.IsGroup && n.AccountId != 0).AccountId;
                App.Services!.GetRequiredService<Services.Pictures>().SetAccountPicture(first, picture);
                await _vm.LoadAsync();
            }
            if (Environment.GetEnvironmentVariable("PLICBOX_RAIL_DOT") is not null) // the unread dots, and one going as its mail is read
            {
                await Task.Delay(1000);
                RenderPng(Rail, System.IO.Path.Combine(dir, "rail-dots.png"));
                var last = _railEntries.Last(e => e.IsAccount);
                Serilog.Log.Information("PRINTTEST dots: {Dots}", string.Join(", ", _railEntries.Select(e => $"{e.Title}={e.HasUnread}")));
                ((SidebarNode)last.Target!).HasUnread = false;
                await Task.Delay(400);
                RenderPng(Rail, System.IO.Path.Combine(dir, "rail-dots-read.png"));
                Serilog.Log.Information("PRINTTEST dots after reading {Account}: {Dots}", last.Title, string.Join(", ", _railEntries.Select(e => $"{e.Title}={e.HasUnread}")));
            }
            if (Environment.GetEnvironmentVariable("PLICBOX_RAIL_OVERFLOW") is not null) // more accounts than room: the rest under ⋯
            {
                var colours = new[] { "#7C4DFF", "#E53935", "#00897B", "#F4511E", "#3949AB", "#6D4C41" };
                for (var i = 0; i < colours.Length; i++)
                    _railEntries.Add(new RailEntry
                    {
                        Title = $"extra{i}@example.com", Group = "accounts", IsAccount = true, Initial = ((char)('A' + i)).ToString(),
                        Color = (Brush)new BrushConverter().ConvertFromString(colours[i])!, HasUnread = i == 5,
                        Target = _railEntries.Last(e => e.IsAccount).Target,
                    });
                Height = 720;
                await Task.Delay(1200);
                WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "rail-overflow.png"));
                var more = (Button)Rail.FindName("MoreButton");
                more.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                await Task.Delay(900);
                WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "rail-overflow-menu-window.png"));
                foreach (var popup in PresentationSource.CurrentSources.OfType<System.Windows.Interop.HwndSource>().Where(s => s.RootVisual?.GetType().Name == "PopupRoot"))
                    WindowCapture.SavePng(popup.Handle, System.IO.Path.Combine(dir, "rail-overflow-menu.png"));
            }
            if (Environment.GetEnvironmentVariable("PLICBOX_RAIL_MENU") is not null) // an account's folder menu from the folded sidebar
            {
                await Task.Delay(800);
                var entry = _railEntries.First(e => e.IsAccount);
                var row = (FrameworkElement)Rail.Items.ItemContainerGenerator.ContainerFromItem(entry);
                var menu = AccountFoldersMenu(entry, (SidebarNode)entry.Target!, row);
                menu.IsOpen = true;
                await Task.Delay(900);
                if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource popup)
                    WindowCapture.SavePng(popup.Handle, System.IO.Path.Combine(dir, "rail-menu.png"));
                WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "rail-menu-window.png"));
                if (menu.Items.OfType<MenuItem>().LastOrDefault(m => m.HasItems) is { } more) // its More, open
                {
                    more.IsSubmenuOpen = true;
                    await Task.Delay(600);
                    if (more.Items.OfType<MenuItem>().FirstOrDefault() is { } first && PresentationSource.FromVisual(first) is System.Windows.Interop.HwndSource sub)
                        WindowCapture.SavePng(sub.Handle, System.IO.Path.Combine(dir, "rail-menu-more.png"));
                }
                menu.IsOpen = false;
            }
            if (Environment.GetEnvironmentVariable("PLICBOX_FOLD_CLICK") is not null) // ‹ then › at the end of the module tabs
            {
                foreach (var _ in new[] { 1, 2 })
                {
                    FoldButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    UpdateLayout();
                    Serilog.Log.Information("PRINTTEST fold click: sidebar {Width} wide, strip {Rail}, button {Glyph}",
                        SidebarColumn.ActualWidth, Rail.Visibility, (int)((string)FoldButton.Content)[0]);
                }
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("PLICBOX_SIDEBAR"), out var sidebar)) // at a given sidebar width
                foreach (var column in new[] { SidebarColumn, _calendarView.SidebarColumn, _contactsView.SidebarColumn, _tasksView.SidebarColumn })
                    column.Width = new GridLength(sidebar);
            foreach (var module in new[] { "mail", "calendar", "contacts", "tasks" })
            {
                ShowModule(module);
                await Task.Delay(1200);
                WindowCapture.SavePng(this, System.IO.Path.Combine(dir, $"search-{module}.png"));
            }
            ShowModule("mail");
            await Task.Delay(800);
            var options = (FrameworkElement)MailSearch.FindName("OptionsButton");
            Activate();
            GetCursorPos(out var before);
            var centre = options.PointToScreen(new Point(options.ActualWidth / 2, options.ActualHeight / 2));
            SetCursorPos((int)centre.X, (int)centre.Y);
            await Task.Delay(900);
            WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "search-hover.png"));
            SetCursorPos(before.X, before.Y);
            App.ExitApp();
            return;
        }
        if (Environment.GetEnvironmentVariable("PLICBOX_FONT_CHECK") is not null) // menus and tooltips follow the app font
        {
            foreach (var accent in new[] { "#F7931E", Services.Appearance.MonoAccent })
            {
                Services.Appearance.Apply("dark", accent, null, 1);
                Serilog.Log.Information("PRINTTEST accent {Accent} (dark mode): text on it {Text}", accent, Application.Current.Resources["OnAccentBrush"]);
            }
            Services.Appearance.Apply(null, "#F7931E", null, 1);
            Serilog.Log.Information("PRINTTEST accent #F7931E (light mode): text on it {Text}", Application.Current.Resources["OnAccentBrush"]);
            Application.Current.Resources["UiFont"] = new FontFamily("Georgia");
            var menu = new ContextMenu { PlacementTarget = MessageList };
            menu.Items.Add(new MenuItem { Header = "Reply" });
            var sub = new MenuItem { Header = "More" };
            sub.Items.Add(new MenuItem { Header = "Inner" });
            menu.Items.Add(sub);
            menu.IsOpen = true;
            sub.IsSubmenuOpen = true;
            var tip = new ToolTip { Content = "Tip", PlacementTarget = MessageList, IsOpen = true };
            await Task.Delay(800);
            Serilog.Log.Information("PRINTTEST fonts: menu {Menu}, item {Item}, submenu item {Inner}, tooltip {Tip}",
                menu.FontFamily, ((MenuItem)menu.Items[0]).FontFamily, ((MenuItem)sub.Items[0]).FontFamily, tip.FontFamily);
            menu.IsOpen = tip.IsOpen = false;
        }
        PrintWindow? print = null;
        for (var i = 0; i < 20 && print is null; i++) // until the message has been read in
        {
            await Task.Delay(1000);
            if (Environment.GetEnvironmentVariable("PLICBOX_PRINT_LONG") is not null) // several pages: a long made-up message
            {
                var text = string.Concat(Enumerable.Range(1, 60).Select(n => $"<h3>Section {n}</h3><p>{string.Join(" ", Enumerable.Repeat("Notes from the planning session, with the decisions and the open questions.", 6))}</p>"));
                var body = new MimeKit.MimeMessage { Body = new MimeKit.TextPart("html") { Text = text } };
                print = PrintWindow.Open(this, new Plicbox.Mail.MessageRenderer().Render(body, false).Html, "Q4 planning notes",
                    [("From", "Diego Ruiz <diego@work.example>"), ("Date", DateTime.Now.ToString("g"))], null);
                break;
            }
            if (Environment.GetEnvironmentVariable("PLICBOX_PRINT_KEYS") is not null) // Ctrl+P typed in the message body
            {
                Activate();
                Reading.DevFocusBody();
                await Task.Delay(800);
                keybd_event(0x11, 0, 0, 0); // Ctrl
                keybd_event(0x50, 0, 0, 0); // P
                keybd_event(0x50, 0, 2, 0);
                keybd_event(0x11, 0, 2, 0);
                await Task.Delay(1500);
                Serilog.Log.Information("PRINTTEST Ctrl+P in the body: print window {Open}", Application.Current.Windows.OfType<PrintWindow>().Any());
            }
            else Reading.Print();
            print = Application.Current.Windows.OfType<PrintWindow>().FirstOrDefault();
        }
        if (print is null)
        {
            Serilog.Log.Warning("PRINTTEST no print window opened");
            App.ExitApp();
            return;
        }
        async Task Shot(string name)
        {
            for (var i = 0; i < 60 && !print.DevReady; i++) await Task.Delay(250);
            await Task.Delay(1200); // the visible pages' pictures
            WindowCapture.SavePng(print, System.IO.Path.Combine(dir, name));
            Serilog.Log.Information("PRINTTEST {Shot}: ready {Ready}, {Pages} pages", name, print.DevReady, print.DevPageCount);
        }
        await Shot("print-open.png");
        Serilog.Log.Information("PRINTTEST printers: {Printers}", print.DevPrinters);
        print.DevChoose(landscape: true, gray: true, pages: null);
        await Shot("print-landscape-gray.png");
        var pdf = System.IO.Path.Combine(dir, "print.pdf");
        Serilog.Log.Information("PRINTTEST save as PDF: {Saved}, {Bytes} bytes", await print.DevSavePdfAsync(pdf), new System.IO.FileInfo(pdf) is { Exists: true } f ? f.Length : 0);
        print.DevChoose(landscape: false, gray: false, pages: "2-x");
        await Shot("print-bad-range.png");
        print.DevChoose(landscape: false, gray: false, pages: "2-");
        await Shot("print-range.png");
        print.Close();
        await Task.Delay(500);
        Serilog.Log.Information("PRINTTEST closed: window open {Open}", print.IsVisible);
        App.ExitApp();
    }

    /// <summary>Dev aid: an element drawn to a PNG from WPF itself, whatever covers it on screen.</summary>
    private static void RenderPng(FrameworkElement element, string path)
    {
        var scale = VisualTreeHelper.GetDpi(element).DpiScaleX;
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(element.ActualWidth * scale), (int)(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
            dc.DrawRectangle((Brush)element.FindResource("ChromeBrush"), null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(background);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);

    private async Task InspectScrollbarsAsync(string path)
    {
        var lines = new List<string>();
        async Task Probe(string module, string label, FrameworkElement list)
        {
            ShowModule(module);
            await Task.Delay(2500);
            list.UpdateLayout();
            var bar = FindAll<System.Windows.Controls.Primitives.ScrollBar>(list).FirstOrDefault(b => b.Orientation == Orientation.Vertical);
            var viewer = FindChild<ScrollViewer>(list) ?? list as ScrollViewer;
            if (bar is null) { lines.Add($"{label}: no vertical scrollbar (viewer scrollable {viewer?.ScrollableHeight})"); return; }
            var topLeft = bar.TranslatePoint(new Point(0, 0), this);
            var listLeft = list.TranslatePoint(new Point(0, 0), this);
            var mid = bar.TranslatePoint(new Point(bar.ActualWidth / 2, bar.ActualHeight / 2), this);
            var hit = InputHitTest(mid) as DependencyObject;
            var covered = hit is not null && !IsInside(hit, bar);
            lines.Add($"{label}: bar x={topLeft.X:0.#} w={bar.ActualWidth:0.#} h={bar.ActualHeight:0.#} vis={bar.Visibility} enabled={bar.IsEnabled} opacity={bar.Opacity:0.##} " +
                      $"computed={viewer?.ComputedVerticalScrollBarVisibility} scrollable={viewer?.ScrollableHeight:0} list x={listLeft.X:0.#} w={list.ActualWidth:0.#} " +
                      $"hit={(hit as FrameworkElement)?.GetType().Name}/{(hit as FrameworkElement)?.Name} covered={covered}");
            // A real hover: the pointer over the middle of the list for a moment (put back afterwards).
            GetCursorPos(out var before);
            var centre = list.PointToScreen(new Point(list.ActualWidth / 2, list.ActualHeight / 2));
            SetCursorPos((int)centre.X, (int)centre.Y);
            await Task.Delay(700);
            var viewerHover = viewer?.IsMouseOver;
            var hovered = bar.Opacity;
            SetCursorPos(before.X, before.Y);
            lines.Add($"   hover: viewer.IsMouseOver={viewerHover} bar opacity after 0.7s={hovered:0.##}");
            bar.BeginAnimation(OpacityProperty, null);
            bar.Opacity = 1; // for the screenshot
            await Task.Delay(300);
            var track = FindChild<System.Windows.Controls.Primitives.Track>(bar);
            var thumb = track?.Thumb;
            lines.Add($"   track vis={track?.Visibility} h={track?.ActualHeight:0.#} thumb vis={thumb?.Visibility} w={thumb?.ActualWidth:0.#} h={thumb?.ActualHeight:0.#} " +
                      $"max={bar.Maximum:0} viewport={bar.ViewportSize:0} opacityNow={bar.Opacity} ours={bar.Template == (ControlTemplate)FindResource("VerticalScrollBar")} " +
                      $"styleChain={string.Join(" > ", StyleChain(bar.Style))}");
        }
        static bool IsInside(DependencyObject d, DependencyObject ancestor)
        {
            for (var x = d; x is not null; x = System.Windows.Media.VisualTreeHelper.GetParent(x)) if (x == ancestor) return true;
            return false;
        }
        await Probe("mail", "mail messages (plain style)", MessageList);
        await Probe("mail", "mail sidebar", SidebarList);
        WindowCapture.SavePng(this, System.IO.Path.ChangeExtension(path, ".mail.png"));
        await Probe("contacts", "contacts people", _contactsView.PeopleList);
        WindowCapture.SavePng(this, System.IO.Path.ChangeExtension(path, ".contacts.png"));
        System.IO.File.WriteAllLines(path, lines);
        Application.Current.Shutdown();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, nuint extra);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint p);
    private struct CursorPoint { public int X, Y; }

    private static IEnumerable<string> StyleChain(Style? style)
    {
        for (var st = style; st is not null; st = st.BasedOn)
            yield return st == Application.Current.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] ? $"app({st.Triggers.Count} triggers)"
                : st == Application.Current.Resources["GutterScrollBar"] ? "gutter" : $"other({st.Triggers.Count} triggers)";
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var deeper in FindAll<T>(child)) yield return deeper;
        }
    }

    /// <summary>The mail actions in the reading pane's message menu (between Find and Format).</summary>
    private IEnumerable<object> MessageMenuItems(MenuMaker run)
    {
        MenuItem Command(string header, string? glyph, ICommand command, object? parameter = null, string? gesture = null) =>
            run(header, glyph, () => command.Execute(parameter), command.CanExecute(parameter), gesture);

        if (_vm.SelectedInOutbox)
        {
            yield return Command(L.T("Send Now"), "", _vm.SendNowCommand);
            yield return new Separator();
        }
        yield return Command(L.T("Reply"), "\uE97A", _vm.ReplyCommand);
        yield return Command(L.T("Reply All"), "\uE8C2", _vm.ReplyAllCommand);
        var forward = Submenu(L.T("Forward"), "\uEE35", []);
        forward.Items.Add(Command(L.T("Forward"), "\uEE35", _vm.ForwardCommand));
        forward.Items.Add(Command(L.T("Forward as Attachment…"), "\uE723", _vm.ForwardAsAttachmentCommand));
        forward.Items.Add(Command(L.T("Forward as Original…"), "\uE8A5", _vm.ForwardAsOriginalCommand));
        yield return forward;
        yield return Command(L.T("Resend…"), "\uE8EE", _vm.ResendCommand);
        yield return new Separator();
        yield return Command(L.T("Flag"), "\uE7C1", _vm.ToggleFlagCommand);
        yield return Command(L.T("Mark as Read"), "\uE8C3", _vm.MarkReadCommand, gesture: L.T("Ctrl+Q"));
        yield return Command(L.T("Mark as Unread"), "\uE715", _vm.MarkUnreadCommand);
        yield return new Separator();
        yield return Command(L.T("Save As…"), "\uE74E", _vm.SaveAsCommand);
        yield return run(L.T("Save Attachments…"), "\uE896", () => _vm.Reader.SaveAttachmentsCommand.Execute(null), _vm.Reader.Attachments.Count > 0);
        yield return Command(L.T("Delete"), "\uE74D", _vm.DeleteCommand, gesture: L.T("Del"));
        yield return new Separator();
        yield return Command(L.T("Copy to Folder…"), "\uE8F4", _vm.CopyToFolderCommand);
        yield return Command(L.T("Move to Folder…"), "\uE8DE", _vm.MoveToFolderCommand);
        yield return _vm.SelectedInJunk ? Command(L.T("Not Junk"), "\uE8FB", _vm.NotJunkCommand) : Command(L.T("Move to Junk"), "\uE7BA", _vm.MoveToJunkCommand);
        yield return new Separator();
        yield return Command(L.T("Print…"), "\uE749", _vm.PrintCommand);
        yield return new Separator();
        yield return Command(L.T("Properties…"), "\uE946", _vm.MessagePropertiesCommand);
        yield return Command(L.T("View Mail Header…"), "\uF0E3", _vm.ViewHeadersCommand);
        yield return Command(L.T("View Mail Source…"), "\uE943", _vm.ViewSourceCommand);
    }

    // ───────── resizing ─────────

    private const int WM_SIZING = 0x0214, WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
    private bool _inSizeMove, _sizing;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source) source.AddHook(WndProc);
        UpdateEdgeGap();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                _inSizeMove = true;
                break;
            case WM_SIZING when _inSizeMove && !_sizing:
                // A resize (not a move) has started.
                _sizing = true;
                _ = Reading.FreezeAsync(() => _sizing);
                break;
            case WM_EXITSIZEMOVE:
                _inSizeMove = false;
                if (_sizing)
                {
                    _sizing = false;
                    Reading.Unfreeze();
                    FitToolbar();
                }
                break;
        }
        return IntPtr.Zero;
    }

    private void UpdateEdgeGap() => Reading.SetEdgeGap(WindowState != WindowState.Maximized);

    private void OnStateChanged()
    {
        UpdateEdgeGap();
        if (WindowState == WindowState.Minimized && _settings.Current.MinimizeToTray)
        {
            Hide();
            return;
        }
        // A chrome-less maximized window overhangs the screen by the resize border; pad it back in.
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    // ───────── message menu and double-click ─────────

    private void MessageMenu_Opened(object sender, RoutedEventArgs e) => _vm.RefreshMenuState();

    private void MessageList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Rows only: double-clicking the flag or thread badge shouldn't open a window.
        if (e.OriginalSource is FrameworkElement { DataContext: MessageItemViewModel } fe
            && FindParent<System.Windows.Controls.Primitives.ButtonBase>(fe) is null)
            _vm.OpenSelectedCommand.Execute(null);
    }

    // ───────── drag messages onto sidebar folders ─────────

    private const string DragFormat = "Plicbox.Messages";
    private Point _dragStart;
    private MessageItemViewModel? _dragItem;
    /// <summary>A press on a row inside a multi-row selection: kept for dragging; becomes a plain click if no drag follows.</summary>
    private MessageItemViewModel? _pressedInSelection;
    private SidebarNode? _dropTarget;

    private void MessageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        // Only rows start a drag (not the flag/thread buttons, whose clicks must keep working).
        _dragItem = e.OriginalSource is FrameworkElement { DataContext: MessageItemViewModel item } fe
                    && FindParent<System.Windows.Controls.Primitives.ButtonBase>(fe) is null ? item : null;
        _pressedInSelection = null;
        // Pressing on one of several selected rows would normally reselect just that row, so they couldn't be dragged together.
        if (_dragItem is not null && e.ClickCount == 1 && MessageList.SelectedItems.Count > 1 && MessageList.SelectedItems.Contains(_dragItem)
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            _pressedInSelection = _dragItem;
            MessageList.Focus();
            e.Handled = true;
        }
    }

    private void MessageList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Fill screen: a plain click on a row (not a drag, not Ctrl/Shift to pick several) opens it in place.
        if (_vm.ReadingLayout == "full" && _dragItem is { } clicked && e.ClickCount == 1
            && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0)
        {
            _dragItem = null;
            _pressedInSelection = null;
            Dispatcher.BeginInvoke(() =>
            {
                if (_vm.SelectedMessage != clicked) _vm.SelectedMessage = clicked;
                _vm.OpenSelectedCommand.Execute(null);
            });
            return;
        }
        if (_pressedInSelection is not { } item) return;
        _pressedInSelection = null;
        // No drag after all: a plain click selects just this row, as usual.
        foreach (var other in MessageList.SelectedItems.Cast<object>().Where(o => o != item).ToList()) MessageList.SelectedItems.Remove(other);
    }

    // ───────── reading pane layout (Settings > Appearance) ─────────

    private DataTemplate? _wideRow;
    private Style? _wideRowStyle;

    /// <summary>
    /// Places the reading pane as Settings > Appearance says: under the list, beside it (the list then gets compact
    /// two-line rows and a sort menu for a header), in the list's place once a message is opened (Fill screen), or
    /// nowhere (messages open in their own window).
    /// </summary>
    private void ApplyReadingLayout()
    {
        _wideRow ??= MessageList.ItemTemplate;
        _wideRowStyle ??= MessageList.ItemContainerStyle;
        var layout = _vm.ReadingLayout;
        var right = layout == "right";
        var bottom = layout is not ("right" or "full" or "popout");

        ListHeader.Visibility = right ? Visibility.Collapsed : Visibility.Visible;
        CompactHeader.Visibility = right ? Visibility.Visible : Visibility.Collapsed;
        var template = right ? (DataTemplate)MessageList.FindResource("CompactRow") : _wideRow;
        if (MessageList.ItemTemplate != template)
        {
            MessageList.ItemTemplate = template;
            MessageList.ItemContainerStyle = right ? (Style)MessageList.FindResource("CompactRowItem") : _wideRowStyle;
        }

        ListRow.Height = bottom ? new GridLength(2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        ReadingRow.Height = bottom ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        ReadingRow.MinHeight = bottom ? 160 : 0;
        ListColumn.Width = right ? new GridLength(2, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        ListColumn.MinWidth = right ? 300 : 0;
        ReadingColumn.Width = right ? new GridLength(3, GridUnitType.Star) : new GridLength(0);
        ReadingColumn.MinWidth = right ? 360 : 0;
        RowSplitter.Visibility = bottom ? Visibility.Visible : Visibility.Collapsed;
        ColumnSplitter.Visibility = right ? Visibility.Visible : Visibility.Collapsed;

        // Under the list; beside it, the full height; or over it (Fill screen, while a message is open).
        Grid.SetRow(ReadingHost, bottom ? 3 : 0);
        Grid.SetRowSpan(ReadingHost, bottom ? 1 : 4);
        Grid.SetColumn(ReadingHost, right ? 2 : 0);
        ReadingHost.Visibility = bottom || right || (layout == "full" && _vm.IsReadingOpen) ? Visibility.Visible : Visibility.Collapsed;
        BackBar.Visibility = layout == "full" ? Visibility.Visible : Visibility.Collapsed;
        ShowCompactSort();
    }

    /// <summary>Fill screen: back from a message to the list, with its row still picked and the keyboard on it.</summary>
    private void FocusListAfterReading()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_vm.SelectedMessage is { } item && MessageList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem row) row.Focus();
            else MessageList.Focus();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private static readonly (MessageSortColumn Column, string Label)[] SortChoices =
    [
        (MessageSortColumn.Received, L.T("Date received")), (MessageSortColumn.From, L.T("Sender")), (MessageSortColumn.Subject, L.T("Subject")),
        (MessageSortColumn.Flag, L.T("Flag")), (MessageSortColumn.ReadStatus, L.T("Read status")), (MessageSortColumn.Attachments, L.T("Attachments")),
    ];

    private void ShowCompactSort()
    {
        var label = SortChoices.FirstOrDefault(c => c.Column == _vm.SortColumn).Label ?? L.T("Date received");
        CompactSortText.Text = L.F("Sort: {0}", label) + (_vm.SortDescending ? " ↓" : " ↑");
    }

    private void CompactSort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = CompactSortButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var (column, label) in SortChoices)
        {
            var current = column == _vm.SortColumn;
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = current };
            item.Click += (_, _) => _vm.SortByCommand.Execute(column.ToString());
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var flip = new MenuItem { Header = _vm.SortDescending ? L.T("Reverse order (ascending)") : L.T("Reverse order (descending)") };
        flip.Click += (_, _) => _vm.SortByCommand.Execute(_vm.SortColumn.ToString());
        menu.Items.Add(flip);
        menu.IsOpen = true;
    }

    private void MessageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _dragItem;
        _dragItem = null;
        _pressedInSelection = null;
        // Dragging a selected row takes the whole selection along; any other row goes alone.
        IReadOnlyList<MessageItemViewModel> items = MessageList.SelectedItems.Contains(item)
            ? MessageList.SelectedItems.OfType<MessageItemViewModel>().ToList()
            : [item];
        DragDrop.DoDragDrop(MessageList, new DataObject(DragFormat, items), DragDropEffects.Move);
        SetDropTarget(null);
    }

    private void Sidebar_DragOver(object sender, DragEventArgs e)
    {
        var node = (e.OriginalSource as FrameworkElement)?.DataContext as SidebarNode;
        var items = e.Data.GetData(DragFormat) as IReadOnlyList<MessageItemViewModel>;
        var ok = items is not null && node is not null && _vm.CanMoveTo(items, node);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        SetDropTarget(ok ? node : null);
        e.Handled = true;
    }

    private void Sidebar_DragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    // The folded sidebar takes drops too: on a folder's icon (Favorites shortcuts, Trash) as on its row when open.
    // Account icons only open a menu, so nothing drops on them.
    private ListBoxItem? _railDropRow;

    private SidebarNode? RailDropNode(DragEventArgs e, out ListBoxItem? row)
    {
        row = null;
        for (var d = e.OriginalSource as DependencyObject; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is ListBoxItem item) { row = item; break; }
        return row?.DataContext is RailEntry { IsAccount: false, Target: SidebarNode node } ? node : null;
    }

    private void Rail_DragOver(object sender, DragEventArgs e)
    {
        var node = RailDropNode(e, out var row);
        var items = e.Data.GetData(DragFormat) as IReadOnlyList<MessageItemViewModel>;
        var ok = items is not null && node is not null && _vm.CanMoveTo(items, node);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        SetRailDropRow(ok ? row : null);
        e.Handled = true;
    }

    private void Rail_DragLeave(object sender, DragEventArgs e) => SetRailDropRow(null);

    private async void Rail_Drop(object sender, DragEventArgs e)
    {
        var node = RailDropNode(e, out _);
        SetRailDropRow(null);
        if (node is not null && e.Data.GetData(DragFormat) is IReadOnlyList<MessageItemViewModel> items)
            await _vm.MoveToFolderAsync(items, node);
    }

    /// <summary>The icon under a drag lights up as when its menu is open.</summary>
    private void SetRailDropRow(ListBoxItem? row)
    {
        if (_railDropRow == row) return;
        if (_railDropRow is not null && Equals(_railDropRow.Tag, "menu-open")) _railDropRow.Tag = null;
        _railDropRow = row;
        if (row is not null) row.Tag = "menu-open";
    }

    private async void Sidebar_Drop(object sender, DragEventArgs e)
    {
        var node = (e.OriginalSource as FrameworkElement)?.DataContext as SidebarNode;
        SetDropTarget(null);
        if (node is not null && e.Data.GetData(DragFormat) is IReadOnlyList<MessageItemViewModel> items)
            await _vm.MoveToFolderAsync(items, node);
    }

    private void SetDropTarget(SidebarNode? node)
    {
        if (_dropTarget == node) return;
        if (_dropTarget is not null) _dropTarget.IsDropTarget = false;
        _dropTarget = node;
        if (node is not null) node.IsDropTarget = true;
    }

    private static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null and not System.Windows.Controls.ListBoxItem)
        {
            if (child is T match) return match;
            child = child is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        var menu = MenuButton.ContextMenu!;
        menu.PlacementTarget = MenuButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.DataContext = DataContext;
        menu.IsOpen = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => App.ExitApp();

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    // ───────── sidebar menus (folder, account, Favorites), like eM Client's ─────────

    // Right-clicking a folder opens its menu without switching to it (the list would select it on button-down).
    private void Sidebar_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void Sidebar_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if ((e.OriginalSource as FrameworkElement)?.DataContext is SidebarNode node) OpenSidebarMenu(node);
    }

    // Keyboard (Shift+F10 / Menu key): the menu for the focused row.
    private void Sidebar_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        e.Handled = true;
        if ((Keyboard.FocusedElement as FrameworkElement)?.DataContext is SidebarNode node) OpenSidebarMenu(node);
    }

    private void OpenSidebarMenu(SidebarNode node)
    {
        var items = node switch
        {
            { Kind: NodeKind.Folder } when _vm.FolderOf(node) is { } folder => FolderMenu(node, folder),
            { Kind: NodeKind.Trash } => TrashMenu(),
            { IsGroup: true, AccountId: > 0, Level: 0 } => AccountMenu(node),
            { IsGroup: true, AccountId: > 0 } => [Item(L.T("New Folder…"), "\uE8F4", _vm.NewFolderCommand, node), new Separator(), .. TreeItems()],
            _ => TreeItems(),
        };
        var menu = new ContextMenu { DataContext = DataContext, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        foreach (var item in items) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private List<object> FolderMenu(SidebarNode node, Folder folder)
    {
        var custom = MainViewModel.IsCustom(folder);
        var prefs = _vm.FolderPrefsOf(folder);
        List<object> items =
        [
            Item(L.T("New Folder…"), "\uE8F4", _vm.NewFolderCommand, node),
            Enabled(Item(L.T("Rename…"), "\uE8AC", _vm.RenameFolderCommand, node), custom),
            Enabled(Item(L.T("Move Folder…"), "\uE8DE", _vm.MoveFolderCommand, node), custom),
            Enabled(Item(L.T("Delete"), "\uE74D", _vm.DeleteFolderCommand, node), custom),
        ];
        if (folder.Role is FolderRole.Trash or FolderRole.Junk)
            items.Add(Item(folder.Role == FolderRole.Trash ? L.T("Empty Trash") : L.T("Empty the junk folder"), GlyphIcon.EmptyTrash, _vm.EmptyFolderCommand, node));
        items.Add(Item(L.T("Mark All Messages as Read"), "\uE8C3", _vm.MarkFolderReadCommand, node));
        items.Add(new Separator());
        items.AddRange(TreeItems());
        items.Add(new Separator());
        foreach (var (label, visibility) in new[] { (L.T("Show"), FolderVisibility.Show), (L.T("Show if unread"), FolderVisibility.ShowIfUnread), (L.T("Hide"), FolderVisibility.Hide) })
            items.Add(Action(label, null, () => _vm.SetFolderVisibilityAsync(folder, visibility), isChecked: prefs.Visibility == visibility));
        items.Add(new Separator());
        items.Add(Action(prefs.Favorite ? L.T("Remove from Favorites") : L.T("Add to Favorites"), "\uE734", () => _vm.ToggleFavoriteAsync(folder)));
        items.Add(new Separator());
        var colors = Submenu(L.T("Folder Color"), "\uE790", []);
        colors.Items.Add(Action(L.T("None"), null, () => _vm.SetFolderColorAsync(folder, null), isChecked: prefs.Color is null));
        foreach (var (name, hex) in FolderColors.Palette)
        {
            var current = string.Equals(prefs.Color, hex, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = FolderColors.Brush(hex),
                // The current colour shows its check mark inside the swatch.
                Child = current ? new TextBlock
                {
                    Text = "\uE73E", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 11, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                } : null,
            };
            colors.Items.Add(Action(name, null, () => _vm.SetFolderColorAsync(folder, hex), icon: swatch));
        }
        items.Add(colors);
        items.Add(Item(L.T("Properties…"), "\uE946", _vm.FolderPropertiesCommand, node));
        return items;
    }

    private List<object> AccountMenu(SidebarNode node)
    {
        var hidden = Submenu(L.T("Show Hidden Folder"), "\uE7B3", _vm.HiddenFolders(node.AccountId)
            .Select(f => (object)Action(f.Name, null, () => _vm.SetFolderVisibilityAsync(f, FolderVisibility.Show))));
        hidden.IsEnabled = hidden.Items.Count > 0;
        return
        [
            Item(L.T("Sync now"), "\uE72C", _vm.SyncAccountCommand, node),
            Item(L.T("Sign in again…"), "\uE77B", _vm.SignInAgainCommand, node),
            new Separator(),
            Item(L.T("New Folder…"), "\uE8F4", _vm.NewFolderCommand, node),
            hidden,
            new Separator(),
            .. TreeItems(),
            new Separator(),
            Item(L.T("Remove account…"), "\uE74D", _vm.RemoveAccountCommand, node),
        ];
    }

    private List<object> TreeItems() =>
    [
        Item(L.T("Collapse All"), "\uE972", _vm.CollapseAllCommand),
        Item(L.T("Expand All"), "\uE70D", _vm.ExpandAllCommand),
    ];

    private static MenuItem Enabled(MenuItem item, bool enabled)
    {
        if (!enabled)
        {
            item.Command = null; // a command would re-enable it
            item.IsEnabled = false;
        }
        return item;
    }

    private static GlyphIcon Glyph(string glyph) => new() { Glyph = glyph, FontSize = 16 };

    /// <summary>A menu item that runs an async view-model action (for choices that need more than a command parameter).</summary>
    /// <param name="isChecked">Shown as a check mark in the icon column (checkable items would shift submenu rows out of line).</param>
    private MenuItem Action(string header, string? glyph, Func<Task> run, bool isChecked = false, UIElement? icon = null)
    {
        var item = new MenuItem
        {
            Header = header,
            Icon = icon ?? (isChecked ? Glyph("\uE73E") : glyph is null ? null : Glyph(glyph)),
        };
        item.Click += async (_, _) =>
        {
            try { await run(); }
            catch (Exception ex) { Serilog.Log.Warning(ex, "Sidebar action {Action} failed", header); }
        };
        return item;
    }

    // ───────── toolbar menus (eM Client's split buttons) and the responsive "More" ─────────

    /// <summary>The ▾ beside Refresh, Forward or Mark: its menu opens under the whole split button.</summary>
    private void Dropdown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string which } button) return;
        _vm.RefreshMenuState();
        var items = which switch
        {
            "Refresh" => RefreshItems(),
            "Forward" => ForwardItems(),
            "Mark" => MarkItems(),
            _ => [],
        };
        OpenMenu(items, button.Parent is StackPanel { Name: "LeftTools" } ? button : (FrameworkElement)button.Parent);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        _vm.RefreshMenuState();
        // Whatever no longer fits, in toolbar order, then the actions that always live here.
        var items = ActionBar.Children.OfType<FrameworkElement>()
            .Where(c => c != MoreGroup && c.Visibility == Visibility.Collapsed)
            .Select(OverflowItem).OfType<object>().ToList();
        if (items.Count > 0) items.Add(new Separator());
        items.Add(Submenu(L.T("Snooze"), "\uE823", [], enabled: false));
        items.Add(Submenu(L.T("Quick Actions"), "\uE945", [], enabled: false));
        OpenMenu(items, MoreGroup);
    }

    private bool _loadingOlder;

    /// <summary>
    /// Near the end of the list (two screens to go), the next page of older mail is added below. Scrolling, End, the
    /// scrollbar and a list too short to scroll all come through here, since a change in the list's height does too.
    /// </summary>
    private async void MessageList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.OriginalSource is not ScrollViewer sv || _loadingOlder || !NearEnd()) return;
        _loadingOlder = true;
        try
        {
            // Once more if the end is near again when the page is in (its height is an estimate at first): at the very
            // end nothing scrolls any more, so nothing else would ask for the next one.
            for (var pages = 0; pages < 3 && NearEnd(); pages++)
            {
                var offset = sv.VerticalOffset;
                // The page goes in with one Reset, which sends the list back to the top: put it back where it was.
                if (!await _vm.LoadMoreMessagesAsync()) break;
                await Dispatcher.InvokeAsync(() => sv.ScrollToVerticalOffset(offset), System.Windows.Threading.DispatcherPriority.Loaded);
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background); // laid out
            }
        }
        finally
        {
            _loadingOlder = false;
        }

        bool NearEnd() => _vm.HasMoreMessages && sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - 2 * sv.ViewportHeight;
    }

    /// <summary>The search box's scope button: all mail (the default), the selected account or folder, and Trash/Junk.</summary>
    private void SearchOptions_Click(object sender, RoutedEventArgs e)
    {
        Task Set(SearchArea area)
        {
            _vm.SearchArea = area;
            return Task.CompletedTask;
        }
        var account = _vm.SearchAccount;
        var folder = _vm.SearchFolder;
        var thisAccount = Action(account is null ? L.T("This account") : L.F("This account ({0})", account.SidebarTitle), null,
            () => Set(SearchArea.Account), isChecked: _vm.SearchArea == SearchArea.Account);
        thisAccount.IsEnabled = account is not null;
        var thisFolder = Action(folder is null ? L.T("This folder") : L.F("This folder ({0})", folder.Name), null,
            () => Set(SearchArea.Folder), isChecked: _vm.SearchArea == SearchArea.Folder);
        thisFolder.IsEnabled = folder is not null;
        var trash = Action(L.T("Include Trash and Junk"), null, () =>
        {
            _vm.SearchIncludesTrash = !_vm.SearchIncludesTrash;
            return Task.CompletedTask;
        }, isChecked: _vm.SearchIncludesTrash);
        trash.IsEnabled = _vm.SearchArea != SearchArea.Folder || folder is null; // a folder search covers that folder, whatever it is
        OpenMenu(
        [
            Action(L.T("All mail"), null, () => Set(SearchArea.All), isChecked: _vm.SearchArea == SearchArea.All),
            thisAccount,
            thisFolder,
            new Separator(),
            trash,
        ], (FrameworkElement)sender);
    }

    private void OpenMenu(IEnumerable<object> items, FrameworkElement target)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            DataContext = DataContext,
        };
        foreach (var item in items) menu.Items.Add(item);
        menu.IsOpen = true;
    }

    private MenuItem? OverflowItem(FrameworkElement group) => group.Name switch
    {
        nameof(ReplyAllGroup) => Item(L.T("Reply All"), "\uE8C2", _vm.ReplyAllCommand),
        nameof(ForwardGroup) => Submenu(L.T("Forward"), "\uEE35", [Item(L.T("Forward"), "\uEE35", _vm.ForwardCommand), new Separator(), .. ForwardItems()]),
        nameof(MarkGroup) => Submenu(L.T("Mark"), "\uE8C3", [Item(L.T("Toggle Read"), "\uE8C3", _vm.ToggleReadCommand, gesture: L.T("Ctrl+Q")), new Separator(), .. MarkItems()]),
        nameof(FlagGroup) => Item(L.T("Flag"), "\uE7C1", _vm.ToggleFlagCommand),
        nameof(ArchiveGroup) => Item(L.T("Archive"), "\uE7B8", _vm.ArchiveCommand, gesture: L.T("Ctrl+E")),
        nameof(DeleteGroup) => Item(L.T("Delete"), "\uE74D", _vm.DeleteCommand, gesture: L.T("Del")),
        _ => null,
    };

    private List<object> RefreshItems()
    {
        List<object> items =
        [
            Item(L.T("Receive All"), "\uE896", _vm.RefreshCommand, gesture: "F5"),
            Item(L.T("Show Operations…"), "\uE8CB", _vm.ShowOperationsCommand),
        ];
        var accounts = _vm.AccountList;
        if (accounts.Count > 1)
        {
            items.Add(new Separator());
            items.AddRange(accounts.Select(a => Item(L.F("Receive \"{0}\" only", a.SidebarTitle), "\uE77B", _vm.RefreshAccountCommand, a)));
        }
        return items;
    }

    private List<object> ForwardItems() =>
    [
        Item(L.T("Forward as Attachment…"), "\uE723", _vm.ForwardAsAttachmentCommand),
        Item(L.T("Forward as Original…"), "\uE8A5", _vm.ForwardAsOriginalCommand),
        Item(L.T("Resend…"), "\uE8EE", _vm.ResendCommand),
    ];

    private List<object> MarkItems() =>
    [
        Item(L.T("Flag"), "\uE7C1", _vm.ToggleFlagCommand),
        Item(L.T("Mark as Read"), "\uE8C3", _vm.MarkReadCommand),
        Item(L.T("Mark as Unread"), "\uE715", _vm.MarkUnreadCommand),
        new Separator(),
        _vm.SelectedInJunk ? Item(L.T("Not Junk"), "\uE8FB", _vm.NotJunkCommand) : Item(L.T("Move to Junk"), "\uE7BA", _vm.MoveToJunkCommand),
    ];

    /// <summary>
    /// Favorites > Trash: each account's own Trash to open, and to empty (one account at a time, as the folder's menu
    /// does, so nothing goes for good across all accounts with one click).
    /// </summary>
    private List<object> TrashMenu()
    {
        var trashes = _vm.SidebarNodes.Where(n => n is { Kind: NodeKind.Folder, Role: FolderRole.Trash, IsFavoriteShortcut: false }).ToList();
        string Name(SidebarNode n) => trashes.Count == 1 ? L.T("Trash") : n.Parent?.Title ?? L.T("Trash");
        var items = new List<object>();
        foreach (var trash in trashes)
        {
            var open = new MenuItem { Header = trashes.Count == 1 ? L.T("Open Trash") : L.F("Open Trash of {0}", Name(trash)), Icon = Glyph(Glyphs.Trash) };
            open.Click += (_, _) => _vm.SelectedNode = trash;
            items.Add(open);
        }
        if (trashes.Count > 0) items.Add(new Separator());
        foreach (var trash in trashes)
            items.Add(Item(trashes.Count == 1 ? L.T("Empty Trash") : L.F("Empty Trash of {0}", Name(trash)), GlyphIcon.EmptyTrash, _vm.EmptyFolderCommand, trash));
        // All of them at once, after one question.
        if (trashes.Count > 1)
        {
            items.Add(new Separator());
            items.Add(Item(L.T("Empty All Trash…"), GlyphIcon.EmptyTrash, _vm.EmptyAllTrashCommand));
        }
        return items;
    }

    private MenuItem Item(string header, string glyph, ICommand command, object? parameter = null, string? gesture = null) => new()
    {
        Header = header,
        Command = command,
        CommandParameter = parameter,
        InputGestureText = gesture ?? "",
        Icon = Glyph(glyph),
    };

    private MenuItem Submenu(string header, string glyph, IEnumerable<object> children, bool enabled = true)
    {
        var item = new MenuItem
        {
            Header = header,
            IsEnabled = enabled,
            Icon = Glyph(glyph),
        };
        foreach (var child in children) item.Items.Add(child);
        return item;
    }

    /// <summary>
    /// Narrow window: message actions give way to ⋯, least important first (Flag, Archive, Mark, Forward, Delete,
    /// Reply All). Reply always stays, and so does ⋯ (Snooze and Quick Actions are in it).
    /// </summary>
    private void FitToolbar()
    {
        if (_sizing) return; // done once when the drag ends
        if (!IsLoaded) return;
        FrameworkElement[] giveWay = [FlagGroup, ArchiveGroup, MarkGroup, ForwardGroup, DeleteGroup, ReplyAllGroup];
        static double Width(UIElement e)
        {
            e.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return e.DesiredSize.Width;
        }

        foreach (var group in giveWay) group.Visibility = Visibility.Visible;
        var available = ToolbarRow.ActualWidth - LeftTools.ActualWidth - MailSearch.ActualWidth - 30;
        var needed = ActionBar.Children.OfType<FrameworkElement>().Sum(Width); // ⋯ included: it's always there

        var hidden = 0;
        while (hidden < giveWay.Length && needed > available)
        {
            needed -= Width(giveWay[hidden]);
            giveWay[hidden++].Visibility = Visibility.Collapsed;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async Task MeasureFolderSwitchingAsync()
    {
        await Task.Delay(1500); // let startup settle
        var nodes = _vm.SidebarNodes.Where(n => !n.IsGroup).ToList();
        for (var round = 1; round <= 2; round++)
        {
            foreach (var node in nodes)
            {
                var sw = Stopwatch.StartNew();
                _vm.SelectedNode = node;
                // Wait until this folder's list is in (not an earlier one's) and WPF has finished layout and rendering.
                bool Shown() => _vm.ListedNode == node;
                for (var i = 0; i < 3000 && !Shown(); i++)
                    await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
                var listed = sw.ElapsedMilliseconds;
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                var drawn = sw.ElapsedMilliseconds;
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
                var realized = CountRows(MessageList);
                Serilog.Log.Information("PERF round {Round} {Folder}: {Ms} ms for {Count} rows ({Realized} row controls built; listed at {Listed} ms, drawn at {Drawn} ms)",
                    round, node.Title, sw.ElapsedMilliseconds, _vm.Messages.Count, realized, listed, drawn);
            }
        }
        Serilog.Log.Information("PERF pictures after folder switching: {N} decoded, {Ms} ms", Pictures.Decoded, Pictures.DecodeTime.ElapsedMilliseconds);
        await MeasureOlderMailAsync();
        Serilog.Log.Information("PERF pictures in total: {N} decoded, {Ms} ms", Pictures.Decoded, Pictures.DecodeTime.ElapsedMilliseconds);
        App.ExitApp();
    }

    private static void LogMemory(string what)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        Serilog.Log.Information("PERF {What}: managed {Managed:0.0} MB, private {Private:0.0} MB", what, GC.GetTotalMemory(false) / 1048576.0, p.PrivateMemorySize64 / 1048576.0);
    }

    /// <summary>Dev aid (--dev-perf): scrolling deep into the biggest folder, a refresh while there, and a whole-folder sort.</summary>
    private async Task MeasureOlderMailAsync()
    {
        async Task Settle()
        {
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            await Task.Delay(50);
        }
        var counts = await Task.Run(() => App.Services!.GetService(typeof(IMessageRepository)) is IMessageRepository repo ? repo.GetCountsAsync(withTotals: true) : null!);
        var biggest = _vm.SidebarNodes.Where(n => n.Kind == NodeKind.Folder).MaxBy(n => counts.TotalByFolder.GetValueOrDefault(n.FolderId));
        if (biggest is null || FindChild<ScrollViewer>(MessageList) is not { } sv) return;
        _vm.SelectedNode = biggest;
        await Task.Delay(1500);
        LogMemory("memory with the first page");
        var sw = Stopwatch.StartNew();
        for (var page = 1; page <= 10 && _vm.HasMoreMessages; page++)
        {
            var before = _vm.Messages.Count;
            var step = Stopwatch.StartNew();
            sv.ScrollToEnd();
            for (var i = 0; i < 200 && _vm.Messages.Count == before; i++) await Task.Delay(10);
            await Settle();
            Serilog.Log.Information("PERF older page {Page} in {Folder}: {Ms} ms, {Rows} rows, offset {Offset:0} of {Extent:0}",
                page, biggest.Title, step.ElapsedMilliseconds, _vm.Messages.Count, sv.VerticalOffset, sv.ExtentHeight);
        }
        Serilog.Log.Information("PERF 10 older pages: {Ms} ms", sw.ElapsedMilliseconds);
        LogMemory("memory with 10 more pages");

        // New mail arriving while deep in the list reloads it the way a sync does (dev databases don't sync).
        var offset = sv.VerticalOffset;
        sw.Restart();
        await _vm.SimulateNewMailAsync("Perf arrival");
        await Settle();
        Serilog.Log.Information("PERF reload while deep: {Ms} ms, {Rows} rows, offset {Before:0} -> {After:0}",
            sw.ElapsedMilliseconds, _vm.Messages.Count, offset, sv.VerticalOffset);

        var sort = (_vm.SortColumn, _vm.SortDescending);
        try
        {
            sw.Restart();
            await _vm.SortByCommand.ExecuteAsync("From");
            await Settle();
            Serilog.Log.Information("PERF whole-folder sort by sender: {Ms} ms for {Rows} rows", sw.ElapsedMilliseconds, _vm.Messages.Count);
        }
        finally
        {
            // Back to the sort the settings had.
            await _vm.SortByCommand.ExecuteAsync(sort.SortColumn.ToString());
            if (_vm.SortDescending != sort.SortDescending) await _vm.SortByCommand.ExecuteAsync(sort.SortColumn.ToString());
        }
    }

    private static int CountRows(DependencyObject root)
    {
        var count = root is System.Windows.Controls.ListBoxItem ? 1 : 0;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            count += CountRows(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
        return count;
    }

    private static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match || (match = FindChild<T>(child)!) is not null) return match;
        }
        return null;
    }

    /// <summary>Dev aid (--screenshot-calendar dir): every calendar view, plus the event editor.</summary>
    public string? CalendarScreenshotDir { get; set; }
    public string? TasksScreenshotDir { get; set; }

    /// <summary>Dev aid (--screenshot-tasks dir): the tasks module's lists, a task open with its steps, and the new-task window.</summary>
    private async Task CaptureTasksAsync(string dir)
    {
        ShowModule("tasks");
        await _tasks.LoadAsync();
        await Task.Delay(800);
        async Task Shot(Func<TaskListEntry, bool> list, string? open, string file)
        {
            _tasks.SelectedList = _tasks.Lists.First(list);
            await Task.Delay(300);
            _tasks.Selected = open is null ? null : _tasks.Rows.FirstOrDefault(r => r.Title.StartsWith(open, StringComparison.Ordinal));
            await Task.Delay(900);
            WindowCapture.SavePng(this, System.IO.Path.Combine(dir, file));
        }
        await Shot(l => l.Kind == TaskView.Today, null, "tasks-today.png");
        await Shot(l => l.Kind == TaskView.Today, "Plan the weekend", "tasks-detail.png");
        await Shot(l => l.Kind == TaskView.All, null, "tasks-all.png");
        await Shot(l => l.Kind == TaskView.List, "Renew", "tasks-list.png");
        _tasks.QuickText = "Call the bank next friday";
        await Shot(l => l.Kind == TaskView.All, null, "tasks-quickadd.png");
        _tasks.QuickText = "";
        if (_vm.Messages.FirstOrDefault()?.Model is { } message)
        {
            await _tasks.CreateFromMessageAsync(message);
            await Task.Delay(900);
            if (Application.Current.Windows.OfType<TaskWindow>().FirstOrDefault() is { } w)
            {
                WindowCapture.SavePng(w, System.IO.Path.Combine(dir, "tasks-from-email.png"));
                w.Close();
            }
        }
        // Switching away: nothing of Tasks (like the quick-add hint) may stay on top of the next module.
        ShowModule("contacts");
        await Task.Delay(1200);
        WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "tasks-then-contacts.png"));
        ShowModule("mail");
    }

    private async Task CaptureCalendarAsync(string dir)
    {
        ShowModule("calendar");
        var cal = (CalendarViewModel)_calendarView.DataContext;
        // PLICBOX_LOCAL_CAL: a calendar on this computer too, with a weekly event (the demo itself only has Google's).
        var local = Environment.GetEnvironmentVariable("PLICBOX_LOCAL_CAL") is not null;
        if (local && App.Services?.GetService(typeof(Plicbox.Dav.CalendarEngine)) is Plicbox.Dav.CalendarEngine engine)
        {
            var id = await engine.CreateCalendarAsync(null, "Personal", "#F4511E");
            var today = DateTime.Today;
            var tuesday = today.AddDays(1 - ((int)today.DayOfWeek + 6) % 7).AddHours(18); // this week's
            await engine.CreateAsync(id, new Plicbox.Dav.EventDraft
            {
                Title = "Gym", Start = tuesday, End = tuesday.AddHours(1), Recurrence = Plicbox.Dav.Calendars.BuildRule(Plicbox.Dav.RepeatKind.Weekly, tuesday),
                Guests = ["ignored@example.com"], AddVideoCall = true, ReminderMinutes = 30,
            });
            await Task.Delay(1000);
        }
        foreach (var mode in new[] { CalendarViewMode.Week, CalendarViewMode.Month, CalendarViewMode.Agenda, CalendarViewMode.Day, CalendarViewMode.WorkWeek })
        {
            cal.Mode = mode;
            await Task.Delay(1200);
            WindowCapture.SavePng(this, System.IO.Path.Combine(dir, $"cal-{mode.ToString().ToLowerInvariant()}.png"));
        }
        cal.Mode = CalendarViewMode.Week;
        await Task.Delay(800);

        // Menus are popups (their own windows): capture those directly.
        static async Task CaptureMenu(ContextMenu menu, string path)
        {
            await Task.Delay(600);
            if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource popup) WindowCapture.SavePng(popup.Handle, path);
            menu.IsOpen = false;
        }
        var newMenu = BuildNewMenu();
        newMenu.PlacementTarget = _calendarView;
        newMenu.IsOpen = true;
        await CaptureMenu(newMenu, System.IO.Path.Combine(dir, "cal-newmenu.png"));
        if (_calendarView.OpenCalendarMenuForPreview() is { } calMenu)
            await CaptureMenu(calMenu, System.IO.Path.Combine(dir, "cal-calmenu.png"));
        if (_calendarView.OpenSlotMenuForPreview() is { } slotMenu)
            await CaptureMenu(slotMenu, System.IO.Path.Combine(dir, "cal-slotmenu.png"));

        cal.SearchText = "lunch";
        await Task.Delay(1500);
        WindowCapture.SavePng(this, System.IO.Path.Combine(dir, "cal-search.png"));
        cal.SearchText = "";
        await Task.Delay(500);

        foreach (var (title, file) in new[] { ("Design review", "cal-editor.png"), ("Quarterly planning", "cal-invite.png"), ("Gym", "cal-local-editor.png") })
        {
            if (cal.Events.FirstOrDefault(e => e.Title == title) is not { } ev) continue;
            cal.OpenCommand.Execute(ev);
            await Task.Delay(1500);
            if (Application.Current.Windows.OfType<EventWindow>().FirstOrDefault() is { } editor)
            {
                WindowCapture.SavePng(editor, System.IO.Path.Combine(dir, file));
                editor.Close();
            }
        }
        // Contacts: a few cards that show the smart parts.
        ShowModule("contacts");
        await Task.Delay(2000);
        async Task Shot(PeopleList list, string? name, string file)
        {
            _contacts.SelectedList = _contacts.Lists.First(l => l.Kind == list);
            await Task.Delay(400);
            if (name is not null && _contacts.People.FirstOrDefault(p => p.Name.StartsWith(name, StringComparison.Ordinal)) is { } person)
                _contacts.Selected = person;
            await Task.Delay(1800);
            WindowCapture.SavePng(this, System.IO.Path.Combine(dir, file));
        }
        await Shot(PeopleList.NotSaved, "Mia", "contacts-notsaved.png");
        await Shot(PeopleList.KeepInTouch, "Laura", "contacts-keepintouch.png");
        await Shot(PeopleList.Favorites, "Ana", "contacts-favorite.png");
        // The contact window: editing Ana, saving Mia from mail (filled in from her signature), and a new contact.
        async Task Window(Action open, string file)
        {
            open();
            await Task.Delay(900);
            if (Application.Current.Windows.OfType<ContactWindow>().FirstOrDefault() is { } w)
            {
                WindowCapture.SavePng(w, System.IO.Path.Combine(dir, file));
                w.Close();
            }
        }
        await Window(() => _contacts.EditCommand.Execute(null), "contacts-editor.png");
        await Shot(PeopleList.NotSaved, "Mia", "contacts-notsaved.png");
        await Window(() => _contacts.EditCommand.Execute(null), "contacts-save-from-mail.png");
        await Window(_contacts.NewContact, "contacts-new.png");
        ShowModule("mail");
    }

    private async Task CaptureAndExitAsync(string path)
    {
        // Wait for mail to show up (demo data loads instantly; a real sync takes a moment).
        for (var i = 0; i < 40 && _vm.Messages.Count == 0; i++) await Task.Delay(250);
        await Task.Delay(800);
        // Show an opened conversation and a message with blocked pictures/trackers in the same shot.
        if (_vm.Messages.FirstOrDefault(m => m.IsThread) is { } thread)
        {
            await _vm.ToggleThreadCommand.ExecuteAsync(thread);
            MessageList.ScrollIntoView(_vm.Messages.LastOrDefault(m => m.Parent == thread) ?? thread);
        }
        _vm.SelectedMessage = _vm.Messages.FirstOrDefault(m => m.Subject.StartsWith("Finish", StringComparison.Ordinal))
            ?? _vm.Messages.FirstOrDefault(m => m.Model.HasAttachments)
            ?? _vm.Messages.FirstOrDefault();
        await Task.Delay(2500);
        WindowCapture.SavePng(this, path);
        if (ComposeScreenshotPath is not null)
        {
            // A reply quoting a newsletter with a web picture (shown as a placeholder while writing).
            if (_vm.Messages.FirstOrDefault(m => m.Subject.Contains("Night Transit", StringComparison.Ordinal)) is { } newsletter)
            {
                _vm.SelectedMessage = newsletter;
                await Task.Delay(1500);
                // A message on a grey page beside the sidebar: the divider keeps them apart.
                WindowCapture.SavePng(this, System.IO.Path.ChangeExtension(ComposeScreenshotPath, ".reading.png"));
                // Narrow: the header in a narrower reading pane (chips wrap, the subject shortens).
                var wide = Width;
                Width = 1100;
                await Task.Delay(1200);
                WindowCapture.SavePng(this, System.IO.Path.ChangeExtension(ComposeScreenshotPath, ".reading-narrow.png"));
                Width = wide;
                await Task.Delay(600);
            }
            _vm.ReplyCommand.Execute(null);
            await Task.Delay(2500);
            if (Application.Current.Windows.OfType<ComposeWindow>().FirstOrDefault() is { } compose)
            {
                await compose.TypeLinesAsync("First line", "Second line", "Third line");
                await Task.Delay(400);
                if (Environment.GetEnvironmentVariable("PLICBOX_PICTURE_TEST") is { } picture)
                    await compose.DevPictureTestAsync(picture, System.IO.Path.GetDirectoryName(ComposeScreenshotPath)!);
                WindowCapture.SavePng(compose, ComposeScreenshotPath);
                var suggestions = await compose.PreviewSuggestionsAsync("mi");
                if (suggestions.Child is { } list && PresentationSource.FromVisual(list) is System.Windows.Interop.HwndSource popupWindow)
                    WindowCapture.SavePng(popupWindow.Handle, System.IO.Path.ChangeExtension(ComposeScreenshotPath, ".suggestions.png"));
                compose.CloseWithoutPrompt();
            }
        }
        if (DialogScreenshotDir is not null && App.Services is { } services)
        {
            // The Refresh button's arrows turning, then at rest.
            _vm.RefreshCommand.Execute(null);
            await Task.Delay(450);
            WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, "refresh-spinning.png"));
            await Task.Delay(2600);
            WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, "refresh-done.png"));

            // New mail arriving while the inbox is on screen.
            _vm.SelectedNode = _vm.SidebarNodes.First(n => n.Kind == NodeKind.Folder && n.Title == "Inbox");
            await Task.Delay(1500);
            FindChild<ScrollViewer>(MessageList)?.ScrollToTop(); // reading at the top of the inbox
            await Task.Delay(300);
            WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, "inbox-before.png"));
            await _vm.SimulateNewMailAsync("Brand new message");
            await Task.Delay(700);
            WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, "inbox-after.png"));

            // Unread: read a message, a sync refreshes the list; it should still be there.
            _vm.SelectedNode = _vm.SidebarNodes.First(n => n.Kind == NodeKind.Unread);
            await Task.Delay(1200);
            var reading = _vm.Messages.FirstOrDefault(m => !m.Model.IsRead);
            var before = _vm.Messages.Count;
            if (reading is not null)
            {
                _vm.SelectedMessage = reading;
                await Task.Delay(2500); // marked read after 1.5 s
                await _vm.SimulateNewMailAsync("Another arrival");
                await Task.Delay(800);
                System.IO.File.WriteAllText(System.IO.Path.Combine(DialogScreenshotDir, "unread-check.txt"),
                    $"unread list before: {before}, after reading + new mail: {_vm.Messages.Count}, " +
                    $"read message still listed: {_vm.Messages.Any(m => m.Model.Id == reading.Model.Id)}, still selected: {_vm.SelectedMessage?.Model.Id == reading.Model.Id}");
                WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, "unread-after.png"));
            }
            // Expand "More" for the shot, then capture the main window again plus both dialogs (shown modeless).
            foreach (var node in _vm.SidebarNodes.Where(n => n.Title == "More")) node.IsExpanded = true;
            _vm.SidebarView.Refresh();
            await Task.Delay(500);
            WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, "main-more.png"));
            MailSearch.FocusBox();
            await Task.Delay(500);
            WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, "main-search.png"));
            MailSearch.Text = "";

            // Link bubbles: one per rating, captured from their own popup window. "listed" is on a sample threat list
            // (the demo downloads none), put in place for the moment.
            var threatsBefore = Plicbox.Mail.ThreatLists.Current;
            var sampleThreats = new Plicbox.Mail.ThreatLists();
            sampleThreats.Add(Plicbox.Mail.ThreatKind.Phishing, "northwind-verify.example");
            foreach (var (name, url, text, from) in new[]
            {
                ("safe", "https://neoouija.bandcamp.com/album/oriens-occidens?from=fanpub_fnb&utm_source=album_release&utm_medium=email&utm_content=fanpub_fnb", null, "noreply@bandcamp.com"),
                ("caution", "https://bit.ly/3xYzAb", "Track your parcel", "info@shop.example"),
                ("danger", "https://paypal.com.account-verify.net/login?session=8f3a", "www.paypal.com", "service@paypal.com"),
                ("listed", "https://secure.northwind-verify.example/login?case=4471", "Verify your account", "service@northwind.example"),
            })
            {
                Plicbox.Mail.ThreatLists.Current = name == "listed" ? sampleThreats : threatsBefore;
                var bubble = Services.MailWebView.PreviewBubble(Reading.Body, url, text, from);
                await Task.Delay(500);
                if (bubble?.Child is { } child && PresentationSource.FromVisual(child) is System.Windows.Interop.HwndSource bubbleWindow)
                    WindowCapture.SavePng(bubbleWindow.Handle, System.IO.Path.Combine(DialogScreenshotDir, $"link-{name}.png"));
                if (bubble?.Child is FrameworkElement { IsVisible: true } shown && PresentationSource.FromVisual(shown) is not null)
                {
                    var corner = WindowCapture.SaveCornerPng(new System.Windows.Interop.WindowInteropHelper(this).Handle,
                        System.IO.Path.Combine(DialogScreenshotDir, $"corner-{name}.png"));
                    System.IO.File.AppendAllText(System.IO.Path.Combine(DialogScreenshotDir, "bubble-bounds.txt"), corner + Environment.NewLine);
                    var b = shown.PointToScreen(new Point(0, 0));
                    var p = Reading.Body.PointToScreen(new Point(0, 0));
                    var e = Reading.Body.PointToScreen(new Point(Reading.Body.ActualWidth, Reading.Body.ActualHeight));
                    var end = shown.PointToScreen(new Point(shown.ActualWidth, shown.ActualHeight));
                    System.IO.File.AppendAllText(System.IO.Path.Combine(DialogScreenshotDir, "bubble-bounds.txt"),
                        $"{name}: bubble {b.X:0},{b.Y:0} > {end.X:0},{end.Y:0}  pane {p.X:0},{p.Y:0} > {e.X:0},{e.Y:0}" + Environment.NewLine);
                }
                WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, $"main-link-{name}.png"));
                if (bubble is not null) bubble.IsOpen = false;
            }
            Plicbox.Mail.ThreatLists.Current = threatsBefore;

            // Sent while the server can't be reached (the demo has none): it waits in the account's Outbox.
            if (_vm.AccountList.FirstOrDefault() is { } sender)
            {
                var mail = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Plicbox.Mail.MailEngine>(services);
                await mail.SendAsync(sender, Plicbox.Mail.Composing.ToMimeMessage(sender, new Plicbox.Mail.ComposeDraft { AccountId = sender.Id },
                    "ana.popescu@example.com", "", "", "Slides for Thursday", "<p>Hi Ana,</p><p>Here are the slides for Thursday.</p>", []));
                await Task.Delay(2500); // the first try (no server) is over, the sidebar has its Outbox
                if (_vm.SidebarNodes.FirstOrDefault(n => n.Kind == NodeKind.Outbox) is { } outbox) _vm.SelectedNode = outbox;
                await Task.Delay(1500);
                WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, "outbox.png"));
            }
            ShowModule("contacts"); // its toolbar, with import and export
            await Task.Delay(2500);
            WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, "contacts.png"));
            ShowModule("mail");
            await Task.Delay(800);

            var accounts = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AccountsWindow>(services);
            accounts.Owner = this;
            accounts.Show();
            await Task.Delay(1500);
            WindowCapture.SavePng(accounts, System.IO.Path.Combine(DialogScreenshotDir, "accounts.png"));
            accounts.SelectTab(1);
            await Task.Delay(700);
            WindowCapture.SavePng(accounts, System.IO.Path.Combine(DialogScreenshotDir, "accounts-imap.png"));
            if (accounts.DataContext is AccountsViewModel { Selected: { } editor }) editor.SmtpAuthIndex = 1; // shown with its own login (not saved)
            accounts.SelectTab(2);
            await Task.Delay(700);
            WindowCapture.SavePng(accounts, System.IO.Path.Combine(DialogScreenshotDir, "accounts-smtp.png"));
            accounts.SelectTab(3);
            await Task.Delay(700);
            WindowCapture.SavePng(accounts, System.IO.Path.Combine(DialogScreenshotDir, "accounts-diagnostics.png"));
            accounts.Close();

            var settingsWindow = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<SettingsWindow>(services);
            settingsWindow.Owner = this;
            settingsWindow.Show();
            await Task.Delay(700);
            WindowCapture.SavePng(settingsWindow, System.IO.Path.Combine(DialogScreenshotDir, "settings.png"));
            foreach (var page in new[] { "appearance", "mail", "calendar", "senders", "backup" })
            {
                settingsWindow.StartPage = page;
                if (page == "senders") settingsWindow.DevFillSenders();
                await Task.Delay(page == "mail" ? 1200 : 400); // the space used is worked out first
                WindowCapture.SavePng(settingsWindow, System.IO.Path.Combine(DialogScreenshotDir, $"settings-{page}.png"));
                if (page == "mail")
                {
                    settingsWindow.DevShowOfflineSpace();
                    await Task.Delay(400);
                    WindowCapture.SavePng(settingsWindow, System.IO.Path.Combine(DialogScreenshotDir, "settings-mail-offline.png"));
                    settingsWindow.DevShowThreatLists();
                    await Task.Delay(400);
                    WindowCapture.SavePng(settingsWindow, System.IO.Path.Combine(DialogScreenshotDir, "settings-mail-threats.png"));
                }
                if (page == "appearance")
                {
                    settingsWindow.DevShowReadingPane();
                    await Task.Delay(400);
                    WindowCapture.SavePng(settingsWindow, System.IO.Path.Combine(DialogScreenshotDir, "settings-appearance-reading.png"));
                    settingsWindow.DevShowTextSample();
                    await Task.Delay(400);
                    WindowCapture.SavePng(settingsWindow, System.IO.Path.Combine(DialogScreenshotDir, "settings-appearance-text.png"));
                }
            }
            settingsWindow.Close();

            // Appearance applied live: another accent and font on the main window, then back.
            // Which text colour list rows really get (dark-mode check).
            {
                var item = FindAll<ListBoxItem>(SidebarList).FirstOrDefault(i => i.IsVisible);
                var text = item is null ? null : FindAll<TextBlock>(item).FirstOrDefault(t => t.IsVisible && t.Text.Length > 1);
                string Describe(DependencyObject? d) => d is null ? "-" : $"{(System.Windows.Media.Brush)d.GetValue(TextElement.ForegroundProperty)} from {DependencyPropertyHelper.GetValueSource(d, TextElement.ForegroundProperty).BaseValueSource}";
                System.IO.File.WriteAllLines(System.IO.Path.Combine(DialogScreenshotDir, "foregrounds.txt"),
                [$"item: {Describe(item)}", $"text '{text?.Text}': {Describe(text)}", $"list: {Describe(SidebarList)}", $"window: {Describe(this)}"]);
            }
            var mode = Services.Theme.IsDark ? "dark" : "light";
            Services.Appearance.Apply(mode, "#7C4DDB", Services.Appearance.Fonts.Contains("Roboto") ? "Roboto" : null, 1.0, "Georgia", 17);
            await Task.Delay(1500); // the message on screen shows again in the new font
            WindowCapture.SavePng(this, System.IO.Path.Combine(DialogScreenshotDir, "appearance-live.png"));
            Services.Appearance.Apply(_settings.Current with { Theme = mode });

            var setup = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AccountSetupWindow>(services);
            setup.Owner = this;
            setup.Show();
            await Task.Delay(800);
            WindowCapture.SavePng(setup, System.IO.Path.Combine(DialogScreenshotDir, "add-account.png"));
            setup.PreviewWaiting();
            await Task.Delay(600);
            WindowCapture.SavePng(setup, System.IO.Path.Combine(DialogScreenshotDir, "add-account-waiting.png"));
            setup.Close();
            setup = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AccountSetupWindow>(services);
            setup.Owner = this;
            setup.Show();
            setup.PreviewServers();
            await Task.Delay(800);
            WindowCapture.SavePng(setup, System.IO.Path.Combine(DialogScreenshotDir, "add-account-servers.png"));
            setup.Close();
            setup = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AccountSetupWindow>(services);
            setup.Owner = this;
            setup.Show();
            setup.PreviewMicrosoftWaiting();
            await Task.Delay(800);
            WindowCapture.SavePng(setup, System.IO.Path.Combine(DialogScreenshotDir, "add-account-microsoft.png"));
            setup.PreviewAppPassword();
            await Task.Delay(800);
            WindowCapture.SavePng(setup, System.IO.Path.Combine(DialogScreenshotDir, "add-account-app-password.png"));
            setup.PreviewDone();
            await Task.Delay(600);
            WindowCapture.SavePng(setup, System.IO.Path.Combine(DialogScreenshotDir, "add-account-done.png"));
            setup.Close();

            // The message menu is its own popup window: open it on the selected row and capture just that popup.
            var menu = MessageList.ContextMenu!;
            menu.PlacementTarget = MessageList;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
            menu.IsOpen = true;
            await Task.Delay(700);
            if (PresentationSource.FromVisual(menu) is System.Windows.Interop.HwndSource popup)
                WindowCapture.SavePng(popup.Handle, System.IO.Path.Combine(DialogScreenshotDir, "message-menu.png"));
            // …and its Create Rule submenu.
            if (menu.Items.OfType<MenuItem>().FirstOrDefault(i => i.Header as string == "Create Rule") is { } createRule)
            {
                createRule.IsSubmenuOpen = true;
                await Task.Delay(600);
                if (createRule.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first
                    && PresentationSource.FromVisual(first) is System.Windows.Interop.HwndSource submenu)
                    WindowCapture.SavePng(submenu.Handle, System.IO.Path.Combine(DialogScreenshotDir, "create-rule-menu.png"));
                createRule.IsSubmenuOpen = false;
            }
            menu.IsOpen = false;

            // The Rules window, opened on a rule made from the selected message (not saved).
            if (_vm.SelectedMessage?.Model is { } sample)
            {
                var rulesVm = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<RulesViewModel>(services);
                var archive = _vm.SidebarNodes.FirstOrDefault(n => n.Kind == NodeKind.Folder && n.AccountId == sample.AccountId && n.Title == "Archive");
                rulesVm.StartWith = new Core.MailRule
                {
                    Name = $"{(sample.FromName.Length > 0 ? sample.FromName : sample.FromAddress)} > Archive", AccountId = sample.AccountId,
                    Conditions = [new Core.RuleCondition { Field = Core.RuleField.From, Operator = Core.RuleOperator.Is, Values = [sample.FromAddress] },
                                  new Core.RuleCondition { Field = Core.RuleField.Subject, Values = ["account", "benefits"] }],
                    MatchAll = false,
                    Actions = [new Core.RuleAction { Kind = Core.RuleActionKind.MoveTo, FolderAccountId = sample.AccountId, FolderPath = "Archive" },
                               new Core.RuleAction { Kind = Core.RuleActionKind.MarkRead }],
                };
                var rules = new RulesWindow(rulesVm) { Owner = this };
                rules.Show();
                await Task.Delay(1500);
                WindowCapture.SavePng(rules, System.IO.Path.Combine(DialogScreenshotDir, "rules.png"));
                rules.Close();

                // A rule on the phishing and malware lists (a yes/no test: the wording takes the values' room).
                var threatVm = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<RulesViewModel>(services);
                threatVm.StartWith = new Core.MailRule
                {
                    Name = "Reported phishing and malware > Junk",
                    Conditions = [new Core.RuleCondition { Field = Core.RuleField.KnownThreat }],
                    Actions = [new Core.RuleAction { Kind = Core.RuleActionKind.MoveToJunk }],
                    StopAfter = true,
                };
                var threatRules = new RulesWindow(threatVm) { Owner = this };
                threatRules.Show();
                await Task.Delay(1500);
                WindowCapture.SavePng(threatRules, System.IO.Path.Combine(DialogScreenshotDir, "rules-threat-list.png"));
                threatRules.Close();
            }

            // Every account's folders, grouped under each account (as the rule editor shows them).
            var accountTitles = _vm.SidebarNodes.Where(n => n.Kind == NodeKind.Group && n.AccountId > 0).GroupBy(n => n.AccountId).ToDictionary(g => g.Key, g => g.First().Title);
            var picker = new FolderPickerWindow("Move matching mail to", "Choose",
                _vm.SidebarNodes.Where(n => n.Kind == NodeKind.Folder && n.AccountId > 0)
                    .Select(n => new Plicbox.Core.Folder { AccountId = n.AccountId, Path = n.Title, Name = n.Title, Role = n.Role }),
                id => accountTitles.GetValueOrDefault(id) ?? "")
            { Owner = this };
            picker.Show();
            await Task.Delay(600);
            WindowCapture.SavePng(picker, System.IO.Path.Combine(DialogScreenshotDir, "folder-picker.png"));
            picker.Close();

            // A message with its body stored (demo mail has some without), attachments preferred.
            var bodies = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IBodyStore>(services);
            bool Stored(MessageItemViewModel m)
            {
                using var body = bodies.Open(m.Model.Id);
                return body is not null;
            }
            _vm.SelectedMessage = _vm.Messages.Where(Stored).OrderByDescending(m => m.Model.HasAttachments).FirstOrDefault() ?? _vm.SelectedMessage;
            await Task.Delay(800);
            await _vm.OpenMessageCommand.ExecuteAsync(null);
            await Task.Delay(2500);
            if (Application.Current.Windows.OfType<MessageWindow>().FirstOrDefault() is { } opened)
            {
                WindowCapture.SavePng(opened, System.IO.Path.Combine(DialogScreenshotDir, "message-window.png"));
                opened.Close();
            }

            var discard = MessageDialog.CreateForPreview("Discard this message?", "What you've written will be lost.", "Discard", "Keep editing", DialogKind.Danger);
            discard.Owner = this;
            discard.Show();
            await Task.Delay(600);
            WindowCapture.SavePng(discard, System.IO.Path.Combine(DialogScreenshotDir, "discard.png"));
            discard.Close();

            var question = MessageDialog.CreateForPreview("Send without a subject?", "A subject helps people find your message later.", "Send anyway", "Add a subject", DialogKind.Question);
            question.Owner = this;
            question.Show();
            await Task.Delay(600);
            WindowCapture.SavePng(question, System.IO.Path.Combine(DialogScreenshotDir, "question.png"));
            question.Close();

            var about = new AboutWindow { Owner = this };
            about.Show();
            await Task.Delay(600);
            WindowCapture.SavePng(about, System.IO.Path.Combine(DialogScreenshotDir, "about.png"));
            about.Close();
        }
        if (CalendarScreenshotDir is not null) await CaptureCalendarAsync(CalendarScreenshotDir);
        if (TasksScreenshotDir is not null) await CaptureTasksAsync(TasksScreenshotDir);
        Application.Current.Shutdown();
    }
}
