using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Plicbox.App.Services;
using Plicbox.Core;
using Plicbox.Dav;
using Plicbox.Mail;

namespace Plicbox.App;

/// <summary>
/// Like eM Client's "Show Operations", live: per account what its sync is doing this moment (the folder, the step,
/// how far along), older mail still loading in the background, the last pass, changes waiting for the server, and
/// how calendar, contacts and tasks are doing.
/// </summary>
public partial class OperationsWindow : Window
{
    private readonly IReadOnlyList<Account> _accounts;
    private readonly MailEngine _mail;
    private readonly CalendarEngine _calendar;
    private readonly ContactsEngine _contacts;
    private readonly TasksEngine _tasks;
    private readonly Func<IReadOnlyList<PendingOp>, string> _describeOps;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _updating;

    public OperationsWindow(IReadOnlyList<Account> accounts, MailEngine mail, CalendarEngine calendar, ContactsEngine contacts, TasksEngine tasks,
        Func<IReadOnlyList<PendingOp>, string> describeOps)
    {
        InitializeComponent();
        Icon = WindowIcons.Operations;
        (_accounts, _mail, _calendar, _contacts, _tasks, _describeOps) = (accounts, mail, calendar, contacts, tasks, describeOps);
        _timer.Tick += async (_, _) => await UpdateAsync();
        Loaded += async (_, _) =>
        {
            await UpdateAsync();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task UpdateAsync()
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var pending = new Dictionary<long, IReadOnlyList<PendingOp>>();
            foreach (var account in _accounts) pending[account.Id] = await Task.Run(() => _mail.GetPendingOpsAsync(account.Id));
            Cards.Children.Clear();
            if (_accounts.Count == 0) Cards.Children.Add(Line(null, L.T("No accounts yet."), Theme.Brush("MutedTextBrush")));
            foreach (var account in _accounts) Cards.Children.Add(Card(account, _mail.GetActivity(account.Id), pending[account.Id]));
        }
        finally
        {
            _updating = false;
        }
    }

