namespace Plicbox.Core;

// Stored as numbers: new kinds go at the end.
public enum ProviderKind { Imap, Gmail, ICloud, Yahoo, Microsoft }

public enum AuthKind { Password, GoogleOAuth, MicrosoftOAuth }

/// <summary>Outbox is kept on this computer only (mail waiting to go out); every other role is a server folder's.</summary>
public enum FolderRole { Other, Inbox, Sent, Drafts, Trash, Junk, Archive, All, Snoozed, Outbox }

/// <summary>Where a mail search looks: one account, one folder, or everywhere (both null).</summary>
/// <param name="IncludeTrashAndJunk">Searching a Trash or Junk folder itself always includes it.</param>
public sealed record SearchScope(long? AccountId = null, long? FolderId = null, bool IncludeTrashAndJunk = true);

public enum ListSortKey { Date, Sender, Subject, Flag, ReadStatus, Attachments }

/// <summary>
/// The message list's order, worked out by the database so the list can load a page at a time in any order.
/// Descending means: newest, Z to A, flagged, unread or with attachments first. Ties go newest first.
/// </summary>
public sealed record ListSort(ListSortKey Key = ListSortKey.Date, bool Descending = true);

public static class FolderNames
{
    /// <summary>The name shown for a folder: special folders by their role, in the app's language; others as on the server.</summary>
    public static string Display(FolderRole role, string serverName) => role switch
    {
        FolderRole.Inbox => L.T("Inbox"),
        FolderRole.Sent => L.T("Sent"),
        FolderRole.Drafts => L.T("Drafts"),
        FolderRole.Trash => L.T("Trash"),
        FolderRole.Junk => L.T("Junk Email"),
        FolderRole.Archive => L.T("Archive"),
        FolderRole.All => L.T("All Mail"),
        FolderRole.Outbox => L.T("Outbox"),
        _ => serverName,
    };
}

public sealed record Account
{
    public long Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Email { get; init; }
    public ProviderKind Provider { get; init; }
    public AuthKind Auth { get; init; }
    public required string Username { get; init; }
    public required string ImapHost { get; init; }
    public int ImapPort { get; init; } = 993;
    public required string SmtpHost { get; init; }
    public int SmtpPort { get; init; } = 465;
    public string? DavUrl { get; init; }
    public string Color { get; init; } = "#3B82F6";
    /// <summary>Signature: plain text, or HTML if it contains markup.</summary>
    public string? Signature { get; init; }
    /// <summary>Name shown in the sidebar; defaults to the address.</summary>
    public string? Label { get; init; }
    public int SortOrder { get; init; }
    public bool IsDefault { get; init; }
    /// <summary>Always Bcc this address on outgoing mail (e.g. an archive mailbox).</summary>
    public string? BccAddress { get; init; }

    public TlsMode ImapSecurity { get; init; }
    public TlsMode SmtpSecurity { get; init; }
    /// <summary>Outgoing sign-in; with <see cref="SmtpAuthMode.Separate"/> its password is kept apart from the incoming one.</summary>
    public SmtpAuthMode SmtpAuth { get; init; }
    /// <summary>The outgoing user name when <see cref="SmtpAuth"/> is Separate.</summary>
    public string? SmtpUsername { get; init; }
    /// <summary>Only mail received in the last N days is synced; 0 = all time.</summary>
    public int SyncPeriodDays { get; init; }
    /// <summary>Download full messages in the background so they open instantly, offline too, and search covers their text.</summary>
    public bool OfflineDownload { get; init; } = true;
    /// <summary>How far back <see cref="OfflineDownload"/> goes, in days; 0 = everything that's synced.</summary>
    public int OfflineDays { get; init; } = 365;
    /// <summary>JSON: extra From identities. Use <see cref="Aliases"/>.</summary>
    public string? AliasesJson { get; init; }
    /// <summary>JSON: role -> server path, overriding automatic special-folder detection. Use <see cref="FolderOverrides"/>.</summary>
    public string? FolderOverridesJson { get; init; }
    /// <summary>JSON: server paths (and their subfolders) that are neither shown nor synced. Use <see cref="IgnoredPaths"/>.</summary>
    public string? IgnoredPathsJson { get; init; }

