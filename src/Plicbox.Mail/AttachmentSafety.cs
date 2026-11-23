using Plicbox.Core;
using System.Text.RegularExpressions;

namespace Plicbox.Mail;

public enum AttachmentRisk { None, Caution, Danger }

/// <summary>An attachment name split for display, with the reason it deserves a second look (null when it doesn't).</summary>
/// <param name="Stem">The name without its extension, tidied for display (hidden direction marks and padding removed).</param>
/// <param name="Extension">".pdf", ".exe", … or empty when the file has none.</param>
public sealed record AttachmentAssessment(string Stem, string Extension, AttachmentRisk Risk, string? Warning);

/// <summary>Which attachments can harm the computer, and names built to hide what a file really is.</summary>
public static partial class AttachmentSafety
{
    // Opening these runs code (the list Outlook blocks or warns about, trimmed to what still matters on Windows).
    private static readonly HashSet<string> Programs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msp", ".mst", ".msix", ".msixbundle", ".appx", ".appxbundle", ".scr", ".pif",
        ".cpl", ".hta", ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".ws", ".ps1", ".psm1", ".psd1", ".lnk", ".url", ".reg",
        ".jar", ".dll", ".sys", ".iso", ".img", ".vhd", ".vhdx", ".chm", ".application", ".appref-ms", ".gadget", ".scf", ".sct",
        ".xll", ".inf", ".settingcontent-ms", ".library-ms", ".search-ms",
    };

    // Not programs, but common carriers: Office files with macros, and web pages posing as sign-in forms.
    private static readonly Dictionary<string, string> Risky = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docm"] = L.T("Word file with macros"), [".dotm"] = L.T("Word file with macros"), [".xlsm"] = L.T("Excel file with macros"),
        [".xltm"] = L.T("Excel file with macros"), [".xlam"] = L.T("Excel add-in"), [".pptm"] = L.T("PowerPoint file with macros"),
        [".potm"] = L.T("PowerPoint file with macros"), [".ppam"] = L.T("PowerPoint add-in"), [".html"] = L.T("web page"), [".htm"] = L.T("web page"),
        [".shtml"] = L.T("web page"), [".svg"] = L.T("picture that can contain scripts"), [".one"] = L.T("OneNote file"),
    };

    /// <summary>Files that run code when opened (.exe, .js, .lnk, …): what mail services refuse to carry.</summary>
    internal static bool IsProgram(string extension) => Programs.Contains(extension);

    private static readonly HashSet<string> Documents =new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".rtf", ".txt", ".csv", ".jpg", ".jpeg", ".png",
        ".gif", ".zip", ".mp3", ".mp4", ".mov", ".wav",
    };

    public static AttachmentAssessment Assess(string? fileName)
    {
        var raw = string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName;
        // Direction marks make "invoice‮fdp.exe" display as "invoiceexe.pdf": drop them and say so.
        var hiddenDirection = DirectionMarks().IsMatch(raw);
        var name = Whitespace().Replace(DirectionMarks().Replace(raw, ""), " ").Trim();

        var dot = name.LastIndexOf('.');
        var (stem, extension) = dot > 0 && dot < name.Length - 1 ? (name[..dot].TrimEnd(), name[dot..]) : (name, "");
        // "invoice.pdf.exe": the inner extension is what the sender wants you to see.
        var inner = Path.GetExtension(stem);
        var disguised = Documents.Contains(inner);

        string? warning = null;
        var risk = AttachmentRisk.None;
        if (Programs.Contains(extension))
        {
            risk = AttachmentRisk.Danger;
            warning = disguised
                ? L.F("Looks like a {0} file, but it's a {1} file that can run programs on your computer.", inner, extension)
                : L.F("{0} files can run programs on your computer. Only open it if you trust the sender.", extension);
        }
        else if (Risky.TryGetValue(extension, out var kind))
        {
            risk = AttachmentRisk.Caution;
            warning = extension.StartsWith(".htm", StringComparison.OrdinalIgnoreCase) || extension.Equals(".shtml", StringComparison.OrdinalIgnoreCase)
                ? L.T("A web page sent as a file: these are often fake sign-in pages. Don't enter passwords in it.")
                : L.F("{0}: only open it if you expected it.", $"{char.ToUpper(kind[0], System.Globalization.CultureInfo.CurrentCulture)}{kind[1..]}");
        }
        if (hiddenDirection)
        {
            risk = AttachmentRisk.Danger;
            warning = L.F("The name uses hidden characters to disguise its real type ({0}).", extension.Length > 0 ? extension : L.T("no extension")) + " "
                      + (warning ?? "");
        }
        return new AttachmentAssessment(stem, extension, risk, warning?.Trim());
    }

    /// <summary>True when opening the file deserves a confirmation first.</summary>
    public static bool NeedsConfirmation(string? fileName) => Assess(fileName).Risk == AttachmentRisk.Danger;

    // Archives whose contents can't be read here (only zip can): said plainly, since malware hides in them too.
    private static readonly HashSet<string> OtherArchives = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rar", ".7z", ".ace", ".arj", ".cab", ".gz", ".tgz", ".tar", ".bz2", ".xz", ".z", ".lzh", ".uue", ".zst",
    };

    // Bigger archives aren't opened (reading the list of files is cheap, but the attachment has to be decoded first).
    private const long MaxArchiveBytes = 50L * 1024 * 1024;
    private const long MaxNestedBytes = 20L * 1024 * 1024;

    /// <summary>
    /// The name, and for a zip archive what's inside it: programs, disguised names and password protection (which
    /// hides the contents from antivirus) count as if the attachment itself were one. Only the list of files is
    /// read, plus one zip inside a zip; nothing is unpacked to disk.
    /// </summary>
    public static AttachmentAssessment Assess(MimeKit.MimePart part)
    {
        var byName = Assess(part.FileName);
        if (byName.Risk == AttachmentRisk.Danger) return byName;

        var isZip = byName.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase)
                    || part.ContentType.MimeType is "application/zip" or "application/x-zip-compressed";
        if (!isZip)
        {
            return OtherArchives.Contains(byName.Extension) && byName.Risk == AttachmentRisk.None
                ? byName with
                {
                    Risk = AttachmentRisk.Caution,
                    Warning = L.F("Plicbox can't look inside {0} archives. Only open it if you expected it.", byName.Extension),
                }
                : byName;
        }
        if (part.Content is null) return byName;

        try
        {
            using var content = new MemoryStream();
            using (var decoded = part.Content.Open())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = decoded.Read(buffer)) > 0)
                {
                    content.Write(buffer, 0, read);
                    if (content.Length > MaxArchiveBytes) return byName;
                }
            }
            content.Position = 0;
            var (risk, warning) = LookInside(content, nested: false);
            return risk > byName.Risk ? byName with { Risk = risk, Warning = warning } : byName;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {
            return byName with
            {
                Risk = AttachmentRisk.Caution,
                Warning = L.T("Plicbox couldn't look inside this archive (it may be damaged, or built to hide what's in it). Only open it if you expected it."),
            };
        }
    }

    private static (AttachmentRisk Risk, string? Warning) LookInside(Stream zip, bool nested)
    {
        using var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: true);
        (AttachmentRisk Risk, string? Warning) found = (AttachmentRisk.None, null);
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue; // a folder
            if (entry.IsEncrypted)
                return (AttachmentRisk.Danger,
                    L.T("It's locked with a password, so no antivirus can look inside: a common way to send malware. Only open it if you were expecting it from someone you know."));

            var name = entry.Name.Length > 0 ? entry.Name : entry.FullName;
            var inner = Assess(name);
            if (inner.Risk == AttachmentRisk.Danger)
                return (AttachmentRisk.Danger, L.F("It contains “{0}”.", inner.Stem + inner.Extension) + " " + inner.Warning);
            if (inner.Risk == AttachmentRisk.Caution && found.Risk == AttachmentRisk.None)
                found = (AttachmentRisk.Caution, L.F("It contains “{0}”.", inner.Stem + inner.Extension) + " " + inner.Warning);

            // A zip inside the zip: looked into once more (archives nested deeper are a warning sign of their own).
            if (inner.Extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) && entry.Length <= MaxNestedBytes)
            {
                if (nested)
                {
                    if (found.Risk == AttachmentRisk.None)
                        found = (AttachmentRisk.Caution, L.T("It has archives packed inside archives, a way to slip past checks. Only open it if you expected it."));
                    continue;
                }
                using var inside = new MemoryStream();
                using (var stream = entry.Open()) stream.CopyTo(inside);
                inside.Position = 0;
                var deeper = LookInside(inside, nested: true);
                if (deeper.Risk == AttachmentRisk.Danger) return deeper;
                if (deeper.Risk > found.Risk) found = deeper;
            }
        }
        return found;
    }

    [GeneratedRegex("[\u200E\u200F\u202A-\u202E\u2066-\u2069]")]
    private static partial Regex DirectionMarks();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Whitespace();
}
