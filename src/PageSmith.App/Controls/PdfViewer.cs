using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using PageSmith.App.Documents;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Pdf;
using PageSmith.Core.Text;

namespace PageSmith.App.Controls;

public enum ZoomMode
{
    FitWidth,
    FitPage,
    Custom,
}

public enum ViewerTool
{
    /// <summary>Drag to select text.</summary>
    Select,

    /// <summary>Drag to move the pages around.</summary>
    Hand,

    /// <summary>Click to place a sticky note.</summary>
    Note,
}

/// <summary>A point on a page, in display-space points.</summary>
public readonly record struct PagePoint(int Page, Point Point);

/// <summary>
/// A document's pages in one continuous scrolling view, like a PDF reader. Only the pages in or near
/// the view are rendered, sharper as you zoom in; zooming keeps the point under the mouse in place.
/// Text can be selected (in reading order, across pages) and search matches are highlighted.
/// </summary>
public sealed class PdfViewer : UserControl
{
    private const double PointToDip = 96.0 / 72.0;
    private const double Gap = 12, Edge = 16, ScrollBarAllowance = 18;
    public const double MinZoom = 0.1, MaxZoom = 8;
    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2, 2.5, 3, 4, 5, 6, 8];

    /// <summary>Rendered pages kept for scrolling back, per tab.</summary>
    private const long CacheBudget = 160L * 1024 * 1024;

    private static readonly Brush SelectionBrush = Frozen(Color.FromArgb(0x55, 0x2F, 0x80, 0xED));
    private static readonly Brush HitBrush = Frozen(Color.FromArgb(0x66, 0xFF, 0xD4, 0x00));
    private static readonly Brush CurrentHitBrush = Frozen(Color.FromArgb(0x99, 0xFF, 0x8C, 0x00));

    private readonly ScrollViewer _scroll;
    private readonly Canvas _canvas;
    private readonly Dictionary<int, PageView> _views = [];
    private readonly Dictionary<int, CachedPage> _cache = [];
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly DispatcherTimer _spotTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private (int Page, Rect Box)? _spot;
    private ILookup<int, PageComment> _comments = Array.Empty<PageComment>().ToLookup(c => c.PageIndex);
    private DocumentSession? _session;
    private double[] _tops = [];
    private double _contentWidth;
    private double _zoom = 1;
    private int _version;
    private bool _pumping;
    private long _tick;
    private int _currentPage;

    private IReadOnlyList<TextHit> _hits = [];
    private ILookup<int, int> _hitsByPage = Array.Empty<int>().ToLookup(i => i);
    private int _currentHit = -1;
    private TextPosition? _anchor, _focus;

    private enum Drag { None, Pending, Select, Pan }

    private Drag _drag;
    private Point _dragStart;
    private Point _panStartOffset;
    private (int Page, Point Point)? _pressAt;
    private PageLink? _pressLink;
    private ILookup<int, PageLink> _links = Array.Empty<PageLink>().ToLookup(l => l.PageIndex);
    private ViewerTool _tool;

    public PdfViewer()
    {
        _canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true };
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            CanContentScroll = false,
            Focusable = true,
            FocusVisualStyle = null,
            PanningMode = PanningMode.Both,
            Content = _canvas,
        };
        Content = _scroll;
        SetResourceReference(BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        UseLayoutRounding = true;

        _scroll.ScrollChanged += (_, e) =>
        {
            if (e.ViewportWidthChange != 0) RecenterPages();
            UpdateVisible();
        };
        SizeChanged += (_, _) =>
        {
            if (Mode != ZoomMode.Custom) ApplyZoom(FitZoom(), new Point(0, 0));
            else RecenterPages();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) UpdateVisible();
            else _cache.Clear();
        };
        _renderTimer.Tick += (_, _) =>
        {
            _renderTimer.Stop();
            Pump();
        };
        _autoScroll.Tick += OnAutoScroll;
        _scroll.PreviewMouseWheel += OnWheel;
        _scroll.PreviewKeyDown += OnKey;
        _canvas.MouseLeftButtonDown += OnLeftDown;
        _canvas.MouseDown += OnOtherDown;
        _canvas.PreviewMouseRightButtonDown += (_, e) =>
            ContextPoint = HitPage(e.GetPosition(_canvas), nearest: false) is { } hit ? new PagePoint(hit.Page, hit.Point) : null;
        _spotTimer.Tick += (_, _) =>
        {
            _spotTimer.Stop();
            var page = _spot?.Page;
            _spot = null;
            if (page is { } p && _views.TryGetValue(p, out var view)) DrawOverlay(view);
        };
        _canvas.MouseMove += OnMove;
        _canvas.MouseUp += OnUp;
        _canvas.LostMouseCapture += (_, _) => EndDrag();
    }

    public event EventHandler? ZoomChanged;
    public event EventHandler? CurrentPageChanged;
    public event EventHandler? SelectionChanged;

    /// <summary>A link was clicked; the reader goes to its page or offers to open its address.</summary>
    public event EventHandler<PageLink>? LinkClicked;

    /// <summary>With the note tool, a page was clicked where a note should go.</summary>
    public event EventHandler<PagePoint>? NotePlaced;

    /// <summary>Where the context menu was opened on a page, if it was.</summary>
    public PagePoint? ContextPoint { get; private set; }

    /// <summary>1 is actual size (100 %).</summary>
    public double Zoom => _zoom;

    public ZoomMode Mode { get; private set; } = ZoomMode.FitWidth;

    public int CurrentPage => _currentPage;

    public int PageCount => _tops.Length;

    public ViewerTool Tool
    {
        get => _tool;
        set
        {
            _tool = value;
            _canvas.Cursor = ToolCursor;
        }
    }

    public DocumentSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;
            if (_session != null) _session.VersionChanged -= OnVersionChanged;
            _session = value;
            if (value != null) value.VersionChanged += OnVersionChanged;
            Reset(keepPosition: false);
        }
    }

    private Cursor? ToolCursor => _tool switch
    {
        ViewerTool.Hand => Cursors.Hand,
        ViewerTool.Note => Cursors.Cross,
        _ => null,
    };

    private double Scale => _zoom * PointToDip;

    private double ViewportWidth => _scroll.ViewportWidth > 0 ? _scroll.ViewportWidth : ActualWidth;

    private double ViewportHeight => _scroll.ViewportHeight > 0 ? _scroll.ViewportHeight : ActualHeight;

    /// <summary>The current version's text, once it has been read (see <see cref="DocumentSession.GetTextAsync"/>).</summary>
    private TextIndex? Text => _session?.GetTextAsync() is { IsCompletedSuccessfully: true } t ? t.Result : null;

    private Size PageSize(int index) => _session!.PageSizes[index];

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public void FocusView() => _scroll.Focus();

    // ------------------------------------------------------------------ layout

    private void OnVersionChanged(object? sender, EventArgs e) => Reset(keepPosition: true);

    /// <summary>Starts over with the session's current version, keeping the reading position if asked.</summary>
    private void Reset(bool keepPosition)
    {
        var anchor = keepPosition && _tops.Length > 0 ? CaptureAnchor() : ((int Page, double Y)?)null;
        _version++;
        _cache.Clear();
        foreach (var v in _views.Values) _canvas.Children.Remove(v);
        _views.Clear();
        _hits = [];
        _hitsByPage = Array.Empty<int>().ToLookup(i => i);
        _currentHit = -1;
        _links = Array.Empty<PageLink>().ToLookup(l => l.PageIndex);
        _comments = Array.Empty<PageComment>().ToLookup(c => c.PageIndex);
        _spot = null;
        var hadSelection = _anchor != null;
        _anchor = _focus = null;
        if (Mode != ZoomMode.Custom && ActualWidth > 0) _zoom = FitZoom();
        Relayout();
        _scroll.UpdateLayout();
        if (anchor is { } a) RestoreAnchor(a);
        else _scroll.ScrollToVerticalOffset(0);
        _ = _session?.GetTextAsync();
        UpdateVisible();
        if (hadSelection) SelectionChanged?.Invoke(this, EventArgs.Empty);
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    private (int Page, double Y) CaptureAnchor()
    {
        var y = _scroll.VerticalOffset;
        var page = PageAt(y);
        return (page, (y - _tops[page]) / Scale);
    }

    private void RestoreAnchor((int Page, double Y) anchor)
    {
        if (_tops.Length == 0) return;
        var page = Math.Min(anchor.Page, _tops.Length - 1);
        _scroll.ScrollToVerticalOffset(_tops[page] + anchor.Y * Scale);
    }

    private void Relayout()
    {
        var sizes = _session?.PageSizes ?? [];
        var scale = Scale;
        _tops = new double[sizes.Count];
        double y = Edge, widest = 0;
        for (var i = 0; i < sizes.Count; i++)
        {
            _tops[i] = y;
            y += sizes[i].Height * scale + Gap;
            widest = Math.Max(widest, sizes[i].Width * scale);
        }
        _contentWidth = widest + 2 * Edge;
        _canvas.Height = sizes.Count == 0 ? 0 : y - Gap + Edge;
        RecenterPages();
    }

    private void RecenterPages()
    {
        _canvas.Width = Math.Max(ViewportWidth, _contentWidth);
        foreach (var v in _views.Values) Place(v);
    }

    private double PageLeft(int index, double scale) => (_canvas.Width - PageSize(index).Width * scale) / 2;

    private void Place(PageView v)
    {
        var scale = Scale;
        var size = PageSize(v.Index);
        v.Width = size.Width * scale;
        v.Height = size.Height * scale;
        Canvas.SetLeft(v, PageLeft(v.Index, scale));
        Canvas.SetTop(v, _tops[v.Index]);
        v.OverlayScale.ScaleX = v.OverlayScale.ScaleY = scale;
    }

    /// <summary>The page at a vertical position of the canvas (the one above, in the gap between two).</summary>
    private int PageAt(double y)
    {
        var i = Array.BinarySearch(_tops, y);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, Math.Max(0, _tops.Length - 1));
    }

    /// <summary>Creates views for the pages in or near the viewport, drops the others and queues rendering.</summary>
    private void UpdateVisible()
    {
        if (_tops.Length == 0 || _session == null)
        {
            UpdateCurrentPage();
            return;
        }
        var height = ViewportHeight;
        var top = _scroll.VerticalOffset;
        var first = PageAt(top - height * 0.5);
        var last = PageAt(top + height * 1.5);
        foreach (var index in _views.Keys.Where(i => i < first || i > last).ToList())
        {
            _canvas.Children.Remove(_views[index]);
            _views.Remove(index);
        }
        for (var i = first; i <= last; i++)
        {
            if (_views.ContainsKey(i)) continue;
            var view = new PageView(i);
            _views[i] = view;
            _canvas.Children.Add(view);
            Place(view);
            if (_cache.TryGetValue(i, out var cached))
            {
                cached.Used = ++_tick;
                view.Show(cached.Bitmap, cached.Dpi);
            }
            DrawOverlay(view);
        }
        UpdateCurrentPage();
        if (!_pumping && !_renderTimer.IsEnabled && _views.Values.Any(NeedsRender)) _renderTimer.Start();
    }

    private void UpdateCurrentPage()
    {
        var page = 0;
        if (_tops.Length > 0)
        {
            if (_scroll.ScrollableHeight > 0 && _scroll.VerticalOffset >= _scroll.ScrollableHeight - 1) page = _tops.Length - 1;
            else if (_scroll.VerticalOffset > 1) page = PageAt(_scroll.VerticalOffset + ViewportHeight * 0.35);
        }
        if (page == _currentPage) return;
        _currentPage = page;
        CurrentPageChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ rendering

    private double TargetDpi(int index)
    {
        var size = PageSize(index);
        var dpi = 96 * _zoom * VisualTreeHelper.GetDpi(this).DpiScaleX;
        // Very large renders are capped; beyond that the page is drawn a little soft rather than run out of memory.
        dpi = Math.Min(dpi, 6000 * 72 / Math.Max(size.Width, size.Height));
        dpi = Math.Min(dpi, Math.Sqrt(30e6 / Math.Max(1, size.Width * size.Height)) * 72);
        return Math.Max(dpi, 8);
    }

    private bool NeedsRender(PageView v)
    {
        if (v.Failed) return false;
        if (v.Dpi <= 0) return true;
        var target = TargetDpi(v.Index);
        return Math.Abs(v.Dpi - target) / target > 0.12;
    }

    private PageView? NextToRender()
    {
        var center = _scroll.VerticalOffset + ViewportHeight / 2;
        PageView? best = null;
        var bestDistance = double.MaxValue;
        foreach (var v in _views.Values)
        {
            if (!NeedsRender(v)) continue;
            var distance = Math.Abs(_tops[v.Index] + v.Height / 2 - center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = v;
            }
        }
        return best;
    }

    /// <summary>Renders the pages that need it, nearest to the middle of the view first, one at a time.</summary>
    private async void Pump()
    {
        if (_pumping) return;
        _pumping = true;
        try
        {
            while (IsVisible && _session?.Renderer is { } renderer && NextToRender() is { } next)
            {
                var version = _version;
                var index = next.Index;
                var dpi = TargetDpi(index);
                BitmapSource? bitmap = null;
                try
                {
                    bitmap = await renderer.RenderAsync(index, dpi);
                }
                catch (ObjectDisposedException)
                {
                    // The document changed while this page was drawing.
                }
                catch (Exception ex)
                {
                    AppServices.LogError(ex);
                    if (version == _version && _views.TryGetValue(index, out var failed)) failed.ShowError();
                    continue;
                }
                if (version != _version) continue;
                if (bitmap == null) break;
                Remember(index, bitmap, dpi);
                if (_views.TryGetValue(index, out var view)) view.Show(bitmap, dpi);
                PrewarmText(index);
            }
        }
        finally
        {
            _pumping = false;
        }
    }

    private void Remember(int index, BitmapSource bitmap, double dpi)
    {
        _cache[index] = new CachedPage(bitmap, dpi) { Used = ++_tick };
        var total = _cache.Values.Sum(c => c.Bytes);
        while (total > CacheBudget && _cache.Count > 1)
        {
            var oldest = _cache.MinBy(kv => kv.Value.Used);
            total -= oldest.Value.Bytes;
            _cache.Remove(oldest.Key);
        }
    }

    /// <summary>Reads a rendered page's words in the background, so hovering text shows the text cursor at once.</summary>
    private void PrewarmText(int index)
    {
        if (Text is not { } text) return;
        _ = Task.Run(() =>
        {
            try { text.GetPage(index); } catch (ObjectDisposedException) { /* version changed */ }
        });
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateVisible();
    }

    // ------------------------------------------------------------------ zoom and navigation

    private double FitZoom()
    {
        var sizes = _session?.PageSizes;
        if (sizes is not { Count: > 0 } || ActualWidth <= 0) return _zoom;
        var width = Math.Max(50, ActualWidth - 2 * Edge - ScrollBarAllowance);
        var widest = sizes.Max(s => s.Width);
        var fit = width / (widest * PointToDip);
        if (Mode == ZoomMode.FitPage)
        {
            var page = sizes[Math.Clamp(_currentPage, 0, sizes.Count - 1)];
            var height = Math.Max(50, ActualHeight - 2 * Gap);
            fit = Math.Min(width / (page.Width * PointToDip), height / (page.Height * PointToDip));
        }
        return Math.Clamp(fit, MinZoom, MaxZoom);
    }

    public void SetMode(ZoomMode mode)
    {
        Mode = mode;
        if (mode == ZoomMode.Custom) return;
        var page = _currentPage;
        ApplyZoom(FitZoom(), new Point(0, 0));
        if (mode == ZoomMode.FitPage) GoToPage(page);
    }

    /// <summary>Zooms to <paramref name="zoom"/>, keeping the point under <paramref name="anchor"/> (viewer coordinates) in place.</summary>
    public void SetZoom(double zoom, Point? anchor = null)
    {
        Mode = ZoomMode.Custom;
        ApplyZoom(Math.Clamp(zoom, MinZoom, MaxZoom), anchor);
    }

    public void ZoomIn(Point? anchor = null) => SetZoom(ZoomSteps.FirstOrDefault(s => s > _zoom * 1.01, MaxZoom), anchor);

    public void ZoomOut(Point? anchor = null) => SetZoom(ZoomSteps.LastOrDefault(s => s < _zoom / 1.01, MinZoom), anchor);

    private void ApplyZoom(double zoom, Point? anchor)
    {
        if (_tops.Length == 0 || Math.Abs(zoom - _zoom) < 1e-4)
        {
            _zoom = zoom;
            if (_tops.Length > 0) RecenterPages();
            ZoomChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        var a = anchor ?? new Point(ViewportWidth / 2, ViewportHeight / 2);
        var oldScale = Scale;
        var cx = _scroll.HorizontalOffset + a.X;
        var cy = _scroll.VerticalOffset + a.Y;
        var page = PageAt(cy);
        var py = (cy - _tops[page]) / oldScale;
        var px = (cx - PageLeft(page, oldScale)) / oldScale;
        _zoom = zoom;
        Relayout();
        _scroll.UpdateLayout();
        var scale = Scale;
        _scroll.ScrollToVerticalOffset(_tops[page] + py * scale - a.Y);
        _scroll.ScrollToHorizontalOffset(PageLeft(page, scale) + px * scale - a.X);
        UpdateVisible();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public void GoToPage(int index)
    {
        if (_tops.Length == 0) return;
        index = Math.Clamp(index, 0, _tops.Length - 1);
        _scroll.ScrollToVerticalOffset(Math.Max(0, _tops[index] - Gap / 2));
        // Show the new page number at once, before the scroll has been laid out.
        if (_currentPage != index)
        {
            _currentPage = index;
            CurrentPageChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void NextPage() => GoToPage(_currentPage + 1);

    public void PreviousPage() => GoToPage(_currentPage - 1);

    /// <summary>Scrolls a rectangle of a page (in points) into view, if it isn't already.</summary>
    private void Reveal(int page, Rect box)
    {
        var scale = Scale;
        var r = new Rect(PageLeft(page, scale) + box.X * scale, _tops[page] + box.Y * scale, box.Width * scale, box.Height * scale);
        if (r.Top < _scroll.VerticalOffset + 8 || r.Bottom > _scroll.VerticalOffset + ViewportHeight - 8)
            _scroll.ScrollToVerticalOffset(r.Top - ViewportHeight / 3);
        if (r.Left < _scroll.HorizontalOffset || r.Right > _scroll.HorizontalOffset + ViewportWidth)
            _scroll.ScrollToHorizontalOffset(r.Left - ViewportWidth / 3);
    }

    // ------------------------------------------------------------------ search matches

    /// <summary>Highlights search matches; the current one is drawn stronger and scrolled into view.</summary>
    public void ShowHits(IReadOnlyList<TextHit> hits, int current)
    {
        _hits = hits;
        _hitsByPage = Enumerable.Range(0, hits.Count).ToLookup(i => hits[i].PageIndex);
        _currentHit = current;
        foreach (var v in _views.Values) DrawOverlay(v);
        if (current >= 0 && current < hits.Count) Reveal(hits[current].PageIndex, hits[current].Boxes.Aggregate(Rect.Union));
    }

    // ------------------------------------------------------------------ selection

    public bool HasSelection => _anchor != null && _focus != null;

    public void ClearSelection()
    {
        if (!HasSelection) return;
        _anchor = _focus = null;
        SelectionUpdated();
    }

    public void SelectAll()
    {
        if (_tops.Length == 0) return;
        _anchor = new TextPosition(0, 0);
        _focus = new TextPosition(_tops.Length - 1, int.MaxValue);
        SelectionUpdated();
    }

    /// <summary>The selected text (read in the background, since a whole document can be selected).</summary>
    public async Task<string> GetSelectedTextAsync()
    {
        if (_anchor is not { } a || _focus is not { } f || Text is not { } text) return "";
        try
        {
            return await Task.Run(() => text.TextBetween(a, f));
        }
        catch (ObjectDisposedException)
        {
            return "";
        }
    }

    /// <summary>The selection as boxes per page (points), e.g. for highlighting it.</summary>
    public IReadOnlyList<(int Page, IReadOnlyList<Rect> Boxes)> GetSelectionBoxes()
    {
        var result = new List<(int, IReadOnlyList<Rect>)>();
        if (_anchor is not { } a || _focus is not { } f) return result;
        var (from, to) = a < f ? (a, f) : (f, a);
        for (var p = from.Page; p <= to.Page; p++)
        {
            var boxes = SelectionBoxes(p);
            if (boxes.Count > 0) result.Add((p, boxes));
        }
        return result;
    }

    private IReadOnlyList<Rect> SelectionBoxes(int page)
    {
        if (_anchor is not { } a || _focus is not { } f || Text is not { } text) return [];
        var (from, to) = a < f ? (a, f) : (f, a);
        if (page < from.Page || page > to.Page) return [];
        PageWords words;
        try { words = text.GetPage(page); } catch (ObjectDisposedException) { return []; }
        if (!words.HasText) return [];
        var first = page == from.Page ? from.Word : 0;
        var last = page == to.Page ? Math.Min(to.Word, words.Words.Count - 1) : words.Words.Count - 1;
        return first > last ? [] : words.BoxesOf(first, last);
    }

    private void SelectionUpdated()
    {
        foreach (var v in _views.Values) DrawOverlay(v);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DrawOverlay(PageView view)
    {
        view.Overlay.Children.Clear();
        foreach (var i in _hitsByPage[view.Index])
            foreach (var box in _hits[i].Boxes)
                view.Overlay.Children.Add(Box(box, i == _currentHit ? CurrentHitBrush : HitBrush));
        foreach (var box in SelectionBoxes(view.Index))
            view.Overlay.Children.Add(Box(box, SelectionBrush));
        if (_spot is { } spot && spot.Page == view.Index)
        {
            var frame = Box(spot.Box, Brushes.Transparent);
            frame.SetResourceReference(Shape.StrokeProperty, "AccentFillColorDefaultBrush");
            frame.StrokeThickness = 2;
            frame.RadiusX = frame.RadiusY = 3;
            view.Overlay.Children.Add(frame);
        }
    }

    /// <summary>Brings an area of a page into view and outlines it for a moment (a comment picked in the list, say).</summary>
    public void ShowSpot(int page, Rect box)
    {
        if (page < 0 || page >= _tops.Length) return;
        var old = _spot?.Page;
        box.Inflate(3, 3);
        _spot = (page, box);
        Reveal(page, box);
        UpdateVisible();
        foreach (var p in new[] { old, page }.OfType<int>().Distinct())
            if (_views.TryGetValue(p, out var view)) DrawOverlay(view);
        _spotTimer.Stop();
        _spotTimer.Start();
    }

    private static Rectangle Box(Rect r, Brush fill)
    {
        var rect = new Rectangle { Width = r.Width + 1, Height = r.Height + 1, Fill = fill, RadiusX = 1, RadiusY = 1 };
        Canvas.SetLeft(rect, r.X - 0.5);
        Canvas.SetTop(rect, r.Y - 0.5);
        return rect;
    }

    // ------------------------------------------------------------------ mouse and keyboard

    /// <summary>The page under a canvas point and the point in page coordinates; with <paramref name="nearest"/>, the closest page.</summary>
    private (int Page, Point Point)? HitPage(Point canvasPoint, bool nearest)
    {
        if (_tops.Length == 0 || _session == null) return null;
        var scale = Scale;
        var i = PageAt(canvasPoint.Y);
        // In the gap below a page, the next page is nearer once past the middle.
        if (nearest && i + 1 < _tops.Length && canvasPoint.Y > (_tops[i] + PageSize(i).Height * scale + _tops[i + 1]) / 2) i++;
        var size = PageSize(i);
        var p = new Point((canvasPoint.X - PageLeft(i, scale)) / scale, (canvasPoint.Y - _tops[i]) / scale);
        var inside = p.X >= 0 && p.Y >= 0 && p.X <= size.Width && p.Y <= size.Height;
        if (!inside && !nearest) return null;
        return (i, new Point(Math.Clamp(p.X, 0, size.Width), Math.Clamp(p.Y, 0, size.Height)));
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        _scroll.Focus();
        if (Tool == ViewerTool.Hand || Keyboard.IsKeyDown(Key.Space))
        {
            StartPan(e);
            return;
        }
        if (Tool == ViewerTool.Note)
        {
            if (HitPage(e.GetPosition(_canvas), nearest: false) is { } spot) NotePlaced?.Invoke(this, new PagePoint(spot.Page, spot.Point));
            e.Handled = true;
            return;
        }
        Press(e.GetPosition(_canvas), e.ClickCount);
        if (_drag == Drag.Pending) _canvas.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>The left button goes down at a canvas point, with the select tool.</summary>
    private void Press(Point pos, int clickCount)
    {
        if (clickCount == 2 && Text is { } text && HitPage(pos, nearest: false) is { } hit)
        {
            // Double-click selects a word.
            var word = SafeWordAt(text, hit.Page, hit.Point, nearest: false);
            if (word >= 0)
            {
                _anchor = _focus = new TextPosition(hit.Page, word);
                SelectionUpdated();
            }
            return;
        }
        _drag = Drag.Pending;
        _dragStart = pos;
        _pressAt = HitPage(pos, nearest: true);
        _pressLink = clickCount == 1 ? LinkAt(pos) : null;
    }

    /// <summary>The mouse moves to a canvas point with the button down: starts or extends a text selection.</summary>
    private void DragTo(Point pos)
    {
        if (_drag == Drag.Pending && (pos - _dragStart).Length > 3)
        {
            if (Text is { } text && _pressAt is { } press && SafeWordAt(text, press.Page, press.Point, nearest: true) is var word and >= 0)
            {
                _anchor = new TextPosition(press.Page, word);
                _focus = null;
                _drag = Drag.Select;
            }
            else
            {
                _drag = Drag.None;
            }
        }
        if (_drag == Drag.Select) ExtendSelection(pos);
    }

    /// <summary>The button comes up: a click without a drag follows a link, or clears the selection.</summary>
    private void Release()
    {
        if (_drag == Drag.Pending)
        {
            if (_pressLink is { } link) LinkClicked?.Invoke(this, link);
            else ClearSelection();
        }
        EndDrag();
    }

    /// <summary>Development aid for --snapshot: a drag between two viewer points, through the same steps as the mouse.</summary>
    internal void SimulateDrag(Point from, Point to)
    {
        var offset = new Vector(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        Press(from + offset, 1);
        for (var i = 1; i <= 8; i++) DragTo(from + (to - from) * (i / 8.0) + offset);
        Release();
    }

    private void OnOtherDown(object sender, MouseButtonEventArgs e)
    {
        // The middle button moves the pages, whatever the tool.
        if (e.ChangedButton == MouseButton.Middle) StartPan(e);
    }

    private void StartPan(MouseButtonEventArgs e)
    {
        _drag = Drag.Pan;
        _dragStart = e.GetPosition(this);
        _panStartOffset = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
        _canvas.Cursor = Cursors.SizeAll;
        _canvas.CaptureMouse();
        e.Handled = true;
    }

    private static int SafeWordAt(TextIndex text, int page, Point p, bool nearest)
    {
        try { return text.GetPage(page).WordAt(p, nearest); } catch (ObjectDisposedException) { return -1; }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(_canvas);
        switch (_drag)
        {
            case Drag.Pan:
                var delta = e.GetPosition(this) - _dragStart;
                _scroll.ScrollToHorizontalOffset(_panStartOffset.X - delta.X);
                _scroll.ScrollToVerticalOffset(_panStartOffset.Y - delta.Y);
                break;
            case Drag.Pending:
                DragTo(pos);
                break;
            case Drag.Select:
                DragTo(pos);
                var inView = e.GetPosition(_scroll);
                var outside = inView.Y < 0 || inView.Y > ViewportHeight || inView.X < 0 || inView.X > ViewportWidth;
                if (outside && !_autoScroll.IsEnabled) _autoScroll.Start();
                else if (!outside) _autoScroll.Stop();
                break;
            case Drag.None when Tool == ViewerTool.Select:
                UpdateCursor(pos);
                break;
        }
    }

    private void ExtendSelection(Point canvasPoint)
    {
        if (Text is not { } text || HitPage(canvasPoint, nearest: true) is not { } hit) return;
        var word = SafeWordAt(text, hit.Page, hit.Point, nearest: true);
        if (word < 0) return;
        var focus = new TextPosition(hit.Page, word);
        if (focus == _focus) return;
        _focus = focus;
        SelectionUpdated();
    }

    /// <summary>While selecting with the mouse outside the view, keeps scrolling towards it.</summary>
    private void OnAutoScroll(object? sender, EventArgs e)
    {
        if (_drag != Drag.Select)
        {
            _autoScroll.Stop();
            return;
        }
        var p = Mouse.GetPosition(_scroll);
        var dy = p.Y < 0 ? p.Y : p.Y > ViewportHeight ? p.Y - ViewportHeight : 0;
        var dx = p.X < 0 ? p.X : p.X > ViewportWidth ? p.X - ViewportWidth : 0;
        _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + Math.Clamp(dy, -60, 60));
        _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + Math.Clamp(dx, -60, 60));
        ExtendSelection(Mouse.GetPosition(_canvas));
    }

    /// <summary>A pointing hand over links (with where they go), the text cursor over words.</summary>
    private void UpdateCursor(Point canvasPoint)
    {
        if (LinkAt(canvasPoint) is { } link)
        {
            _canvas.Cursor = Cursors.Hand;
            var tip = link.Uri ?? $"Go to page {link.TargetPage + 1}";
            if (!Equals(_canvas.ToolTip, tip)) _canvas.ToolTip = tip;
            return;
        }
        if (CommentAt(canvasPoint) is { } comment)
        {
            _canvas.Cursor = null;
            var heading = DocumentComments.Describe(comment.Kind) + (comment.Author != null ? $" · {comment.Author}" : "");
            var tip = $"{heading}\n{comment.Text}";
            if (!Equals(_canvas.ToolTip, tip)) _canvas.ToolTip = tip;
            return;
        }
        _canvas.ToolTip = null;
        var overText = Text is { } text && HitPage(canvasPoint, nearest: false) is { } hit &&
                       text.Loaded(hit.Page)?.WordAt(hit.Point, nearest: false) >= 0;
        _canvas.Cursor = overText ? Cursors.IBeam : null;
    }

    /// <summary>The document's comments, so hovering one shows what it says.</summary>
    public void SetComments(IReadOnlyList<PageComment> comments) =>
        _comments = comments.Where(c => c.Text != null).ToLookup(c => c.PageIndex);

    private PageComment? CommentAt(Point canvasPoint)
    {
        if (HitPage(canvasPoint, nearest: false) is not { } hit) return null;
        return _comments[hit.Page].LastOrDefault(c =>
        {
            var area = c.Bounds;
            area.Inflate(2, 2);
            return area.Contains(hit.Point);
        });
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_drag != Drag.None) e.Handled = true;
        if (_drag == Drag.Pan) EndDrag();
        else Release();
    }

    // ------------------------------------------------------------------ links

    /// <summary>The document's links; the reader sets them once they have been read.</summary>
    public void SetLinks(IReadOnlyList<PageLink> links) => _links = links.ToLookup(l => l.PageIndex);

    private PageLink? LinkAt(Point canvasPoint) =>
        HitPage(canvasPoint, nearest: false) is { } hit ? _links[hit.Page].LastOrDefault(l => l.Bounds.Contains(hit.Point)) : null;

    private void EndDrag()
    {
        _pressLink = null;
        if (_drag == Drag.None) return;
        _drag = Drag.None;
        _autoScroll.Stop();
        _canvas.Cursor = ToolCursor;
        if (_canvas.IsMouseCaptured) _canvas.ReleaseMouseCapture();
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            SetZoom(_zoom * Math.Pow(1.1, e.Delta / 120.0), e.GetPosition(_scroll));
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta);
        }
        else
        {
            // A notch moves about a hand's width of text, more than the default three lines.
            _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset - e.Delta * 0.9);
        }
        e.Handled = true;
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
            case Key.A when ctrl:
                SelectAll();
                break;
            case Key.C when ctrl:
                await CopySelectionAsync();
                break;
            case Key.Escape when HasSelection:
                ClearSelection();
                break;
            case Key.Home when !ctrl:
                GoToPage(0);
                break;
            case Key.End when !ctrl:
                _scroll.ScrollToBottom();
                break;
            case Key.Space:
                _scroll.ScrollToVerticalOffset(_scroll.VerticalOffset + (shift ? -1 : 1) * ViewportHeight * 0.9);
                break;
            case Key.Left when !ctrl && _scroll.ScrollableWidth <= 0:
                PreviousPage();
                break;
            case Key.Right when !ctrl && _scroll.ScrollableWidth <= 0:
                NextPage();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Copies the selection to the clipboard; returns false when nothing is selected.</summary>
    public async Task<bool> CopySelectionAsync()
    {
        var text = await GetSelectedTextAsync();
        if (text.Length == 0) return false;
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program has the clipboard open; one retry usually succeeds.
            await Task.Delay(100);
            Clipboard.SetText(text);
        }
        return true;
    }

    // ------------------------------------------------------------------ parts

    private sealed class CachedPage(BitmapSource bitmap, double dpi)
    {
        public BitmapSource Bitmap { get; } = bitmap;
        public double Dpi { get; } = dpi;
        public long Bytes { get; } = (long)bitmap.PixelWidth * bitmap.PixelHeight * 4;
        public long Used { get; set; }
    }

    /// <summary>One page: white paper with a soft shadow, the rendered image and the highlight layer.</summary>
    private sealed class PageView : Grid
    {
        private static readonly Effect Shadow = CreateShadow();
        private readonly Image _image = new() { Stretch = Stretch.Fill };

        public PageView(int index)
        {
            Index = index;
            Background = Brushes.White;
            Effect = Shadow;
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            Children.Add(_image);
            Overlay = new Canvas
            {
                IsHitTestVisible = false,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                RenderTransform = OverlayScale,
            };
            Children.Add(Overlay);
        }

        public int Index { get; }
        public Canvas Overlay { get; }
        public ScaleTransform OverlayScale { get; } = new();
        public double Dpi { get; private set; }
        public bool Failed { get; private set; }

        public void Show(BitmapSource bitmap, double dpi)
        {
            _image.Source = bitmap;
            Dpi = dpi;
        }

        public void ShowError()
        {
            Failed = true;
            Children.Add(new TextBlock
            {
                Text = "This page couldn't be displayed.",
                Foreground = Brushes.Gray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        private static Effect CreateShadow()
        {
            var shadow = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.22, Direction = 270, Color = Colors.Black };
            shadow.Freeze();
            return shadow;
        }
    }
}
