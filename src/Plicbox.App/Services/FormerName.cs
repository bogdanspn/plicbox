using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Plicbox.App.Services;

/// <summary>
/// Plicbox used to be called Mailroom, and its data folder, database and startup entry carried that name. The first
/// start after the rename moves them over (saved sign-ins follow on their own, see WindowsCredentialStore).
/// </summary>
internal static class FormerName
{
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>
    /// Moves %LocalAppData%\Mailroom to %LocalAppData%\Plicbox. Runs before anything reads the data folder (App's own
    /// paths and settings included). False when it couldn't be moved: starting anyway would make an empty Plicbox
    /// folder, and the mail would never be moved over.
    /// </summary>
    public static bool MoveData()
    {
        var old = Path.Combine(Local, "Mailroom");
        var now = Path.Combine(Local, "Plicbox");
        if (Directory.Exists(now) || !Directory.Exists(old)) return true;
        try
        {
            Directory.Move(old, now);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"Plicbox (formerly Mailroom) needs to move its data to {now}, but the old folder is in use.\n\n" +
                "Quit Mailroom (tray icon → Exit) and start Plicbox again.\n\n" + ex.Message, "Plicbox", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var db = Path.Combine(now, "mailroom.db" + suffix);
            if (File.Exists(db)) File.Move(db, Path.Combine(now, "plicbox.db" + suffix));
        }
        // The old startup entry starts a program that no longer exists; Plicbox sets its own.
        using (var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            run?.DeleteValue("Mailroom", throwOnMissingValue: false);
        return true;
    }
}
