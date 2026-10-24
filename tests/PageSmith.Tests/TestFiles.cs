using System.IO;
using PageSmith.Core.Common;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PageSmith.Tests;

/// <summary>Creates throw-away test documents in a per-test temp folder.</summary>
public sealed class TestFiles : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "PageSmithTests", Guid.NewGuid().ToString("N")[..8]);

    public TestFiles() => Directory.CreateDirectory(Dir);

    public string PathFor(string name) => Path.Combine(Dir, name);

    /// <summary>A PDF whose page i contains the text "Page i" (1-based) plus an optional body line.</summary>
    public string CreatePdf(string name, int pages, double width = 595, double height = 842, Func<int, string>? body = null)
    {
        PdfDocs.EnsureInitialized();
        var doc = new PdfDocument();
        var font = new XFont("Arial", 20);
        var small = new XFont("Arial", 11);
        for (var i = 1; i <= pages; i++)
        {
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(width);
            page.Height = XUnit.FromPoint(height);
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"Page {i}", font, XBrushes.Black, 72, 100);
            var text = body?.Invoke(i);
            if (text != null)
            {
                var y = 140.0;
                foreach (var line in text.Split('\n'))
                {
                    gfx.DrawString(line, small, XBrushes.Black, 72, y);
                    y += 16;
                }
            }
        }
        var path = PathFor(name);
        doc.Save(path);
        return path;
    }

    /// <summary>A solid-colour PNG or JPEG image with a dark rectangle in the top-left quarter.</summary>
    public string CreateImage(string name, int width, int height, int? noiseSeed = null)
    {
        var px = new Core.Imaging.PixelBuffer(width, height);
        var rnd = noiseSeed.HasValue ? new Random(noiseSeed.Value) : null;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var i = (y * width + x) * 4;
            var dark = x < width / 2 && y < height / 2;
            px.Pixels[i] = (byte)(dark ? 40 : 200);
            px.Pixels[i + 1] = (byte)(dark ? 40 : (x * 255 / width));
            px.Pixels[i + 2] = (byte)(dark ? 40 : (y * 255 / height));
            px.Pixels[i + 3] = 255;
            if (rnd != null)
                for (var c = 0; c < 3; c++) px.Pixels[i + c] = (byte)Math.Clamp(px.Pixels[i + c] + rnd.Next(-40, 40), 0, 255);
        }
        var bmp = Core.Imaging.ImageIO.FromPixels(px);
        var bytes = name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? Core.Imaging.ImageIO.EncodePng(bmp)
            : Core.Imaging.ImageIO.EncodeJpeg(bmp, 92);
        var path = PathFor(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { /* best effort */ }
    }
}
