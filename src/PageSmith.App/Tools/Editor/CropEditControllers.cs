using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Infrastructure;
using PageSmith.App.Tools.Editor;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PageSmith.Core.Text;
using PageSmith.Core.Tools.Edit;
using Wpf.Ui.Controls;

namespace PageSmith.App.Tools;

// ---------------------------------------------------------------- Crop

public sealed partial class CropController() : EditorController("crop")
{
    private static readonly EditorTool Adjust = new("crop", "Adjust the crop area", SymbolRegular.Crop24, PointerAction.Select,
        "Drag the frame or its handles to choose what to keep. Everything outside the frame is cut away.");

    private CropItem? _crop;
    private Rect _fraction = new(0.05, 0.05, 0.9, 0.9);

    public override IReadOnlyList<EditorTool> Tools { get; } = [Adjust];

    [ObservableProperty] public partial bool CurrentPageOnly { get; set; }
    [ObservableProperty] public partial string AreaText { get; set; } = "";

    protected override EditorItem? DefaultSelection => _crop;
    protected override bool CountsAsEdit(EditorItem item) => item is not CropItem;

    protected override Task OnDocumentOpenedAsync()
    {
        _fraction = new Rect(0.05, 0.05, 0.9, 0.9);
        PlaceCrop(CurrentPageIndex);
        return Task.CompletedTask;
    }

    protected override void OnDocumentClosed() => _crop = null;

    protected override void OnCurrentPageChanged()
    {
        if (_crop == null || Pages.Count == 0) return;
        _fraction = _crop.Fraction;
        PlaceCrop(CurrentPageIndex);
    }

