using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Plicbox.Mail;

public enum QuarantineResult
{
    /// <summary>Marked as from the internet (and scanned, where an antivirus is installed).</summary>
    Marked,
    /// <summary>The antivirus or Windows' attachment policy removed or blocked the file.</summary>
    Blocked,
    /// <summary>Couldn't be marked (not Windows, or a disk that doesn't keep the mark, such as FAT32 or some network shares).</summary>
    NotMarked,
}

/// <summary>
/// Marks attachments written to disk as downloaded from the internet ("Mark of the Web"), the way browsers and
/// Outlook do, so Windows treats them with suspicion: Office opens them in Protected View with macros blocked,
/// SmartScreen checks programs before they run, and files unpacked from a marked zip are marked too. It goes through
/// Windows' Attachment Services, which also has the installed antivirus scan the file and applies the attachment
/// policies an administrator may have set; when that isn't available the mark is written directly.
/// </summary>
public static class AttachmentQuarantine
{
    // Plicbox's own id for Attachment Services (it keeps per-app state, e.g. "don't ask again" answers, under it).
    private static readonly Guid ClientId = new("6f1b7c1e-2d44-4f3a-9b0e-5c7a1d2e8f40");

    /// <summary>Marks a file just written to <paramref name="path"/>; <paramref name="fileName"/> is the name it came with.</summary>
    public static QuarantineResult Mark(string path, string? fileName = null, ILogger? log = null)
    {
        if (!OperatingSystem.IsWindows()) return QuarantineResult.NotMarked;
        try
        {
            var saved = SaveThroughAttachmentServices(path, fileName ?? Path.GetFileName(path), out var hr);
            if (!File.Exists(path))
            {
                log?.LogInformation("Attachment {Name} was removed when it was saved (antivirus or policy, 0x{Hr:X8})", fileName, hr);
                return QuarantineResult.Blocked;
            }
            if (saved && HasMark(path)) return QuarantineResult.Marked;
            // Attachment Services wasn't there or failed (or the policy not to keep zone information is set; a
            // file in the Internet zone is still the safer default for mail).
            return WriteMark(path) ? QuarantineResult.Marked : QuarantineResult.NotMarked;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException or InvalidCastException)
        {
            log?.LogWarning(ex, "Marking attachment {Name} as from the internet failed", fileName);
            if (!File.Exists(path)) return QuarantineResult.Blocked;
            return WriteMark(path) ? QuarantineResult.Marked : QuarantineResult.NotMarked;
        }
    }

    /// <summary>Whether a file carries the mark (its Zone.Identifier stream names the Internet or Restricted zone).</summary>
    public static bool HasMark(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var text = File.ReadAllText(path + ":Zone.Identifier");
            return text.Contains("ZoneId=3", StringComparison.Ordinal) || text.Contains("ZoneId=4", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool WriteMark(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false; // a disk without alternate streams (FAT32, some shares)
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool SaveThroughAttachmentServices(string path, string fileName, out int hr)
    {
        hr = 0;
        var type = Type.GetTypeFromCLSID(new Guid("4125dd96-e03a-4103-8f70-e0597d803b9c"), throwOnError: false);
        if (type is null || Activator.CreateInstance(type) is not IAttachmentExecute attachment) return false;
        try
        {
            attachment.SetClientGuid(ClientId);
            attachment.SetLocalPath(path);
            attachment.SetFileName(fileName);
            // Mail has no web address to come from: the Internet zone, as browsers do for downloads without one.
            attachment.SetSource("about:internet");
            hr = attachment.Save();
            return hr >= 0;
        }
        finally
        {
            Marshal.ReleaseComObject(attachment);
        }
    }

    // shobjidl.h: IAttachmentExecute. Methods in vtable order; only the ones used are called.
    [ComImport, Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttachmentExecute
    {
        void SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetClientGuid(in Guid guid);
        void SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
        void SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
        [PreserveSig] int CheckPolicy();
        [PreserveSig] int Prompt(IntPtr hwnd, int prompt, out int action);
        [PreserveSig] int Save();
        [PreserveSig] int Execute(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out IntPtr process);
        [PreserveSig] int SaveWithUI(IntPtr hwnd);
        [PreserveSig] int ClearClientState();
    }
}
