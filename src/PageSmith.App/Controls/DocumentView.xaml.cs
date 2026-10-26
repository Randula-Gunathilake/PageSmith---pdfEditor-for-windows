using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using PageSmith.App.Documents;
using PageSmith.App.Infrastructure;
using PageSmith.App.Tools;
using PageSmith.App.Tools.Editor;
using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PageSmith.Core.Text;
using PageSmith.Core.Tools.Edit;
using PageSmith.Core.Tools.Security;
using Ui = Wpf.Ui.Controls;

namespace PageSmith.App.Controls;

/// <summary>
/// A document open in a tab: the reader (toolbar, thumbnails and bookmarks, pages, find) and every tool,
/// which runs on this document and, for tools that change it, replaces it with an undoable new version.
/// </summary>
public partial class DocumentView : UserControl
{
    private const double ThumbWidth = 132, ThumbHeight = 170;

    private readonly DocumentSession _session;
    private readonly List<PageThumbnail> _thumbQueue = [];
    private readonly Queue<PageThumbnail> _loadedThumbs = new();
    private readonly DispatcherTimer _findTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private ObservableCollection<PageThumbnail> _thumbs = [];
    private int _version;
    private bool _thumbPumping, _syncingThumbs, _updatingZoom, _hasOutline;
    private string _panel = "";
    private IReadOnlyList<PageComment> _comments = [];
    private ToolController? _tool;
    private CancellationTokenSource? _findCts;
    private IReadOnlyList<TextHit> _hits = [];
    private int _hit = -1;

    /// <summary>The query and options of the last search, so Enter searches again only when they changed.</summary>
    private string? _searched;

    public DocumentView(DocumentSession session)
    {
        InitializeComponent();
        _session = session;
        DataContext = session;
        Viewer.Session = session;
        Viewer.CurrentPageChanged += (_, _) => OnPageChanged();
        Viewer.ZoomChanged += (_, _) => ShowZoom();
        Viewer.LinkClicked += OnLinkClicked;
        Viewer.NotePlaced += OnNotePlaced;
        session.VersionChanged += OnVersionChanged;
        _findTimer.Tick += (_, _) => RunFind();
        ShowToolList(null);
        ShowPanel(AppServices.Settings.ReaderPanel);
        ShowToolsPane(AppServices.Settings.ReaderToolsPane);
        Loaded += (_, _) => Viewer.FocusView();
        RefreshParts();
        ShowZoom();
    }

    public DocumentSession Session => _session;

    /// <summary>The tool running on this document, if any.</summary>
    public ToolController? ActiveTool => _tool;

    public PdfViewer PageView => Viewer;

    /// <summary>Lets go of the document when its tab closes.</summary>
    public void Detach()
    {
        _session.VersionChanged -= OnVersionChanged;
        _findCts?.Cancel();
        _findTimer.Stop();
        CloseTool();
        Viewer.Session = null;
    }

    public void FocusView() => Viewer.FocusView();

    private void OnVersionChanged(object? sender, EventArgs e)
    {
        RefreshParts();
        if (FindBar.Visibility == Visibility.Visible && FindBox.Text.Trim().Length > 0) RunFind();
    }

    /// <summary>Rebuilds what depends on the document's content: thumbnails, bookmarks and the page count.</summary>
    private void RefreshParts()
    {
        _version++;
        BuildThumbnails();
        _ = LoadOutlineAsync(_version);
        PageBox.Text = (Viewer.CurrentPage + 1).ToString(CultureInfo.CurrentCulture);
    }

    // ------------------------------------------------------------------ keyboard

