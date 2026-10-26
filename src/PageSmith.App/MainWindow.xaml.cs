using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PageSmith.App.Controls;
using PageSmith.App.Documents;
using PageSmith.App.Infrastructure;
using PageSmith.App.Pages;
using PageSmith.Core.Net;
using Wpf.Ui.Controls;

namespace PageSmith.App;

public partial class MainWindow : FluentWindow
{
    private readonly DispatcherTimer _networkTimer = new() { Interval = TimeSpan.FromSeconds(45) };
    private readonly Dictionary<DocumentSession, DocumentView> _views = [];
    private bool _online = true;
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        WindowSizing.Apply(this, AppServices.Settings.Window);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            // Working copies live in %TEMP%; remove them now rather than leave them to Windows.
            foreach (var session in AppServices.Documents.Documents.ToList()) session.Dispose();
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

        var workspace = AppServices.Documents;
        workspace.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DocumentWorkspace.Active)) ShowActive();
        };
        workspace.Documents.CollectionChanged += OnDocumentsChanged;
        PreviewKeyDown += OnPreviewKey;

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

    /// <summary>The view of the document whose tab is in front, or null on Home.</summary>
    public DocumentView? ActiveView => AppServices.Documents.Active is { } s && _views.TryGetValue(s, out var v) ? v : null;

    // ------------------------------------------------------------------ documents

    /// <summary>Shows Home or the document in front; views are created when first shown and kept while open.</summary>
    private void ShowActive()
    {
        var active = AppServices.Documents.Active;
        if (active != null && !_views.ContainsKey(active))
        {
            var view = new DocumentView(active);
            _views[active] = view;
            DocumentHost.Children.Add(view);
        }
        foreach (var (session, view) in _views) view.Visibility = session == active ? Visibility.Visible : Visibility.Collapsed;
        DocumentHost.Visibility = active != null ? Visibility.Visible : Visibility.Collapsed;
        Nav.Visibility = active == null ? Visibility.Visible : Visibility.Collapsed;
        UpdateTitle();
        ActiveView?.Dispatcher.InvokeAsync(() => ActiveView?.FocusView(), DispatcherPriority.Input);
    }

    private DocumentSession? _titled;

    private void UpdateTitle()
    {
        if (_titled != null) _titled.PropertyChanged -= OnActiveSessionChanged;
        _titled = AppServices.Documents.Active;
        if (_titled != null) _titled.PropertyChanged += OnActiveSessionChanged;
        // The taskbar shows which document is in front.
        Title = _titled == null ? "PageSmith" : $"{(_titled.IsDirty ? "• " : "")}{_titled.Title} - PageSmith";
    }

    private void OnActiveSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DocumentSession.Title) or nameof(DocumentSession.IsDirty)) UpdateTitle();
    }

    private void OnDocumentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var gone in _views.Keys.Where(s => !AppServices.Documents.Documents.Contains(s)).ToList())
        {
            var view = _views[gone];
            view.Detach();
            DocumentHost.Children.Remove(view);
            _views.Remove(gone);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled) return;
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Files dropped on a document (not on a tool's file list) open in tabs; Home suggests tools itself.</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || AppServices.Documents.Active == null || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;
        await AppServices.Documents.OpenAsync(files);
    }

    private async void OnPreviewKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var workspace = AppServices.Documents;
        if (ctrl && key == Key.Tab)
        {
            workspace.Cycle(modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
        }
        else if (ctrl && key is Key.PageDown or Key.PageUp)
        {
            workspace.Cycle(key == Key.PageDown ? 1 : -1);
        }
        else if (ctrl && key == Key.O)
        {
            e.Handled = true;
            await workspace.BrowseAsync();
            return;
        }
        else if (ctrl && key is Key.W or Key.F4 && workspace.Active is { } active)
        {
            e.Handled = true;
            await workspace.CloseAsync(active);
            return;
        }
        else if (ActiveView is not { } view || !view.HandleShortcut(key, modifiers))
        {
            return;
        }
        e.Handled = true;
    }

    /// <summary>Offers to save unsaved documents before closing; the window closes once they are dealt with.</summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_closeConfirmed && AppServices.Documents.Documents.Any(d => d.IsDirty))
        {
            e.Cancel = true;
            if (!await AppServices.Documents.CloseAllAsync()) return;
            _closeConfirmed = true;
            Close();
            return;
        }
        AppServices.Settings.Window = WindowSizing.Capture(this);
        AppServices.SaveSettings();
    }

    // ------------------------------------------------------------------ Home

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
