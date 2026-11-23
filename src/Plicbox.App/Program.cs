using Plicbox.App.Services;
using Velopack;

namespace Plicbox.App;

/// <summary>
/// The entry point (App.xaml is built as a page, so WPF doesn't write its own). Velopack's hooks run first: Setup.exe
/// and the updater start Plicbox with their own arguments, and it does its part and quits before anything opens.
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => StartupRegistration.Remove()) // don't leave Windows starting a deleted app
            .Run();
        if (!FormerName.MoveData()) return; // before App: it reads the data folder from its first line
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
