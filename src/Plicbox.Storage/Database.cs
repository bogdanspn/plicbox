using Microsoft.Data.Sqlite;

namespace Plicbox.Storage;

/// <summary>Owns the SQLite file location and applies schema migrations via PRAGMA user_version.</summary>
public sealed class Database
{
    private readonly string _connectionString;

    /// <summary>The database file.</summary>
    public string FilePath { get; }

    public Database(string dbPath)
    {
        FilePath = Path.GetFullPath(dbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            ForeignKeys = true,
            Pooling = true,
        }.ToString();
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Plicbox", "plicbox.db");

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        // For sorting the message list in the database the way the app sorts text: by the user's language, ignoring
        // case ("ui"), and subjects without their "Re:"/"Fwd:" ("subject_key").
        conn.CreateCollation("ui", (a, b) => string.Compare(a, b, System.Globalization.CultureInfo.CurrentCulture,
            System.Globalization.CompareOptions.IgnoreCase));
        conn.CreateFunction("subject_key", (string? subject) => Plicbox.Core.Subjects.WithoutReplyPrefix(subject ?? ""), isDeterministic: true);
        return conn;
    }

    public void Migrate()
    {
        using var conn = Open();
        Exec(conn, "PRAGMA journal_mode = WAL;");
        var version = Convert.ToInt32(new SqliteCommand("PRAGMA user_version;", conn).ExecuteScalar());
        if (version >= Migrations.All.Length) return;

        // Off while migrating (it can't change inside a transaction): a migration that rebuilds a table drops the old
        // one, and with foreign keys on that would delete every row pointing at it.
        Exec(conn, "PRAGMA foreign_keys = OFF;");
        try
        {
            for (var i = version; i < Migrations.All.Length; i++)
            {
                using var tx = conn.BeginTransaction();
                Exec(conn, Migrations.All[i], tx);
                Exec(conn, $"PRAGMA user_version = {i + 1};", tx);
                tx.Commit();
            }
        }
        finally
        {
            Exec(conn, "PRAGMA foreign_keys = ON;"); // the connection goes back to the pool
        }
    }

    private static void Exec(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
