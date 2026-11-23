using Plicbox.Core;

namespace Plicbox.App;

/// <summary>Segoe Fluent Icons code points used across the UI.</summary>
public static class Glyphs
{
    public const string Mail = "";
    public const string MailOpen = "";
    public const string Star = "";
    public const string Flag = "";
    public const string Send = "";
    public const string Drafts = "";
    public const string Trash = "";
    public const string Junk = "";
    public const string Archive = "";
    public const string Folder = "";
    public const string AllMail = "";
    public const string Clock = "";
    public const string Person = "";
    /// <summary>An inbox tray, drawn by <see cref="GlyphIcon"/> (the font has none).</summary>
    public const string Inbox = GlyphIcon.MailInbox;
    /// <summary>A monitor: what's kept only on this computer (calendars and lists without an account).</summary>
    public const string Computer = "";
    /// <summary>An eye: Unread (as the read-status column in the message list).</summary>
    public const string Eye = "";
    public const string Gift = "";

    /// <summary>An arrow up out of a tray: the Outbox, mail waiting to go out.</summary>
    public const string Outbox = "";

    public const string More = "";

    public static string ForRole(FolderRole role) => role switch
    {
        FolderRole.Inbox => Inbox,
        FolderRole.Sent => Send,
        FolderRole.Drafts => Drafts,
        FolderRole.Trash => Trash,
        FolderRole.Junk => Junk,
        FolderRole.Archive => Archive,
        FolderRole.All => AllMail,
        FolderRole.Outbox => Outbox,
        FolderRole.Snoozed => Clock,
        _ => Folder,
    };
}
