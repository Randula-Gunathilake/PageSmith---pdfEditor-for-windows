using System.Windows;
using PageSmith.Core.Common;
using PageSmith.Core.Pdf;
using PageSmith.Core.Text;
using PageSmith.Core.Tools.Edit;
using PageSmith.Core.Tools.Organize;
using PageSmith.Core.Tools.Security;

namespace PageSmith.Tests;

public class ReaderTests
{
    [Fact]
    public async Task Outline_lists_bookmarks_with_their_pages()
    {
        using var files = new TestFiles();
        var a = files.CreatePdf("Chapter one.pdf", 2);
        var b = files.CreatePdf("Chapter two.pdf", 3);
        var merged = files.PathFor("book.pdf");
        await MergeTool.RunAsync(new MergeOptions { Items = [new MergeItem(a), new MergeItem(b)], OutputPath = merged });

        var outline = DocumentOutline.Read(merged);
        Assert.Equal(2, outline.Count);
        Assert.Equal("Chapter one", outline[0].Title);
        Assert.Equal(0, outline[0].PageIndex);
        Assert.Equal("Chapter two", outline[1].Title);
        Assert.Equal(2, outline[1].PageIndex);
    }

    [Fact]
    public void Links_go_to_pages_and_web_addresses()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("links.pdf", 3);
        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(src, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            // PDF user space: the link boxes sit near the top of page 1 (y up from the bottom).
            doc.Pages[0].AddDocumentLink(new PdfSharp.Pdf.PdfRectangle(new PdfSharp.Drawing.XRect(72, 700, 100, 20)), 3);
            doc.Pages[0].AddWebLink(new PdfSharp.Pdf.PdfRectangle(new PdfSharp.Drawing.XRect(72, 650, 100, 20)), "https://example.com/report");
            doc.Save(src);
        }

