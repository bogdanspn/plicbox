using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Plicbox.Core;

namespace Plicbox.Auth;

/// <summary>Keeps account secrets in Windows Credential Manager (encrypted per Windows user, never in the database).</summary>
/// <param name="formerPrefix">Where secrets were kept before the app was renamed (from Mailroom): read once, then kept under <paramref name="prefix"/>.</param>
[SupportedOSPlatform("windows")]
public sealed class WindowsCredentialStore(string prefix = "Plicbox/account/", string? formerPrefix = null) : ICredentialStore
{
    private const uint CRED_TYPE_GENERIC = 1;
    private const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;
    // Credential Manager holds at most 2560 bytes per entry (UTF-16: 1280 characters); longer secrets are split.
    private const int MaxChars = 1280;
    private const string PartsMarker = "\u0001plicbox-parts:";
    private const string FormerPartsMarker = "\u0001mailroom-parts:"; // long secrets saved before the rename

    public void Save(long accountId, string secret) => SaveAt(prefix + accountId, secret);
    public string? Get(long accountId) => GetOrAdopt(accountId.ToString());
    public void Delete(long accountId) => DeleteBoth(accountId.ToString());

    // The outgoing server's own password, when it has one.
    public void SaveSmtp(long accountId, string secret) => SaveAt(prefix + accountId + "/smtp", secret);
    public string? GetSmtp(long accountId) => GetOrAdopt(accountId + "/smtp");
    public void DeleteSmtp(long accountId) => DeleteBoth(accountId + "/smtp");

    private string? GetOrAdopt(string key)
    {
        var secret = GetAt(prefix + key);
        if (secret is not null || formerPrefix is null) return secret;
        secret = GetAt(formerPrefix + key);
        if (secret is not null) SaveAt(prefix + key, secret);
        return secret;
    }

    // The former copy goes too, or the next Get would bring a removed account's secret back.
    private void DeleteBoth(string key)
    {
        DeleteAt(prefix + key);
        if (formerPrefix is not null) DeleteAt(formerPrefix + key);
    }

    private static void SaveAt(string target, string secret)
    {
        if (secret.Length <= MaxChars)
        {
            Write(target, secret);
            DeleteParts(target, from: 1);
            return;
        }
        var parts = secret.Chunk(MaxChars).Select(c => new string(c)).ToList();
        for (var i = 0; i < parts.Count; i++) Write(PartTarget(target, i + 1), parts[i]);
        DeleteParts(target, from: parts.Count + 1);
        Write(target, PartsMarker + parts.Count);
    }

    private static string? GetAt(string target)
    {
        var value = Read(target);
        var marker = value is null ? null
            : value.StartsWith(PartsMarker, StringComparison.Ordinal) ? PartsMarker
            : value.StartsWith(FormerPartsMarker, StringComparison.Ordinal) ? FormerPartsMarker
            : null;
        if (value is null || marker is null) return value;
        if (!int.TryParse(value.AsSpan(marker.Length), out var count)) return null;
        var parts = new string?[count];
        for (var i = 0; i < count; i++)
            if ((parts[i] = Read(PartTarget(target, i + 1))) is null) return null; // incomplete: treated as signed out
        return string.Concat(parts);
    }

    private static void DeleteAt(string target)
    {
        Remove(target);
        DeleteParts(target, from: 1);
    }

    private static string PartTarget(string target, int index) => $"{target}/part{index}";

    private static void DeleteParts(string target, int from)
    {
        for (var i = from; Remove(PartTarget(target, i)); i++) { }
    }

    private static void Write(string target, string secret)
    {
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var cred = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = target,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(secret.Length * 2),
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = "Plicbox",
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    private static string? Read(string target)
    {
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            var err = Marshal.GetLastWin32Error();
            return err == ERROR_NOT_FOUND ? null : throw new Win32Exception(err);
        }
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            return cred.CredentialBlob == IntPtr.Zero ? "" : Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    /// <summary>True when something was deleted, false when there was nothing to delete.</summary>
    private static bool Remove(string target)
    {
        if (CredDelete(target, CRED_TYPE_GENERIC, 0)) return true;
        var err = Marshal.GetLastWin32Error();
        return err == ERROR_NOT_FOUND ? false : throw new Win32Exception(err);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
