using System.Text.Json;

namespace Plicbox.Core;

/// <summary>How a connection is encrypted. Auto = implicit TLS on 993/465, STARTTLS elsewhere. Plaintext is never offered.</summary>
public enum TlsMode { Auto = 0, Tls = 1, StartTls = 2 }

/// <summary>How the outgoing server is signed in to: like the incoming one, with its own login, or not at all.</summary>
public enum SmtpAuthMode { SameAsIncoming = 0, Separate = 1, None = 2 }

/// <summary>An extra "From" identity for an account (e.g. Gmail "Send mail as" addresses).</summary>
public sealed record MailAlias(string Name, string Address)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Name) ? Address : $"{Name} <{Address}>";
}

/// <summary>JSON helpers for the list-valued account settings stored in single columns.</summary>
public static class AccountJson
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<MailAlias> Aliases(string? json) => Read<List<MailAlias>>(json) ?? [];
    public static string Aliases(IEnumerable<MailAlias> aliases) => JsonSerializer.Serialize(aliases.ToList(), Options);

    public static IReadOnlyList<string> Paths(string? json) => Read<List<string>>(json) ?? [];
    public static string Paths(IEnumerable<string> paths) => JsonSerializer.Serialize(paths.ToList(), Options);

    public static IReadOnlyDictionary<FolderRole, string> FolderOverrides(string? json) =>
        Read<Dictionary<FolderRole, string>>(json) ?? [];
    public static string FolderOverrides(IReadOnlyDictionary<FolderRole, string> map) => JsonSerializer.Serialize(map, Options);

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return null; } // a corrupt setting shouldn't stop the account from working
    }
}

/// <summary>App-wide preferences (Settings window). Stored as JSON next to the database.</summary>
public sealed record AppSettings
{
    public bool MinimizeToTray { get; init; }
    public bool CloseToTray { get; init; } = true;
    public bool RunOnStartup { get; init; }
    /// <summary>Check for new versions on its own and download them (they install when Plicbox restarts).</summary>
    public bool AutoUpdate { get; init; } = true;
    public bool NotifyNewMail { get; init; } = true;
    public int SyncIntervalMinutes { get; init; } = 5;
    /// <summary>"unified" for All Inboxes, or "inbox:{accountId}" for one account's inbox.</summary>
    public string StartupView { get; init; } = "unified";
    /// <summary>Message list order: Received, From, Subject, Flag or Attachments.</summary>
    public string MessageSort { get; init; } = "Received";
    public bool MessageSortDescending { get; init; } = true;
    /// <summary>Sidebar-only folder preferences, keyed by <see cref="FolderPrefs.Key"/>. Nothing here touches the server.</summary>
    public Dictionary<string, FolderPrefs> Folders { get; init; } = [];
    /// <summary>Look up senders' pictures online (Gravatar, verified brand logos). Off = coloured letters only.</summary>
    public bool SenderPictures { get; init; } = true;
    public CalendarPrefs Calendar { get; init; } = new();
    /// <summary>Addresses ("a@x.com") and domains ("@x.com") whose new mail is moved away as it arrives.</summary>
    public List<string> BlockedSenders { get; init; } = [];
    /// <summary>Where blocked senders' mail goes: Trash instead of Junk.</summary>
    public bool BlockedToTrash { get; init; }
    /// <summary>
    /// Download public lists of reported phishing and malware sites twice a day, and check senders and links against
    /// them here (nothing about the mail is sent anywhere).
    /// </summary>
    public bool ThreatLists { get; init; } = true;
    /// <summary>Mail rules, in the order they run.</summary>
    public List<MailRule> Rules { get; init; } = [];
    /// <summary>The left column's width, the same in every module (changed by dragging its edge).</summary>
    public double SidebarWidth { get; init; } = 300;
    /// <summary>The sidebar is folded down to a strip of icons (dragged narrow); <see cref="SidebarWidth"/> is the width it opens to.</summary>
    public bool SidebarCollapsed { get; init; }
    /// <summary>The app's language: "system" (the Windows display language), or a code from L.Languages ("en", "de", "ro", "es"). Applies on restart.</summary>
    public string Language { get; init; } = "en";
    /// <summary>Appearance: "system" (follow Windows), "light" or "dark".</summary>
    public string Theme { get; init; } = "system";
    /// <summary>Appearance: the accent colour (New, Send, Save…), "#RRGGBB".</summary>
    public string AccentColor { get; init; } = "#F7931E";
    /// <summary>Appearance: links, selections, counts and the unread dot in the accent colour too (in a readable shade), not blue.</summary>
    public bool AccentHighlights { get; init; } = true;
    /// <summary>Appearance: in dark mode, plain messages (no colours or pictures of their own) are shown dark too.</summary>
    public bool DarkMessages { get; init; } = true;
    /// <summary>Appearance: the app's font; null for Segoe UI.</summary>
    public string? UiFont { get; init; }
    /// <summary>Appearance: how large messages are shown (1 = 100%).</summary>
    public double MessageZoom { get; init; } = 1.0;
    /// <summary>
    /// Appearance → Reading pane: "bottom" (under the list), "right" (beside it), "full" (the list fills the area and a
    /// message opens in its place) or "popout" (no reading pane: messages open in their own window).
    /// </summary>
    public string ReadingPane { get; init; } = "bottom";
    /// <summary>Appearance: the font for mail that doesn't choose its own (plain text); null for Segoe UI.</summary>
    public string? MessageFont { get; init; }
    /// <summary>Appearance: the text size for that mail, in pixels (15 by default).</summary>
    public double MessageTextSize { get; init; } = 15;
    /// <summary>The notices above a message (tracking, unsubscribe, pictures) as one row of small chips, rather than full sentences.</summary>
    public bool CompactNotices { get; init; } = true;
    /// <summary>Seconds a message stays open before it counts as read; 0 = right away, below 0 = never automatically.</summary>
    public double MarkReadAfterSeconds { get; init; } = 1.5;
    /// <summary>Seconds a sent message waits in the Outbox, so Undo can take it back; 0 = it goes at once.</summary>
    public int UndoSendSeconds { get; init; } = 30;
    /// <summary>Back up what's kept only on this computer (calendars, contacts, tasks, settings) once a day.</summary>
    public bool BackupDaily { get; init; } = true;
    /// <summary>Where backups go; null for the default (OneDrive when set up, else Documents).</summary>
    public string? BackupFolder { get; init; }
    /// <summary>When the last backup was made.</summary>
    public DateTimeOffset? LastBackup { get; init; }
}