    /// <summary>Shows the crop frame on a page, at the same relative position as before.</summary>
    private void PlaceCrop(int page)
    {
        if (page < 0 || page >= Pages.Count) return;
        if (_crop != null) Items.Remove(_crop);
        var size = Pages[page].Size;
        _crop = new CropItem
        {
            PageIndex = page,
            PageSize = size,
            Bounds = new Rect(_fraction.X * size.Width, _fraction.Y * size.Height, _fraction.Width * size.Width, _fraction.Height * size.Height),
        };
        _crop.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(EditorItem.Bounds)) UpdateAreaText();
        };
        Items.Add(_crop);
        Undo.Clear();
        SelectedItem = _crop;
        UpdateAreaText();
    }

    private void UpdateAreaText()
    {
        if (_crop == null) return;
        static string Mm(double pt) => Math.Round(pt * 25.4 / 72).ToString("0");
        AreaText = $"Keeps {Mm(_crop.Bounds.Width)} × {Mm(_crop.Bounds.Height)} mm of the {Mm(_crop.PageSize.Width)} × {Mm(_crop.PageSize.Height)} mm page.";
    }

    [RelayCommand]
    private async Task AutoDetectAsync()
    {
        if (_crop == null || Renderer is not { } renderer) return;
        try
        {
            var fraction = await CropTool.DetectContentAsync(renderer, CurrentPageIndex);
            var before = _crop.Bounds;
            var size = _crop.PageSize;
            _crop.SetBounds(new Rect(fraction.X * size.Width, fraction.Y * size.Height, fraction.Width * size.Width, fraction.Height * size.Height));
            RecordBoundsChange(_crop, before, _crop.Bounds);
        }
        catch (UserFacingException ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void ResetArea()
    {
        if (_crop == null) return;
        var before = _crop.Bounds;
        _crop.SetBounds(new Rect(_crop.PageSize));
        RecordBoundsChange(_crop, before, _crop.Bounds);
    }

    public override string? Validate()
    {
        if (base.Validate() is { } problem) return problem;
        if (_crop is { } c && c.Fraction.Width > 0.995 && c.Fraction.Height > 0.995) return "Drag the frame's edges inwards to choose the area to keep.";
        return null;
    }

    public override Task SeedDemoAsync() => AutoDetectAsync();

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        CropTool.RunAsync(new CropOptions
        {
            Input = Document!.ToInput(),
            OutputPath = OutputFor("cropped"),
            Area = _crop!.Fraction,
            Pages = CurrentPageOnly ? (CurrentPageIndex + 1).ToString() : null,
        }, progress, ct);
}

// ---------------------------------------------------------------- Edit PDF

public sealed partial class EditController() : EditorController("edit")
{
    private static readonly EditorTool SelectTool = new("select", "Select", SymbolRegular.Cursor24, PointerAction.Select,
        "Click an item to select it; drag to move, use the handles to resize. Double-click text to edit it.");
    private static readonly EditorTool TextTool = new("text", "Add text", SymbolRegular.TextAddT24, PointerAction.Click, "Click where the text should go, then type.");
    private static readonly EditorTool EditTextTool = new("edit-text", "Edit existing text", SymbolRegular.TextEditStyle24, PointerAction.Pick,
        "Click a line of the document's text to replace it. The old text is covered and your new text is written on top.");
    private static readonly EditorTool ImageTool = new("image", "Add image", SymbolRegular.ImageAdd24, PointerAction.Click, "Click where the image should go and choose a picture.");
    private static readonly EditorTool RectTool = new("rect", "Rectangle", SymbolRegular.RectangleLandscape24, PointerAction.DrawRect, "Drag on the page to draw a rectangle.");
    private static readonly EditorTool EllipseTool = new("ellipse", "Ellipse", SymbolRegular.Oval24, PointerAction.DrawRect, "Drag on the page to draw an ellipse.");
    private static readonly EditorTool LineTool = new("line", "Line", SymbolRegular.Line24, PointerAction.DrawRect, "Drag from where the line starts to where it ends.");
    private static readonly EditorTool ArrowTool = new("arrow", "Arrow", SymbolRegular.ArrowUpRight24, PointerAction.DrawRect, "Drag from the arrow's tail to its tip.");
    private static readonly EditorTool InkTool = new("draw", "Draw", SymbolRegular.InkingTool24, PointerAction.Ink, "Draw freehand with the mouse, pen or finger.");
    private static readonly EditorTool WhiteoutTool = new("whiteout", "White-out", SymbolRegular.EraserTool24, PointerAction.DrawRect, "Drag over content to cover it with white.");

    private readonly Dictionary<int, IReadOnlyList<TextLineInfo>> _lines = [];

    public override IReadOnlyList<EditorTool> Tools { get; } =
        [SelectTool, TextTool, EditTextTool, ImageTool, RectTool, EllipseTool, LineTool, ArrowTool, InkTool, WhiteoutTool];

    private static IReadOnlyList<string> Fonts => TextItem.AvailableFonts;

    /// <summary>Style used for new text, shapes and drawings while nothing is selected.</summary>
    public TextItem DefaultText { get; } = new() { FontName = "Arial", FontSize = 14, IsStyleOnly = true };
    public ShapeItem DefaultShape { get; } = new() { IsStyleOnly = true };
    public InkItem DefaultInk { get; } = new() { IsStyleOnly = true };

    /// <summary>What the properties panel edits: the selection, or the style of the active tool.</summary>
    public object? PropertiesTarget => SelectedItem ?? (ActiveTool?.Id switch
    {
        "text" or "edit-text" => DefaultText,
        "rect" or "ellipse" or "line" or "arrow" => DefaultShape,
        "draw" => DefaultInk,
        _ => null,
    });

    public override (string Color, double Width) InkPen => (DefaultInk.Color, DefaultInk.Width);
    public override bool KeepToolAfterCreate(EditorTool tool) => tool == WhiteoutTool;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(SelectedItem) or nameof(ActiveTool)) OnPropertyChanged(nameof(PropertiesTarget));
    }

    protected override void OnDocumentClosed() => _lines.Clear();

    public override EditorItem? CreateAt(EditorTool tool, int page, Point point)
    {
        if (tool == TextTool)
        {
            var text = new TextItem
            {
                PageIndex = page,
                PageWidth = Pages[page].Size.Width,
                AutoWidth = true,
                FontName = DefaultText.FontName,
                FontSize = DefaultText.FontSize,
                Bold = DefaultText.Bold,
                Italic = DefaultText.Italic,
                Underline = DefaultText.Underline,
                Color = DefaultText.Color,
                Align = DefaultText.Align,
            };
            var width = Math.Max(160, text.MeasureWidth());
            var height = text.MeasureHeight(width);
            text.Bounds = PlaceBox(page, new Point(point.X + width / 2, point.Y + height / 2 - text.FontSize * 0.6), new Size(width, height));
            return text;
        }
        if (tool == ImageTool)
        {
            var path = AppServices.PickFile("Choose an image", $"Images|{string.Join(";", ImageIO.SupportedExtensions.Select(e => "*" + e))}");
            if (path == null) return null;
            try
            {
                var item = new ImageItem(LoadImageData(path));
                var max = Math.Min(220, Pages[page].Size.Width * 0.6);
                var scale = Math.Min(max / item.Image.PixelWidth, max / item.Image.PixelHeight);
                item.PageIndex = page;
                item.Bounds = PlaceBox(page, point, new Size(item.Image.PixelWidth * scale, item.Image.PixelHeight * scale));
                return item;
            }
            catch (Exception ex)
            {
                Error = $"That image could not be opened: {ex.Message}";
                return null;
            }
        }
        return null;
    }

    /// <summary>JPEG and PNG go in as they are; other formats are converted to PNG.</summary>
    internal static byte[] LoadImageData(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".png" || ImageIO.IsJpeg(path))
        {
            var bytes = File.ReadAllBytes(path);
            if (ImageIO.IsJpeg(path) && ImageIO.GetExifOrientation(path) is not (0 or 1))
                return ImageIO.EncodeJpeg(ImageIO.Load(path), 92);
            return bytes;
        }
        return ImageIO.EncodePng(ImageIO.Load(path));
    }

    public override EditorItem? CreateFromRect(EditorTool tool, int page, Rect rect)
    {
        var tiny = rect.Width < 3 && rect.Height < 3;
        if (tool == WhiteoutTool)
            return tiny ? null : new ShapeItem { PageIndex = page, Bounds = rect, Shape = ShapeKind.Rectangle, Stroke = null, Fill = "#FFFFFF", IsWhiteout = true };

        var kind = tool == EllipseTool ? ShapeKind.Ellipse : tool == LineTool ? ShapeKind.Line : tool == ArrowTool ? ShapeKind.Arrow : ShapeKind.Rectangle;
        if (tiny) rect = PlaceBox(page, rect.TopLeft, kind is ShapeKind.Line or ShapeKind.Arrow ? new Size(120, 0.01) : new Size(120, 80));
        return new ShapeItem
        {
            PageIndex = page,
            Bounds = rect,
            Shape = kind,
            Stroke = DefaultShape.Stroke,
            Fill = kind is ShapeKind.Line or ShapeKind.Arrow ? null : DefaultShape.Fill,
            StrokeWidth = DefaultShape.StrokeWidth,
            Opacity = DefaultShape.Opacity,
        };
    }

    /// <summary>"Edit existing text": covers the clicked line and puts an editable copy on top.</summary>
    public override async Task PickAsync(EditorTool tool, int page, Point point)
    {
        if (tool != EditTextTool || Document is not { } doc) return;
        if (!_lines.TryGetValue(page, out var lines))
        {
            try
            {
                lines = await Task.Run(() =>
                {
                    var text = TextExtractor.ReadPages(doc.ToInput(), [page]).FirstOrDefault();
                    return text == null ? [] : TextExtractor.GetLines(text.Words);
                });
            }
            catch (Exception ex) when (ex is UserFacingException or PasswordRequiredException)
            {
                lines = [];
            }
            _lines[page] = lines;
        }
        var line = lines.Where(l => Inflate(l.Bounds, 2).Contains(point)).OrderBy(l => l.Bounds.Width * l.Bounds.Height).FirstOrDefault();
        if (line == null)
        {
            AppServices.Notify("No text there", "Click directly on a line of text. Scanned pages have no editable text; run OCR PDF first, or use Add text and White-out.");
            return;
        }
        var covered = ItemsOn(page).OfType<TextItem>().FirstOrDefault(t => t.Background != null && t.Bounds.IntersectsWith(line.Bounds));
        if (covered != null)
        {
            SelectedItem = covered;
            covered.IsEditing = true;
            return;
        }

        var first = line.Words[0];
        var box = Inflate(line.Bounds, 1.5);
        var item = new TextItem
        {
            PageIndex = page,
            PageWidth = Pages[page].Size.Width,
            Text = line.Text,
            FontName = MatchFont(first.FontName),
            FontSize = Math.Round(Math.Max(4, line.FontSize), 1),
            Bold = line.Bold,
            Italic = line.Italic,
            Color = $"#{first.Rgb & 0xFFFFFF:X6}",
            Background = await SampleBackgroundAsync(page, line.Bounds),
        }.WithKind("Edited text");
        // Set the box last so the text measures against the final width; it may grow but never shrink.
        item.Bounds = new Rect(box.X, box.Y, box.Width + item.FontSize, box.Height);
        item.AutoWidth = true;
        item.GrowToFit();
        AddItem(item);
        item.IsEditing = true;
    }

    private static Rect Inflate(Rect r, double by)
    {
        r.Inflate(by, by);
        return r;
    }

    /// <summary>Uses the same font when Windows has it, otherwise a close standard one.</summary>
    private string MatchFont(string pdfFont)
    {
        var name = TextExtractor.CleanFontName(pdfFont);
        var squashed = name.Replace(" ", "").Replace("-", "");
        var match = Fonts.FirstOrDefault(f => f.Replace(" ", "").Equals(squashed, StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;
        var lower = squashed.ToLowerInvariant();
        return lower.Contains("times") || lower.Contains("serif") && !lower.Contains("sans") ? "Times New Roman"
            : lower.Contains("courier") || lower.Contains("mono") ? "Courier New"
            : lower.Contains("calibri") ? "Calibri"
            : lower.Contains("cambria") ? "Cambria"
            : lower.Contains("georgia") ? "Georgia"
            : lower.Contains("verdana") ? "Verdana"
            : lower.Contains("segoe") ? "Segoe UI"
            : "Arial";
    }

    /// <summary>The paper colour around a line, so the cover box blends in on tinted backgrounds.</summary>
    private async Task<string> SampleBackgroundAsync(int page, Rect line)
    {
        const double dpi = 72;
        var bitmap = await RenderPageAsync(page, dpi);
        if (bitmap == null) return "#FFFFFF";
        var px = ImageIO.ToPixels(bitmap);
        var samples = new List<(byte R, byte G, byte B)>();
        void Sample(double x, double y)
        {
            var ix = (int)Math.Round(x * px.Width / Pages[page].Size.Width);
            var iy = (int)Math.Round(y * px.Height / Pages[page].Size.Height);
            if (ix < 0 || iy < 0 || ix >= px.Width || iy >= px.Height) return;
            var i = (iy * px.Width + ix) * 4;
            samples.Add((px.Pixels[i + 2], px.Pixels[i + 1], px.Pixels[i]));
        }
        Sample(line.Left - 3, line.Top + line.Height / 2);
        Sample(line.Right + 3, line.Top + line.Height / 2);
        Sample(line.Left + line.Width / 2, line.Top - 2);
        Sample(line.Left + line.Width / 2, line.Bottom + 2);
        if (samples.Count == 0) return "#FFFFFF";
        // The brightest sample is most likely the paper rather than neighbouring ink.
        var (r, g, b) = samples.OrderByDescending(s => s.R + s.G + s.B).First();
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    public override string? Validate()
    {
        if (base.Validate() is { } problem) return problem;
        return Elements().Count == 0 ? "Add text, an image, a shape or a drawing to the pages first." : null;
    }

    public override async Task SeedDemoAsync()
    {
        CurrentPageIndex = 0;
        var page = CurrentPageIndex;
        await PickAsync(EditTextTool, page, new Point(110, 82));
        if (SelectedItem is TextItem title) title.Text = "Annual Report 2026";
        if (CreateFromRect(WhiteoutTool, page, new Rect(58, 206, 260, 17)) is { } whiteout) AddItem(whiteout);
        if (CreateFromRect(RectTool, page, new Rect(52, 252, 430, 100)) is { } rect) AddItem(rect);
        if (CreateFromRect(ArrowTool, page, new Rect(300, 560, 110, 60)) is { } arrow) AddItem(arrow);
        var ink = new InkItem { PageIndex = page, Color = DefaultInk.Color, Width = 2.5 };
        ink.Strokes.Add(Enumerable.Range(0, 60).Select(i => new Point(80 + i * 3, 720 + Math.Sin(i / 4.0) * 12)).ToList());
        ink.FitBounds();
        AddItem(ink, select: false);
        AddItem(new ImageItem(DemoStamp(), "Stamp") { PageIndex = page, Bounds = new Rect(420, 390, 130, 52) });
        if (CreateAt(TextTool, page, new Point(310, 640)) is TextItem note)
        {
            note.Text = "Approved by the finance team";
            note.Color = "#D32F2F";
            note.Bold = true;
            AddItem(note);
        }
    }

    private static byte[] DemoStamp()
    {
        var visual = new System.Windows.Media.DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var red = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28));
            dc.DrawRoundedRectangle(null, new System.Windows.Media.Pen(red, 6), new Rect(4, 4, 252, 96), 14, 14);
            var text = new System.Windows.Media.FormattedText("PAID", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new System.Windows.Media.Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal), 64, red, 1.0);
            dc.DrawText(text, new Point((260 - text.Width) / 2, (104 - text.Height) / 2));
        }
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(260, 104, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return ImageIO.EncodePng(bitmap);
    }

    private List<PageElement> Elements() => Items.Select<EditorItem, PageElement?>(i => i switch
    {
        TextItem t when !string.IsNullOrWhiteSpace(t.Text) => t.ToElement(),
        ImageItem img => img.ToElement(),
        ShapeItem s => s.ToElement(),
        InkItem ink => ink.ToElement(),
        _ => null,
    }).OfType<PageElement>().ToList();

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        EditPdfTool.RunAsync(new EditPdfOptions { Input = Document!.ToInput(), OutputPath = OutputFor("edited"), Elements = Elements() }, progress, ct);
}
