using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PageSmith.App.Infrastructure;
using PageSmith.App.Tools;

namespace PageSmith.App.Controls;

/// <summary>Grid of page thumbnails: selection (with Shift ranges), per-page actions and drag reordering.</summary>
public partial class PageGrid : UserControl
{
    private Point _dragStart;
    private PageItem? _pressed;
    private PageItem? _anchor;
    private bool _dragging;

    public PageGrid()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Controller is { } c)
            {
                AddButton.Visibility = c.Descriptor.MultipleFiles ? Visibility.Visible : Visibility.Collapsed;
                RotateLeftButton.Visibility = RotateRightButton.Visibility = c.AllowRotate ? Visibility.Visible : Visibility.Collapsed;
            }
        };
    }

    private PageGridController? Controller => DataContext as PageGridController;

    private static PageItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as PageItem;

    private void OnCardLoaded(object sender, RoutedEventArgs e) => ItemOf(sender)?.EnsureThumbnail();

    private void OnCardClick(object sender, MouseButtonEventArgs e)
    {
        if (_dragging || Controller is not { ClickSelects: true } c || ItemOf(sender) is not { } item) return;
        if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.Primitives.ButtonBase>(d) != null) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _anchor != null)
        {
            var a = c.Pages.IndexOf(_anchor);
            var b = c.Pages.IndexOf(item);
            for (var i = Math.Min(a, b); i <= Math.Max(a, b); i++) c.Pages[i].IsSelected = true;
        }
        else
        {
            item.IsSelected = !item.IsSelected;
            _anchor = item;
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        _dragStart = e.GetPosition(null);
        _pressed = (e.OriginalSource as FrameworkElement)?.DataContext as PageItem;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressed == null || e.LeftButton != MouseButtonState.Pressed || Controller?.AllowReorder != true) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2 && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        if (e.OriginalSource is DependencyObject d && FindParent<System.Windows.Controls.Primitives.ButtonBase>(d) != null) return;
        _dragging = true;
        DragDrop.DoDragDrop(List, new DataObject(typeof(PageItem), _pressed), DragDropEffects.Move);
        _pressed = null;
    }

    private void OnItemDrop(object sender, DragEventArgs e)
    {
        if (Controller is not { } c || e.Data.GetData(typeof(PageItem)) is not PageItem source) return;
        if (ItemOf(sender) is not { } target || ReferenceEquals(source, target)) return;
        // Dragging a selected page moves the whole selection.
        var moving = source.IsSelected ? c.Pages.Where(p => p.IsSelected).ToList() : [source];
        var targetIndex = c.Pages.IndexOf(target);
        var forward = c.Pages.IndexOf(source) < targetIndex;
        foreach (var p in moving) c.Pages.Remove(p);
        var insertAt = c.Pages.IndexOf(target) + (forward ? 1 : 0);
        if (moving.Contains(target)) insertAt = Math.Min(targetIndex, c.Pages.Count);
        for (var i = 0; i < moving.Count; i++) c.Pages.Insert(Math.Clamp(insertAt + i, 0, c.Pages.Count), moving[i]);
        c.Renumber();
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
            : e.Data.GetDataPresent(typeof(PageItem)) ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Controller is { } c && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            await c.AddFilesAsync(files);
        }
    }

    private async void OnAddFiles(object sender, RoutedEventArgs e)
    {
        if (Controller is not { } c) return;
        var files = AppServices.PickFiles(c.Descriptor);
        if (files.Length > 0) await c.AddFilesAsync(files);
    }

    private void OnRotateRight(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } p) p.Rotation = (p.Rotation + 90) % 360;
    }

    private void OnRotateLeft(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } p) p.Rotation = (p.Rotation + 270) % 360;
    }

    private void OnDelete(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } p || Controller is not { } c) return;
        c.Pages.Remove(p);
        c.Renumber();
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } p && Controller is OrganizeController c) c.Duplicate(p);
    }

    private void OnInsertBlank(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } p && Controller is OrganizeController c) c.InsertBlank(c.Pages.IndexOf(p) + 1);
    }

    internal static T? FindParent<T>(DependencyObject child) where T : DependencyObject
    {
        for (var d = child; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is T t) return t;
        return null;
    }
}
