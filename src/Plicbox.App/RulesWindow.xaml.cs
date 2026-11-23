using System.Windows;
using Plicbox.App.ViewModels;

namespace Plicbox.App;

public partial class RulesWindow : Window
{
    public RulesWindow(RulesViewModel vm)
    {
        InitializeComponent();
        Icon = Services.WindowIcons.Rules;
        DataContext = vm;
        vm.Saved += () => DialogResult = true;
        Loaded += async (_, _) => await vm.LoadAsync();
    }
}
