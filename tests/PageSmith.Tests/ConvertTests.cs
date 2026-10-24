using System.IO;
using System.IO.Compression;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Office;
using PageSmith.Core.Office.BuiltIn;
using PageSmith.Core.Rendering;
using PageSmith.Core.Tools.Convert;
using PdfSharp.Pdf.IO;

namespace PageSmith.Tests;

public class ConvertTests
{
    [Fact]
    public async Task Images_to_pdf_respects_size_margin_and_orientation()
    {
        using var files = new TestFiles();
        var wide = files.CreateImage("wide.jpg", 800, 400);
        var tall = files.CreateImage("tall.png", 300, 600);
        var output = files.PathFor("images.pdf");
        await ImagesToPdfTool.RunAsync(new ImagesToPdfOptions { Images = [wide, tall], OutputPath = output, PageSize = ImagePageSize.A4, Margin = PageMargin.Small });

        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(2, doc.PageCount);
        Assert.True(doc.Pages[0].Width.Point > doc.Pages[0].Height.Point, "wide image → landscape page");
        Assert.True(doc.Pages[1].Height.Point > doc.Pages[1].Width.Point);

        var fit = await ImagesToPdfTool.RunAsync(new ImagesToPdfOptions { Images = [wide, tall], OutputPath = files.PathFor("x.pdf"), MergeIntoOne = false });
        Assert.Equal(2, fit.OutputFiles.Count);
        using var single = PdfReader.Open(fit.OutputFiles[0], PdfDocumentOpenMode.Import);
        Assert.Equal(800 * 72 / 96.0, single.Pages[0].Width.Point, 1);
    }

    [Fact]
    public async Task Pdf_to_images_renders_and_extracts()
    {
        using var files = new TestFiles();
        var pdf = files.CreatePdf("doc.pdf", 3);
        var pages = await PdfToImagesTool.RunAsync(new PdfToImagesOptions { Input = pdf, OutputDirectory = files.Dir, Dpi = 100, Pages = "1-2" });
        Assert.Equal(2, pages.OutputFiles.Count);
        var bmp = ImageIO.Load(pages.OutputFiles[0]);
        Assert.InRange(bmp.PixelWidth, 825, 828); // 595pt at 100 DPI

        var withImage = files.PathFor("img.pdf");
        await ImagesToPdfTool.RunAsync(new ImagesToPdfOptions { Images = [files.CreateImage("p.jpg", 120, 80)], OutputPath = withImage });
        var extracted = await PdfToImagesTool.RunAsync(new PdfToImagesOptions { Input = withImage, OutputDirectory = files.Dir, Mode = PdfToImageMode.ExtractImages });
        var img = ImageIO.Load(Assert.Single(extracted.OutputFiles));
        Assert.Equal(120, img.PixelWidth);
    }

    [Fact]
    public async Task Builtin_word_round_trip()
    {
        using var files = new TestFiles();
        var pdf = files.CreatePdf("doc.pdf", 2, body: i => $"This is the body of page {i}.\nIt continues on a second line.");
        var docx = files.PathFor("doc.docx");
        var result = await PdfToWordTool.RunAsync(new PdfToOfficeOptions { Input = pdf, OutputPath = docx });
        using (var word = WordprocessingDocument.Open(docx, false))
        {
            var text = word.MainDocumentPart!.Document!.Body!.InnerText;
            Assert.Contains("Page 1", text);
            Assert.Contains("body of page 2", text);
        }

        // And back to PDF with the built-in renderer.
        var back = files.PathFor("back.pdf");
        await OfficeToPdfTool.ConvertAsync(docx, back, ConversionEngine.BuiltIn);
        var texts = OrganizeTests.PageTexts(back);
        Assert.Contains(texts, t => t.Contains("body of page 2"));
    }

    [Fact]
    public async Task Pdf_to_excel_builds_columns_and_numbers()
    {
        using var files = new TestFiles();
        var pdf = files.CreatePdf("table.pdf", 1, body: _ => "Item              Qty            Price\nApples            12             3.50\nPears             7              1,250.00");
        var xlsx = files.PathFor("t.xlsx");
        await PdfToExcelTool.RunAsync(new PdfToOfficeOptions { Input = pdf, OutputPath = xlsx });
        using var wb = new XLWorkbook(xlsx);
        var ws = wb.Worksheet(1);
        var cells = ws.CellsUsed().ToList();
        var apples = cells.First(c => c.GetString() == "Apples");
        Assert.Equal(12, apples.CellRight().GetDouble());
        Assert.Equal(1250, cells.First(c => c.GetString() == "Pears").CellRight().CellRight().GetDouble());
    }

    [Fact]
    public async Task Pdf_to_powerpoint_package_is_valid()
    {
        using var files = new TestFiles();
        var pdf = files.CreatePdf("deck.pdf", 2, 792, 612, body: i => $"Slide text {i}");
        foreach (var asImages in new[] { true, false })
        {
            var pptx = files.PathFor($"deck_{asImages}.pptx");
            await PdfToPowerPointTool.RunAsync(new PdfToOfficeOptions { Input = pdf, OutputPath = pptx, SlidesAsImages = asImages });
            using var pres = PresentationDocument.Open(pptx, false);
            Assert.Equal(2, pres.PresentationPart!.SlideParts.Count());
            var validator = new DocumentFormat.OpenXml.Validation.OpenXmlValidator();
            var errors = validator.Validate(pres).Take(5).Select(e => e.Description).ToList();
            Assert.True(errors.Count == 0, string.Join("\n", errors));
            // The built-in renderer reads it back.
            var back = files.PathFor($"back_{asImages}.pdf");
            await OfficeToPdfTool.ConvertAsync(pptx, back, ConversionEngine.BuiltIn);
            using var doc = PdfReader.Open(back, PdfDocumentOpenMode.Import);
            Assert.Equal(2, doc.PageCount);
            if (!asImages) Assert.Contains("Slide text 2", OrganizeTests.PageTexts(back)[1]);
        }
    }

