using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using PageSmith.App.Tools.Editor;
using Wpf.Ui.Controls;
using TextBox = System.Windows.Controls.TextBox;

namespace PageSmith.App.Controls;

/// <summary>
/// Shows one page of a PDF with the items a tool places on it, and lets the user add, select, move and
/// resize them. The page surface is laid out in PDF points and scaled for zoom, so item coordinates
/// are exactly what the PDF engines expect.
/// </summary>
public partial class PageEditor : UserControl
{
    private enum DragMode { None, Move, Resize, Draw, Ink }

    private const double MinScale = 0.25, MaxScale = 6;

    private readonly ObservableCollection<EditorItem> _visible = [];
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private EditorController? _controller;
    private double _scale = 1;
    private bool _fitWidth = true;
    private int _renderVersion;

    private DragMode _drag;
    private Point _dragStart;
    private Rect _originalBounds;
    private int _handle;
    private bool _originalRising;
    private Rectangle? _preview;
    private Polyline? _inkPreview;
    private List<Point>? _stroke;

    public PageEditor()
    {
        InitializeComponent();
        ItemsLayer.ItemsSource = _visible;
        _renderTimer.Tick += async (_, _) =>
        {
            _renderTimer.Stop();
            await RenderAsync();
        };
        DataContextChanged += (_, _) => Attach(DataContext as EditorController);
        Loaded += (_, _) =>
        {
            Attach(DataContext as EditorController);
            Focus();
        };
        Unloaded += (_, _) => Attach(null);
    }

    private EditorController C => _controller!;

    // ------------------------------------------------------------------ wiring

    private void Attach(EditorController? controller)
    {
        if (ReferenceEquals(controller, _controller)) return;
        if (_controller != null)
        {
            _controller.PropertyChanged -= OnControllerChanged;
            _controller.Items.CollectionChanged -= OnItemsChanged;
        }
        _controller = controller;
        if (controller == null) return;
        controller.PropertyChanged += OnControllerChanged;
        controller.Items.CollectionChanged += OnItemsChanged;
        BuildToolButtons();
        ShowPage();
    }

