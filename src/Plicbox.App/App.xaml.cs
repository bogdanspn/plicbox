using System.IO;
using System.Windows;
using System.Windows.Threading;
using Plicbox.App.Services;
using Plicbox.App.ViewModels;
using Plicbox.Auth;
using Plicbox.Core;
using Plicbox.Dav;
using Plicbox.Mail;
using Plicbox.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Plicbox.App;

public partial class App : Application
{
    private IHost? _host;
    // Created here rather than by the container: it's a UI object and must be disposed on the UI thread,
    // while the container is disposed on the thread pool (see OnExit).
    private readonly TrayService _tray = new();
    private Mutex? _singleInstance;
    private EventWaitHandle? _showSignal;

    /// <summary>True once the user chose Exit; window closes then really quit instead of going to the tray.</summary>
    public static bool IsExiting { get; private set; }

    public static void ExitApp()
    {
        IsExiting = true;
        Current.Shutdown();
    }

    /// <summary>Quits and starts again (after restoring a backup): the new copy waits until this one has gone.</summary>
    public static void Restart()
    {
        System.Diagnostics.Process.Start(Environment.ProcessPath!, $"--after {Environment.ProcessId}");
        ExitApp();
    }

    /// <summary>For dev tooling only (screenshots); normal code gets dependencies by constructor injection.</summary>
    internal static IServiceProvider? Services { get; private set; }

    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plicbox");

    /// <summary>
    /// Files only this copy uses (the message view's browser data, pages being shown). A dev build shares the
    /// installed app's mail and settings and runs beside it, so it keeps these apart.
    /// </summary>
    public static string OwnDir { get; } = Updates.IsInstalled ? DataDir : Path.Combine(DataDir, "dev");

    /// <summary>
    /// Everything you click shows the hand, as the app's own buttons do: the plain Windows buttons (Cancel, Discard,
    /// Add…), checkboxes, radio buttons and dropdowns too. Done as the controls' default cursor rather than with styles,
    /// so they keep the Fluent look (a style based on theirs, set up in App.xaml, gets the classic one). A style that
    /// sets its own cursor still wins (the window's caption buttons keep the arrow).
    /// </summary>
    private static void HandCursors()
    {
        foreach (var type in new[] { typeof(System.Windows.Controls.Button), typeof(System.Windows.Controls.Primitives.ToggleButton), typeof(System.Windows.Controls.ComboBox) })
            FrameworkElement.CursorProperty.OverrideMetadata(type, new FrameworkPropertyMetadata(System.Windows.Input.Cursors.Hand));
    }

