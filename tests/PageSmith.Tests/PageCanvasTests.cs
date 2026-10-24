using System.IO;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PageSmith.Tests;

public class PageCanvasTests
{
    /// <summary>Bounding box of dark pixels in a rendered page at 72 DPI (1 pixel = 1 point).</summary>
    internal static async Task<(int X0, int Y0, int X1, int Y1, int W, int H)> DarkBounds(string pdf, int page = 0)
    {
        using var r = await PdfRenderer.OpenAsync(pdf);
        var px = ImageIO.ToPixels(await r.RenderAsync(page, 72));
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < px.Height; y++)
        for (var x = 0; x < px.Width; x++)
        {
            if (px.Pixels[(y * px.Width + x) * 4] >= 100) continue;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return (minX, minY, maxX, maxY, px.Width, px.Height);
    }

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(90, 0, 0, false)]
    [InlineData(180, 0, 0, false)]
    [InlineData(270, 0, 0, false)]
    [InlineData(0, 100, 50, false)]
    [InlineData(90, 100, 50, false)]
    [InlineData(270, 100, 50, true)]
    [InlineData(0, 0, 0, true)]
    [InlineData(180, 30, 70, true)]
    public async Task Display_coordinates_match_rendered_page(int rotate, double ox, double oy, bool crop)
    {
        using var files = new TestFiles();
        PdfDocs.EnsureInitialized();
        var src = new PdfDocument();
        var p = src.AddPage();
        p.MediaBox = new PdfRectangle(new XPoint(ox, oy), new XPoint(ox + 400, oy + 600));
        if (crop) p.CropBox = new PdfRectangle(new XPoint(ox + 20, oy + 40), new XPoint(ox + 380, oy + 560));
        p.Elements.SetInteger("/Rotate", rotate);
        src.Save(files.PathFor("a.pdf"));

        var doc = PdfReader.Open(files.PathFor("a.pdf"), PdfDocumentOpenMode.Modify);
        using (var canvas = PageCanvas.Open(doc.Pages[0]))
        {
            canvas.Graphics.DrawRectangle(XBrushes.Black, 50, 100, 30, 20);
        }
        doc.Save(files.PathFor("b.pdf"));

        var b = await DarkBounds(files.PathFor("b.pdf"));
        Assert.InRange(b.X0, 49, 51);
        Assert.InRange(b.Y0, 99, 101);
        Assert.InRange(b.X1, 78, 80);
        Assert.InRange(b.Y1, 118, 120);
    }

    [Fact]
    public void Geometry_round_trips()
    {
        foreach (var rot in new[] { 0, 90, 180, 270 })
        {
            var g = new PageGeometry(new System.Windows.Rect(10, 20, 300, 500), rot, 520);
            var d = new System.Windows.Point(33, 44);
            var back = g.ToDisplay(g.ToUser(d));
            Assert.Equal(d.X, back.X, 6);
            Assert.Equal(d.Y, back.Y, 6);
        }
    }
}