    private FrameworkElement Card(Account account, SyncSnapshot? sync, IReadOnlyList<PendingOp> pending)
    {
        var body = new StackPanel();
        var (state, stateBrush) = sync?.Status switch
        {
            null => (L.T("Not connected"), "MutedTextBrush"),
            { State: SyncState.Error } => (L.T("Problem"), "DangerTextBrush"),
            _ when sync.Current is { Scope: not (SyncScope.PushChanges or SyncScope.Backfill) } => (L.T("Syncing…"), "LinkBrush"),
            { LastSync: not null } => (L.T("Up to date"), "SecondaryTextBrush"),
            _ => (L.T("Waiting for the first sync"), "MutedTextBrush"),
        };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var pill = new TextBlock { Text = state, Foreground = Theme.Brush(stateBrush), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(pill, Dock.Right);
        header.Children.Add(pill);
        header.Children.Add(new TextBlock { Text = account.SidebarTitle, FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        body.Children.Add(header);

        body.Children.Add(Heading(L.T("Mail")));
        if (sync is not null) AddMail(body, sync);
        body.Children.Add(pending.Count == 0
            ? Line("", L.T("Nothing waiting to be sent to the server."), Theme.Brush("MutedTextBrush"))
            : Line("", L.F("Waiting for the server: {0}.", _describeOps(pending)), Theme.Brush("TextBrush")));

        AddOther(body, L.T("Calendar"), _calendar.Statuses.GetValueOrDefault(account.Id));
        AddOther(body, L.T("Contacts"), _contacts.Statuses.GetValueOrDefault(account.Id));
        AddOther(body, L.T("Tasks"), _tasks.Statuses.GetValueOrDefault(account.Id));

        return new Border
        {
            Child = body, BorderBrush = Theme.Brush("DividerBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 12, 0, 0),
        };
    }

    private static void AddMail(StackPanel body, SyncSnapshot sync)
    {
        if (sync.Status is { State: SyncState.Error, Error: { } error })
            body.Children.Add(Line("", L.F("Problem: {0}", error), Theme.Brush("DangerTextBrush")));

        // The pass running now: what kind, where, how far, for how long.
        if (sync.Current is { } now)
        {
            var kind = now.Scope switch
            {
                SyncScope.Full => L.T("Full sync"),
                SyncScope.Inbox => L.T("Inbox check"),
                SyncScope.Backfill => L.T("Loading older mail"),
                _ => L.T("Sending changes"),
            };
            var running = Duration(DateTimeOffset.Now - now.Started);
            body.Children.Add(Line("", $"{kind} · {StepText(now)} · {L.F("running for {0}", running)}", Theme.Brush("TextBrush")));
            if (now is { Step: SyncStep.NewMessages, Total: > 0 }) body.Children.Add(Progress(now.Done, now.Total));
        }
        else if (sync.LastPass is { } last)
        {
            var took = Duration(last.Took);
            body.Children.Add(Line("", L.F("Last sync {0:t}, took {1}: {2} folders checked, {3} unchanged",
                last.Finished.LocalDateTime, took, last.Folders, last.Unchanged), Theme.Brush("SecondaryTextBrush")));
        }

        body.Children.Add(sync.WatchingInbox
            ? Line("", L.T("New mail arrives instantly (connected)"), Theme.Brush("MutedTextBrush"))
            : Line("", L.T("Instant new mail: not connected right now"), Theme.Brush("CautionTextBrush")));

        if (sync.Backlog.Count > 0)
        {
            body.Children.Add(Line("", L.T("Loading older mail in the background"), Theme.Brush("TextBrush")));
            foreach (var b in sync.Backlog)
            {
                body.Children.Add(Line(null, L.F("{0}: {1:N0} of {2:N0}", b.Folder, b.Have, b.OnServer), Theme.Brush("SecondaryTextBrush"), indent: 26));
                body.Children.Add(Progress(b.Have, b.OnServer, indent: 26));
            }
        }
    }

    private static string StepText(SyncActivity a) => a.Step switch
    {
        SyncStep.Starting => L.T("Starting"),
        SyncStep.CheckingConnection => L.T("Checking the connection"),
        SyncStep.Connecting => L.T("Connecting to the server"),
        SyncStep.SendingChanges => L.T("Sending local changes"),
        SyncStep.ListingFolders => L.T("Listing folders"),
        SyncStep.Opening => L.F("{0}: opening", a.Folder),
        SyncStep.ListingMessages => L.F("{0}: listing messages", a.Folder),
        SyncStep.FlagChanges => L.F("{0}: checking read and flag changes", a.Folder),
        _ => L.F("{0}: downloading messages {1:N0} of {2:N0}", a.Folder, a.Done, a.Total)
             + (a.Missing > a.Total ? " " + L.F("({0:N0} missing in this folder)", a.Missing) : ""),
    };

    /// <summary>Calendar, contacts or tasks: one line, when the account has that kind of sync at all.</summary>
    private static void AddOther(StackPanel body, string heading, CalendarAccountStatus? status)
    {
        if (status is null) return;
        body.Children.Add(Heading(heading));
        body.Children.Add(status switch
        {
            { State: CalendarSyncState.Syncing } => Line("", L.T("Syncing…"), Theme.Brush("TextBrush")),
            { State: CalendarSyncState.Error } s => Line("", L.F("Problem: {0}", s.Message), Theme.Brush("DangerTextBrush")),
            { State: CalendarSyncState.NeedsPermission } s => Line("", s.Message ?? L.T("Needs permission"), Theme.Brush("CautionTextBrush")),
            { State: CalendarSyncState.NeedsSignIn } s => Line("", s.Message ?? L.T("Needs sign-in"), Theme.Brush("CautionTextBrush")),
            { LastSync: { } last } => Line("", L.F("Up to date ({0:t})", last.LocalDateTime), Theme.Brush("SecondaryTextBrush")),
            _ => Line(null, L.T("Waiting for the first sync"), Theme.Brush("MutedTextBrush")),
        });
    }

    private static string Duration(TimeSpan t) =>
        t.TotalSeconds < 10 ? L.F("{0:0.0} s", t.TotalSeconds)
        : t.TotalSeconds < 60 ? L.F("{0} s", (int)t.TotalSeconds)
        : L.F("{0} min {1} s", (int)t.TotalMinutes, t.Seconds);

    private static TextBlock Heading(string text) => new()
    {
        Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Brush("MutedTextBrush"), Margin = new Thickness(0, 10, 0, 4),
    };

    private static FrameworkElement Line(string? glyph, string text, Brush brush, double indent = 0)
    {
        var row = new DockPanel { Margin = new Thickness(indent, 2, 0, 2) };
        if (glyph is not null)
        {
            var icon = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = brush,
                Width = 26, Margin = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Top,
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
        }
        else if (indent == 0)
        {
            row.Margin = new Thickness(26, 2, 0, 2);
        }
        row.Children.Add(new TextBlock { Text = text, Foreground = brush, TextWrapping = TextWrapping.Wrap });
        return row;
    }

    private static ProgressBar Progress(int done, int total, double indent = 26) => new()
    {
        Minimum = 0, Maximum = Math.Max(1, total), Value = Math.Min(done, total), Height = 4, Margin = new Thickness(indent, 4, 0, 6),
    };

    private void SyncAll_Click(object sender, RoutedEventArgs e) => _mail.RequestSync(userInitiated: true);

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(App.DataDir, "logs");
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }
}