    public IReadOnlyList<MailAlias> Aliases => AccountJson.Aliases(AliasesJson);
    public IReadOnlyDictionary<FolderRole, string> FolderOverrides => AccountJson.FolderOverrides(FolderOverridesJson);
    public IReadOnlyList<string> IgnoredPaths => AccountJson.Paths(IgnoredPathsJson);

    /// <summary>The account's own address first, then its aliases.</summary>
    public IReadOnlyList<MailAlias> Identities =>
        [new MailAlias(DisplayName, Email), .. Aliases.Where(a => !a.Address.Equals(Email, StringComparison.OrdinalIgnoreCase))];

    /// <summary>What new mail is sent from: the alias chosen for that, else the account's own address.</summary>
    public MailAlias DefaultIdentity => Identities.FirstOrDefault(i => i.IsDefault) ?? Identities[0];

    public string SidebarTitle => string.IsNullOrWhiteSpace(Label) ? Email : Label;
}

public sealed record Folder
{
    /// <summary>The Outbox's path: no server folder is ever called this, so the two can't meet.</summary>
    public const string OutboxPath = "\u0001outbox";

    public long Id { get; init; }
    public long AccountId { get; init; }
    public required string Path { get; init; }
    public required string Name { get; init; }
    public FolderRole Role { get; init; }
    public long UidValidity { get; init; }
    public long HighestModSeq { get; init; }
    /// <summary>Highest server UID already downloaded; anything above it is new.</summary>
    public long LastUid { get; init; }
    /// <summary>Messages the server had in the folder at the last sync; -1 before the first.</summary>
    public long ServerCount { get; init; } = -1;
    /// <summary>False for folders synced only while the user has them open (Gmail's All Mail, Important).</summary>
    public bool IsSynced { get; init; } = true;
    /// <summary>A view over mail stored elsewhere (Gmail's All Mail, Important): kept out of counts and search duplicates.</summary>
    public bool IsVirtual { get; init; }
    public int UnreadCount { get; init; }
    public int TotalCount { get; init; }
}

/// <summary>
/// Append: a message kept here (sent mail filed in Sent) is uploaded to the folder it's in. SaveDraft: the same for a
/// draft, replacing its earlier copies (same Message-ID) in <see cref="PendingOp.FolderPath"/>. DeleteDraft: every copy
/// of a draft goes from there.
/// </summary>
public enum PendingOpKind { Flags = 1, Move = 2, Delete = 3, Copy = 4, Append = 5, SaveDraft = 6, DeleteDraft = 7 }

/// <summary>
/// A local change waiting to be applied on the server. Move/Delete capture the server location at the time of the
/// action (the local row already moved); Flags resolve the message's current location when they run.
/// </summary>
public sealed record PendingOp
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public PendingOpKind Kind { get; init; }
    public long MessageId { get; init; }
    public string? FolderPath { get; init; }
    public long Uid { get; init; }
    public string? TargetPath { get; init; }
    public int Attempts { get; init; }
    /// <summary>SaveDraft and DeleteDraft: the draft's Message-ID header, which finds its copies on the server.</summary>
    public string? HeaderMessageId { get; init; }
}

public sealed record MessageSummary
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    public long FolderId { get; init; }
    public long Uid { get; init; }
    public string? MessageId { get; init; }
    public string? InReplyTo { get; init; }
    public string? ThreadKey { get; init; }
    public string Subject { get; init; } = "";
    public string FromName { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public string To { get; init; } = "";
    /// <summary>To and Cc as addresses ("Ana Popescu &lt;ana@x.com&gt;, b@y.org"); null for mail synced before this was kept.</summary>
    public string? Recipients { get; init; }
    public DateTimeOffset Date { get; init; }
    public string Preview { get; init; } = "";
    public bool IsRead { get; init; }
    public bool IsFlagged { get; init; }
    public bool HasAttachments { get; init; }
    public long Size { get; init; }
    /// <summary>Gmail's "Important" label (a small amber dot in the list).</summary>
    public bool IsImportant { get; init; }
}
