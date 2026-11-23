using System.IO;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Plicbox.Core;
using Plicbox.Storage;

namespace Plicbox.App.Services;

/// <summary>
/// The daily backup of what exists only on this computer (see <see cref="Backups"/>), and Back up now / Restore in
/// Settings. Checked every half hour while Plicbox runs; a day after the last one, the next is made.
/// </summary>
public sealed class BackupService(Database db, ISettingsStore settings, string settingsPath, string dataDir, ILogger<BackupService> log)
{
    private readonly SemaphoreSlim _running = new(1, 1);
    private DispatcherTimer? _timer;

    /// <summary>A backup finished or failed (raised on a background thread).</summary>
    public event Action? Changed;

    public string Folder => settings.Current.BackupFolder is { Length: > 0 } chosen ? chosen : Backups.DefaultFolder;

    /// <summary>Why the last backup failed (the folder gone, a full disk…); null when it worked.</summary>
    public string? LastError { get; private set; }

    public bool IsRunning => _running.CurrentCount == 0;

    public void Start()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _timer.Tick += (_, _) => _ = BackUpIfDueAsync();
        _timer.Start();
        // Not in the busy first minutes after starting.
        _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => BackUpIfDueAsync(), TaskScheduler.Default);
    }

    private async Task BackUpIfDueAsync()
    {
        var s = settings.Current;
        if (!s.BackupDaily || s.LastBackup is { } last && DateTimeOffset.Now - last < TimeSpan.FromHours(23)) return;
        try { await BackUpNowAsync(); }
        catch (Exception) { } // logged, and shown in Settings
    }

    /// <summary>Makes a backup now; returns where it went.</summary>
    public async Task<string> BackUpNowAsync(string? folder = null)
    {
        await _running.WaitAsync();
        Changed?.Invoke();
        try
        {
            var path = await Backups.CreateAsync(db, settingsPath, folder ?? Folder);
            settings.Save(settings.Current with { LastBackup = DateTimeOffset.Now });
            LastError = null;
            log.LogInformation("Backed up to {Path}", path);
            return path;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            log.LogWarning(ex, "Backup failed");
            throw;
        }
        finally
        {
            _running.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Checks a backup and gets it ready to replace this computer's data when Plicbox restarts.</summary>
    public BackupSummary PrepareRestore(string zip) => Backups.Stage(zip, dataDir);

    public void CancelRestore() => Backups.Unstage(dataDir);
}
