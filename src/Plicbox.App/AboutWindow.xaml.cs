using System.Windows;
using System.Windows.Input;
using Plicbox.App.Services;
using Plicbox.Core;

namespace Plicbox.App;

/// <summary>☰ > About Plicbox: what the app is, its version, and the main pieces it's built from.</summary>
public partial class AboutWindow : Window
{
    public sealed record Tech(string Name, string Use);

    public AboutWindow()
    {
        InitializeComponent();
        Icon = WindowIcons.About;
        VersionText.Text = L.F("Version {0}", Updates.Version);
        // Product names stay as they are; only what each is used for is translated.
        TechList.ItemsSource = new Tech[]
        {
            new(".NET 10, WPF", L.T("The app and its windows")),
            new("MailKit, MimeKit", L.T("Fetching, sending and reading mail (IMAP, SMTP, MIME)")),
            new("SQLite, Dapper", L.T("Storing mail on this computer, with full-text search")),
            new("WebView2", L.T("Showing and writing messages")),
            new("HtmlSanitizer", L.T("Cleaning message HTML before it's shown")),
            new("MSAL, Google APIs", L.T("Signing in with Microsoft and Google")),
            new("DnsClient", L.T("Finding an address's mail servers")),
            new("Velopack", L.T("Installing and updating")),
            new("malware-filter, OpenPhish, PhishTank, IPThreat, URLhaus", L.T("Lists of reported phishing and malware sites (CC BY-SA 4.0, CC0)")),
            new("H.NotifyIcon", L.T("The icon in the notification area")),
            new("Serilog", L.T("Logs")),
            new("SharpVectors", L.T("SVG images")),
            new("CommunityToolkit.Mvvm", L.T("View models")),
            new("Segoe Fluent Icons", L.T("Icons")),
            new("Fluent UI System Icons", L.T("A few extra icons")),
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>The licence and the third-party notices, shipped next to the app: opened with Notepad.</summary>
    private void LicenceFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Documents.Hyperlink { Tag: string name }) return;
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, name);
        if (System.IO.File.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    /// <summary>The websites under the copyright line open in the default browser.</summary>
    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Documents.Hyperlink { NavigateUri: { } uri })
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
}
