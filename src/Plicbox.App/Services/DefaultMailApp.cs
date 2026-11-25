using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Plicbox.App.Services;

/// <summary>
/// Tells Windows that Plicbox can be the mail app, so it's on offer under Settings > Apps > Default apps. Only the user
/// can make it the default, there.
/// </summary>
public static class DefaultMailApp
{
    private const string Name = "Plicbox";
    private const string Description = "Mail, calendar, contacts and tasks in one window.";
    private const string ProgId = "Plicbox.mailto";
    private const string ClassKey = @"Software\Classes\" + ProgId;
    private const string ClientKey = @"Software\Clients\Mail\" + Name;
    private const string RegisteredKey = @"Software\RegisteredApplications";

    /// <summary>Only for the installed app (a dev build would point Windows at the source tree). Cheap when it's already there.</summary>
    public static void Register()
    {
        if (!Updates.IsInstalled || Environment.ProcessPath is not { } exe) return;
        var open = $"\"{exe}\" \"%1\"";
        using (var now = Registry.CurrentUser.OpenSubKey(ClassKey + @"\shell\open\command"))
        using (var registered = Registry.CurrentUser.OpenSubKey(RegisteredKey))
        using (var name = Registry.CurrentUser.OpenSubKey(ClassKey + @"\Application"))
            if (now?.GetValue(null) as string == open && registered?.GetValue(Name) is not null && name?.GetValue("ApplicationName") is not null) return;

        Set(ClassKey, null, "URL:MailTo Protocol");
        Set(ClassKey, "URL Protocol", "");
        Set(ClassKey + @"\DefaultIcon", null, $"{exe},0");
        Set(ClassKey + @"\shell\open\command", null, open);
        // The name in the list of apps; without it Windows takes the program file's, and keeps it once seen ("Plicbox.App").
        Set(ClassKey + @"\Application", "ApplicationName", Name);
        Set(ClassKey + @"\Application", "ApplicationDescription", Description);
        Set(ClassKey + @"\Application", "ApplicationIcon", $"{exe},0");
        Set(ClientKey, null, Name);
        Set(ClientKey + @"\DefaultIcon", null, $"{exe},0");
        Set(ClientKey + @"\shell\open\command", null, $"\"{exe}\"");
        Set(ClientKey + @"\Capabilities", "ApplicationName", Name);
        Set(ClientKey + @"\Capabilities", "ApplicationDescription", Description);
        Set(ClientKey + @"\Capabilities", "ApplicationIcon", $"{exe},0");
        Set(ClientKey + @"\Capabilities\URLAssociations", "mailto", ProgId);
        Set(RegisteredKey, Name, ClientKey + @"\Capabilities");
        Announce();
    }

    /// <summary>Whether Windows opens mailto: links with Plicbox.</summary>
    public static bool IsDefault()
    {
        // Asked of Windows: where the registry keeps the choice differs between versions.
        var length = 260;
        var progId = new StringBuilder(length);
        return AssocQueryString(0, 20 /* ASSOCSTR_PROGID */, "mailto", null, progId, ref length) == 0 && progId.ToString() == ProgId;
    }

    /// <summary>On uninstalling.</summary>
    public static void Remove()
    {
        using (var registered = Registry.CurrentUser.OpenSubKey(RegisteredKey, writable: true)) registered?.DeleteValue(Name, throwOnMissingValue: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClientKey, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(ClassKey, throwOnMissingSubKey: false);
        Announce();
    }

    /// <summary>Windows' own page for choosing it (Windows 10 shows the whole list of apps).</summary>
    public static void OpenWindowsSettings() =>
        Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={Name}") { UseShellExecute = true });

    private static void Set(string path, string? name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path);
        key.SetValue(name, value);
    }

    private static void Announce() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(int flags, int what, string association, string? extra, StringBuilder result, ref int length);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
