using System.Windows;

namespace Plicbox.App.Services;

/// <summary>
/// Windows you work in beside the main one (a message being written, one opened on its own, an event, task or contact):
/// they open centred on the main window, as an owned window would, then stand on their own, so clicking the main window
/// brings it to the front instead of the owned window always staying on top of it. Each has its own taskbar button.
/// </summary>
public static class FreeWindows
{
    public static void Place(Window window)
    {
        if (Application.Current.MainWindow is { IsVisible: true } main && !ReferenceEquals(main, window))
        {
            // Owned only for the first moment, which is what places it over the main window (CenterOwner).
            window.Owner = main;
            void Release(object? sender, EventArgs e)
            {
                window.ContentRendered -= Release;
                window.Owner = null;
            }
            window.ContentRendered += Release;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen; // the main window is in the tray
        }
    }
}
