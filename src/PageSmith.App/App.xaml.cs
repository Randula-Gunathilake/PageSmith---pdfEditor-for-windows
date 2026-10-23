using System.Windows;
using Wpf.Ui.Appearance;

namespace PageSmith.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme(true);
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
