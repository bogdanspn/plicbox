using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plicbox.Core;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Plicbox.App.Services;

/// <summary>
/// Keeps an installed Plicbox up to date from the public releases repo (its GitHub releases, read without a token).
/// Checks a minute after start and every few hours, downloads in the background (only what changed), and installs
/// when Plicbox quits, or right away with Restart. A build started from the source tree isn't installed and never updates.
/// </summary>
public sealed partial class Updates : ObservableObject
{
    public const string Feed = "https://github.com/bogdanspn/plicbox-releases";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1), CheckEvery = TimeSpan.FromHours(4);

    private static readonly UpdateManager? Manager = CreateManager();

    /// <summary>Running from Setup.exe's install; false for a dev build started from the source tree.</summary>
    public static bool IsInstalled => Manager is not null;

    /// <summary>"0.1.3", or the project's version marked as a dev build.</summary>
    public static string Version => Manager?.CurrentVersion?.ToString()
        ?? L.F("{0} (dev build)", Assembly.GetEntryAssembly()?.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?");

    private static UpdateManager? CreateManager()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(Feed, null, prerelease: false));
            return manager.IsInstalled ? manager : null;
        }
        catch (Exception)
        {
            return null; // not installed by Velopack (a dev build)
        }
    }

    private readonly ISettingsStore _settings;
    private readonly ILogger<Updates> _log;
    private VelopackAsset? _ready;
    private bool _checking, _installing;

    /// <summary>A new version is downloaded: it installs when Plicbox quits, or now with Restart.</summary>
    [ObservableProperty] private bool _isReady;
    /// <summary>What the last check found, for Settings.</summary>
    [ObservableProperty] private string _status = "";

    public Updates(ISettingsStore settings, ILogger<Updates> log)
    {
        (_settings, _log) = (settings, log);
        if (Manager?.UpdatePendingRestart is { } pending) Ready(pending); // downloaded last time, not installed yet
    }

    /// <summary>Starts the background checks (installed copies only; with automatic updates off, only Settings checks).</summary>
    public void Start()
    {
        if (IsInstalled) _ = CheckRegularlyAsync();
    }

    private async Task CheckRegularlyAsync()
    {
        await Task.Delay(FirstCheck);
        while (true)
        {
            if (_settings.Current.AutoUpdate && !IsReady) await CheckAsync(userAsked: false);
            await Task.Delay(CheckEvery);
        }
    }

    [RelayCommand]
    private Task Check() => CheckAsync(userAsked: true);

    private async Task CheckAsync(bool userAsked)
    {
        if (Manager is null || _checking || IsReady) return;
        _checking = true;
        Status = L.T("Checking for updates…");
        try
        {
            var update = await Task.Run(() => Manager.CheckForUpdatesAsync());
            if (update is null)
            {
                Status = L.F("Plicbox is up to date ({0}).", Version);
                return;
            }
            var version = update.TargetFullRelease.Version;
            Status = L.F("Downloading version {0}…", version);
            // How far it is, so a slow download doesn't look stuck (reported off the UI thread, now and then).
            var ui = SynchronizationContext.Current ?? new SynchronizationContext();
            var shown = -1;
            void Progress(int percent)
            {
                if (percent == Interlocked.Exchange(ref shown, percent)) return;
                ui.Post(_ => { if (!IsReady) Status = L.F("Downloading version {0}… {1} %", version, percent); }, null);
            }
            await Task.Run(() => Manager.DownloadUpdatesAsync(update, Progress));
            _log.LogInformation("Update {Version} downloaded", update.TargetFullRelease.Version);
            Ready(update.TargetFullRelease);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Checking for updates failed");
            Status = userAsked ? L.F("Couldn't check for updates: {0}", ex.Message) : "";
        }
        finally
        {
            _checking = false;
        }
    }

    private void Ready(VelopackAsset release)
    {
        _ready = release;
        IsReady = true;
        Status = L.F("Version {0} is ready. It installs when Plicbox restarts.", release.Version);
    }

    /// <summary>Quits (the usual way, so everything closes cleanly), installs the update and starts Plicbox again.</summary>
    [RelayCommand]
    private void Restart()
    {
        if (Manager is null || _ready is null) return;
        try
        {
            Manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: true);
            _installing = true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Couldn't start the update");
            Status = L.F("Couldn't install the update: {0}", ex.Message);
            return;
        }
        App.ExitApp();
    }

    /// <summary>Plicbox is quitting: a downloaded update goes in now, so the next start is the new version.</summary>
    public void InstallOnExit()
    {
        if (Manager is null || _ready is null || _installing) return;
        try { Manager.WaitExitThenApplyUpdates(_ready, silent: true, restart: false); }
        catch (Exception ex) { _log.LogWarning(ex, "Couldn't hand the update to the installer"); }
    }
}
