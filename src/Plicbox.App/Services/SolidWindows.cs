using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Plicbox.App.Services;

/// <summary>
/// Every window solid, in both modes: no Windows 11 backdrop (Mica lets the desktop's colour through, a bluish tinge),
/// a title bar in the app's own chrome colour, and a page background for windows that don't set one. Applied as each
/// window loads, and again when light/dark changes.
/// </summary>
public static partial class SolidWindows
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) => Apply((Window)sender)));
        // The theme switch re-applies its own backdrop: ours goes on after it.
        Theme.Changed += () => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (Window window in Application.Current.Windows) Apply(window);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    public static void Apply(Window window)
    {
        // A window without a background of its own would show the desktop through: give it the page colour. Not the
        // dialogs drawn as a card in a see-through window (AllowsTransparency), whose clear edge holds the card's shadow.
        if (!window.AllowsTransparency
            && (window.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue || window.Background is null
                || window.Background is SolidColorBrush { Color.A: 0 }))
            window.SetResourceReference(Control.BackgroundProperty, "SurfaceBrush");

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var none = DWMSBT_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref none, sizeof(int));
        var dark = Theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        var chrome = Theme.Color("ChromeBrush");
        var colorRef = chrome.R | (chrome.G << 8) | (chrome.B << 16);
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
    }
}
