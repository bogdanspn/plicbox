using System.IO.Compression;
using Plicbox.Core;

namespace Plicbox.Mail;

/// <summary>Something about a file being sent that may stop the message getting through, or the file being opened.</summary>
/// <param name="Risk">Danger: likely refused or bounced, worth a second thought before sending. Caution: may be held back.</param>
public sealed record OutgoingWarning(AttachmentRisk Risk, string Text);

/// <summary>
/// Attachments as the sender sees them: what Gmail, Outlook and company mail filters refuse or hold back (programs, even
/// zipped; archives locked with a password; Office files with macros), files that won't open elsewhere (shortcuts),
/// empty files, and messages too big for most mail services.
/// </summary>
public static class OutgoingAttachments
{
    /// <summary>What most mail services take (Gmail and Outlook say 25 MB of attachments); company servers often less.</summary>
    public const long TypicalLimitBytes = 25L * 1024 * 1024;

    // A zip is only looked into up to this size (its list of files is read, nothing is unpacked), one zip inside it too.
    private const long MaxArchiveBytes = 100L * 1024 * 1024;
    private const long MaxNestedBytes = 20L * 1024 * 1024;

    private static readonly Dictionary<string, string> MacroFreeFormat = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docm"] = ".docx", [".dotm"] = ".docx", [".xlsm"] = ".xlsx", [".xltm"] = ".xlsx", [".pptm"] = ".pptx", [".potm"] = ".pptx",
        [".xlam"] = "", [".ppam"] = "",
    };

    /// <summary>A file on disk about to be attached.</summary>
    public static OutgoingWarning? Check(string path)
    {
        long size;
        try { size = new FileInfo(path).Length; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        return Check(Path.GetFileName(path), size, () => File.OpenRead(path));
    }

    /// <param name="size">Its size in bytes, when known.</param>
    /// <param name="open">Opens its content, to look inside a zip; null when it can't be read.</param>
    public static OutgoingWarning? Check(string fileName, long? size, Func<Stream>? open)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
            return new(AttachmentRisk.Danger,
                L.T("A shortcut, not the file itself: it won't open on anyone else's computer, and mail services often refuse it."));
        if (AttachmentSafety.IsProgram(extension))
            return new(AttachmentRisk.Danger,
                L.F("Gmail, Outlook and most companies refuse {0} files: the message may bounce or arrive without it. Share it through a cloud folder instead.", extension.ToLowerInvariant()));
        if (MacroFreeFormat.TryGetValue(extension, out var plain))
            return new(AttachmentRisk.Caution, plain.Length > 0
                ? L.F("Files with macros are often held back by company mail filters. If it doesn't need them, save it as {0} instead.", plain)
                : L.T("Office add-ins are often held back by company mail filters."));
        if (size == 0)
            return new(AttachmentRisk.Caution, L.T("This file is empty."));
        if (open is not null && extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && size is null or <= MaxArchiveBytes)
        {
            try
            {
                using var stream = open();
                return LookInside(stream, nested: false);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return null; // not a zip after all, or unreadable: nothing to say
            }
        }
        return null;
    }

    /// <summary>All attachments together, against what most mail services take.</summary>
    public static OutgoingWarning? CheckTotal(long bytes) => bytes > TypicalLimitBytes
        ? new(AttachmentRisk.Danger,
            L.F("The attachments add up to {0}: most mail services refuse messages over {1}. Share big files through a cloud folder instead.",
                Megabytes(bytes), Megabytes(TypicalLimitBytes)))
        : null;

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):0} MB";

    private static OutgoingWarning? LookInside(Stream zip, bool nested)
    {
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: true);
        OutgoingWarning? found = null;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // a folder
            var name = entry.Name.Length > 0 ? entry.Name : entry.FullName;
            var extension = Path.GetExtension(name);
            if (AttachmentSafety.IsProgram(extension))
                return new(AttachmentRisk.Danger,
                    L.F("It contains “{0}”: Gmail, Outlook and most companies refuse programs even inside a zip. Share it through a cloud folder instead.", name));
            if (entry.IsEncrypted)
                found ??= new(AttachmentRisk.Caution,
                    L.T("It's locked with a password: many mail filters hold back archives they can't look inside. Sending a link to it is safer."));
            if (!nested && extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && entry.Length <= MaxNestedBytes && !entry.IsEncrypted)
            {
                using var inside = new MemoryStream();
                using (var stream = entry.Open()) stream.CopyTo(inside);
                inside.Position = 0;
                var deeper = LookInside(inside, nested: true);
                if (deeper?.Risk == AttachmentRisk.Danger) return deeper;
                found ??= deeper;
            }
        }
        return found;
    }
}