        var links = DocumentLinks.Read(src);
        Assert.Equal(2, links.Count);
        var internalLink = Assert.Single(links, l => l.Uri == null);
        Assert.Equal(0, internalLink.PageIndex);
        Assert.Equal(2, internalLink.TargetPage);
        // Display space has y down from the top: 842 - 720 = 122.
        Assert.Equal(122, internalLink.Bounds.Top, 1);
        Assert.Equal(72, internalLink.Bounds.Left, 1);
        var web = Assert.Single(links, l => l.Uri != null);
        Assert.Equal("https://example.com/report", web.Uri);
        Assert.True(DocumentLinks.IsSafeToOpen(web.Uri!));
        Assert.False(DocumentLinks.IsSafeToOpen(@"file:///C:/Windows/System32/cmd.exe"));
    }

    [Fact]
    public void Outline_is_empty_without_bookmarks() =>
        Assert.Empty(DocumentOutline.Read(new TestFiles().CreatePdf("plain.pdf", 1)));

    [Fact]
    public void Text_index_finds_matches_and_reads_selections_in_reading_order()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("text.pdf", 2, body: i => i == 1 ? "The quick brown fox\njumps over the lazy dog" : "Another fox appears");
        using var index = TextIndex.Open(new InputFile(src));
        Assert.Equal(2, index.PageCount);

        var hits = index.Find("fox");
        Assert.Equal(2, hits.Count);
        Assert.Equal([0, 1], hits.Select(h => h.PageIndex));
        Assert.Single(hits[0].Boxes);
        // A space in the query also matches a line break; the match then has one box per line.
        var across = Assert.Single(index.Find("fox jumps"));
        Assert.Equal(2, across.Boxes.Count);
        Assert.Empty(index.Find("FOX", matchCase: true));
        Assert.Empty(index.Find("ox", wholeWord: true));
        Assert.Single(index.Find("ox", wholeWord: false), h => h.PageIndex == 0);

        var page = index.GetPage(0);
        var words = page.Words.Select(w => w.Text).ToList();
        var quick = words.IndexOf("quick");
        var lazy = words.IndexOf("lazy");
        Assert.Equal("quick brown fox\njumps over the lazy", page.TextOf(quick, lazy));
        Assert.Equal(2, page.BoxesOf(quick, lazy).Count);
        var middle = page.Words[quick].Bounds;
        Assert.Equal(quick, page.WordAt(new Point(middle.X + middle.Width / 2, middle.Y + middle.Height / 2), nearest: false));
        Assert.Equal(-1, page.WordAt(new Point(500, 800), nearest: false));
        // Either direction of selecting gives the same text; across pages there is a blank line.
        Assert.Equal("quick brown fox\njumps over the lazy", index.TextBetween(new(0, lazy), new(0, quick)));
        Assert.EndsWith("dog\n\nPage 2\nAnother fox appears", index.TextBetween(new(0, 0), new(1, int.MaxValue)));
    }

    [Theory]
    [InlineData(MarkupKind.Highlight, "/Highlight")]
    [InlineData(MarkupKind.Underline, "/Underline")]
    [InlineData(MarkupKind.StrikeOut, "/StrikeOut")]
    public async Task Markup_adds_standard_annotations_that_render(MarkupKind kind, string subtype)
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("text.pdf", 1, body: _ => "Highlight this sentence please");
        IReadOnlyList<TextHit> hits;
        using (var index = TextIndex.Open(new InputFile(src))) hits = index.Find("this sentence");
        var hit = Assert.Single(hits);
        var output = files.PathFor("marked.pdf");
        await MarkupTool.RunAsync(new MarkupOptions { Input = src, OutputPath = output, Kind = kind, Marks = [new MarkupMark(0, hit.Boxes, hit.Text)] });

        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(output, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import))
        {
            var annots = doc.Pages[0].Elements.GetArray("/Annots")!;
            var annot = (PdfSharp.Pdf.PdfDictionary)((PdfSharp.Pdf.Advanced.PdfReference)annots.Elements[0]).Value;
            Assert.Equal(subtype, annot.Elements.GetName("/Subtype"));
            Assert.Equal(8, annot.Elements.GetArray("/QuadPoints")!.Elements.Count);
            Assert.Equal("this sentence", annot.Elements.GetString("/Contents"));
        }

        // The annotation's appearance shows up when the page is rendered.
        using var renderer = await PageSmith.Core.Rendering.PdfRenderer.OpenAsync(output);
        var bmp = await renderer.RenderAsync(0, 144);
        var px = PageSmith.Core.Imaging.ImageIO.ToPixels(bmp);
        var box = hit.Boxes[0];
        var coloured = 0;
        var total = 0;
        for (var y = (int)(box.Top * 2); y < (int)(box.Bottom * 2); y++)
        for (var x = (int)(box.Left * 2); x < (int)(box.Right * 2); x++)
        {
            var i = (y * px.Width + x) * 4;
            var (b, g, r) = (px.Pixels[i], px.Pixels[i + 1], px.Pixels[i + 2]);
            total++;
            if (kind == MarkupKind.Highlight ? r > 200 && g > 170 && b < 120 : r > 180 && g < 140 && b < 140) coloured++;
        }
        // A highlight fills the box; a thin line crosses it.
        Assert.True(coloured > total * (kind == MarkupKind.Highlight ? 0.3 : 0.015), $"{coloured} of {total} pixels are coloured");
    }

    [Fact]
    public async Task Notes_are_added_listed_and_deleted_alongside_other_comments()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("doc.pdf", 2, body: _ => "Some text to highlight");
        // A link is an annotation too, but not a comment.
        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(src, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            doc.Pages[0].AddWebLink(new PdfSharp.Pdf.PdfRectangle(new PdfSharp.Drawing.XRect(72, 700, 100, 20)), "https://example.com");
            doc.Save(src);
        }
        var noted = files.PathFor("noted.pdf");
        await DocumentComments.AddNoteAsync(src, noted, 1, new Point(100, 200), "Check these numbers", "Reviewer");
        IReadOnlyList<TextHit> hits;
        using (var index = TextIndex.Open(new InputFile(noted))) hits = index.Find("highlight");
        var marked = files.PathFor("marked.pdf");
        await MarkupTool.RunAsync(new MarkupOptions { Input = noted, OutputPath = marked, Kind = MarkupKind.Highlight, Marks = [new MarkupMark(0, hits[0].Boxes, "highlight")] });

        var comments = DocumentComments.Read(marked);
        Assert.Equal(2, comments.Count);
        var note = Assert.Single(comments, c => c.Kind == CommentKind.Note);
        Assert.Equal(1, note.PageIndex);
        Assert.Equal("Check these numbers", note.Text);
        Assert.Equal("Reviewer", note.Author);
        Assert.Equal(100, note.Bounds.Left, 1);
        Assert.Equal(200, note.Bounds.Top, 1);
        Assert.NotNull(note.Modified);
        var highlight = Assert.Single(comments, c => c.Kind == CommentKind.Highlight);
        Assert.Equal(0, highlight.PageIndex);
        Assert.Equal("highlight", highlight.Text);

        // The note shows as an icon: Windows draws its own orange one, other readers the note's yellow bubble.
        using (var renderer = await PageSmith.Core.Rendering.PdfRenderer.OpenAsync(marked))
        {
            var px = PageSmith.Core.Imaging.ImageIO.ToPixels(await renderer.RenderAsync(1, 144));
            var coloured = 0;
            for (var y = 400; y < 440; y++)
            for (var x = 200; x < 240; x++)
            {
                var i = (y * px.Width + x) * 4;
                if (px.Pixels[i + 2] > 180 && px.Pixels[i] < 110) coloured++;
            }
            Assert.True(coloured > 100, $"the note icon is drawn ({coloured} coloured pixels)");
        }

        var cleaned = files.PathFor("cleaned.pdf");
        await DocumentComments.DeleteAsync(marked, cleaned, highlight);
        Assert.Equal(CommentKind.Note, Assert.Single(DocumentComments.Read(cleaned)).Kind);
        Assert.Single(DocumentLinks.Read(cleaned));
    }

    [Theory]
    [InlineData("D:20261027143005+05'30'", 2026, 10, 27, 14, 30, 5)]
    [InlineData("D:20261027", 2026, 10, 27, 0, 0, 0)]
    [InlineData("20200101120000Z", 2020, 1, 1, 12, 0, 0)]
    public void Pdf_dates_are_read(string text, int y, int mo, int d, int h, int mi, int s) =>
        Assert.Equal(new DateTime(y, mo, d, h, mi, s), DocumentComments.ParseDate(text));

    [Fact]
    public void Pdf_app_registration_adds_and_removes_open_with_entries()
    {
        // A scratch key: the real HKCU\Software\Classes is never touched by tests.
        var root = $@"Software\PageSmithTests\{Guid.NewGuid():N}";
        var previous = PdfAppRegistration.Root;
        PdfAppRegistration.Root = root;
        try
        {
            const string exe = @"C:\Apps\PageSmith\PageSmith.exe";
            Assert.False(PdfAppRegistration.IsRegistered(exe));
            PdfAppRegistration.Register(exe);
            Assert.True(PdfAppRegistration.IsRegistered(exe));
            Assert.False(PdfAppRegistration.IsRegistered(@"D:\Elsewhere\PageSmith.exe"));
            Assert.NotNull(Value($@"{root}\Classes\.pdf\OpenWithProgids", PdfAppRegistration.ProgId));
            Assert.Equal("\"C:\\Apps\\PageSmith\\PageSmith.exe\" \"%1\"", Value($@"{root}\Classes\Applications\PageSmith.exe\shell\open\command", ""));
            Assert.Equal(PdfAppRegistration.ProgId, Value($@"{root}\PageSmith\Capabilities\FileAssociations", ".pdf"));
            Assert.Equal($@"{root}\PageSmith\Capabilities", Value($@"{root}\RegisteredApplications", "PageSmith"));

            PdfAppRegistration.Unregister(exe);
            Assert.False(PdfAppRegistration.IsRegistered(exe));
            Assert.Null(Value($@"{root}\Classes\.pdf\OpenWithProgids", PdfAppRegistration.ProgId));
            Assert.Null(Value($@"{root}\RegisteredApplications", "PageSmith"));
            using var app = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"{root}\PageSmith");
            Assert.Null(app);
        }
        finally
        {
            PdfAppRegistration.Root = previous;
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }

        static object? Value(string key, string name)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name);
        }
    }

    [Fact]
    public async Task A_second_start_hands_its_files_to_the_first()
    {
        // A private name, so a PageSmith running on this PC is not involved.
        var key = $"PageSmithTest-{Guid.NewGuid():N}";
        using var first = SingleInstance.TryClaim(key);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryClaim(key));

        var received = new TaskCompletionSource<IReadOnlyList<string>>();
        first!.Listen(files => received.TrySetResult(files));
        Assert.True(SingleInstance.Forward(key, [@"C:\Docs\Report.pdf", @"D:\Scans\Receipt 2.pdf"]));
        var files = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([@"C:\Docs\Report.pdf", @"D:\Scans\Receipt 2.pdf"], files);

        // Nobody listening: the second start runs on its own.
        Assert.False(SingleInstance.Forward($"PageSmithTest-{Guid.NewGuid():N}", []));
    }

    [Fact]
    public async Task Properties_describe_the_document()
    {
        using var files = new TestFiles();
        var src = files.CreatePdf("doc.pdf", 3, width: 595, height: 842);
        var props = DocumentProperties.Read(src);
        Assert.Equal(3, props.PageCount);
        Assert.Equal(210, props.PageSizeMm.Width, 0);
        Assert.Equal(297, props.PageSizeMm.Height, 0);
        Assert.False(props.Encrypted);
        Assert.True(props.FileSize > 0);

        var protectedResult = await ProtectTool.RunAsync(new ProtectOptions { Files = [src], OutputDirectory = files.Dir, OpenPassword = "Open-Sesame1" });
        Assert.True(DocumentProperties.Read(new PageSmith.Core.Common.InputFile(protectedResult.OutputFiles[0], "Open-Sesame1")).Encrypted);
        // Restriction-only encryption (no open password) is reported too.
        var restricted = await ProtectTool.RunAsync(new ProtectOptions { Files = [files.CreatePdf("r.pdf", 1)], OutputDirectory = files.PathFor("r"), AllowCopying = false });
        Assert.True(DocumentProperties.Read(restricted.OutputFiles[0]).Encrypted);
    }
}
