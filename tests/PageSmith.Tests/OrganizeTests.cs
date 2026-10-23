using System.IO;
using PageSmith.Core.Common;
using PageSmith.Core.Tools.Organize;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Pig = UglyToad.PdfPig.PdfDocument;

namespace PageSmith.Tests;

public class OrganizeTests(Xunit.Abstractions.ITestOutputHelper log)
{
    internal static string[] PageTexts(string path)
    {
        using var pig = Pig.Open(path);
        return pig.GetPages().Select(p => p.Text).ToArray();
    }

    [Fact]
    public async Task Merge_combines_in_order_with_bookmarks_and_rotation()
    {
        using var files = new TestFiles();
        var a = files.CreatePdf("a.pdf", 3, body: i => $"alpha {i}");
        var b = files.CreatePdf("b.pdf", 2, body: i => $"bravo {i}");
        var output = files.PathFor("merged.pdf");

        var result = await MergeTool.RunAsync(new MergeOptions
        {
            Items = [new MergeItem(b, Rotation: 90), new MergeItem(a, Pages: "1,3")],
            OutputPath = output,
        });

        Assert.Single(result.OutputFiles);
        var texts = PageTexts(output);
        Assert.Equal(4, texts.Length);
        Assert.Contains("bravo 1", texts[0]);
        Assert.Contains("bravo 2", texts[1]);
        Assert.Contains("alpha 1", texts[2]);
        Assert.Contains("alpha 3", texts[3]);
        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(90, doc.Pages[0].Rotate);
        Assert.Equal(0, doc.Pages[2].Rotate);
        Assert.Equal(2, doc.Outlines.Count);
    }

    [Fact]
    public async Task Split_custom_ranges_and_merge_option()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("doc.pdf", 6);
        var r = await SplitTool.RunAsync(new SplitOptions { Input = src, OutputDirectory = files.Dir, Mode = SplitMode.CustomRanges, Ranges = ["1-2", "5-6"] });
        Assert.Equal(2, r.OutputFiles.Count);
        Assert.Equal(2, PageTexts(r.OutputFiles[0]).Length);
        Assert.Contains("Page 5", PageTexts(r.OutputFiles[1])[0]);

