using Plicbox.Core;
using MailKit;

namespace Plicbox.Mail;

public static class FolderRoles
{
    // Fallback names (English + German) for servers that don't advertise SPECIAL-USE.
    private static readonly Dictionary<string, FolderRole> KnownNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sent"] = FolderRole.Sent, ["Sent Items"] = FolderRole.Sent, ["Sent Messages"] = FolderRole.Sent, ["Sent Mail"] = FolderRole.Sent,
        ["Gesendet"] = FolderRole.Sent, ["Gesendete Elemente"] = FolderRole.Sent, ["Gesendete Objekte"] = FolderRole.Sent,
        ["Drafts"] = FolderRole.Drafts, ["Entwürfe"] = FolderRole.Drafts,
        ["Trash"] = FolderRole.Trash, ["Deleted Items"] = FolderRole.Trash, ["Deleted Messages"] = FolderRole.Trash,
        ["Bin"] = FolderRole.Trash, ["Papierkorb"] = FolderRole.Trash, ["Gelöschte Elemente"] = FolderRole.Trash,
        ["Junk"] = FolderRole.Junk, ["Spam"] = FolderRole.Junk, ["Junk Email"] = FolderRole.Junk, ["Junk E-mail"] = FolderRole.Junk,
        ["Bulk Mail"] = FolderRole.Junk, ["Junk-E-Mail"] = FolderRole.Junk,
        ["Archive"] = FolderRole.Archive, ["Archives"] = FolderRole.Archive, ["Archiv"] = FolderRole.Archive,
    };

    public static string DisplayName(FolderRole role, string serverName) => FolderNames.Display(role, serverName);

    /// <summary>Gmail's "views" over mail that already lives in other folders.</summary>
    public static bool IsVirtual(IMailFolder f, FolderRole role) =>
        role == FolderRole.All
        || f.Attributes.HasFlag(FolderAttributes.Important)
        || IsGmailSystem(f.FullName, "Important");

    private static bool IsGmailSystem(string fullName, string name) =>
        fullName.Equals($"[Gmail]/{name}", StringComparison.OrdinalIgnoreCase)
        || fullName.Equals($"[Google Mail]/{name}", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Assigns a role to each folder: SPECIAL-USE attributes first, then well-known names. Each role is used once.
    /// Gmail's Starred is left out entirely (the Flagged favorite already shows it).
    /// </summary>
    public static IReadOnlyDictionary<IMailFolder, FolderRole> Assign(IEnumerable<IMailFolder> folders, IMailFolder inbox,
        IReadOnlyDictionary<FolderRole, string>? overrides = null)
    {
        var list = folders.Where(f => !f.Attributes.HasFlag(FolderAttributes.NonExistent)
                                      && !f.Attributes.HasFlag(FolderAttributes.NoSelect)
                                      && !f.Attributes.HasFlag(FolderAttributes.Flagged)
                                      && !IsGmailSystem(f.FullName, "Starred")).ToList();
        var result = list.ToDictionary(f => f, _ => FolderRole.Other);
        var taken = new HashSet<FolderRole>();

        foreach (var f in list)
        {
            if (string.Equals(f.FullName, inbox.FullName, StringComparison.OrdinalIgnoreCase)) { result[f] = FolderRole.Inbox; taken.Add(FolderRole.Inbox); }
        }
        // Folders the user picked by hand in Accounts → IMAP → Special folders win over detection.
        foreach (var (role, path) in overrides ?? new Dictionary<FolderRole, string>())
        {
            var f = list.FirstOrDefault(x => x.FullName == path && result[x] == FolderRole.Other);
            if (f is not null && taken.Add(role)) result[f] = role;
        }
        foreach (var f in list.Where(f => result[f] == FolderRole.Other))
        {
            var role = FromAttributes(f.Attributes);
            if (role != FolderRole.Other && taken.Add(role)) result[f] = role;
        }
        // Prefer shallow folders for name matches (e.g. "INBOX.Sent" over "INBOX.Clients.Sent").
        foreach (var f in list.Where(f => result[f] == FolderRole.Other).OrderBy(f => f.FullName.Count(c => c == f.DirectorySeparator)))
        {
            if (KnownNames.TryGetValue(f.Name, out var role) && taken.Add(role)) result[f] = role;
        }
        return result;
    }

    private static FolderRole FromAttributes(FolderAttributes a) =>
        a.HasFlag(FolderAttributes.Sent) ? FolderRole.Sent
        : a.HasFlag(FolderAttributes.Drafts) ? FolderRole.Drafts
        : a.HasFlag(FolderAttributes.Trash) ? FolderRole.Trash
        : a.HasFlag(FolderAttributes.Junk) ? FolderRole.Junk
        : a.HasFlag(FolderAttributes.Archive) ? FolderRole.Archive
        : a.HasFlag(FolderAttributes.All) ? FolderRole.All
        : FolderRole.Other;

    /// <summary>True when the folder is one of the ignored paths or below one.</summary>
    public static bool IsIgnored(string fullName, char separator, IEnumerable<string> ignoredPaths) =>
        ignoredPaths.Any(p => p.Length > 0
                              && (fullName.Equals(p, StringComparison.OrdinalIgnoreCase)
                                  || fullName.StartsWith(p + separator, StringComparison.OrdinalIgnoreCase)));

    /// <summary>User-facing name for non-role folders: path without the "INBOX." or "[Gmail]/" prefix, levels joined with " / ".</summary>
    public static string FriendlyPath(string fullName, char separator)
    {
        var parts = fullName.Split(separator).ToList();
        if (parts.Count > 1 && (parts[0].Equals("INBOX", StringComparison.OrdinalIgnoreCase)
                                || parts[0] is "[Gmail]" or "[Google Mail]"))
            parts.RemoveAt(0);
        return string.Join(" / ", parts);
    }
}
