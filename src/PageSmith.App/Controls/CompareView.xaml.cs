using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PageSmith.App.Tools;
using PageSmith.Core.Tools.Security;
using TextChange = PageSmith.Core.Tools.Security.TextChange;

namespace PageSmith.App.Controls;

/// <summary>Two versions of a document side by side (or overlaid), scrolling together, with changes highlighted.</summary>
public partial class CompareView : UserControl
{
    private CompareController? _controller;
    private ScrollViewer? _oldScroll, _newScroll;
    private bool _syncing;

    public CompareView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as CompareController);
        Loaded += (_, _) =>
        {
            Attach(DataContext as CompareController);
            HookScrolling();
        };
        Unloaded += (_, _) => Attach(null);
    }

    private void Attach(CompareController? controller)
    {
        if (ReferenceEquals(controller, _controller)) return;
        if (_controller != null) _controller.PropertyChanged -= OnControllerChanged;
        _controller = controller;
        if (controller != null) controller.PropertyChanged += OnControllerChanged;
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CompareController.SelectedChange) && _controller?.SelectedChange is { } row)
            Dispatcher.BeginInvoke(() => ShowChange(row.Change), System.Windows.Threading.DispatcherPriority.Loaded);
        if (e.PropertyName == nameof(CompareController.HasPair) && _controller?.HasPair == true)
            Dispatcher.BeginInvoke(HookScrolling, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ------------------------------------------------------------------ pages load while visible

    private static ComparePage? PageOf(object sender) => (sender as FrameworkElement)?.DataContext as ComparePage;

    private static readonly object Hooked = new();

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement container && container.Tag != Hooked)
        {
            container.Tag = Hooked;
            container.DataContextChanged += OnPageDataChanged;
        }
        PageOf(sender)?.Load();
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => PageOf(sender)?.Unload();

    private void OnPageDataChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Recycled containers switch pages without unloading.
        (e.OldValue as ComparePage)?.Unload();
        if (sender is FrameworkElement { IsLoaded: true }) (e.NewValue as ComparePage)?.Load();
    }

    // ------------------------------------------------------------------ scrolling together

    private void HookScrolling()
    {
        OldList.UpdateLayout();
        NewList.UpdateLayout();
        var oldScroll = FindScrollViewer(OldList);
        var newScroll = FindScrollViewer(NewList);
        if (oldScroll == null || newScroll == null || ReferenceEquals(oldScroll, _oldScroll)) return;
        _oldScroll = oldScroll;
        _newScroll = newScroll;
        oldScroll.ScrollChanged += (_, e) => Follow(e, newScroll);
        newScroll.ScrollChanged += (_, e) => Follow(e, oldScroll);
    }

    private void Follow(ScrollChangedEventArgs e, ScrollViewer other)
    {
        if (_syncing || SyncScroll.IsChecked != true || e.VerticalChange == 0) return;
        if (e.OriginalSource is not ScrollViewer source || source.ScrollableHeight <= 0) return;
        _syncing = true;
        other.ScrollToVerticalOffset(source.VerticalOffset / source.ScrollableHeight * other.ScrollableHeight);
        Dispatcher.BeginInvoke(() => _syncing = false, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    // ------------------------------------------------------------------ jumping to a change

    private void ShowChange(TextChange change)
    {
        if (_controller == null) return;
        _syncing = true;
        try
        {
            ScrollTo(OldList, _controller.OldPages, change.OldLocations.Count > 0 ? change.OldLocations[0] : new WordLocation(change.Page, Rect.Empty));
            ScrollTo(NewList, _controller.NewPages, change.NewLocations.Count > 0 ? change.NewLocations[0] : new WordLocation(change.Page, Rect.Empty));
        }
        finally
        {
            Dispatcher.BeginInvoke(() => _syncing = false, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private static void ScrollTo(ListBox list, IList<ComparePage> pages, WordLocation where)
    {
        if (pages.Count == 0) return;
        var page = pages[Math.Clamp(where.Page, 0, pages.Count - 1)];
        list.ScrollIntoView(page);
        list.UpdateLayout();
        if (FindScrollViewer(list) is not { } sv || list.ItemContainerGenerator.ContainerFromItem(page) is not FrameworkElement container) return;
        var top = container.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
        var scale = container.ActualWidth / Math.Max(1, page.Size.Width);
        var y = where.Bounds.IsEmpty ? 0 : where.Bounds.Y * scale;
        sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset + top + y - sv.ViewportHeight * 0.3));
    }
}
