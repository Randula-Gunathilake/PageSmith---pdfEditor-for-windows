using System.Windows;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PageSmith.Core.Pdf;

/// <summary>
/// Converts between PDF user space (origin bottom-left, y up, rotation not applied) and
/// display space: points with the origin at the top-left of the visible page and y down,
/// after applying /Rotate — the page exactly as viewers (and <c>PdfRenderer</c>) show it.
/// </summary>
public sealed class PageGeometry
{
    public PageGeometry(Rect visibleBox, int rotation, double mediaBoxHeight)
    {
        Box = visibleBox;
        Rotation = ((rotation % 360) + 360) % 360;
        MediaBoxHeight = mediaBoxHeight;
    }

    /// <summary>Visible area in user space (CropBox clipped to MediaBox).</summary>
    public Rect Box { get; }

    /// <summary>Clockwise display rotation: 0, 90, 180 or 270.</summary>
    public int Rotation { get; }

    public double MediaBoxHeight { get; }

    public bool IsSideways => Rotation is 90 or 270;
    public double Width => IsSideways ? Box.Height : Box.Width;
    public double Height => IsSideways ? Box.Width : Box.Height;

    public static PageGeometry From(PdfPage page)
    {
        var media = ToRect(page.MediaBox);
        var crop = page.HasCropBox ? ToRect(page.CropBox) : media;
        var visible = Rect.Intersect(crop, media);
        if (visible.IsEmpty || visible.Width < 1 || visible.Height < 1) visible = media;
        return new PageGeometry(visible, page.Rotate, page.Height.Point);
    }

    public static PageGeometry From(UglyToad.PdfPig.Content.Page page)
    {
        var media = page.MediaBox.Bounds;
        var crop = page.CropBox.Bounds;
        var m = new Rect(media.Left, media.Bottom, media.Width, media.Height);
        var c = new Rect(crop.Left, crop.Bottom, crop.Width, crop.Height);
        var visible = Rect.Intersect(c, m);
        if (visible.IsEmpty || visible.Width < 1 || visible.Height < 1) visible = m;
        return new PageGeometry(visible, page.Rotation.Value, media.Height);
    }

    public static Rect ToRect(PdfRectangle r) =>
        new(Math.Min(r.X1, r.X2), Math.Min(r.Y1, r.Y2), Math.Abs(r.X2 - r.X1), Math.Abs(r.Y2 - r.Y1));

    public Point ToDisplay(Point user)
    {
        double x0 = Box.Left, y0 = Box.Top, x1 = Box.Right, y1 = Box.Bottom; // Rect.Top is the smaller y
        var (ux, uy) = (user.X, user.Y);
        return Rotation switch
        {
            90 => new Point(uy - y0, ux - x0),
            180 => new Point(x1 - ux, uy - y0),
            270 => new Point(y1 - uy, x1 - ux),
            _ => new Point(ux - x0, y1 - uy),
        };
    }

    public Point ToUser(Point display)
    {
        double x0 = Box.Left, y0 = Box.Top, x1 = Box.Right, y1 = Box.Bottom;
        var (dx, dy) = (display.X, display.Y);
        return Rotation switch
        {
            90 => new Point(x0 + dy, y0 + dx),
            180 => new Point(x1 - dx, y0 + dy),
            270 => new Point(x1 - dy, y1 - dx),
            _ => new Point(x0 + dx, y1 - dy),
        };
    }

    /// <summary>Converts a user-space rectangle to display space.</summary>
    public Rect ToDisplay(Rect user)
    {
        var a = ToDisplay(new Point(user.Left, user.Top));
        var b = ToDisplay(new Point(user.Right, user.Bottom));
        return new Rect(a, b);
    }

    /// <summary>Converts a display-space rectangle to user space.</summary>
    public Rect ToUser(Rect display)
    {
        var a = ToUser(new Point(display.Left, display.Top));
        var b = ToUser(new Point(display.Right, display.Bottom));
        return new Rect(a, b);
    }

    /// <summary>
    /// Matrix that maps display space to the coordinate space of <c>XGraphics.FromPdfPage</c>,
    /// which PDFsharp anchors at user (0, MediaBox height) with y down, ignoring /Rotate and box offsets.
    /// </summary>
    public XMatrix DisplayToGraphics()
    {
        double x0 = Box.Left, y0 = Box.Top, x1 = Box.Right, y1 = Box.Bottom, h = MediaBoxHeight;
        // XMatrix(m11, m12, m21, m22, dx, dy): x' = m11*x + m21*y + dx; y' = m12*x + m22*y + dy.
        return Rotation switch
        {
            90 => new XMatrix(0, -1, 1, 0, x0, h - y0),
            180 => new XMatrix(-1, 0, 0, -1, x1, h - y0),
            270 => new XMatrix(0, 1, -1, 0, x1, h - y1),
            _ => new XMatrix(1, 0, 0, 1, x0, h - y1),
        };
    }
}