    /// <summary>Shortcuts for the document in front; called by the window before controls see the keys.</summary>
    public bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (_tool != null) return false;
        var ctrl = modifiers.HasFlag(ModifierKeys.Control);
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var typing = Keyboard.FocusedElement is TextBoxBase;
        switch (key)
        {
            case Key.S when ctrl && shift: _ = AppServices.Documents.SaveAsAsync(_session); return true;
            case Key.S when ctrl: _ = AppServices.Documents.SaveAsync(_session); return true;
            case Key.P when ctrl: _ = PrintAsync(); return true;
            case Key.F when ctrl: ShowFind(); return true;
            case Key.F3: FindStep(shift ? -1 : 1); return true;
            case Key.G when ctrl: PageBox.Focus(); PageBox.SelectAll(); return true;
            case Key.D when ctrl: _ = ShowPropertiesAsync(); return true;
            case Key.Z when ctrl && !typing && shift: _ = RedoAsync(); return true;
            case Key.Z when ctrl && !typing: _ = UndoAsync(); return true;
            case Key.Y when ctrl && !typing: _ = RedoAsync(); return true;
            case Key.OemPlus or Key.Add when ctrl: Viewer.ZoomIn(); return true;
            case Key.OemMinus or Key.Subtract when ctrl: Viewer.ZoomOut(); return true;
            case Key.D0 or Key.NumPad0 when ctrl: Viewer.SetMode(ZoomMode.FitPage); return true;
            case Key.D1 or Key.NumPad1 when ctrl: Viewer.SetZoom(1); return true;
            case Key.D2 or Key.NumPad2 when ctrl: Viewer.SetMode(ZoomMode.FitWidth); return true;
            case Key.Escape when FindBar.Visibility == Visibility.Visible && FindBox.IsKeyboardFocusWithin: CloseFind(); return true;
            default: return false;
        }
    }

    // ------------------------------------------------------------------ toolbar

    private async void OnSave(object sender, RoutedEventArgs e) => await AppServices.Documents.SaveAsync(_session);

    private async void OnSaveAs(object sender, RoutedEventArgs e) => await AppServices.Documents.SaveAsAsync(_session);

    private async void OnPrint(object sender, RoutedEventArgs e) => await PrintAsync();

    public Task PrintAsync() => PdfPrinter.PrintAsync(_session, Viewer.CurrentPage);

    private async void OnUndo(object sender, RoutedEventArgs e) => await UndoAsync();

    private async void OnRedo(object sender, RoutedEventArgs e) => await RedoAsync();

    public async Task UndoAsync()
    {
        if (_session.UndoDescription is not { } what) return;
        await _session.UndoAsync();
        AppServices.Notify("Undone", what, Ui.ControlAppearance.Secondary, Ui.SymbolRegular.ArrowUndo24);
    }

    public async Task RedoAsync()
    {
        if (_session.RedoDescription is not { } what) return;
        await _session.RedoAsync();
        AppServices.Notify("Redone", what, Ui.ControlAppearance.Secondary, Ui.SymbolRegular.ArrowRedo24);
    }

    private void OnPreviousPage(object sender, RoutedEventArgs e) => Viewer.PreviousPage();

    private void OnNextPage(object sender, RoutedEventArgs e) => Viewer.NextPage();

    private void OnPageChanged()
    {
        if (!PageBox.IsKeyboardFocusWithin) PageBox.Text = (Viewer.CurrentPage + 1).ToString(CultureInfo.CurrentCulture);
        SyncThumbSelection();
    }

    private void OnPageBoxFocus(object sender, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();

    private void OnPageBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            GoToTypedPage();
            Viewer.FocusView();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            PageBox.Text = (Viewer.CurrentPage + 1).ToString(CultureInfo.CurrentCulture);
            Viewer.FocusView();
            e.Handled = true;
        }
    }

    private void OnPageBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => GoToTypedPage();

    private void GoToTypedPage()
    {
        if (int.TryParse(PageBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var page) && page >= 1 && page <= _session.PageCount)
            Viewer.GoToPage(page - 1);
        PageBox.Text = (Viewer.CurrentPage + 1).ToString(CultureInfo.CurrentCulture);
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => Viewer.ZoomIn();

    private void OnZoomOut(object sender, RoutedEventArgs e) => Viewer.ZoomOut();

    private void OnFitWidth(object sender, RoutedEventArgs e) => Viewer.SetMode(ZoomMode.FitWidth);

    private void OnFitPage(object sender, RoutedEventArgs e) => Viewer.SetMode(ZoomMode.FitPage);

    private void ShowZoom()
    {
        _updatingZoom = true;
        ZoomBox.SelectedItem = null;
        ZoomBox.Text = $"{Viewer.Zoom * 100:0}%";
        FitWidthToggle.IsChecked = Viewer.Mode == ZoomMode.FitWidth;
        FitPageToggle.IsChecked = Viewer.Mode == ZoomMode.FitPage;
        _updatingZoom = false;
    }

    private void OnZoomPicked(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingZoom || ZoomBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        switch (tag)
        {
            case "width": Viewer.SetMode(ZoomMode.FitWidth); break;
            case "page": Viewer.SetMode(ZoomMode.FitPage); break;
            default: Viewer.SetZoom(double.Parse(tag, CultureInfo.InvariantCulture)); break;
        }
        // Show the resulting percentage rather than the picked item.
        Dispatcher.InvokeAsync(ShowZoom, DispatcherPriority.Background);
    }

    private void OnZoomBoxKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        var text = ZoomBox.Text.Replace("%", "").Trim();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var percent) && percent > 0)
            Viewer.SetZoom(percent / 100);
        ShowZoom();
        Viewer.FocusView();
        e.Handled = true;
    }

    private void OnSelectTool(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Select);

    private void OnHandTool(object sender, RoutedEventArgs e) => SetTool(ViewerTool.Hand);

    private void SetTool(ViewerTool tool)
    {
        Viewer.Tool = tool;
        SelectToggle.IsChecked = tool == ViewerTool.Select;
        HandToggle.IsChecked = tool == ViewerTool.Hand;
        NoteToggle.IsChecked = tool == ViewerTool.Note;
    }

    private async void OnProperties(object sender, RoutedEventArgs e) => await ShowPropertiesAsync();

    // ------------------------------------------------------------------ side panel

    private void OnRailClick(object sender, RoutedEventArgs e)
    {
        var panel = (string)((FrameworkElement)sender).Tag;
        ShowPanel(_panel == panel ? "" : panel);
        AppServices.Settings.ReaderPanel = _panel;
        AppServices.SaveSettings();
    }

    private void OnCloseSidePanel(object sender, RoutedEventArgs e)
    {
        ShowPanel("");
        AppServices.Settings.ReaderPanel = "";
        AppServices.SaveSettings();
    }

    private void ShowPanel(string? panel)
    {
        _panel = panel is "pages" or "bookmarks" or "comments" ? panel : "";
        PagesToggle.IsChecked = _panel == "pages";
        BookmarksToggle.IsChecked = _panel == "bookmarks";
        CommentsToggle.IsChecked = _panel == "comments";
        SidePanel.Visibility = _panel.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePanelParts();
        if (_panel == "pages") Dispatcher.InvokeAsync(SyncThumbSelection, DispatcherPriority.Loaded);
    }

    private void UpdatePanelParts()
    {
        SideTitle.Text = _panel switch { "bookmarks" => "Bookmarks", "comments" => "Comments", _ => "Pages" };
        Thumbs.Visibility = Show(_panel == "pages");
        Outline.Visibility = Show(_panel == "bookmarks" && _hasOutline);
        NoBookmarks.Visibility = Show(_panel == "bookmarks" && !_hasOutline);
        CommentList.Visibility = Show(_panel == "comments" && _comments.Count > 0);
        NoComments.Visibility = Show(_panel == "comments" && _comments.Count == 0);

        static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildThumbnails()
    {
        _thumbQueue.Clear();
        _loadedThumbs.Clear();
        var sizes = _session.PageSizes;
        var list = new ObservableCollection<PageThumbnail>();
        for (var i = 0; i < sizes.Count; i++)
        {
            var k = Math.Min(ThumbWidth / Math.Max(1, sizes[i].Width), ThumbHeight / Math.Max(1, sizes[i].Height));
            list.Add(new PageThumbnail(i, Math.Round(sizes[i].Width * k), Math.Round(sizes[i].Height * k)));
        }
        _thumbs = list;
        _syncingThumbs = true;
        Thumbs.ItemsSource = list;
        _syncingThumbs = false;
        SyncThumbSelection();
    }

    private void OnThumbShown(object sender, EventArgs e)
    {
        // Containers are recycled while scrolling, so a thumbnail is asked for whenever one shows a new page.
        if ((sender as FrameworkElement)?.DataContext is not PageThumbnail t || t.Image != null || t.Requested) return;
        t.Requested = true;
        _thumbQueue.Add(t);
        PumpThumbnails();
    }

    private void OnThumbLoaded(object sender, RoutedEventArgs e) => OnThumbShown(sender, e);

    private void OnThumbDataChanged(object sender, DependencyPropertyChangedEventArgs e) => OnThumbShown(sender, EventArgs.Empty);

    /// <summary>Renders requested thumbnails one by one, the most recently shown first.</summary>
    private async void PumpThumbnails()
    {
        if (_thumbPumping) return;
        _thumbPumping = true;
        try
        {
            // The page in the main view comes first.
            await Task.Delay(150);
            while (_thumbQueue.Count > 0)
            {
                var t = _thumbQueue[^1];
                _thumbQueue.RemoveAt(_thumbQueue.Count - 1);
                if (Thumbs.ItemContainerGenerator.ContainerFromItem(t) == null)
                {
                    // Scrolled away before its turn; it is asked for again when shown.
                    t.Requested = false;
                    continue;
                }
                if (_session.Renderer is not { } renderer) break;
                var version = _version;
                try
                {
                    var png = await renderer.RenderThumbnailPngAsync(t.Index, 264);
                    var image = await Task.Run(() => ImageIO.Decode(png));
                    if (version == _version)
                    {
                        t.Image = image;
                        ForgetOldThumbnails(t);
                    }
                }
                catch (ObjectDisposedException)
                {
                    t.Requested = false;
                }
                catch (Exception ex)
                {
                    AppServices.LogError(ex);
                }
            }
        }
        finally
        {
            _thumbPumping = false;
        }
    }

    /// <summary>
    /// Keeps the images of the most recently shown thumbnails only, so a long document doesn't hold one
    /// per page; a dropped image is rendered again when its thumbnail scrolls back into view.
    /// </summary>
    private void ForgetOldThumbnails(PageThumbnail loaded)
    {
        const int Keep = 150;
        _loadedThumbs.Enqueue(loaded);
        for (var checks = _loadedThumbs.Count; _loadedThumbs.Count > Keep && checks > 0; checks--)
        {
            var oldest = _loadedThumbs.Dequeue();
            if (Thumbs.ItemContainerGenerator.ContainerFromItem(oldest) != null)
            {
                _loadedThumbs.Enqueue(oldest);
                continue;
            }
            oldest.Image = null;
            oldest.Requested = false;
        }
    }

    private void SyncThumbSelection()
    {
        var page = Viewer.CurrentPage;
        if (page < 0 || page >= _thumbs.Count || Thumbs.SelectedItems.Count > 1) return;
        _syncingThumbs = true;
        Thumbs.SelectedItem = _thumbs[page];
        if (Thumbs.IsVisible) Thumbs.ScrollIntoView(_thumbs[page]);
        _syncingThumbs = false;
    }

    private void OnThumbSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingThumbs || Thumbs.SelectedItems.Count != 1 || Thumbs.SelectedItem is not PageThumbnail t) return;
        Viewer.GoToPage(t.Index);
    }

    /// <summary>The pages selected in the thumbnails, or the current page.</summary>
    private IReadOnlyList<int> SelectedPages()
    {
        var pages = Thumbs.SelectedItems.OfType<PageThumbnail>().Select(t => t.Index).Order().ToList();
        return pages.Count > 0 ? pages : [Math.Clamp(Viewer.CurrentPage, 0, Math.Max(0, _session.PageCount - 1))];
    }

    private void OnThumbMenuOpened(object sender, RoutedEventArgs e)
    {
        var pages = SelectedPages();
        DeletePagesItem.Header = pages.Count == 1 ? "Delete page" : $"Delete {pages.Count} pages";
        DeletePagesItem.IsEnabled = pages.Count < _session.PageCount;
    }

    private async void OnRotatePagesRight(object sender, RoutedEventArgs e) => await PageActions.RotateAsync(_session, SelectedPages(), 90);

    private async void OnRotatePagesLeft(object sender, RoutedEventArgs e) => await PageActions.RotateAsync(_session, SelectedPages(), -90);

    private async void OnDeletePages(object sender, RoutedEventArgs e) => await DeleteSelectedPagesAsync();

    private async Task DeleteSelectedPagesAsync()
    {
        var pages = SelectedPages();
        if (pages.Count >= _session.PageCount)
        {
            AppServices.Notify("Can't delete every page", "A document needs at least one page.", Ui.ControlAppearance.Caution);
            return;
        }
        await PageActions.DeleteAsync(_session, pages);
    }

    private async void OnExtractPages(object sender, RoutedEventArgs e) => await PageActions.ExtractAsync(_session, SelectedPages());

    private async void OnInsertFromFile(object sender, RoutedEventArgs e)
    {
        var file = AppServices.PickFile("Insert pages from", "PDFs and images|*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.heic|All files|*.*");
        if (file != null) await PageActions.InsertFileAsync(_session, SelectedPages().Max() + 1, file);
    }

    private async void OnInsertBlank(object sender, RoutedEventArgs e) => await PageActions.InsertBlankAsync(_session, SelectedPages().Max() + 1);

    private async void OnThumbsKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        await DeleteSelectedPagesAsync();
    }

    // Thumbnails can be dragged to move pages; PDFs and images dropped on them are inserted there.

    private const string PagesFormat = "PageSmith.Pages";
    private Point? _thumbPress;
    private InsertionLine? _insertion;

    private static ListBoxItem? ItemUnder(object source)
    {
        for (var d = source as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ListBoxItem item) return item;
        return null;
    }

    private void OnThumbsMouseDown(object sender, MouseButtonEventArgs e) =>
        _thumbPress = ItemUnder(e.OriginalSource) != null ? e.GetPosition(Thumbs) : null;

    private void OnThumbsMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _thumbPress is not { } start) return;
        var moved = e.GetPosition(Thumbs) - start;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance * 2 && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        _thumbPress = null;
        DragDrop.DoDragDrop(Thumbs, new DataObject(PagesFormat, SelectedPages().ToArray()), DragDropEffects.Move);
        ShowInsertion(-1);
    }

    /// <summary>Where a drop would insert: before the page under the pointer (after it in its lower half).</summary>
    private (int Index, double Y) DropTarget(DragEventArgs e)
    {
        if (ItemUnder(Thumbs.InputHitTest(e.GetPosition(Thumbs))) is { DataContext: PageThumbnail thumb } item)
        {
            var after = e.GetPosition(item).Y > item.ActualHeight / 2;
            var y = item.TranslatePoint(new Point(0, after ? item.ActualHeight : 0), Thumbs).Y;
            return (thumb.Index + (after ? 1 : 0), y);
        }
        return (_session.PageCount, -1);
    }

    private void ShowInsertion(double y)
    {
        if (_insertion == null && y >= 0 && AdornerLayer.GetAdornerLayer(Thumbs) is { } layer)
        {
            _insertion = new InsertionLine(Thumbs);
            layer.Add(_insertion);
        }
        if (_insertion != null) _insertion.Y = y;
    }

    private void OnThumbsDragOver(object sender, DragEventArgs e)
    {
        var pages = e.Data.GetDataPresent(PagesFormat);
        var files = e.Data.GetData(DataFormats.FileDrop) is string[] dropped && dropped.Any(PageActions.CanInsert);
        e.Effects = pages ? DragDropEffects.Move : files ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        ShowInsertion(pages || files ? DropTarget(e).Y : -1);
        // Near the top or bottom edge, scroll to reach pages out of view.
        if (FindScrollViewer(Thumbs) is { } scroll)
        {
            var y = e.GetPosition(Thumbs).Y;
            if (y < 40) scroll.ScrollToVerticalOffset(scroll.VerticalOffset - 12);
            else if (y > Thumbs.ActualHeight - 40) scroll.ScrollToVerticalOffset(scroll.VerticalOffset + 12);
        }
    }

    private void OnThumbsDragLeave(object sender, DragEventArgs e) => ShowInsertion(-1);

    private async void OnThumbsDrop(object sender, DragEventArgs e)
    {
        ShowInsertion(-1);
        var (index, _) = DropTarget(e);
        e.Handled = true;
        if (e.Data.GetData(PagesFormat) is int[] pages)
        {
            await PageActions.MoveAsync(_session, pages, index);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var insertable = files.Where(PageActions.CanInsert).ToList();
            if (insertable.Count < files.Length)
                AppServices.Notify("Some files were skipped", "Only PDFs and images can be inserted as pages.", Ui.ControlAppearance.Caution);
            // Last file first, each at the same place, so they end up in the order they were dropped.
            foreach (var file in Enumerable.Reverse(insertable)) await PageActions.InsertFileAsync(_session, index, file);
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScrollViewer(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>The accent line showing where dragged pages or files will go.</summary>
    private sealed class InsertionLine : Adorner
    {
        private double _y = -1;

        public InsertionLine(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

        public double Y
        {
            get => _y;
            set
            {
                _y = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (_y < 0) return;
            var brush = TryFindResource("AccentFillColorDefaultBrush") as Brush ?? Brushes.DodgerBlue;
            dc.DrawRoundedRectangle(brush, null, new Rect(14, _y - 1.5, Math.Max(0, AdornedElement.RenderSize.Width - 28), 3), 1.5, 1.5);
        }
    }

    /// <summary>Reads the bookmarks, links and comments of the current version in the background.</summary>
    private async Task LoadOutlineAsync(int version)
    {
        IReadOnlyList<OutlineItem> outline = [];
        IReadOnlyList<PageLink> links = [];
        IReadOnlyList<PageComment> comments = [];
        try
        {
            var input = _session.CurrentInput;
            (outline, links, comments) = await Task.Run(() =>
            {
                using var doc = Core.Common.PdfDocs.Open(input, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
                return (DocumentOutline.Read(doc), DocumentLinks.Read(doc), DocumentComments.Read(doc));
            });
        }
        catch (Exception ex)
        {
            AppServices.LogError(ex);
        }
        if (version != _version) return;
        Outline.ItemsSource = outline;
        _hasOutline = outline.Count > 0;
        _comments = comments;
        CommentList.ItemsSource = comments.Select(c => new CommentRow(c)).ToList();
        UpdatePanelParts();
        Viewer.SetLinks(links);
        Viewer.SetComments(comments);
    }

    /// <summary>Development aid for --snapshot: highlights the first match of a search, as the context menu does.</summary>
    internal async Task DemoHighlightAsync(string query)
    {
        if (await _session.GetTextAsync() is not { } text || text.Find(query) is not { Count: > 0 } hits) return;
        await PageActions.MarkupAsync(_session, MarkupKind.Highlight, [new MarkupMark(hits[0].PageIndex, hits[0].Boxes, hits[0].Text)]);
    }

    /// <summary>Development aid for --snapshot: adds a note, as the note tool does after its dialog.</summary>
    internal Task DemoNoteAsync(int page, Point at, string text) => PageActions.AddNoteAsync(_session, page, at, text);

    private void OnCommentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CommentList.SelectedItem is CommentRow row) Viewer.ShowSpot(row.Comment.PageIndex, row.Comment.Bounds);
    }

    private async void OnDeleteComment(object sender, RoutedEventArgs e) => await DeleteSelectedCommentAsync();

    private async void OnCommentListKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        e.Handled = true;
        await DeleteSelectedCommentAsync();
    }

    private async Task DeleteSelectedCommentAsync()
    {
        if (CommentList.SelectedItem is CommentRow row) await PageActions.DeleteCommentAsync(_session, row.Comment);
    }

    private void OnNoteTool(object sender, RoutedEventArgs e) => SetTool(Viewer.Tool == ViewerTool.Note ? ViewerTool.Select : ViewerTool.Note);

    private async void OnNotePlaced(object? sender, PagePoint at)
    {
        SetTool(ViewerTool.Select);
        await AddNoteAsync(at);
    }

    private async void OnAddNoteHere(object sender, RoutedEventArgs e)
    {
        if (Viewer.ContextPoint is { } at) await AddNoteAsync(at);
    }

    private async Task AddNoteAsync(PagePoint at)
    {
        var box = new Ui.TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 380,
            Height = 120,
            VerticalContentAlignment = VerticalAlignment.Top,
            PlaceholderText = "Type your note",
        };
        var dialog = new Ui.ContentDialog
        {
            Title = $"Add a note on page {at.Page + 1}",
            Content = box,
            PrimaryButtonText = "Add note",
            CloseButtonText = "Cancel",
            DefaultButton = Ui.ContentDialogButton.Primary,
        };
        dialog.Loaded += (_, _) => box.Focus();
        if (await AppServices.ShowDialogAsync(dialog) != Ui.ContentDialogResult.Primary || string.IsNullOrWhiteSpace(box.Text)) return;
        await PageActions.AddNoteAsync(_session, at.Page, at.Point, box.Text);
    }

    private async void OnLinkClicked(object? sender, PageLink link)
    {
        if (link.TargetPage >= 0)
        {
            Viewer.GoToPage(link.TargetPage);
            return;
        }
        if (link.Uri is not { } uri) return;
        if (!DocumentLinks.IsSafeToOpen(uri))
        {
            AppServices.Notify("Link not opened", $"PageSmith opens web and e-mail links only, not \"{uri}\".", Ui.ControlAppearance.Caution);
            return;
        }
        // A link can say one thing and go somewhere else: show where it goes first.
        if (await AppServices.ConfirmAsync("Open this link?", uri, "Open", "Cancel")) AppServices.OpenUri(uri);
    }

    private void OnBookmarkClick(object sender, MouseButtonEventArgs e)
    {
        // The event bubbles through the parent items too: only the innermost one handles it.
        if (e.Handled || (sender as TreeViewItem)?.DataContext is not OutlineItem item) return;
        e.Handled = true;
        // Clicking the expander arrow only expands.
        for (var d = e.OriginalSource as DependencyObject; d != null && d != sender; d = VisualTreeHelper.GetParent(d))
            if (d is ToggleButton) return;
        if (item.PageIndex >= 0) Viewer.GoToPage(item.PageIndex);
    }

    // ------------------------------------------------------------------ selection

    private void OnViewerMenuOpened(object sender, RoutedEventArgs e)
    {
        var selected = Viewer.HasSelection;
        foreach (var item in new[] { CopyItem, HighlightItem, UnderlineItem, StrikeItem, RedactSelectionItem }) item.IsEnabled = selected;
        AddNoteItem.IsEnabled = Viewer.ContextPoint != null;
    }

    private async void OnCopy(object sender, RoutedEventArgs e) => await Viewer.CopySelectionAsync();

    private void OnSelectAll(object sender, RoutedEventArgs e) => Viewer.SelectAll();

    private async void OnHighlight(object sender, RoutedEventArgs e) => await MarkSelectionAsync(MarkupKind.Highlight);

    private async void OnUnderline(object sender, RoutedEventArgs e) => await MarkSelectionAsync(MarkupKind.Underline);

    private async void OnStrikeout(object sender, RoutedEventArgs e) => await MarkSelectionAsync(MarkupKind.StrikeOut);

    private async Task MarkSelectionAsync(MarkupKind kind)
    {
        var boxes = Viewer.GetSelectionBoxes();
        if (boxes.Count == 0) return;
        var text = await Viewer.GetSelectedTextAsync();
        var marks = boxes.Select((b, i) => new MarkupMark(b.Page, b.Boxes, boxes.Count == 1 ? text : null)).ToList();
        await PageActions.MarkupAsync(_session, kind, marks);
    }

    private async void OnRedactSelection(object sender, RoutedEventArgs e)
    {
        var marks = Viewer.GetSelectionBoxes().SelectMany(p => p.Boxes.Select(b => new RedactionMark(p.Page, b))).ToList();
        if (marks.Count == 0) return;
        if (!await AppServices.ConfirmAsync("Redact the selected text?",
                "The text is removed for good and the pages it is on are rebuilt as images with searchable text. You can still undo it before saving.",
                "Redact")) return;
        await PageActions.RedactAsync(_session, marks);
    }

    // ------------------------------------------------------------------ find

    private void OnFindToggle(object sender, RoutedEventArgs e)
    {
        if (FindBar.Visibility == Visibility.Visible) CloseFind();
        else ShowFind();
    }

    public async void ShowFind()
    {
        FindBar.Visibility = Visibility.Visible;
        FindToggle.IsChecked = true;
        // A short selection is what you probably want to look for.
        if (Viewer.HasSelection && await Viewer.GetSelectedTextAsync() is { Length: > 0 and < 80 } selected && !selected.Contains('\n'))
            FindBox.Text = selected;
        FindBox.Focus();
        FindBox.SelectAll();
    }

    /// <summary>Searches for <paramref name="query"/> as if it had been typed in the find bar.</summary>
    public void Find(string query)
    {
        FindBar.Visibility = Visibility.Visible;
        FindToggle.IsChecked = true;
        FindBox.Text = query;
        RunFind();
    }

    private void CloseFind()
    {
        _findCts?.Cancel();
        _findTimer.Stop();
        FindBar.Visibility = Visibility.Collapsed;
        FindToggle.IsChecked = false;
        _hits = [];
        _hit = -1;
        _searched = null;
        Viewer.ShowHits([], -1);
        Viewer.FocusView();
    }

    private void OnCloseFind(object sender, RoutedEventArgs e) => CloseFind();

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        _findTimer.Stop();
        _findTimer.Start();
    }

    private void OnFindOptionChanged(object sender, RoutedEventArgs e)
    {
        if (FindBox.Text.Trim().Length > 0) RunFind();
    }

    private void OnFindKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                if (_searched != SearchKey()) RunFind();
                else FindStep(shift ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseFind();
                e.Handled = true;
                break;
        }
    }

    private void OnFindNext(object sender, RoutedEventArgs e) => FindStep(1);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => FindStep(-1);

    private string SearchKey() => $"{FindBox.Text.Trim()}|{MatchCase.IsChecked}|{WholeWords.IsChecked}";

    private void FindStep(int step)
    {
        if (FindBar.Visibility != Visibility.Visible)
        {
            ShowFind();
            return;
        }
        if (_searched != SearchKey())
        {
            RunFind();
            return;
        }
        if (_hits.Count == 0) return;
        _hit = (_hit + step + _hits.Count) % _hits.Count;
        ShowHit();
    }

    /// <summary>Searches the whole document in the background; the first match on or after the current page comes first.</summary>
    private async void RunFind()
    {
        _findTimer.Stop();
        _findCts?.Cancel();
        var cts = _findCts = new CancellationTokenSource();
        var query = FindBox.Text.Trim();
        var key = SearchKey();
        var matchCase = MatchCase.IsChecked == true;
        var wholeWords = WholeWords.IsChecked == true;
        OcrButton.Visibility = Visibility.Collapsed;
        if (query.Length == 0)
        {
            _hits = [];
            _hit = -1;
            _searched = key;
            Viewer.ShowHits([], -1);
            FindStatus.Text = "";
            return;
        }
        FindStatus.Text = "Searching…";
        var text = await _session.GetTextAsync();
        if (cts.IsCancellationRequested) return;
        if (text == null)
        {
            FindStatus.Text = "The text of this document can't be read.";
            return;
        }
        IReadOnlyList<TextHit> hits;
        try
        {
            var progress = new Progress<double>(f =>
            {
                if (!cts.IsCancellationRequested && f < 1) FindStatus.Text = $"Searching… {f:P0}";
            });
            hits = await Task.Run(() => text.Find(query, matchCase, wholeWords, progress, cts.Token), cts.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }
        catch (RegexMatchTimeoutException)
        {
            FindStatus.Text = "That search took too long.";
            return;
        }
        if (cts.IsCancellationRequested) return;
        _searched = key;
        _hits = hits;
        var page = Viewer.CurrentPage;
        _hit = hits.Count == 0 ? -1 : Math.Max(0, hits.ToList().FindIndex(h => h.PageIndex >= page));
        ShowHit();
        if (hits.Count == 0 && Enumerable.Range(0, text.PageCount).All(i => text.Loaded(i) is not { HasText: true }))
        {
            // A scan: there is no text to find until OCR adds it.
            FindStatus.Text = "No text in this document.";
            OcrButton.Visibility = Visibility.Visible;
        }
    }

    private void ShowHit()
    {
        Viewer.ShowHits(_hits, _hit);
        FindStatus.Text = _hits.Count == 0 ? "No matches" : $"{_hit + 1} of {_hits.Count}";
    }

    private async void OnRunOcr(object sender, RoutedEventArgs e)
    {
        CloseFind();
        await OpenToolAsync("ocr");
    }

    // ------------------------------------------------------------------ properties

    public async Task ShowPropertiesAsync()
    {
        DocumentProperties props;
        try
        {
            var input = _session.CurrentInput;
            props = await Task.Run(() => DocumentProperties.Read(input));
        }
        catch (Exception ex)
        {
            AppServices.Notify("Couldn't read the properties", ex.Message, Ui.ControlAppearance.Danger, Ui.SymbolRegular.ErrorCircle24);
            return;
        }
        var rows = new List<(string Label, string? Value)>
        {
            ("File", _session.FilePath ?? "Not saved yet"),
            ("Title", props.Title),
            ("Author", props.Author),
            ("Subject", props.Subject),
            ("Keywords", props.Keywords),
            ("Created", props.Created?.ToString("f", CultureInfo.CurrentCulture)),
            ("Modified", props.Modified?.ToString("f", CultureInfo.CurrentCulture)),
            ("Application", props.Creator),
            ("PDF producer", props.Producer),
            ("PDF version", props.PdfVersion),
            ("Pages", props.PageCount.ToString(CultureInfo.CurrentCulture)),
            ("Page size", $"{props.PageSizeMm.Width:0} × {props.PageSizeMm.Height:0} mm{PaperName(props.PageSizeMm)}"),
            ("File size", Core.Tools.Optimize.CompressTool.Format(props.FileSize) + (_session.IsDirty ? " (with your unsaved changes)" : "")),
            ("Security", props.Encrypted ? (_session.Password != null ? "Password protected" : "Restrictions on printing, copying or editing") : "None"),
            ("Form fields", props.HasForm ? "Yes" : "No"),
            ("Bookmarks", props.HasBookmarks ? "Yes" : "No"),
        };
        var grid = new Grid { MinWidth = 420 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var (label, value) in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Margin = new Thickness(0, 3, 18, 3) };
            name.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            var text = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value) ? "—" : value,
                Margin = new Thickness(0, 3, 0, 3),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
            Grid.SetRow(name, grid.RowDefinitions.Count - 1);
            Grid.SetRow(text, grid.RowDefinitions.Count - 1);
            Grid.SetColumn(text, 1);
            grid.Children.Add(name);
            grid.Children.Add(text);
        }
        await AppServices.ShowDialogAsync(new Ui.ContentDialog
        {
            Title = "Document properties",
            Content = new ScrollViewer { Content = grid, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Close",
        });
    }

    private static string PaperName((double Width, double Height) mm)
    {
        var (w, h) = (Math.Min(mm.Width, mm.Height), Math.Max(mm.Width, mm.Height));
        static bool Near(double a, double b) => Math.Abs(a - b) < 2;
        var name = (w, h) switch
        {
            _ when Near(w, 210) && Near(h, 297) => "A4",
            _ when Near(w, 148) && Near(h, 210) => "A5",
            _ when Near(w, 297) && Near(h, 420) => "A3",
            _ when Near(w, 215.9) && Near(h, 279.4) => "Letter",
            _ when Near(w, 215.9) && Near(h, 355.6) => "Legal",
            _ => null,
        };
        return name == null ? "" : $" ({name})";
    }

    // ------------------------------------------------------------------ tools

    private void OnToolsToggle(object sender, RoutedEventArgs e)
    {
        var show = ToolsPane.Visibility != Visibility.Visible;
        ShowToolsPane(show);
        AppServices.Settings.ReaderToolsPane = show;
        AppServices.SaveSettings();
    }

    private void ShowToolsPane(bool show)
    {
        ToolsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToolsToggle.IsChecked = show;
    }

    private void ShowToolList(string? filter)
    {
        var tools = ToolRegistry.DocumentTools.Where(t => filter == null ||
                                                          t.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
                                                          t.Description.Contains(filter, StringComparison.CurrentCultureIgnoreCase));
        ToolGroups.ItemsSource = tools.GroupBy(t => t.Category).Select(g => new ToolGroup(ToolRegistry.CategoryTitle(g.Key), g.ToList())).ToList();
    }

    private void OnToolSearchChanged(object sender, TextChangedEventArgs e) =>
        ShowToolList(string.IsNullOrWhiteSpace(ToolSearch.Text) ? null : ToolSearch.Text.Trim());

    private async void OnToolClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) await OpenToolAsync(id);
    }

    /// <summary>Opens a tool on this document, in place of the reader until the tool is done or closed.</summary>
    public async Task OpenToolAsync(string toolId)
    {
        if (!await CloseToolAsync()) return;
        var tool = ToolRegistry.Get(toolId);
        var controller = tool.CreateController();
        controller.Context = new DocumentToolContext(tool.EditsDocument ? _session.NewOutputFolder() : _session.OutputFolder, tool.EditsDocument);
        if (_session.Password is { } password) controller.KnownPasswords[_session.CurrentPath] = password;
        // Subscribed before the tool's screen is built, so results are dealt with before the screen shows them.
        controller.PropertyChanged += OnToolChanged;
        _tool = controller;
        ToolContent.Content = new ToolShell { DataContext = controller };
        ToolModeTitle.Text = $"{tool.Title}  ·  {_session.Title}";
        ToolHint.Text = tool.EditsDocument
            ? "The result replaces the document in this tab. You can undo it."
            : $"New files are saved in {_session.OutputFolder}.";
        ToolMode.Visibility = Visibility.Visible;
        Reader.Visibility = Visibility.Collapsed;
        await controller.AddFilesAsync([_session.CurrentPath]);
        await controller.OnShownAsync();
    }

    /// <summary>Leaves the tool; asks first when it holds changes that haven't been applied.</summary>
    public async Task<bool> CloseToolAsync()
    {
        if (_tool == null) return true;
        if (_tool is EditorController { Undo.CanUndo: true, HasResult: false } editor &&
            !await AppServices.ConfirmAsync("Discard your changes?", $"What you did in {editor.Descriptor.Title} hasn't been applied to the document yet.", "Discard", "Keep editing"))
            return false;
        CloseTool();
        return true;
    }

    private async void OnCloseTool(object sender, RoutedEventArgs e) => await CloseToolAsync();

    private void CloseTool()
    {
        if (_tool == null) return;
        _tool.PropertyChanged -= OnToolChanged;
        if (_tool.IsBusy) _tool.CancelCommand.Execute(null);
        _tool = null;
        ToolContent.Content = null;
        ToolMode.Visibility = Visibility.Collapsed;
        Reader.Visibility = Visibility.Visible;
        Viewer.FocusView();
    }

    /// <summary>
    /// When a tool that changes the document finishes, its result becomes the document's next version.
    /// Any other files it made (several inputs, say) are moved next to the document.
    /// </summary>
    private async void OnToolChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ToolController.Result) || sender is not ToolController { Result: { } result } tool || tool != _tool) return;
        if (!tool.Descriptor.EditsDocument) return;
        var outputs = result.OutputFiles;
        var replaces = string.Equals(tool.Files.FirstOrDefault()?.Path, _session.CurrentPath, StringComparison.OrdinalIgnoreCase) &&
                       outputs.Count > 0 && outputs[0].EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        // Done before anything awaits, so the result screen (if it shows) lists the moved files.
        var moved = 0;
        for (var i = replaces ? 1 : 0; i < outputs.Count; i++)
        {
            try
            {
                var target = Core.Common.OutputPaths.Unique(_session.OutputFolder, Path.GetFileName(outputs[i]));
                File.Move(outputs[i], target);
                outputs[i] = target;
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                AppServices.LogError(ex);
            }
        }
        if (!replaces) return;

        var title = tool.Descriptor.Title;
        var newPassword = (tool as ProtectController)?.OpenPassword;
        bool applied;
        try
        {
            applied = await _session.CommitAsync(outputs[0], title, wrong => AppServices.AskPasswordAsync(_session.Title, wrong), newPassword);
        }
        catch (Exception ex)
        {
            AppServices.LogError(ex);
            AppServices.Notify($"Couldn't apply {title}", ex.Message, Ui.ControlAppearance.Danger, Ui.SymbolRegular.ErrorCircle24);
            return;
        }
        if (tool != _tool) return;
        CloseTool();
        if (!applied) return;
        var others = moved > 0 ? $" {moved} other file{(moved == 1 ? " was" : "s were")} saved in {_session.OutputFolder}." : "";
        AppServices.Notify($"{title}: done", $"{result.Summary ?? "The document was updated."}{others} Undo with Ctrl+Z.", Ui.ControlAppearance.Success, Ui.SymbolRegular.CheckmarkCircle24);
    }
}

