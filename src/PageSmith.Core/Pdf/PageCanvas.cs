using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PageSmith.Core.Pdf;

/// <summary>
/// An <see cref="XGraphics"/> for drawing on an existing page in display space: points, origin at
/// the top-left of the visible page, y down, page rotation applied — what the user sees.
/// </summary>
public sealed class PageCanvas : IDisposable
{
    private PageCanvas(XGraphics gfx, PageGeometry geometry)
    {
        Graphics = gfx;
        Geometry = geometry;
    }

    public XGraphics Graphics { get; }
    public PageGeometry Geometry { get; }
    public double Width => Geometry.Width;
    public double Height => Geometry.Height;

    /// <param name="page">Target page.</param>
    /// <param name="options">Append draws over the content, Prepend draws beneath it.</param>
    public static PageCanvas Open(PdfPage page, XGraphicsPdfPageOptions options = XGraphicsPdfPageOptions.Append)
    {
        var geometry = PageGeometry.From(page);
        var gfx = XGraphics.FromPdfPage(page, options);
        gfx.MultiplyTransform(geometry.DisplayToGraphics());
        return new PageCanvas(gfx, geometry);
    }

    public void Dispose() => Graphics.Dispose();
}
