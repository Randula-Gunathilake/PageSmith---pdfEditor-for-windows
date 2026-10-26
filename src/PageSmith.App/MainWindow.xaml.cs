using System.Windows;
using System.Windows.Threading;
using PageSmith.App.Infrastructure;
using PageSmith.App.Pages;
using PageSmith.Core.Net;
using Wpf.Ui.Controls;

namespace PageSmith.App;

public partial class MainWindow : FluentWindow
{
    private readonly DispatcherTimer _networkTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private bool _online = true;

    public MainWindow()
    {
        InitializeComponent();
        WindowSizing.Apply(this, AppServices.Settings.Window);
        Closing += (_, _) =>
        {
            AppServices.Settings.Window = WindowSizing.Capture(this);
            AppServices.SaveSettings();
        };
        AppServices.Window = this;
        AppServices.Navigation = Nav;
        AppServices.Snackbars = Snackbars;
        AppServices.DialogHost = DialogHost;

        Nav.SetPageProviderService(PageProvider);
        BuildMenu();
        SearchBox.OriginalItemsSource = ToolRegistry.All.Select(t => t.Title).ToList();
        SearchBox.SuggestionChosen += (_, args) => OpenToolByTitle(args.SelectedItem as string);
        SearchBox.QuerySubmitted += (_, args) => OpenToolByTitle(args.QueryText);

        Loaded += async (_, _) =>
        {
            AppServices.ApplyTheme();
            Nav.Navigate(typeof(HomePage));
            await UpdateNetworkStatusAsync();
        };
        _networkTimer.Tick += async (_, _) => await UpdateNetworkStatusAsync();
        _networkTimer.Start();
        Connectivity.NetworkAvailabilityChanged += (_, _) => Dispatcher.InvokeAsync(UpdateNetworkStatusAsync);
    }

    public bool IsOnline => _online;

    public CachingPageProvider PageProvider { get; } = new();

    private void BuildMenu()
    {
        Nav.MenuItems.Add(new NavigationViewItem("Home", SymbolRegular.Home24, typeof(HomePage)));
        foreach (var category in Enum.GetValues<ToolCategory>())
        {
            var group = new NavigationViewItem
            {
                Content = ToolRegistry.CategoryTitle(category),
                Icon = new SymbolIcon(ToolRegistry.CategoryIcon(category)),
            };
            foreach (var tool in ToolRegistry.InCategory(category))
                group.MenuItems.Add(new NavigationViewItem(tool.Title, tool.Icon, ToolPages.TypeFor(tool.Id)));
            Nav.MenuItems.Add(group);
        }
        Nav.MenuItems.Add(new NavigationViewItem("Workflows", SymbolRegular.Flowchart24, typeof(WorkflowsPage)));
        Nav.FooterMenuItems.Add(new NavigationViewItem("Settings", SymbolRegular.Settings24, typeof(SettingsPage)));
        Nav.FooterMenuItems.Add(new NavigationViewItem("About", SymbolRegular.Info24, typeof(AboutPage)));
    }

    private void OpenToolByTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var tool = ToolRegistry.All.FirstOrDefault(t => t.Title.Equals(title, StringComparison.OrdinalIgnoreCase))
                   ?? ToolRegistry.All.FirstOrDefault(t => t.Title.Contains(title, StringComparison.OrdinalIgnoreCase) || t.Description.Contains(title, StringComparison.OrdinalIgnoreCase));
        if (tool != null) AppServices.OpenTool(tool.Id);
    }

    private async Task UpdateNetworkStatusAsync()
    {
        try
        {
            _online = await Connectivity.HasInternetAsync();
        }
        catch
        {
            _online = false;
        }
        NetworkIcon.Symbol = _online ? SymbolRegular.Wifi124 : SymbolRegular.WifiOff24;
        NetworkText.Text = _online ? "Online" : "Offline — local tools still work";
    }

    private async void OnNetworkChipClick(object sender, RoutedEventArgs e)
    {
        NetworkText.Text = "Checking…";
        await UpdateNetworkStatusAsync();
        if (!_online) AppServices.OpenUri("ms-settings:network-status");
    }
}
