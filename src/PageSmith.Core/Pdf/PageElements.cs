using System.Text.Json.Serialization;
using System.Windows;
using PdfSharp.Drawing;

namespace PageSmith.Core.Pdf;

public enum ShapeKind
{
    Rectangle,
    Ellipse,
    Line,
    Arrow,
}

/// <summary>
/// Something drawn onto a page by Edit PDF, Sign PDF or Redact PDF. Coordinates are display-space points
/// (see <see cref="PageGeometry"/>); <see cref="Rotation"/> turns the element clockwise about its centre.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextElement), "text")]
[JsonDerivedType(typeof(ImageElement), "image")]
[JsonDerivedType(typeof(ShapeElement), "shape")]
[JsonDerivedType(typeof(InkElement), "ink")]
public abstract record PageElement
{
    public int PageIndex { get; init; }
    public Rect Bounds { get; init; }
    public double Rotation { get; init; }
    public double Opacity { get; init; } = 1;
}

public sealed record TextElement : PageElement
{
    public string Text { get; init; } = "";
    public TextStyle Style { get; init; } = new();
    public TextAlign Align { get; init; } = TextAlign.Left;

    /// <summary>Optional background, e.g. white to cover text being replaced.</summary>
    public string? Background { get; init; }
}

public sealed record ImageElement : PageElement
{
    /// <summary>Encoded PNG or JPEG bytes.</summary>
    public byte[] Data { get; init; } = [];
}

public sealed record ShapeElement : PageElement
{
    public ShapeKind Shape { get; init; } = ShapeKind.Rectangle;
    public string? Stroke { get; init; } = "#000000";
    public string? Fill { get; init; }
    public double StrokeWidth { get; init; } = 1.5;

    /// <summary>For lines and arrows: true when the line runs from the bottom-left to the top-right corner.</summary>
    public bool RisingDiagonal { get; init; }
}

public sealed record InkElement : PageElement
{
    /// <summary>Freehand strokes as point lists (display space).</summary>
    public IReadOnlyList<IReadOnlyList<Point>> Strokes { get; init; } = [];
    public string Color { get; init; } = "#1F3A93";
    public double Width { get; init; } = 2;
}

/// <summary>Draws page elements with PDFsharp.</summary>
public static class PageElementRenderer
{
    public static void Draw(XGraphics gfx, PageElement element)
    {
        var b = element.Bounds;
        var state = gfx.Save();
        if (element.Rotation != 0)
            gfx.RotateAtTransform(element.Rotation, new XPoint(b.X + b.Width / 2, b.Y + b.Height / 2));
        switch (element)
        {
            case TextElement t:
                DrawText(gfx, t);
                break;
            case ImageElement img:
                DrawImage(gfx, img);
                break;
            case ShapeElement s:
                DrawShape(gfx, s);
                break;
            case InkElement ink:
                DrawInk(gfx, ink);
                break;
        }
        gfx.Restore(state);
    }

    private static void DrawText(XGraphics gfx, TextElement t)
    {
        var r = new XRect(t.Bounds.X, t.Bounds.Y, t.Bounds.Width, t.Bounds.Height);
        if (t.Background != null) gfx.DrawRectangle(new XSolidBrush(TextStyle.ParseColor(t.Background, t.Opacity)), r);
        var run = new TextRun(t.Text, t.Style.Font, t.Style.Size, t.Style.Bold, t.Style.Italic, t.Style.Underline, t.Style.ToColor(t.Opacity));
        var paragraphs = t.Text.Split('\n').Select(line => new RichParagraph([run with { Text = line.Length == 0 ? " " : line }], t.Align)).ToList();
        RichTextLayout.Draw(gfx, paragraphs, t.Bounds, VerticalAnchor.Top, shrinkToFit: false, lineSpacing: 1.15);
    }

    private static void DrawImage(XGraphics gfx, ImageElement img)
    {
        if (img.Data.Length == 0) return;
        var data = img.Opacity < 1 ? WithOpacity(img.Data, img.Opacity) : img.Data;
        using var ms = new MemoryStream(data);
        using var x = XImage.FromStream(ms);
        gfx.DrawImage(x, img.Bounds.X, img.Bounds.Y, img.Bounds.Width, img.Bounds.Height);
    }

    private static byte[] WithOpacity(byte[] data, double opacity)
    {
        var px = Imaging.ImageIO.ToPixels(Imaging.ImageIO.Decode(data));
        for (var i = 3; i < px.Pixels.Length; i += 4) px.Pixels[i] = (byte)(px.Pixels[i] * opacity);
        return Imaging.ImageIO.EncodePng(Imaging.ImageIO.FromPixels(px));
    }

    private static void DrawShape(XGraphics gfx, ShapeElement s)
    {
        var r = new XRect(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height);
        var pen = s.Stroke != null && s.StrokeWidth > 0 ? new XPen(TextStyle.ParseColor(s.Stroke, s.Opacity), s.StrokeWidth) { LineJoin = XLineJoin.Round, LineCap = XLineCap.Round } : null;
        var brush = s.Fill != null ? new XSolidBrush(TextStyle.ParseColor(s.Fill, s.Opacity)) : null;
        switch (s.Shape)
        {
            case ShapeKind.Ellipse:
                if (pen != null && brush != null) gfx.DrawEllipse(pen, brush, r);
                else if (brush != null) gfx.DrawEllipse(brush, r);
                else if (pen != null) gfx.DrawEllipse(pen, r);
                break;
            case ShapeKind.Line or ShapeKind.Arrow:
                pen ??= new XPen(XColors.Black, 1.5);
                var (x1, y1, x2, y2) = s.RisingDiagonal ? (r.Left, r.Bottom, r.Right, r.Top) : (r.Left, r.Top, r.Right, r.Bottom);
                gfx.DrawLine(pen, x1, y1, x2, y2);
                if (s.Shape == ShapeKind.Arrow)
                {
                    var angle = Math.Atan2(y2 - y1, x2 - x1);
                    var head = Math.Max(6, s.StrokeWidth * 4);
                    var p1 = new XPoint(x2 - head * Math.Cos(angle - 0.45), y2 - head * Math.Sin(angle - 0.45));
                    var p2 = new XPoint(x2 - head * Math.Cos(angle + 0.45), y2 - head * Math.Sin(angle + 0.45));
                    gfx.DrawPolygon(new XSolidBrush(pen.Color), [new XPoint(x2, y2), p1, p2], XFillMode.Winding);
                }
                break;
            default:
                if (pen != null && brush != null) gfx.DrawRectangle(pen, brush, r);
                else if (brush != null) gfx.DrawRectangle(brush, r);
                else if (pen != null) gfx.DrawRectangle(pen, r);
                break;
        }
    }

    private static void DrawInk(XGraphics gfx, InkElement ink)
    {
        var pen = new XPen(TextStyle.ParseColor(ink.Color, ink.Opacity), ink.Width) { LineCap = XLineCap.Round, LineJoin = XLineJoin.Round };
        foreach (var stroke in ink.Strokes)
        {
            if (stroke.Count == 1)
            {
                var p = stroke[0];
                gfx.DrawEllipse(new XSolidBrush(pen.Color), p.X - ink.Width / 2, p.Y - ink.Width / 2, ink.Width, ink.Width);
                continue;
            }
            if (stroke.Count > 1) gfx.DrawLines(pen, stroke.Select(p => new XPoint(p.X, p.Y)).ToArray());
        }
    }
}
