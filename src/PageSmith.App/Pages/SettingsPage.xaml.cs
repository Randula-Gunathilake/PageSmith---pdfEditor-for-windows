using System.Windows.Controls;

namespace PageSmith.App.Pages;

public partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) await vm.CheckConnectivityCommand.ExecuteAsync(null);
        };
    }
}