    // The language is set before anything is built, App.xaml's own resources included (it changes on restart).
    // Demo and dev modes start from fresh settings, so they're in the default language unless --lang says otherwise.
    static App()
    {
        HandCursors();
        var args = Environment.GetCommandLineArgs();
        var sandboxed = args.Contains("--demo");
#if DEBUG
        sandboxed |= args.Contains(DevGreenMail.Flag);
#endif
        var langIndex = Array.IndexOf(args, "--lang");
        L.Apply(langIndex >= 0 && langIndex + 1 < args.Length ? args[langIndex + 1]
            : sandboxed ? new AppSettings().Language : new JsonSettingsStore(JsonSettingsStore.DefaultPath).Current.Language);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var demo = e.Args.Contains("--demo");
#if DEBUG
        var devGreenMail = e.Args.Contains(DevGreenMail.Flag);
#else
        const bool devGreenMail = false;
#endif
        // Demo and dev modes get their own throwaway data so they never mix with real mail.
        var sandbox = demo ? "demo" : devGreenMail ? "dev-greenmail" : null;
        var shotIndex = Array.IndexOf(e.Args, "--screenshot");
        var screenshotPath = shotIndex >= 0 && shotIndex + 1 < e.Args.Length ? e.Args[shotIndex + 1] : null;
        var composeShotIndex = Array.IndexOf(e.Args, "--screenshot-compose");
        var composeShotPath = composeShotIndex >= 0 && composeShotIndex + 1 < e.Args.Length ? e.Args[composeShotIndex + 1] : null;
        var calendarShotIndex = Array.IndexOf(e.Args, "--screenshot-calendar");
        var calendarShotDir = calendarShotIndex >= 0 && calendarShotIndex + 1 < e.Args.Length ? e.Args[calendarShotIndex + 1] : null;
        var tasksShotIndex = Array.IndexOf(e.Args, "--screenshot-tasks");
        var tasksShotDir = tasksShotIndex >= 0 && tasksShotIndex + 1 < e.Args.Length ? e.Args[tasksShotIndex + 1] : null;
        var dialogShotIndex = Array.IndexOf(e.Args, "--screenshot-dialogs");
        var dialogShotDir = dialogShotIndex >= 0 && dialogShotIndex + 1 < e.Args.Length ? e.Args[dialogShotIndex + 1] : null;
        var background = e.Args.Contains("--background"); // started by Windows at sign-in
#if DEBUG
        // Dev: open a copy of a real database without syncing, e.g. to measure UI speed (--dev-db <file> [--dev-perf]).
        var devDbIndex = Array.IndexOf(e.Args, "--dev-db");
        var devDb = devDbIndex >= 0 && devDbIndex + 1 < e.Args.Length ? e.Args[devDbIndex + 1] : null;
#else
        string? devDb = null;
#endif
        var devPerf = devDb is not null && e.Args.Contains("--dev-perf");

        // One Plicbox per data folder: a second launch just brings the running one to the front. A dev build is a
        // separate one, so it can run while the installed Plicbox does.
        var instanceName = "Plicbox-" + (devDb is not null ? "devdb" : sandbox ?? (Updates.IsInstalled ? "main" : "dev"));
        // Restarted by the app itself: the old copy is still closing down.
        var afterIndex = Array.IndexOf(e.Args, "--after");
        if (afterIndex >= 0 && afterIndex + 1 < e.Args.Length && int.TryParse(e.Args[afterIndex + 1], out var previous))
        {
            try { System.Diagnostics.Process.GetProcessById(previous).WaitForExit(TimeSpan.FromSeconds(30)); }
            catch (ArgumentException) { } // already gone
        }
        _singleInstance = new Mutex(initiallyOwned: true, instanceName, out var isFirst);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, instanceName + "-show");
        if (!isFirst)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning) // HttpClient chatter
            .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(DataDir, "logs", Updates.IsInstalled ? "plicbox-.log" : "plicbox-dev-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        DispatcherUnhandledException += OnUnhandledException;
        // Big messages are shown from temporary files; anything an earlier run left behind goes (this is the only instance).
        if (devDb is null && sandbox is null) _ = Task.Run(MailWebView.CleanUpRenderFiles);

        var dbPath = devDb ?? (sandbox is null ? Database.DefaultPath : Path.Combine(DataDir, sandbox, "plicbox.db"));
        var bodyRoot = devDb is not null ? Path.Combine(Path.GetDirectoryName(devDb)!, "dev-bodies")
            : sandbox is null ? FileBodyStore.DefaultRoot : Path.Combine(DataDir, sandbox, "bodies");
        if (sandbox is not null && Directory.Exists(Path.Combine(DataDir, sandbox)))
            Directory.Delete(Path.Combine(DataDir, sandbox), recursive: true);
        // A backup chosen under Settings → Backup takes the place of this computer's data before anything opens it.
        if (sandbox is null && devDb is null)
        {
            try { if (Backups.ApplyStaged(DataDir, dbPath, JsonSettingsStore.DefaultPath, bodyRoot)) Log.Information("Restored a backup"); }
            catch (Exception ex) { Log.Error(ex, "Restoring the backup failed"); }
        }
        var settingsPath = sandbox is null ? JsonSettingsStore.DefaultPath : Path.Combine(DataDir, sandbox, "settings.json");

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog();
        builder.Services.AddSingleton(new Database(dbPath));
        builder.Services.AddSingleton<IBodyStore>(new FileBodyStore(bodyRoot));
        builder.Services.AddSingleton<IAccountRepository, AccountRepository>();
        builder.Services.AddSingleton<IFolderRepository, FolderRepository>();
        builder.Services.AddSingleton<IMessageRepository, MessageRepository>();
        builder.Services.AddSingleton<IPendingOpRepository, PendingOpRepository>();
        builder.Services.AddSingleton<ITrustedSenders, TrustedSenders>();
#if DEBUG
        if (devGreenMail) builder.Services.AddSingleton<ICredentialStore>(new DevGreenMail.MemoryCredentials());
        else
#endif
        builder.Services.AddSingleton<ICredentialStore>(new WindowsCredentialStore(demo ? "Plicbox-demo/account/" : "Plicbox/account/",
            formerPrefix: demo ? null : "Mailroom/account/")); // sign-ins saved before the rename
        builder.Services.AddSingleton(sp => new GoogleAuth(sp.GetRequiredService<ICredentialStore>(), GoogleAuth.DefaultClientFilePath));
        builder.Services.AddSingleton(sp => new MicrosoftAuth(sp.GetRequiredService<ICredentialStore>(),
            sandbox is null ? MicrosoftAuth.DefaultCacheFilePath : Path.Combine(DataDir, sandbox, "microsoft-tokens.bin")));
        builder.Services.AddSingleton<MailConnector>();
        builder.Services.AddSingleton<MailEngine>();
        builder.Services.AddHttpClient<AccountDiscovery>();
        builder.Services.AddSingleton<MessageRenderer>();
        builder.Services.AddSingleton<Dialogs>();
        builder.Services.AddSingleton<ISettingsStore>(new JsonSettingsStore(settingsPath));
        builder.Services.AddSingleton(sp => new BackupService(sp.GetRequiredService<Database>(), sp.GetRequiredService<ISettingsStore>(), settingsPath,
            sandbox is null ? DataDir : Path.Combine(DataDir, sandbox), sp.GetRequiredService<ILogger<BackupService>>()));
        builder.Services.AddSingleton(_tray); // instance registration: the container won't dispose it
        builder.Services.AddHttpClient<GmailAliases>();
        // Sender pictures: one cache for real use and dev databases; the demo keeps its own (and wipes it each start).
        var pictureDir = Path.Combine(DataDir, sandbox ?? "", "pictures");
        builder.Services.AddHttpClient(nameof(SenderPictures));
        builder.Services.AddSingleton(sp => new SenderPictures(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(nameof(SenderPictures)), Path.Combine(pictureDir, "senders"),
            sp.GetRequiredService<ILogger<SenderPictures>>()));
        builder.Services.AddSingleton(sp => new Pictures(sp.GetRequiredService<SenderPictures>(), sp.GetRequiredService<ISettingsStore>(), pictureDir));
        // Phishing and malware lists: their own folder, shared by the real app and dev runs (they're public data).
        builder.Services.AddHttpClient(nameof(ThreatListUpdater), http =>
            {
                http.Timeout = TimeSpan.FromMinutes(2);
                http.DefaultRequestHeaders.UserAgent.ParseAdd("Plicbox");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All });
        builder.Services.AddSingleton(sp => new ThreatListUpdater(
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(nameof(ThreatListUpdater)), Path.Combine(DataDir, "threat-lists"),
            sp.GetRequiredService<ILogger<ThreatListUpdater>>()));
        builder.Services.AddTransient<SettingsWindow>();
        builder.Services.AddSingleton<DemoData>();
        builder.Services.AddSingleton<ICalendarRepository, CalendarRepository>();
        builder.Services.AddHttpClient<GoogleCalendarApi>();
        builder.Services.AddSingleton<CalendarEngine>();
        builder.Services.AddSingleton<CalendarViewModel>();
        builder.Services.AddSingleton<IContactRepository, ContactRepository>();
        builder.Services.AddHttpClient<GoogleContactsApi>();
        builder.Services.AddSingleton<ContactsEngine>();
        builder.Services.AddSingleton<ContactsViewModel>();
        builder.Services.AddSingleton<ITaskRepository, TaskRepository>();
        builder.Services.AddHttpClient<GoogleTasksApi>();
        builder.Services.AddSingleton<TasksEngine>();
        builder.Services.AddSingleton<TasksViewModel>();
        builder.Services.AddSingleton<Updates>();
        builder.Services.AddSingleton<PeopleData>();
        builder.Services.AddSingleton<AddressBook>();
        builder.Services.AddTransient<MessageReader>(); // one per reading pane and message window
        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();
        builder.Services.AddTransient<AccountSetupWindow>();
        builder.Services.AddTransient<AccountsViewModel>();
        builder.Services.AddTransient<AccountsWindow>();
        builder.Services.AddTransient<RulesViewModel>();
        _host = builder.Build();
        Services = _host.Services;

        try
        {
            _host.Services.GetRequiredService<Database>().Migrate();
            if (demo)
            {
                await _host.Services.GetRequiredService<DemoData>().SeedAsync();
                // The website's pictures show the same week whatever day they're made: events from this week's Tuesday on.
                var forSite = e.Args.Contains("--record-site") || e.Args.Contains("--screenshot-site");
                var tuesday = DateOnly.FromDateTime(DateTime.Today.AddDays(1 - ((int)DateTime.Today.DayOfWeek + 6) % 7));
                await DemoCalendar.SeedAsync(_host.Services.GetRequiredService<IAccountRepository>(), _host.Services.GetRequiredService<ICalendarRepository>(),
                    forSite ? tuesday : null);
                await DemoContacts.SeedAsync(_host.Services.GetRequiredService<IAccountRepository>(), _host.Services.GetRequiredService<IContactRepository>());
                await DemoTasks.SeedAsync(_host.Services.GetRequiredService<IAccountRepository>(), _host.Services.GetRequiredService<ITaskRepository>(),
                    _host.Services.GetRequiredService<IMessageRepository>());
            }

            // Settings → Appearance (accent, font, message size): now, and whenever settings change.
            var settingsStore = _host.Services.GetRequiredService<ISettingsStore>();
            Theme.ForceDark = e.Args.Contains("--dark"); // dev aid: screenshots in dark mode
            MessageDialog.Unattended = e.Args.Any(a => a.StartsWith("--screenshot", StringComparison.Ordinal) || a.StartsWith("--dev-", StringComparison.Ordinal) || a == "--measure");
            Appearance.Apply(settingsStore.Current);
            SolidWindows.Register(); // no see-through backdrop, title bars in the app's colours
            RenderRecovery.Register(Dispatcher); // a fresh drawing surface after sleep, unlock or a display change
            settingsStore.Changed += s => Dispatcher.BeginInvoke(() => Appearance.Apply(s));
            Appearance.WatchWindows(() => settingsStore.Current);
            // The startup entry follows the installed app's own path (it changes on reinstall; a dev build may have
            // changed the setting meanwhile).
            try { StartupRegistration.Set(settingsStore.Current.RunOnStartup); }
            catch (Exception ex) { Log.Warning(ex, "Couldn't update the startup entry"); }
            var window = _host.Services.GetRequiredService<MainWindow>();
            window.ScreenshotPath = screenshotPath;
            window.ComposeScreenshotPath = composeShotPath;
            window.DialogScreenshotDir = dialogShotDir;
            var printShotIndex = Array.IndexOf(e.Args, "--screenshot-print");
            window.PrintScreenshotDir = printShotIndex >= 0 && printShotIndex + 1 < e.Args.Length ? e.Args[printShotIndex + 1] : null;
            window.CalendarScreenshotDir = calendarShotDir;
            window.TasksScreenshotDir = tasksShotDir;
            var siteShotIndex = Array.IndexOf(e.Args, "--screenshot-site");
            window.SiteScreenshotDir = siteShotIndex >= 0 && siteShotIndex + 1 < e.Args.Length ? e.Args[siteShotIndex + 1] : null;
            var siteVideoIndex = Array.IndexOf(e.Args, "--record-site");
            window.SiteVideoPath = siteVideoIndex >= 0 && siteVideoIndex + 1 < e.Args.Length ? e.Args[siteVideoIndex + 1] : null;
            ViewModels.CalendarViewModel.DevShowQuickAdd = window.SiteVideoPath is not null || window.SiteScreenshotDir is not null;
            window.PerfRun = devPerf;
            ViewModels.MainViewModel.PerfTrace = devPerf;
            window.MemoryReport = devDb is not null && e.Args.Contains("--dev-memory") ? Path.Combine(Path.GetDirectoryName(devDb)!, "memory.txt") : null;
            var measureIndex = Array.IndexOf(e.Args, "--measure"); // only with the demo's data, never real mail
            window.MeasureReport = demo && measureIndex >= 0 && measureIndex + 1 < e.Args.Length ? e.Args[measureIndex + 1] : null;
            window.ScrollbarReport = devDb is not null && e.Args.Contains("--dev-scrollbars") ? Path.Combine(Path.GetDirectoryName(devDb)!, "scrollbars.txt") : null;
            MainWindow = window;
            RenderRecovery.Watch(window);
            window.Show();
            if (background) window.SendToBackground();
            ListenForSecondLaunch(window);
            await _host.Services.GetRequiredService<MainViewModel>().LoadAsync();
            // Demo accounts point at fake servers, so nothing syncs in demo mode.
            if (!demo && devDb is null)
            {
                await _host.Services.GetRequiredService<MailEngine>().StartAsync();
                WatchForWakeUp();
            }
            var calendarEngine = _host.Services.GetRequiredService<CalendarEngine>();
            if (demo || devDb is not null) calendarEngine.StartLocalOnly();
            else calendarEngine.Start();
            await _host.Services.GetRequiredService<CalendarViewModel>().LoadAsync();
            var contactsEngine = _host.Services.GetRequiredService<ContactsEngine>();
            if (demo || devDb is not null) contactsEngine.StartLocalOnly();
            else contactsEngine.Start();
            var tasksEngine = _host.Services.GetRequiredService<TasksEngine>();
            if (demo || devDb is not null) tasksEngine.StartLocalOnly();
            else tasksEngine.Start();
            // Get Contacts ready in the background (people data, the merged list), so opening it later and the
            // composer's address suggestions are instant.
            var contacts = _host.Services.GetRequiredService<ContactsViewModel>();
            _ = WarmUpAsync();
            if (!demo && devDb is null)
            {
                _host.Services.GetRequiredService<Updates>().Start();
                _host.Services.GetRequiredService<BackupService>().Start();
                // Reported phishing and malware sites: loaded now, kept up to date, and dropped when the setting goes off.
                var threatLists = _host.Services.GetRequiredService<ThreatListUpdater>();
                var listsOn = settingsStore.Current.ThreatLists;
                threatLists.SetEnabled(listsOn);
                settingsStore.Changed += s =>
                {
                    if (s.ThreatLists == listsOn) return;
                    listsOn = s.ThreatLists;
                    threatLists.SetEnabled(listsOn);
                };
            }
            async Task WarmUpAsync()
            {
                await Task.Delay(4000);
                await contacts.LoadAsync();
                await _host.Services.GetRequiredService<TasksViewModel>().LoadAsync();
            }
#if DEBUG
            if (devGreenMail)
                await DevGreenMail.SeedAsync(_host.Services.GetRequiredService<MailEngine>(), _host.Services.GetRequiredService<IAccountRepository>());
#endif
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            MessageDialog.Show(null, L.T("Plicbox couldn't start"), ex.Message, DialogKind.Danger);
            Shutdown(1);
        }
    }

    private CancellationTokenSource? _wakeDelay;

    /// <summary>
    /// After sleep or when the network comes back, connections left open are usually dead without knowing it:
    /// every account reconnects and catches up (a few seconds later, once the network is really there).
    /// </summary>
    private void WatchForWakeUp()
    {
        Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == Microsoft.Win32.PowerModes.Resume) CatchUpSoon("resumed from sleep");
        };
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) =>
        {
            if (e.IsAvailable) CatchUpSoon("network available");
        };
    }

    private void CatchUpSoon(string why)
    {
        var cts = new CancellationTokenSource();
        Interlocked.Exchange(ref _wakeDelay, cts)?.Cancel();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(TimeSpan.FromSeconds(4), cts.Token); }
            catch (OperationCanceledException) { return; }
            if (_host is null || IsExiting) return;
            Log.Information("Reconnecting: {Why}", why);
            _host.Services.GetRequiredService<MailEngine>().ReconnectAll();
            _host.Services.GetRequiredService<CalendarEngine>().RequestSync();
            _host.Services.GetRequiredService<ContactsEngine>().RequestSync();
            _host.Services.GetRequiredService<TasksEngine>().RequestSync();
        });
    }

    /// <summary>Waits (off the UI thread) for another launch to ask this instance to show itself.</summary>
    private void ListenForSecondLaunch(MainWindow window)
    {
        var signal = _showSignal!;
        var thread = new Thread(() =>
        {
            while (signal.WaitOne())
                Dispatcher.BeginInvoke(window.ShowFromTray);
        }) { IsBackground = true, Name = "Plicbox single-instance listener" };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        Log.Information("Shutting down");
        _host?.Services.GetService<Updates>()?.InstallOnExit(); // the installer waits for us to finish quitting
        _tray.Dispose();
        // Host disposal is async underneath; blocking on it from the UI thread would deadlock the sync workers'
        // continuations, so dispose on the thread pool and give up after a few seconds.
        try
        {
            if (_host is not null && !Task.Run(() => ((IAsyncDisposable)_host).DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(5)))
                Log.Warning("Shutdown timed out");
        }
        catch (AggregateException ex)
        {
            Log.Warning(ex, "Error while shutting down"); // never bother the user on the way out
        }
        Log.Information("Shutdown complete");
        Log.CloseAndFlush();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Unhandled UI exception");
        MessageDialog.Show(null, L.T("Something went wrong"), e.Exception.Message, DialogKind.Warning);
        e.Handled = true;
    }
}