/// <summary>A page in the thumbnails panel; its image is rendered when it scrolls into view.</summary>
public sealed partial class PageThumbnail(int index, double width, double height) : ObservableObject
{
    public int Index { get; } = index;
    public string Label { get; } = (index + 1).ToString(CultureInfo.CurrentCulture);
    public double Width { get; } = width;
    public double Height { get; } = height;
    public bool Requested { get; set; }

    [ObservableProperty] public partial BitmapSource? Image { get; set; }
}

/// <summary>A category of tools in the All tools pane.</summary>
public sealed record ToolGroup(string Title, IReadOnlyList<ToolDescriptor> Tools);

/// <summary>A comment in the Comments panel.</summary>
public sealed record CommentRow(PageComment Comment)
{
    public Ui.SymbolRegular Icon => Comment.Kind switch
    {
        CommentKind.Note => Ui.SymbolRegular.Note24,
        CommentKind.Highlight => Ui.SymbolRegular.Highlight24,
        CommentKind.Underline or CommentKind.Squiggly => Ui.SymbolRegular.TextUnderline24,
        CommentKind.StrikeOut => Ui.SymbolRegular.TextStrikethrough24,
        CommentKind.TextBox => Ui.SymbolRegular.Textbox24,
        CommentKind.Drawing => Ui.SymbolRegular.HandDraw24,
        CommentKind.Shape => Ui.SymbolRegular.Shapes24,
        _ => Ui.SymbolRegular.Comment24,
    };

    public string Title => $"{DocumentComments.Describe(Comment.Kind)} · page {Comment.PageIndex + 1}";

    public string Details => string.Join(" · ", new[] { Comment.Author, Comment.Modified?.ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture) }.Where(s => !string.IsNullOrEmpty(s)));

    public string? Text => Comment.Text;
}
