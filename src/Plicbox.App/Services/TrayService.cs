using Plicbox.Core;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;

namespace Plicbox.App.Services;

/// <summary>Notification-area icon: open, sync, exit, and new-mail notifications.</summary>
public sealed class TrayService : IDisposable
{
    /// <summary>The app's name as the tray and taskbar show it; a dev build says so, to tell it from the installed one.</summary>
    public static string AppName => Updates.IsInstalled ? "Plicbox" : "Plicbox (dev)";

    private TaskbarIcon? _icon;

    public event Action? OpenRequested;
    public event Action? SyncRequested;
    public event Action? ExitRequested;

    public void Initialize()
    {
        if (_icon is not null) return;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(L.T("Open Plicbox"), () => OpenRequested?.Invoke(), bold: true));
        menu.Items.Add(MenuItem(L.T("Sync now"), () => SyncRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L.T("Exit"), () => ExitRequested?.Invoke()));

        _icon = new TaskbarIcon
        {
            ToolTipText = AppName,
            Icon = AppIcon.CreateTrayIcon(),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => OpenRequested?.Invoke();
        _icon.TrayBalloonTipClicked += (_, _) => OpenRequested?.Invoke();
        _icon.ForceCreate(enablesEfficiencyMode: false); // efficiency mode would throttle background sync
    }

    // Kept here, never handed over: the tray icon disposes the icon it's showing when it gets another, so it always
    // gets a copy (handing back one it had would show a disposed icon).
    private System.Drawing.Icon? _plainIcon, _newMailIcon;
    private bool _showsNewMail;

    /// <summary>A dot on the tray icon while new mail is waiting unseen.</summary>
    public void SetNewMail(bool waiting)
    {
        if (_icon is null || waiting == _showsNewMail) return;
        _showsNewMail = waiting;
        _plainIcon ??= AppIcon.CreateTrayIcon();
        _newMailIcon ??= NewMailBadge.TrayIcon(_plainIcon);
        _icon.Icon = (System.Drawing.Icon)(waiting ? _newMailIcon : _plainIcon).Clone();
        _icon.ToolTipText = waiting ? $"{AppName} · {L.T("New mail")}" : AppName;
    }

    public void Notify(string title, string message) =>
        _icon?.ShowNotification(title, message, NotificationIcon.None, largeIcon: false, sound: true, respectQuietTime: true);

    private static MenuItem MenuItem(string header, Action action, bool bold = false)
    {
        var item = new MenuItem { Header = header, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _plainIcon?.Dispose();
        _newMailIcon?.Dispose();
    }
}

/// <summary>Per-user "run at sign-in" entry (HKCU\…\Run), the same mechanism Windows' Startup apps page manages.</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Plicbox";

    /// <summary>
    /// Only the installed Plicbox starts with Windows: a dev build would point the entry at the source tree. The
    /// installed one sets it again at every start, so after an update or a change made in a dev build it's right.
    /// </summary>
    public static void Set(bool enabled)
    {
        if (!Updates.IsInstalled) return;
        if (!enabled)
        {
            Remove();
            return;
        }
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true) ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --background");
    }

    public static void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
