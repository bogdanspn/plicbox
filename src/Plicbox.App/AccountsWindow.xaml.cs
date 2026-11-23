using System.Windows;
using System.Windows.Controls;
using Plicbox.App.ViewModels;

namespace Plicbox.App;

public partial class AccountsWindow : Window
{
    private readonly AccountsViewModel _vm;

    public AccountsWindow(AccountsViewModel vm)
    {
        InitializeComponent();
        Icon = Services.WindowIcons.Accounts;
        DataContext = _vm = vm;
        vm.Saved += () => DialogResult = true;
        Loaded += async (_, _) => await vm.LoadAsync();
        // A password box can't bind: it starts empty for each account (empty keeps that account's saved password).
        SmtpPasswordBox.DataContextChanged += (_, _) => SmtpPasswordBox.Clear();
    }

    private void SmtpPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (SmtpPasswordBox.DataContext is AccountEditor editor) editor.NewSmtpPassword = SmtpPasswordBox.Password;
    }

    internal void SelectTab(int index) => Tabs.SelectedIndex = index;

    // Diagnostics show live sync state, so refresh whenever that tab is opened.
    private async void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.OriginalSource is TabControl { SelectedItem: var tab } && ReferenceEquals(tab, DiagnosticsTab))
            await _vm.RefreshDiagnosticsAsync(_vm.Selected);
    }
}
