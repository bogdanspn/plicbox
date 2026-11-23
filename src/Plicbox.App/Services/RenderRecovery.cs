using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog;

namespace Plicbox.App.Services;

/// <summary>
/// After sleep (or a display change) WPF can lose its GPU surface and not rebuild it: the window keeps working but its
/// picture stays frozen or blank, while the WebView reading pane, drawn separately, still updates. Switching a window to
/// software rendering for one frame and back makes WPF draw onto a fresh surface. Done a moment after waking, unlocking or
/// a display change, and whenever the main window comes back from minimized, so minimize and restore always fixes it.
/// </summary>
public static class RenderRecovery
{
    private static DispatcherTimer? _timer;
    private static string _why = "";

    public static void Register(Dispatcher dispatcher)
    {
        SystemEvents.PowerModeChanged += (_, e) =>
        {
            if (e.Mode == PowerModes.Resume) Soon(dispatcher, "resumed from sleep", TimeSpan.FromSeconds(2));
        };
        SystemEvents.SessionSwitch += (_, e) =>
        {
            if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect)
                Soon(dispatcher, "session unlocked", TimeSpan.FromSeconds(1));
        };
        SystemEvents.DisplaySettingsChanged += (_, _) => Soon(dispatcher, "display settings changed", TimeSpan.FromSeconds(1));
    }

    /// <summary>Redraws <paramref name="window"/> on a fresh surface each time it's restored from minimized.</summary>
    public static void Watch(Window window)
    {
        var wasMinimized = window.WindowState == WindowState.Minimized;
        window.StateChanged += (_, _) =>
        {
            if (wasMinimized && window.WindowState != WindowState.Minimized) Redraw([window], "restored from minimized", log: false);
            wasMinimized = window.WindowState == WindowState.Minimized;
        };
    }

    private static void Soon(Dispatcher dispatcher, string why, TimeSpan delay) => dispatcher.BeginInvoke(() =>
    {
        _why = why;
        _timer?.Stop();
        _timer = new DispatcherTimer(delay, DispatcherPriority.Normal, (sender, _) =>
        {
            ((DispatcherTimer)sender!).Stop();
            Redraw(Application.Current.Windows.Cast<Window>().ToList(), _why, log: true);
        }, dispatcher);
        _timer.Start();
    });

    private static void Redraw(IReadOnlyList<Window> windows, string why, bool log)
    {
        var targets = windows.Select(w => (Window: w, Target: (PresentationSource.FromVisual(w) as HwndSource)?.CompositionTarget))
            .Where(t => t.Target is not null).ToList();
        if (targets.Count == 0) return;
        foreach (var (_, target) in targets) target!.RenderMode = RenderMode.SoftwareOnly;
        // Back to the GPU after a frame has gone through in software (ContextIdle runs after Render).
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var (window, target) in targets)
            {
                target!.RenderMode = RenderMode.Default;
                window.InvalidateVisual();
            }
        }, DispatcherPriority.ContextIdle);
        if (log) Log.Information("Redrew {Count} window(s) on a fresh surface: {Why}", targets.Count, why);
    }
}
