using System.IO;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Rendering;
using PageSmith.Core.Tools.Optimize;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PageSmith.Tests;

public class OptimizeTests
{
    /// <summary>A PDF whose single page is a raster image of a text page (like a scan).</summary>
    internal static async Task<string> CreateScannedPdf(TestFiles files, string name, Func<int, string> body, int pages = 1)
    {
        var vector = files.CreatePdf("vector_" + name, pages, body: body);
        using var renderer = await PdfRenderer.OpenAsync(vector);
        PdfDocs.EnsureInitialized();
        var doc = new PdfDocument();
        for (var i = 0; i < pages; i++)
        {
            var png = await renderer.RenderPngAsync(i, 200);
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            using var ms = new MemoryStream(ImageIO.EncodeJpeg(ImageIO.Decode(png), 90));
            using var img = XImage.FromStream(ms);
            gfx.DrawImage(img, 0, 0, page.Width.Point, page.Height.Point);
        }
        var path = files.PathFor(name);
        doc.Save(path);
        return path;
    }

    [Fact]
    public async Task Compress_downsamples_oversized_images()
    {
        using var files = new TestFiles();
        var big = files.CreateImage("big.png", 2400, 2400);
        PdfDocs.EnsureInitialized();
        var doc = new PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        using (var img = XImage.FromFile(big))
        {
            gfx.DrawImage(img, 100, 100, 200, 200); // 2400 px over ~2.8 in ≈ 860 DPI
        }
        var src = files.PathFor("big.pdf");
        doc.Save(src);

        var output = files.PathFor("small.pdf");
        var result = await CompressTool.RunAsync(new CompressOptions { Input = src, OutputPath = output, Level = CompressionLevel.Recommended });
        Assert.True(result.OutputBytes < result.InputBytes / 3, $"{result.InputBytes} -> {result.OutputBytes}");

        using var renderer = await PdfRenderer.OpenAsync(output);
        var bmp = await renderer.RenderAsync(0, 72);
        var px = ImageIO.ToPixels(bmp);
        // The dark quadrant of the image is still drawn at the same place.
        var i = (130 * px.Width + 130) * 4;
        Assert.True(px.Pixels[i] < 90);
    }

    [Fact]
    public async Task Compress_never_grows_text_only_files()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("t.pdf", 3);
        var output = files.PathFor("t_c.pdf");
        var result = await CompressTool.RunAsync(new CompressOptions { Input = src, OutputPath = output, Level = CompressionLevel.Extreme });
        Assert.True(result.OutputBytes <= result.InputBytes);
        Assert.Equal(3, OrganizeTests.PageTexts(output).Length);
    }

    [Fact]
    public async Task Repair_recovers_file_with_junk_and_broken_xref()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("ok.pdf", 3);
        var bytes = File.ReadAllBytes(src);
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        // Corrupt the xref offsets and prepend junk.
        var xref = text.LastIndexOf("startxref", StringComparison.Ordinal);
        var corrupted = "HTTP/1.1 200 OK\r\n\r\n" + text[..xref] + "startxref\n999999\n%%EOF";
        var broken = files.PathFor("broken.pdf");
        File.WriteAllBytes(broken, System.Text.Encoding.Latin1.GetBytes(corrupted));

        var output = files.PathFor("fixed.pdf");
        var result = await RepairTool.RunAsync(new RepairOptions { Input = broken, OutputPath = output });
        Assert.Equal(3, OrganizeTests.PageTexts(Assert.Single(result.OutputFiles)).Length);
    }

    [Fact]
    public async Task Repair_detects_other_formats()
    {
        using var files = new TestFiles();
        var img = files.CreateImage("photo.jpg", 50, 50);
        var fake = files.PathFor("photo.pdf");
        File.Copy(img, fake);
        var result = await RepairTool.RunAsync(new RepairOptions { Input = fake, OutputPath = files.PathFor("photo_repaired.pdf") });
        Assert.EndsWith(".jpg", Assert.Single(result.OutputFiles));
    }

    [Fact]
    public async Task Ocr_makes_scanned_pages_searchable()
    {
        if (!PageSmith.Core.Ocr.OcrService.IsAvailable) return;
        using var files = new TestFiles();
        var scanned = await CreateScannedPdf(files, "scan.pdf", _ => "The quick brown fox jumps over the lazy dog\nInvoice number 12345 total amount");
        Assert.True(string.IsNullOrWhiteSpace(OrganizeTests.PageTexts(scanned)[0]));

        var output = files.PathFor("ocr.pdf");
        var result = await OcrTool.RunAsync(new OcrOptions { Input = scanned, OutputPath = output });
        var text = OrganizeTests.PageTexts(output)[0];
        Assert.Contains("quick", text);
        Assert.Contains("12345", text);
        Assert.Contains("Invoice", result.Text);

        // The text layer is invisible: the page still renders the same as the scan.
        using var r1 = await PdfRenderer.OpenAsync(scanned);
        using var r2 = await PdfRenderer.OpenAsync(output);
        var a = ImageIO.ToPixels(await r1.RenderAsync(0, 50));
        var b = ImageIO.ToPixels(await r2.RenderAsync(0, 50));
        var diff = 0L;
        for (var i = 0; i < a.Pixels.Length; i++) diff += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        Assert.True(diff / (double)a.Pixels.Length < 1.0, $"mean diff {diff / (double)a.Pixels.Length}");
    }
}