    [Fact]
    public async Task Builtin_xlsx_and_text_render()
    {
        using var files = new TestFiles();
        var xlsx = files.PathFor("book.xlsx");
        using (var wb = new XLWorkbook())
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(1, 2).Value = "Total";
            ws.Cell(2, 1).Value = "Widgets";
            ws.Cell(2, 2).Value = 42.5;
            ws.Range("A4:B4").Merge().Value = "Merged footer";
            wb.SaveAs(xlsx);
        }
        var pdf = files.PathFor("book.pdf");
        await OfficeToPdfTool.ConvertAsync(xlsx, pdf, ConversionEngine.BuiltIn);
        var text = OrganizeTests.PageTexts(pdf)[0];
        Assert.Contains("Widgets", text);
        Assert.Contains("42.5", text);
        Assert.Contains("Merged footer", text);

        var txt = files.PathFor("notes.txt");
        File.WriteAllText(txt, "Hello text file\nSecond line");
        await OfficeToPdfTool.ConvertAsync(txt, files.PathFor("notes.pdf"), ConversionEngine.BuiltIn);
        Assert.Contains("Second line", OrganizeTests.PageTexts(files.PathFor("notes.pdf"))[0]);
    }

    [Fact]
    public async Task Microsoft_office_converts_when_installed()
    {
        if (!OfficeDetection.IsMicrosoftOfficeAppInstalled(OfficeApp.Word)) return;
        using var files = new TestFiles();
        var pdf = files.CreatePdf("doc.pdf", 1, body: _ => "Converted through Microsoft Word");
        var docx = files.PathFor("doc.docx");
        await PdfToWordTool.RunAsync(new PdfToOfficeOptions { Input = pdf, OutputPath = docx });
        var back = files.PathFor("doc_word.pdf");
        var engine = await OfficeToPdfTool.ConvertAsync(docx, back, ConversionEngine.MicrosoftOffice);
        Assert.Equal("Microsoft Word", engine);
        Assert.Contains("Microsoft Word", OrganizeTests.PageTexts(back)[0]);

        // Word's own PDF reflow must run unattended (no dialogs) and finish.
        var reflowed = files.PathFor("reflow.docx");
        var run = PdfToWordTool.RunAsync(new PdfToOfficeOptions { Input = pdf, OutputPath = reflowed, Engine = ConversionEngine.MicrosoftOffice });
        Assert.Same(run, await Task.WhenAny(run, Task.Delay(TimeSpan.FromMinutes(2))));
        await run;
        using var word = WordprocessingDocument.Open(reflowed, false);
        Assert.Contains("Microsoft Word", word.MainDocumentPart!.Document!.Body!.InnerText);
    }

    [Fact]
    public async Task Pdf_to_markdown_marks_headings_and_lists()
    {
        using var files = new TestFiles();
        PdfDocs.EnsureInitialized();
        var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page))
        {
            gfx.DrawString("Quarterly Report", new PdfSharp.Drawing.XFont("Arial", 26), PdfSharp.Drawing.XBrushes.Black, 72, 90);
            var body = new PdfSharp.Drawing.XFont("Arial", 11);
            gfx.DrawString("Revenue grew strongly this quarter across all regions.", body, PdfSharp.Drawing.XBrushes.Black, 72, 140);
            gfx.DrawString("• First point", body, PdfSharp.Drawing.XBrushes.Black, 72, 180);
            gfx.DrawString("• Second point", body, PdfSharp.Drawing.XBrushes.Black, 72, 196);
        }
        var pdf = files.PathFor("r.pdf");
        doc.Save(pdf);
        var md = files.PathFor("r.md");
        await PdfToMarkdownTool.RunAsync(new PdfToMarkdownOptions { Input = pdf, OutputPath = md });
        var text = File.ReadAllText(md);
        Assert.Contains("# Quarterly Report", text);
        Assert.Contains("- First point", text);
        Assert.Contains("- Second point", text);
    }

    [Fact]
    public async Task Pdf_a_adds_metadata_output_intent_and_embeds_fonts()
    {
        using var files = new TestFiles();
        // A page that uses the non-embedded standard font Helvetica must be re-rendered.
        var src = files.PathFor("std14.pdf");
        var builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
        var helvetica = builder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
        var p = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
        p.AddText("Archive me please", 14, new UglyToad.PdfPig.Core.PdfPoint(72, 700), helvetica);
        File.WriteAllBytes(src, builder.Build());

        var output = files.PathFor("a.pdf");
        var result = await PdfToPdfATool.RunAsync(new PdfToPdfAOptions { Input = src, OutputPath = output, Conformance = PdfAConformance.PdfA2b });
        Assert.Contains(result.Warnings, w => w.Contains("re-rendered"));

        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var catalog = doc.Internals.Catalog;
        Assert.NotNull(catalog.Elements.GetArray("/OutputIntents"));
        var xmp = System.Text.Encoding.UTF8.GetString(catalog.Elements.GetDictionary("/Metadata")!.Stream!.Value);
        Assert.Contains("<pdfaid:conformance>B</pdfaid:conformance>", xmp);
        Assert.Contains("<pdfaid:part>2</pdfaid:part>", xmp);
        Assert.Contains($"<pdf:Producer>{doc.Info.Producer}</pdf:Producer>", xmp);
        Assert.Equal(17, doc.Version);
        Assert.Contains("Archive me please", OrganizeTests.PageTexts(output)[0]);
        Assert.Empty(PdfToPdfATool.FindPagesToRasterize(output, 2));
    }
}
