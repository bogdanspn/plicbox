using System.Globalization;
using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Plicbox.Core;

namespace Plicbox.Storage;

/// <summary>What a backup holds, for the "Restore?" question.</summary>
public sealed record BackupSummary(DateTime Made, int Accounts, int Calendars, int Events, int Contacts, int Tasks);

/// <summary>
/// Backups of everything that exists only on this computer: calendars, contacts and tasks kept here, accounts and
/// their settings, rules, signatures, preferences. A zip with the database (mail left out: it's on the mail servers and
/// downloads again) and settings.json. A restore is staged and swapped in when Plicbox next starts.
/// </summary>
public static class Backups
{
    private const string Prefix = "Plicbox backup ";
    private const string DbEntry = "plicbox.db", SettingsEntry = "settings.json";

    /// <summary>OneDrive when it's set up (so backups leave the computer), else Documents.</summary>
    public static string DefaultFolder =>
        Path.Combine(Environment.GetEnvironmentVariable("OneDrive") is { Length: > 0 } oneDrive && Directory.Exists(oneDrive)
            ? oneDrive : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Plicbox Backups");

    /// <summary>Writes a backup into <paramref name="folder"/>, keeping the newest <paramref name="keep"/>; returns its path.</summary>
    public static async Task<string> CreateAsync(Database db, string settingsPath, string folder, int keep = 10, CancellationToken ct = default)
    {
        Directory.CreateDirectory(folder);
        var work = Path.Combine(Path.GetTempPath(), "plicbox-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            await Task.Run(() =>
            {
                var copy = Path.Combine(work, DbEntry);
                using (var source = db.Open())
                using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString()))
                {
                    target.Open();
                    source.BackupDatabase(target);
                    // Mail is the servers' to keep: without it the backup is small, and the folders start afresh after a restore.
                    Exec(target, """
                        INSERT INTO messages_fts(messages_fts) VALUES('delete-all');
                        DELETE FROM pending_ops;
                        DELETE FROM messages;
                        UPDATE folders SET uid_validity = 0, last_uid = 0, highest_modseq = 0, server_count = -1;
                        """);
                    Exec(target, "PRAGMA journal_mode = DELETE;");
                    Exec(target, "VACUUM;");
                }
                if (File.Exists(settingsPath)) File.Copy(settingsPath, Path.Combine(work, SettingsEntry));
            }, ct);

            var name = Path.Combine(folder, Prefix + DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + ".zip");
            var partial = name + ".partial";
            File.Delete(partial);
            ZipFile.CreateFromDirectory(work, partial, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(partial, name, overwrite: true);

            // The newest few are plenty; names sort by date.
            foreach (var old in Directory.GetFiles(folder, Prefix + "*.zip").OrderByDescending(f => f, StringComparer.Ordinal).Skip(keep))
            {
                try { File.Delete(old); } catch (IOException) { }
            }
            return name;
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Checks a backup and stages it to replace this computer's data at the next start; what's in it.</summary>
    /// <exception cref="InvalidDataException">Not a Plicbox backup, or from a newer Plicbox.</exception>
    public static BackupSummary Stage(string zip, string dataDir)
    {
        var staged = StagingDir(dataDir);
        if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
        Directory.CreateDirectory(staged);
        try
        {
            DateTime made;
            using (var archive = ZipFile.OpenRead(zip))
            {
                var db = archive.GetEntry(DbEntry) ?? throw new InvalidDataException(L.T("That file isn't a Plicbox backup."));
                db.ExtractToFile(Path.Combine(staged, DbEntry));
                made = db.LastWriteTime.LocalDateTime;
                archive.GetEntry(SettingsEntry)?.ExtractToFile(Path.Combine(staged, SettingsEntry));
            }
            return Read(Path.Combine(staged, DbEntry), made);
        }
        catch
        {
            Directory.Delete(staged, recursive: true);
            throw;
        }
    }

    /// <summary>A staged restore that won't happen after all.</summary>
    public static void Unstage(string dataDir)
    {
        if (Directory.Exists(StagingDir(dataDir))) Directory.Delete(StagingDir(dataDir), recursive: true);
    }

    /// <summary>
    /// At start, before anything opens the database: swaps in a staged backup. The data it replaces is kept beside it
    /// (".before-restore"); downloaded mail goes, as the restored database doesn't know it. False when nothing was staged.
    /// </summary>
    public static bool ApplyStaged(string dataDir, string dbPath, string settingsPath, string bodiesRoot)
    {
        var staged = StagingDir(dataDir);
        var db = Path.Combine(staged, DbEntry);
        if (!File.Exists(db)) return false;

        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(dbPath + suffix)) File.Move(dbPath + suffix, dbPath + ".before-restore" + suffix, overwrite: true);
        File.Move(db, dbPath);
        if (File.Exists(Path.Combine(staged, SettingsEntry)))
        {
            if (File.Exists(settingsPath)) File.Move(settingsPath, settingsPath + ".before-restore", overwrite: true);
            File.Move(Path.Combine(staged, SettingsEntry), settingsPath);
        }
        if (Directory.Exists(bodiesRoot))
        {
            var old = bodiesRoot + ".old";
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
            Directory.Move(bodiesRoot, old);
            _ = Task.Run(() => { try { Directory.Delete(old, recursive: true); } catch (IOException) { } });
        }
        Directory.Delete(staged, recursive: true);
        return true;
    }

    private static string StagingDir(string dataDir) => Path.Combine(dataDir, "restore");

    private static BackupSummary Read(string dbFile, DateTime made)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbFile, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        conn.Open();
        if (Scalar(conn, "PRAGMA user_version;") > Migrations.All.Length)
            throw new InvalidDataException(L.T("That backup was made by a newer Plicbox. Update Plicbox first, then restore it."));
        int Count(string table) => Scalar(conn, $"SELECT COUNT(*) FROM {table};");
        try
        {
            return new BackupSummary(made, Count("accounts"), Count("calendars"), Count("events"), Count("contacts"), Count("tasks"));
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException(L.T("That file isn't a Plicbox backup."), ex);
        }
    }

    private static int Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
