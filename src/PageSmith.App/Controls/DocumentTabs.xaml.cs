using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using PageSmith.App.Documents;
using PageSmith.App.Infrastructure;

namespace PageSmith.App.Controls;

/// <summary>The tab strip: Home (all tools) and one tab per open document, like a browser.</summary>
public partial class DocumentTabs : UserControl
{
    public DocumentTabs()
    {
        InitializeComponent();
        DataContext = AppServices.Documents;
        AppServices.Documents.PropertyChanged += OnWorkspaceChanged;
        Loaded += (_, _) => UpdateSelection();
    }

    private static DocumentWorkspace Workspace => AppServices.Documents;

    private void OnWorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentWorkspace.Active)) UpdateSelection();
    }

    private void OnTabLoaded(object sender, RoutedEventArgs e) => UpdateSelection();

    /// <summary>Draws the tab in front as a raised card joined to the page below it.</summary>
    private void UpdateSelection()
    {
        Paint(HomeTab, Workspace.Active == null);
        for (var i = 0; i < Tabs.Items.Count; i++)
        {
            if (Tabs.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter presenter) continue;
            if (VisualTreeHelperFirst<Border>(presenter) is not { } box) continue;
            var active = ReferenceEquals(Tabs.Items[i], Workspace.Active);
            Paint(box, active);
            if (active) box.BringIntoView();
        }
    }

    private static void Paint(Border box, bool active)
    {
        if (active)
        {
            // The same layer colour as the document's toolbar, so the tab joins the page below it.
            box.SetResourceReference(Border.BackgroundProperty, "LayerFillColorDefaultBrush");
            box.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
            box.SetValue(TextElement.FontWeightProperty, FontWeights.SemiBold);
        }
        else
        {
            box.Background = System.Windows.Media.Brushes.Transparent;
            box.BorderBrush = System.Windows.Media.Brushes.Transparent;
            box.SetValue(TextElement.FontWeightProperty, FontWeights.Normal);
        }
    }

    private static T? VisualTreeHelperFirst<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (VisualTreeHelperFirst<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void OnHomeClick(object sender, MouseButtonEventArgs e) => Workspace.Active = null;

    private async void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DocumentSession session) return;
        if (e.ChangedButton == MouseButton.Middle)
        {
            e.Handled = true;
            await Workspace.CloseAsync(session);
        }
        else if (e.ChangedButton == MouseButton.Left)
        {
            Workspace.Active = session;
        }
    }

    private async void OnCloseTab(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is DocumentSession session) await Workspace.CloseAsync(session);
    }

    private async void OnOpen(object sender, RoutedEventArgs e) => await Workspace.BrowseAsync();

    private static DocumentSession? MenuSession(object sender) => (sender as FrameworkElement)?.DataContext as DocumentSession;

    private async void OnMenuClose(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender) is { } session) await Workspace.CloseAsync(session);
    }

    private async void OnMenuCloseOthers(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender) is not { } keep) return;
        foreach (var other in Workspace.Documents.Where(d => d != keep).ToList())
            if (!await Workspace.CloseAsync(other)) return;
        Workspace.Active = keep;
    }

    private async void OnMenuCloseAll(object sender, RoutedEventArgs e) => await Workspace.CloseAllAsync();

    private void OnMenuShowInFolder(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender)?.FilePath is { } path) AppServices.ShowInFolder(path);
    }

    private void OnMenuCopyPath(object sender, RoutedEventArgs e)
    {
        if (MenuSession(sender)?.FilePath is { } path) Clipboard.SetText(path);
    }

    private void OnTabsWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroller.ScrollToHorizontalOffset(TabScroller.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            await Workspace.OpenAsync(files);
        }
    }
}