        var merged = await SplitTool.RunAsync(new SplitOptions { Input = src, OutputDirectory = files.Dir, Mode = SplitMode.CustomRanges, Ranges = ["1-2", "5-6"], MergeIntoOne = true });
        Assert.Single(merged.OutputFiles);
        Assert.Equal(4, PageTexts(merged.OutputFiles[0]).Length);
    }

    [Fact]
    public async Task Split_fixed_extract_all_and_select()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("doc.pdf", 5);
        var fixedSplit = await SplitTool.RunAsync(new SplitOptions { Input = src, OutputDirectory = files.Dir, Mode = SplitMode.FixedRange, PagesPerFile = 2 });
        Assert.Equal(3, fixedSplit.OutputFiles.Count);
        Assert.Single(PageTexts(fixedSplit.OutputFiles[2]));

        var all = await SplitTool.RunAsync(new SplitOptions { Input = src, OutputDirectory = files.Dir, Mode = SplitMode.ExtractAll });
        Assert.Equal(5, all.OutputFiles.Count);

        var select = await SplitTool.RunAsync(new SplitOptions { Input = src, OutputDirectory = files.Dir, Mode = SplitMode.SelectPages, SelectedPages = "2,4", MergeIntoOne = true });
        var texts = PageTexts(Assert.Single(select.OutputFiles));
        Assert.Contains("Page 2", texts[0]);
        Assert.Contains("Page 4", texts[1]);
    }

    [Fact]
    public async Task Split_by_size_respects_limit()
    {
        using var files = new TestFiles();
        // Each page carries a distinct, poorly compressible image so pages have real weight.
        PdfDocs.EnsureInitialized();
        var doc = new PdfDocument();
        var rnd = new Random(1);
        for (var i = 0; i < 6; i++)
        {
            var img = files.CreateImage($"img{i}.jpg", 400, 400, noiseSeed: i);
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            using var x = XImage.FromFile(img);
            gfx.DrawImage(x, 50, 50, 300, 300);
        }
        var src = files.PathFor("heavy.pdf");
        doc.Save(src);
        var pageBytes = new FileInfo(src).Length / 6;

        var max = pageBytes * 2 + pageBytes / 2;
        var r = await SplitTool.RunAsync(new SplitOptions { Input = src, OutputDirectory = files.Dir, Mode = SplitMode.BySize, MaxBytesPerFile = max });
        log.WriteLine($"pageâ‰ˆ{pageBytes} max={max} sizes={string.Join(",", r.OutputFiles.Select(f => new FileInfo(f).Length))}");
        Assert.True(r.OutputFiles.Count >= 3);
        Assert.All(r.OutputFiles, f => Assert.True(new FileInfo(f).Length <= max));
        Assert.Equal(6, r.OutputFiles.Sum(f => PageTexts(f).Length));
    }

    [Fact]
    public async Task Smart_split_uses_blank_separators()
    {
        using var files = new TestFiles();
        PdfDocs.EnsureInitialized();
        var doc = new PdfDocument();
        var font = new XFont("Arial", 20);
        foreach (var label in new[] { "A1", "A2", "", "B1", "", "C1" })
        {
            var page = doc.AddPage();
            if (label.Length == 0) continue;
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString(label, font, XBrushes.Black, 100, 100);
        }
        var src = files.PathFor("batch.pdf");
        doc.Save(src);

        var r = await SplitTool.RunAsync(new SplitOptions { Input = src, OutputDirectory = files.Dir, Mode = SplitMode.Smart, SmartStrategy = SmartSplitStrategy.BlankPages });
        Assert.Equal(3, r.OutputFiles.Count);
        Assert.Equal(2, PageTexts(r.OutputFiles[0]).Length);
        Assert.Contains("C1", PageTexts(r.OutputFiles[2])[0]);
    }

    [Fact]
    public async Task Smart_split_uses_bookmarks()
    {
        using var files = new TestFiles();
        var a = files.CreatePdf("a.pdf", 2);
        var b = files.CreatePdf("b.pdf", 3);
        var merged = files.PathFor("m.pdf");
        await MergeTool.RunAsync(new MergeOptions { Items = [new MergeItem(a), new MergeItem(b)], OutputPath = merged });
        var r = await SplitTool.RunAsync(new SplitOptions { Input = merged, OutputDirectory = files.Dir, Mode = SplitMode.Smart });
        Assert.Equal(2, r.OutputFiles.Count);
        Assert.Equal(3, PageTexts(r.OutputFiles[1]).Length);
    }

    [Fact]
    public async Task Remove_pages_keeps_the_rest()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("doc.pdf", 5);
        var output = files.PathFor("out.pdf");
        await RemovePagesTool.RunAsync(new RemovePagesOptions { Input = src, Pages = "2,4", OutputPath = output });
        var texts = PageTexts(output);
        Assert.Equal(3, texts.Length);
        Assert.Contains("Page 3", texts[1]);
        await Assert.ThrowsAsync<UserFacingException>(() => RemovePagesTool.RunAsync(new RemovePagesOptions { Input = src, Pages = "1-5", OutputPath = output }));
    }

    [Fact]
    public async Task Organize_reorders_duplicates_and_inserts_blank_pages()
    {
        using var files = new TestFiles();
        var a = files.CreatePdf("a.pdf", 2, body: i => $"first {i}");
        var b = files.CreatePdf("b.pdf", 1, body: i => $"second {i}");
        var output = files.PathFor("o.pdf");
        await OrganizeTool.RunAsync(new OrganizeOptions
        {
            Sources = [a, b],
            Pages = [new OrganizedPage(1, 0), new OrganizedPage(0, 1, 180), OrganizedPage.Blank(), new OrganizedPage(0, 1)],
            OutputPath = output,
        });
        var texts = PageTexts(output);
        Assert.Equal(4, texts.Length);
        Assert.Contains("second 1", texts[0]);
        Assert.Contains("first 2", texts[1]);
        Assert.True(string.IsNullOrWhiteSpace(texts[2]));
        Assert.Contains("first 2", texts[3]);
        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.Equal(180, doc.Pages[1].Rotate);
        Assert.Equal(0, doc.Pages[3].Rotate);
    }
}