    private void BuildToolButtons()
    {
        ToolButtons.Children.Clear();
        foreach (var tool in C.Tools)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("ToolSegment"),
                Content = new SymbolIcon(tool.Icon) { FontSize = 18 },
                ToolTip = tool.Label,
                Tag = tool,
                GroupName = "EditorTools_" + GetHashCode(),
                IsChecked = ReferenceEquals(tool, C.ActiveTool),
            };
            button.Checked += (_, _) => C.ActiveTool = tool;
            ToolButtons.Children.Add(button);
        }
    }

    private void SyncToolButtons()
    {
        foreach (var b in ToolButtons.Children.OfType<RadioButton>()) b.IsChecked = ReferenceEquals(b.Tag, C.ActiveTool);
    }

    private void OnControllerChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(EditorController.CurrentPageIndex):
                ShowPage();
                break;
            case nameof(EditorController.SelectedItem):
                DrawAdorners();
                break;
            case nameof(EditorController.ActiveTool):
                SyncToolButtons();
                UpdateCursor(null);
                break;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (EditorItem item in e.OldItems) item.PropertyChanged -= OnItemChanged;
        RefreshVisible();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorItem.Bounds) && ReferenceEquals(sender, C.SelectedItem)) DrawAdorners();
    }

    private void RefreshVisible()
    {
        if (_controller == null) return;
        foreach (var item in _visible) item.PropertyChanged -= OnItemChanged;
        _visible.Clear();
        foreach (var item in C.ItemsOn(C.CurrentPageIndex))
        {
            _visible.Add(item);
            item.PropertyChanged += OnItemChanged;
        }
        DrawAdorners();
    }

    // ------------------------------------------------------------------ page and zoom

    private void ShowPage()
    {
        if (_controller == null || C.CurrentPage is not { } page) return;
        Surface.Width = page.Size.Width;
        Surface.Height = page.Size.Height;
        PageImage.Source = null;
        RefreshVisible();
        if (_fitWidth) FitWidth();
        else ApplyScale(_scale);
        PageList.ScrollIntoView(page);
        Scroller.ScrollToTop();
    }

    private void FitWidth()
    {
        if (_controller == null || C.CurrentPage is not { } page) return;
        var available = Scroller.ViewportWidth > 0 ? Scroller.ViewportWidth : Scroller.ActualWidth;
        if (available <= 0) return;
        // Fit the width, but don't blow small pages up beyond 160%.
        ApplyScale(Math.Clamp((available - 52) / page.Size.Width, MinScale, 160 / 75.0));
    }

    private void ApplyScale(double scale)
    {
        _scale = Math.Clamp(scale, MinScale, MaxScale);
        PageScale.ScaleX = PageScale.ScaleY = _scale;
        ZoomButton.Content = $"{Math.Round(_scale * 75)}%";
        DrawAdorners();
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private async Task RenderAsync()
    {
        if (_controller == null || C.CurrentPage is not { } page) return;
        var version = ++_renderVersion;
        var dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var dpi = Math.Clamp(72 * _scale * dpiScale, 36, 400);
        if (PageImage.Source == null) Loading.Visibility = Visibility.Visible;
        var bitmap = await C.RenderPageAsync(page.Index, dpi);
        if (version != _renderVersion || C.CurrentPage != page) return;
        Loading.Visibility = Visibility.Collapsed;
        if (bitmap != null) PageImage.Source = bitmap;
    }

    private void Zoom(double factor)
    {
        _fitWidth = false;
        ApplyScale(_scale * factor);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Zoom(1.25);

    private void OnZoomOut(object sender, RoutedEventArgs e) => Zoom(1 / 1.25);

    private void OnFitWidth(object sender, RoutedEventArgs e)
    {
        _fitWidth = true;
        FitWidth();
    }

    private void OnScrollerSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_fitWidth) FitWidth();
    }

    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        Zoom(e.Delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ selection handles

    private double HandleSize => 9 / _scale;

    private static readonly Cursor[] HandleCursors =
        [Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE, Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE];

    /// <summary>Handle positions 0-7 clockwise from the top-left corner.</summary>
    private static Point HandlePoint(Rect r, int i) => i switch
    {
        0 => r.TopLeft,
        1 => new Point(r.X + r.Width / 2, r.Y),
        2 => r.TopRight,
        3 => new Point(r.Right, r.Y + r.Height / 2),
        4 => r.BottomRight,
        5 => new Point(r.X + r.Width / 2, r.Bottom),
        6 => r.BottomLeft,
        _ => new Point(r.X, r.Y + r.Height / 2),
    };

    private IEnumerable<int> Handles(EditorItem item) =>
        !item.CanResize ? [] : item.KeepAspectRatio ? [0, 2, 4, 6] : [0, 1, 2, 3, 4, 5, 6, 7];

    private void DrawAdorners()
    {
        Adorners.Children.Clear();
        if (_controller == null || C.SelectedItem is not { } item || item.PageIndex != C.CurrentPageIndex) return;
        var accent = TryFindResource("SystemAccentColorPrimaryBrush") as Brush ?? Brushes.DodgerBlue;
        var r = item.Bounds;
        var frame = new Rectangle
        {
            Width = Math.Max(0.5, r.Width),
            Height = Math.Max(0.5, r.Height),
            Stroke = accent,
            StrokeThickness = 1.5 / _scale,
            StrokeDashArray = [4, 2],
        };
        Canvas.SetLeft(frame, r.X);
        Canvas.SetTop(frame, r.Y);
        Adorners.Children.Add(frame);
        var size = HandleSize;
        foreach (var i in Handles(item))
        {
            var p = HandlePoint(r, i);
            var handle = new Rectangle { Width = size, Height = size, Fill = Brushes.White, Stroke = accent, StrokeThickness = 1.5 / _scale, RadiusX = size / 4, RadiusY = size / 4 };
            Canvas.SetLeft(handle, p.X - size / 2);
            Canvas.SetTop(handle, p.Y - size / 2);
            Adorners.Children.Add(handle);
        }
    }

    private int HitHandle(Point p)
    {
        if (C.SelectedItem is not { } item || item.PageIndex != C.CurrentPageIndex) return -1;
        var grab = HandleSize * 0.9;
        foreach (var i in Handles(item))
        {
            var h = HandlePoint(item.Bounds, i);
            if (Math.Abs(p.X - h.X) <= grab && Math.Abs(p.Y - h.Y) <= grab) return i;
        }
        return -1;
    }

    private EditorItem? HitItem(Point p)
    {
        var tolerance = 3 / _scale;
        for (var i = _visible.Count - 1; i >= 0; i--)
        {
            var item = _visible[i];
            var r = item.Bounds;
            r.Inflate(tolerance, tolerance);
            if (r.Contains(p)) return item;
        }
        return null;
    }

    // ------------------------------------------------------------------ mouse

    private void OnSurfaceDown(object sender, MouseButtonEventArgs e)
    {
        if (_controller == null || C.CurrentPage is not { } page) return;
        Focus();
        var p = e.GetPosition(Surface);
        var tool = C.ActiveTool;
        var action = tool?.Action ?? PointerAction.Select;

        if (action != PointerAction.Ink && HitHandle(p) is var handle and >= 0)
        {
            StartDrag(DragMode.Resize, p, C.SelectedItem!);
            _handle = handle;
            e.Handled = true;
            return;
        }

        var hit = HitItem(p);
        if (action == PointerAction.Ink)
        {
            _stroke = [p];
            _inkPreview = new Polyline
            {
                Stroke = (Brush)new Infrastructure.HexToBrushConverter().Convert(C.InkPen.Color, typeof(Brush), null, System.Globalization.CultureInfo.InvariantCulture),
                StrokeThickness = C.InkPen.Width,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Points = [p],
            };
            Adorners.Children.Add(_inkPreview);
            _drag = DragMode.Ink;
            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (hit != null && (action != PointerAction.Interact || hit is not FieldItem { IsExisting: true }))
        {
            if (!ReferenceEquals(C.SelectedItem, hit)) C.SelectedItem = hit;
            if (hit is TextItem text && e.ClickCount >= 2)
            {
                text.IsEditing = true;
                e.Handled = true;
                return;
            }
            if (hit.CanMove) StartDrag(DragMode.Move, p, hit);
            e.Handled = true;
            return;
        }

        C.SelectedItem = null;
        switch (action)
        {
            case PointerAction.DrawRect:
                _preview = new Rectangle
                {
                    Stroke = TryFindResource("SystemAccentColorPrimaryBrush") as Brush ?? Brushes.DodgerBlue,
                    StrokeThickness = 1.5 / _scale,
                    StrokeDashArray = [4, 2],
                    Fill = new SolidColorBrush(Color.FromArgb(30, 0, 120, 215)),
                };
                Adorners.Children.Add(_preview);
                _dragStart = p;
                _drag = DragMode.Draw;
                Surface.CaptureMouse();
                UpdatePreview(p);
                break;
            case PointerAction.Click when tool != null:
                if (C.CreateAt(tool, page.Index, p) is { } created) Created(tool, created);
                break;
            case PointerAction.Pick when tool != null:
                _ = C.PickAsync(tool, page.Index, p);
                break;
        }
        e.Handled = true;
    }

    private void StartDrag(DragMode mode, Point p, EditorItem item)
    {
        _drag = mode;
        _dragStart = p;
        _originalBounds = item.Bounds;
        _originalRising = item is ShapeItem s && s.RisingDiagonal;
        Surface.CaptureMouse();
    }

    private void OnSurfaceMove(object sender, MouseEventArgs e)
    {
        if (_controller == null || C.CurrentPage is not { } page) return;
        var p = e.GetPosition(Surface);
        switch (_drag)
        {
            case DragMode.Move when C.SelectedItem is { } item:
            {
                var dx = p.X - _dragStart.X;
                var dy = p.Y - _dragStart.Y;
                var r = _originalBounds;
                var x = Math.Clamp(r.X + dx, -r.Width + 4, page.Size.Width - 4);
                var y = Math.Clamp(r.Y + dy, -r.Height + 4, page.Size.Height - 4);
                item.SetBounds(new Rect(x, y, r.Width, r.Height));
                break;
            }
            case DragMode.Resize when C.SelectedItem is { } item:
                item.SetBounds(Resized(item, p, page.Size));
                break;
            case DragMode.Draw:
                UpdatePreview(p);
                break;
            case DragMode.Ink when _stroke != null && _inkPreview != null:
                if ((p - _stroke[^1]).Length >= 0.75 / _scale)
                {
                    _stroke.Add(p);
                    _inkPreview.Points.Add(p);
                }
                break;
            default:
                UpdateCursor(p);
                break;
        }
    }

    private Rect Resized(EditorItem item, Point p, Size pageSize)
    {
        var o = _originalBounds;
        p = new Point(Math.Clamp(p.X, 0, pageSize.Width), Math.Clamp(p.Y, 0, pageSize.Height));
        double left = o.Left, top = o.Top, right = o.Right, bottom = o.Bottom;
        if (_handle is 0 or 6 or 7) left = p.X;
        if (_handle is 2 or 3 or 4) right = p.X;
        if (_handle is 0 or 1 or 2) top = p.Y;
        if (_handle is 4 or 5 or 6) bottom = p.Y;

        if (item.KeepAspectRatio && o.Width > 0 && o.Height > 0)
        {
            // Corner handles: keep the proportions, anchored at the opposite corner.
            var ratio = o.Width / o.Height;
            var w = Math.Abs(right - left);
            var h = Math.Abs(bottom - top);
            if (w / ratio > h) h = w / ratio;
            else w = h * ratio;
            if (_handle is 0 or 6) left = right - w;
            else right = left + w;
            if (_handle is 0 or 2) top = bottom - h;
            else bottom = top + h;
        }

        var flippedX = right < left;
        var flippedY = bottom < top;
        if (item is ShapeItem { IsLine: true } line) line.RisingDiagonal = _originalRising ^ (flippedX ^ flippedY);
        var r = new Rect(new Point(left, top), new Point(right, bottom));
        return new Rect(r.X, r.Y, Math.Max(item.MinWidth, r.Width), Math.Max(item.MinHeight, r.Height));
    }

    private void UpdatePreview(Point p)
    {
        if (_preview == null) return;
        var r = new Rect(_dragStart, p);
        Canvas.SetLeft(_preview, r.X);
        Canvas.SetTop(_preview, r.Y);
        _preview.Width = r.Width;
        _preview.Height = r.Height;
    }

    private void OnSurfaceUp(object sender, MouseButtonEventArgs e)
    {
        if (_controller == null || C.CurrentPage is not { } page) return;
        var p = e.GetPosition(Surface);
        var mode = _drag;
        _drag = DragMode.None;
        Surface.ReleaseMouseCapture();
        switch (mode)
        {
            case DragMode.Move or DragMode.Resize when C.SelectedItem is { } item:
                C.RecordBoundsChange(item, _originalBounds, item.Bounds);
                break;
            case DragMode.Draw:
            {
                if (_preview != null) Adorners.Children.Remove(_preview);
                _preview = null;
                var r = new Rect(_dragStart, p);
                r.Intersect(new Rect(page.Size));
                if (C.ActiveTool is { } tool && !r.IsEmpty && C.CreateFromRect(tool, page.Index, r) is { } created) Created(tool, created);
                break;
            }
            case DragMode.Ink:
            {
                if (_inkPreview != null) Adorners.Children.Remove(_inkPreview);
                _inkPreview = null;
                if (_stroke is { Count: > 0 } stroke)
                {
                    var (color, width) = C.InkPen;
                    var ink = new InkItem { PageIndex = page.Index, Color = color, Width = width };
                    ink.Strokes.Add(stroke);
                    ink.FitBounds();
                    C.AddItem(ink, select: false);
                }
                _stroke = null;
                break;
            }
        }
        DrawAdorners();
    }

    private void Created(EditorTool tool, EditorItem item)
    {
        C.AddItem(item);
        if (item is TextItem text) text.IsEditing = true;
        if (!C.KeepToolAfterCreate(tool)) C.ActiveTool = C.Tools.FirstOrDefault(t => t.Action is PointerAction.Select or PointerAction.Interact) ?? tool;
    }

    private void UpdateCursor(Point? p)
    {
        if (_controller == null) return;
        var action = C.ActiveTool?.Action ?? PointerAction.Select;
        Cursor cursor = action switch
        {
            PointerAction.DrawRect or PointerAction.Ink => Cursors.Cross,
            PointerAction.Click => C.ActiveTool?.Id.Contains("text", StringComparison.OrdinalIgnoreCase) == true ? Cursors.IBeam : Cursors.Hand,
            PointerAction.Pick => Cursors.IBeam,
            _ => Cursors.Arrow,
        };
        if (p is { } point && action != PointerAction.Ink)
        {
            if (HitHandle(point) is var h and >= 0) cursor = HandleCursors[h];
            else if (HitItem(point) is { CanMove: true } item && (action != PointerAction.Interact || item is not FieldItem { IsExisting: true })) cursor = Cursors.SizeAll;
        }
        Surface.Cursor = cursor;
    }

    // ------------------------------------------------------------------ text editing

    private void OnEditBoxVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is TextBox box && box.IsVisible)
            Dispatcher.BeginInvoke(() =>
            {
                box.Focus();
                box.CaretIndex = box.Text.Length;
            }, DispatcherPriority.Input);
    }

    private void OnEditBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { DataContext: TextItem item }) item.IsEditing = false;
    }

    // ------------------------------------------------------------------ keyboard

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_controller == null) return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var typing = Keyboard.FocusedElement is TextBox or ComboBox;

        if (e.Key == Key.Escape)
        {
            if (C.SelectedItem is TextItem { IsEditing: true } t)
            {
                t.IsEditing = false;
                Focus();
            }
            else if (C.SelectedItem != null) C.SelectedItem = null;
            else C.ActiveTool = C.Tools.FirstOrDefault();
            e.Handled = true;
            return;
        }
        if (typing) return;

        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                C.DeleteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Z when ctrl && shift:
            case Key.Y when ctrl:
                C.RedoCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Z when ctrl:
                C.UndoCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add when ctrl:
                Zoom(1.25);
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract when ctrl:
                Zoom(1 / 1.25);
                e.Handled = true;
                break;
            case Key.PageDown:
                if (C.NextPageCommand.CanExecute(null)) C.NextPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.PageUp:
                if (C.PreviousPageCommand.CanExecute(null)) C.PreviousPageCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when C.SelectedItem is { CanMove: true } item:
            {
                var step = shift ? 10 : 1;
                var (dx, dy) = e.Key switch { Key.Left => (-step, 0), Key.Right => (step, 0), Key.Up => (0, -step), _ => (0, step) };
                var before = item.Bounds;
                item.SetBounds(new Rect(before.X + dx, before.Y + dy, before.Width, before.Height));
                C.RecordBoundsChange(item, before, item.Bounds);
                e.Handled = true;
                break;
            }
            case Key.Enter when C.SelectedItem is TextItem text:
                text.IsEditing = true;
                e.Handled = true;
                break;
        }
    }
}

/// <summary>Picks how an item is drawn on the page.</summary>
public sealed class EditorVisualSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        var key = item switch
        {
            TextItem => "TextVisual",
            ImageItem => "ImageVisual",
            FieldItem { IsExisting: true } => "FieldVisual",
            _ => "ElementVisual",
        };
        return (container as FrameworkElement)?.TryFindResource(key) as DataTemplate;
    }
}