/// <summary>Calendar preferences (Settings → Calendar).</summary>
public sealed record CalendarPrefs
{
    public DayOfWeek FirstDayOfWeek { get; init; } = System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
    public List<DayOfWeek> WorkDays { get; init; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public int WorkDayStartHour { get; init; } = 8;
    public int WorkDayEndHour { get; init; } = 18;
    public bool ShowWeekNumbers { get; init; } = true;
    /// <summary>Grid rows: 15, 30 or 60 minutes; also what dragging snaps to.</summary>
    public int SlotMinutes { get; init; } = 30;
    public int DefaultDurationMinutes { get; init; } = 60;
    /// <summary>Reminder for new events, in minutes before; null = none.</summary>
    public int? DefaultReminderMinutes { get; init; } = 10;
    public bool NotifyReminders { get; init; } = true;
    /// <summary>Last view used: Day, WorkWeek, Week, Month or Agenda.</summary>
    public string View { get; init; } = "Week";
    /// <summary>The sidebar's small month folded down to its heading.</summary>
    public bool MiniMonthCollapsed { get; init; }
}

public enum FolderVisibility { Show, ShowIfUnread, Hide }

/// <summary>How a folder appears in the sidebar (eM Client's Show/Hide, Favorites and Folder Color).</summary>
public sealed record FolderPrefs
{
    public FolderVisibility Visibility { get; init; }
    public bool Favorite { get; init; }
    /// <summary>"#RRGGBB" for the folder icon, or null for the default.</summary>
    public string? Color { get; init; }

    public bool IsDefault => Visibility == FolderVisibility.Show && !Favorite && Color is null;

    /// <summary>Folders are identified by account and server path, so preferences survive a database rebuild.</summary>
    public static string Key(long accountId, string path) => $"{accountId}:{path}";
}

public interface ISettingsStore
{
    AppSettings Current { get; }
    void Save(AppSettings settings);
    event Action<AppSettings>? Changed;
}
