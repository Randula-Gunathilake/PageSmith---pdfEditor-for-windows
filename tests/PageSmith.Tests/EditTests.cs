using System.IO;
using System.Windows;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;
using PageSmith.Core.Text;
using PageSmith.Core.Tools.Edit;
using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace PageSmith.Tests;

public class EditTests
{
    [Fact]
    public async Task Rotate_whole_files_pages_and_filters()
    {
        using var files = new TestFiles();
        var portrait = files.CreatePdf("p.pdf", 3);
        var r = await RotateTool.RunAsync(new RotateOptions
        {
            Items = [new RotateItem(portrait, 90, new Dictionary<int, int> { [2] = 90 })],
            OutputDirectory = files.Dir,
        });
        using var doc = PdfReader.Open(r.OutputFiles[0], PdfDocumentOpenMode.Import);
        Assert.Equal(90, doc.Pages[0].Rotate);
        Assert.Equal(180, doc.Pages[2].Rotate);

        var filtered = await RotateTool.RunAsync(new RotateOptions { Items = [new RotateItem(portrait, 90)], OutputDirectory = files.Dir, Filter = PageFilter.Landscape });
        using var none = PdfReader.Open(filtered.OutputFiles[0], PdfDocumentOpenMode.Import);
        for (var i = 0; i < none.PageCount; i++) Assert.Equal(0, none.Pages[i].Rotate);
    }

