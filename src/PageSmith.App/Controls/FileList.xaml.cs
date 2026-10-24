using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PageSmith.App.Infrastructure;
using PageSmith.App.Tools;

namespace PageSmith.App.Controls;

/// <summary>Drop zone and file cards for tools that work on whole files.</summary>
public partial class FileList : UserControl
{
    private Point _dragStart;
    private FileEntry? _dragged;

    public FileList()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Refresh();
        Loaded += (_, _) => Refresh();
    }

    private ToolController? Controller => DataContext as ToolController;

    private void Refresh()
    {
        if (Controller is not { } c) return;
        var kind = c.Descriptor.Input switch
        {
            InputKind.Pdf => "PDF",
            InputKind.Images => "image",
            InputKind.Word => "Word",
            InputKind.Excel => "Excel",
            InputKind.PowerPoint => "PowerPoint",
            InputKind.Html => "HTML",
            _ => "",
        };
        BrowseText.Text = $"Select {kind} file{(c.Descriptor.MultipleFiles ? "s" : "")}";
        FormatsText.Text = string.Join("  ", c.Descriptor.Extensions.Take(12).Select(e => e.TrimStart('.').ToUpperInvariant()));
        AddButton.Visibility = c.Descriptor.MultipleFiles ? Visibility.Visible : Visibility.Collapsed;
        SortAscButton.Visibility = SortDescButton.Visibility = c.CanReorder ? Visibility.Visible : Visibility.Collapsed;
        c.Files.CollectionChanged -= OnFilesChanged;
        c.Files.CollectionChanged += OnFilesChanged;
        UpdateCount();
    }

    private void OnFilesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        if (Controller is not { } c) return;
        CountText.Text = c.Files.Count == 1 ? "1 file" : $"{c.Files.Count} files";
    }

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (Controller is not { } c) return;
        var files = AppServices.PickFiles(c.Descriptor);
        if (files.Length > 0) await c.AddFilesAsync(files);
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy
            : e.Data.GetDataPresent(typeof(FileEntry)) ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Controller is not { } c) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            e.Handled = true;
            await c.AddFilesAsync(files);
        }
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragged = (e.OriginalSource as FrameworkElement)?.DataContext as FileEntry;
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragged == null || e.LeftButton != MouseButtonState.Pressed || Controller?.CanReorder != true) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;
        DragDrop.DoDragDrop(List, new DataObject(typeof(FileEntry), _dragged), DragDropEffects.Move);
        _dragged = null;
    }

    private void OnItemDrop(object sender, DragEventArgs e)
    {
        if (Controller is not { } c || e.Data.GetData(typeof(FileEntry)) is not FileEntry source) return;
        if ((sender as FrameworkElement)?.DataContext is not FileEntry target || ReferenceEquals(source, target)) return;
        c.Files.Move(c.Files.IndexOf(source), c.Files.IndexOf(target));
        e.Handled = true;
    }

    private static FileEntry? EntryOf(object sender) => (sender as FrameworkElement)?.DataContext as FileEntry;

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) Controller?.Files.Remove(entry);
    }

    private void OnRotate(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) entry.Rotation = (entry.Rotation + 90) % 360;
    }

    private void OnMoveLeft(object sender, RoutedEventArgs e) => Move(EntryOf(sender), -1);

    private void OnMoveRight(object sender, RoutedEventArgs e) => Move(EntryOf(sender), 1);

    private void Move(FileEntry? entry, int delta)
    {
        if (entry == null || Controller is not { } c) return;
        var index = c.Files.IndexOf(entry);
        var target = Math.Clamp(index + delta, 0, c.Files.Count - 1);
        if (target != index) c.Files.Move(index, target);
    }

    private void OnSortAscending(object sender, RoutedEventArgs e) => Sort(false);

    private void OnSortDescending(object sender, RoutedEventArgs e) => Sort(true);

    private void Sort(bool descending)
    {
        if (Controller is not { } c) return;
        var sorted = (descending ? c.Files.OrderByDescending(f => f.Name, StringComparer.CurrentCultureIgnoreCase) : c.Files.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
        for (var i = 0; i < sorted.Count; i++) c.Files.Move(c.Files.IndexOf(sorted[i]), i);
    }

    private void OnClear(object sender, RoutedEventArgs e) => Controller?.Files.Clear();

    private async void OnUnlock(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry || Controller is not { } c) return;
        var wrong = false;
        while (true)
        {
            var password = await AppServices.AskPasswordAsync(entry.Name, wrong);
            if (password == null) return;
            entry.Password = password;
            entry.Note = null;
            await c.LoadDetailsAsync(entry);
            if (!entry.NeedsPassword) return;
            wrong = true;
        }
    }
}