    [Fact]
    public async Task Page_numbers_follow_template_range_and_position()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("doc.pdf", 4);
        var r = await PageNumbersTool.RunAsync(new PageNumberOptions
        {
            Files = [src],
            OutputDirectory = files.Dir,
            FirstPageIsCover = true,
            Template = "Page {n} of {r}",
            Position = PagePosition.BottomRight,
        });
        var pages = TextExtractor.ReadPages(r.OutputFiles[0]);
        Assert.DoesNotContain(pages[0].Words, w => w.Text == "of");
        var line = TextExtractor.GetLines(pages[1].Words).Last();
        Assert.Equal("Page 1 of 3", line.Text);
        Assert.True(line.Bounds.Right > pages[1].Width - 60 && line.Bounds.Bottom > pages[1].Height - 60, line.Bounds.ToString());
        Assert.Equal("Page 3 of 3", TextExtractor.GetLines(pages[3].Words).Last().Text);
    }

    [Fact]
    public async Task Watermark_text_and_image_with_transparency()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("doc.pdf", 2);
        var r = await WatermarkTool.RunAsync(new WatermarkOptions { Files = [src], OutputDirectory = files.Dir, Text = "DRAFT", Pages = "2", Rotation = 0 });
        var texts = OrganizeTests.PageTexts(r.OutputFiles[0]);
        Assert.DoesNotContain("DRAFT", texts[0]);
        Assert.Contains("DRAFT", texts[1]);

        var img = files.CreateImage("logo.png", 100, 100);
        var ri = await WatermarkTool.RunAsync(new WatermarkOptions { Files = [src], OutputDirectory = files.Dir, Kind = WatermarkKind.Image, ImagePath = img, Mosaic = true, Opacity = 0.25 });
        using var renderer = await PdfRenderer.OpenAsync(ri.OutputFiles[0]);
        var px = ImageIO.ToPixels(await renderer.RenderAsync(0, 36));
        var tinted = 0;
        for (var i = 0; i < px.Pixels.Length; i += 4)
            if (px.Pixels[i] < 245 && px.Pixels[i] > 150) tinted++;
        Assert.True(tinted > 500, $"faint watermark pixels: {tinted}");
    }

    [Fact]
    public async Task Crop_sets_visible_area_and_detects_content()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("doc.pdf", 2, 600, 800);
        var output = files.PathFor("cropped.pdf");
        await CropTool.RunAsync(new CropOptions { Input = src, OutputPath = output, Area = new Rect(0.1, 0.1, 0.5, 0.25), Pages = "1" });
        using var renderer = await PdfRenderer.OpenAsync(output);
        var size = renderer.GetPageSize(0);
        Assert.Equal(300, size.Width, 1);
        Assert.Equal(200, size.Height, 1);
        Assert.Equal(600, renderer.GetPageSize(1).Width, 1);
        // "Page 1" sits at (72,100): still visible at (12,20) inside the cropped page.
        Assert.Contains("Page 1", OrganizeTests.PageTexts(output)[0]);

        using var original = await PdfRenderer.OpenAsync(src);
        var content = await CropTool.DetectContentAsync(original, 0);
        Assert.InRange(content.Left, 0.08, 0.13);
        Assert.True(content.Width < 0.3);
    }

    [Fact]
    public async Task Edit_pdf_draws_all_element_kinds()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("doc.pdf", 1);
        var output = files.PathFor("edited.pdf");
        var png = ImageIO.EncodePng(ImageIO.Load(files.CreateImage("i.png", 40, 40)));
        await EditPdfTool.RunAsync(new EditPdfOptions
        {
            Input = src,
            OutputPath = output,
            Elements =
            [
                new ShapeElement { PageIndex = 0, Bounds = new Rect(60, 80, 120, 30), Fill = "#FFFFFF", Stroke = null }, // white-out "Page 1"
                new TextElement { PageIndex = 0, Bounds = new Rect(72, 300, 300, 40), Text = "Added by PageSmith", Style = new TextStyle { Size = 14 } },
                new ImageElement { PageIndex = 0, Bounds = new Rect(400, 400, 80, 80), Data = png },
                new ShapeElement { PageIndex = 0, Bounds = new Rect(100, 500, 200, 100), Shape = ShapeKind.Ellipse, Stroke = "#FF0000", StrokeWidth = 3 },
                new InkElement { PageIndex = 0, Strokes = [[new Point(100, 700), new Point(200, 720), new Point(300, 700)]], Width = 4 },
            ],
        });
        Assert.Contains("Added by PageSmith", OrganizeTests.PageTexts(output)[0]);
        using var renderer = await PdfRenderer.OpenAsync(output);
        var px = ImageIO.ToPixels(await renderer.RenderAsync(0, 72));
        int At(int x, int y) => px.Pixels[(y * px.Width + x) * 4 + 2]; // red channel
        Assert.True(At(80, 95) > 240, "white-out covers the old text");
        Assert.True(px.Pixels[(550 * px.Width + 100) * 4] < 80 && At(100, 550) > 200, "red ellipse edge");
        Assert.True(At(200, 720) < 120, "ink stroke");
    }

    [Fact]
    public async Task Forms_create_read_fill_and_flatten()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("form.pdf", 1);
        var withFields = files.PathFor("fields.pdf");
        await FormsTool.CreateFieldsAsync(new CreateFieldsOptions
        {
            Input = src,
            OutputPath = withFields,
            Fields =
            [
                new NewFormField(FormFieldKind.Text, "Full name", 0, new Rect(100, 200, 200, 20)),
                new NewFormField(FormFieldKind.CheckBox, "Agree", 0, new Rect(100, 240, 14, 14)),
                new NewFormField(FormFieldKind.ComboBox, "Country", 0, new Rect(100, 270, 150, 20), ["Sri Lanka", "Japan"]),
                new NewFormField(FormFieldKind.RadioGroup, "Size", 0, new Rect(100, 310, 120, 45), ["Small", "Large"]),
            ],
        });
        var fields = FormsTool.ReadFields(withFields);
        Assert.Equal(4, fields.Count);
        Assert.Contains(fields, f => f.Name == "Country" && f.Options.SequenceEqual(["Sri Lanka", "Japan"]));
        Assert.Contains(fields, f => f.Name == "Size" && f.Kind == FormFieldKind.RadioGroup && f.Options.Contains("Large"));
        var name = fields.Single(f => f.Name == "Full name");
        Assert.Equal(100, name.Bounds.Left, 1);
        Assert.Equal(200, name.Bounds.Top, 1);

        var filled = files.PathFor("filled.pdf");
        await FormsTool.FillAsync(new FillFormOptions
        {
            Input = withFields,
            OutputPath = filled,
            Values = new Dictionary<string, string> { ["Full name"] = "Ada Lovelace", ["Agree"] = "true", ["Country"] = "Japan", ["Size"] = "Large" },
        });
        var values = FormsTool.ReadFields(filled).ToDictionary(f => f.Name, f => f.Value);
        Assert.Equal("Ada Lovelace", values["Full name"]);
        Assert.Equal("true", values["Agree"]);
        Assert.Equal("Japan", values["Country"]);
        Assert.Equal("Large", values["Size"]);

        // The chosen dropdown value must be visible even in viewers that ignore /NeedAppearances (Windows' renderer).
        using (var renderer = await PageSmith.Core.Rendering.PdfRenderer.OpenAsync(filled))
        {
            var px = PageSmith.Core.Imaging.ImageIO.ToPixels(await renderer.RenderAsync(0, 72));
            var dark = 0;
            for (var y = 272; y < 288; y++)
            for (var x = 103; x < 245; x++)
            {
                var i = (y * px.Width + x) * 4;
                if (px.Pixels[i] + px.Pixels[i + 1] + px.Pixels[i + 2] < 300) dark++;
            }
            Assert.True(dark > 20, $"dropdown shows no text ({dark} dark pixels)");
        }

        var flat = files.PathFor("flat.pdf");
        await FormsTool.FillAsync(new FillFormOptions
        {
            Input = withFields,
            OutputPath = flat,
            Values = new Dictionary<string, string> { ["Full name"] = "Grace Hopper", ["Country"] = "Japan" },
            Flatten = true,
        });
        Assert.Empty(FormsTool.ReadFields(flat));
        var flatText = OrganizeTests.PageTexts(flat)[0];
        Assert.Contains("Grace Hopper", flatText);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(PageSmith.Core.Text.TextExtractor.GetPlainText(flat), "Japan"));
    }

    [Fact]
    public void Forms_detect_blanks_and_boxes()
    {
        using var files = new TestFiles();
        PdfDocs.EnsureInitialized();
        var doc = new PdfSharp.Pdf.PdfDocument();
        var page = doc.AddPage();
        using (var gfx = XGraphics.FromPdfPage(page))
        {
            var font = new XFont("Arial", 11);
            gfx.DrawString("Name: ______________________", font, XBrushes.Black, 72, 100);
            gfx.DrawString("Email", font, XBrushes.Black, 72, 150);
            gfx.DrawRectangle(new XPen(XColors.Black, 0.75), 120, 138, 200, 18);
            gfx.DrawRectangle(new XPen(XColors.Black, 0.75), 72, 190, 10, 10);
            gfx.DrawString("I agree", font, XBrushes.Black, 90, 199);
        }
        var path = files.PathFor("blank.pdf");
        doc.Save(path);
        var detected = FormsTool.DetectFields(path);
        Assert.Contains(detected, f => f.Kind == FormFieldKind.Text && f.Name == "Name");
        Assert.Contains(detected, f => f.Kind == FormFieldKind.Text && f.Name == "Email");
        Assert.Contains(detected, f => f.Kind == FormFieldKind.CheckBox);
    }
}
