using System.Windows;
using PageSmith.Core.Imaging;
using PageSmith.Core.Rendering;

namespace PageSmith.Tests;

public class PageRowsTests
{
    private const double Gap = 12, Edge = 16;

    private static Size[] Pages(params (double W, double H)[] sizes) => sizes.Select(s => new Size(s.W, s.H)).ToArray();

    private static Size[] Same(int count, double w = 100, double h = 140) => Enumerable.Repeat(new Size(w, h), count).ToArray();

    [Fact]
    public void Auto_puts_one_page_per_row_until_more_fit()
    {
        // Room for one page and a bit: one per row.
        var rows = PageRows.Arrange(Same(5), PageLayout.Auto, 150, Gap, Edge);
        Assert.Equal(5, rows.RowCount);
        Assert.Equal(1, rows.MostPerRow);
        Assert.Equal(Edge, rows.TopOf(0));
        Assert.Equal(Edge + 140 + Gap, rows.TopOf(1));
        Assert.Equal(Edge + 5 * 140 + 4 * Gap + Edge, rows.Height);

        // Zoomed out (the same room holds more): three per row, like Word.
        var zoomedOut = PageRows.Arrange(Same(7), PageLayout.Auto, 3 * 100 + 2 * Gap, Gap, Edge);
        Assert.Equal(3, zoomedOut.MostPerRow);
        Assert.Equal(3, zoomedOut.RowCount);
        Assert.Equal([0, 0, 0, 1, 1, 1, 2], Enumerable.Range(0, 7).Select(zoomedOut.RowOf));
        Assert.Equal(6, zoomedOut.RowStart(2));
        Assert.Equal(7, zoomedOut.RowEnd(2));
        // Pages sit side by side with a gap; a shorter last row is centred.
        Assert.Equal(0, zoomedOut.LeftOf(0, 0));
        Assert.Equal(112, zoomedOut.LeftOf(1, 0));
        Assert.Equal(224, zoomedOut.LeftOf(2, 0));
        Assert.Equal(112, zoomedOut.LeftOf(6, 0));
        Assert.Equal(324, zoomedOut.PagesWidth);
    }

    [Fact]
    public void Auto_packs_mixed_page_sizes_and_centres_them_in_their_row()
    {
        // Portrait, landscape, portrait: the landscape page is wider, so only two fit in 300.
        var rows = PageRows.Arrange(Pages((100, 140), (140, 100), (100, 140)), PageLayout.Auto, 300, Gap, Edge);
        Assert.Equal(2, rows.RowCount);
        Assert.Equal(2, rows.RowEnd(0));
        // The landscape page is centred vertically in the row of the taller portrait page.
        Assert.Equal(Edge + 20, rows.TopOf(1));
        Assert.Equal(Edge, rows.TopOf(0));
    }

    [Fact]
    public void Single_keeps_one_page_per_row_however_small()
    {
        var rows = PageRows.Arrange(Same(4, 20, 30), PageLayout.Single, 1000, Gap, Edge);
        Assert.Equal(4, rows.RowCount);
        Assert.Equal(1, rows.MostPerRow);
    }

    [Fact]
    public void Two_pages_meet_in_the_middle_and_a_book_has_its_cover_alone()
    {
        var two = PageRows.Arrange(Pages((100, 140), (80, 140), (100, 140)), PageLayout.Two, 50, Gap, Edge);
        Assert.Equal(2, two.RowCount);
        Assert.Equal(2 * 100 + Gap, two.PagesWidth);
        // The left page's right edge and the right page's left edge meet at the middle gap.
        Assert.Equal(100, two.LeftOf(0, 0) + 100);
        Assert.Equal(100 + Gap, two.LeftOf(1, 0));
        Assert.Equal(0, two.LeftOf(2, 0));

        var book = PageRows.Arrange(Same(4), PageLayout.Book, 50, Gap, Edge);
        Assert.Equal([0, 1, 1, 2], Enumerable.Range(0, 4).Select(book.RowOf));
        // The cover is on the right, like the front of a closed book.
        Assert.Equal(100 + Gap, book.LeftOf(0, 0));
        Assert.Equal((1, 2), PageRows.RowAround(PageLayout.Book, 2, 4));
        Assert.Equal((0, 1), PageRows.RowAround(PageLayout.Book, 0, 4));
        Assert.Equal((2, 2), PageRows.RowAround(PageLayout.Two, 3, 4));
        Assert.Equal((3, 1), PageRows.RowAround(PageLayout.Auto, 3, 4));
    }

    [Fact]
    public void Rows_are_found_by_height_and_compared()
    {
        var rows = PageRows.Arrange(Same(6), PageLayout.Auto, 212, Gap, Edge);
        Assert.Equal(0, rows.RowAt(-50));
        Assert.Equal(0, rows.RowAt(Edge + 10));
        // In the gap below a row, the row above.
        Assert.Equal(0, rows.RowAt(Edge + 140 + Gap / 2));
        Assert.Equal(1, rows.RowAt(Edge + 140 + Gap + 1));
        Assert.Equal(2, rows.RowAt(1e6));
        Assert.True(rows.SameRowsAs(PageRows.Arrange(Same(6), PageLayout.Auto, 220, Gap, Edge)));
        Assert.False(rows.SameRowsAs(PageRows.Arrange(Same(6), PageLayout.Auto, 400, Gap, Edge)));
    }

    [Fact]
    public void No_pages_make_no_rows()
    {
        var rows = PageRows.Arrange([], PageLayout.Auto, 500, Gap, Edge);
        Assert.Equal(0, rows.RowCount);
        Assert.Equal(0, rows.Height);
        Assert.Equal(0, rows.RowAt(10));
    }

    [Fact]
    public async Task Pages_for_the_screen_match_the_png_render()
    {
        using var files = new TestFiles();
        var pdf = files.CreatePdf("r.pdf", 1, 300, 200, _ => "Some text to compare");
        using var renderer = await PdfRenderer.OpenAsync(pdf);
        var screen = await renderer.RenderAsync(0, 96);
        var png = ImageIO.Decode(await renderer.RenderPngAsync(0, 96));
        Assert.Equal(png.PixelWidth, screen.PixelWidth);
        Assert.Equal(png.PixelHeight, screen.PixelHeight);
        Assert.Equal(96, screen.DpiX, 1);
        Assert.True(screen.IsFrozen);
        // Made straight from the BMP's pixels rather than through the image decoder.
        Assert.Equal(System.Windows.Media.PixelFormats.Bgr32, screen.Format);
        var a = ImageIO.ToPixels(screen);
        var b = ImageIO.ToPixels(png);
        var different = 0;
        for (var i = 0; i < a.Pixels.Length; i += 4)
            if (Math.Abs(a.Pixels[i] - b.Pixels[i]) > 2 || Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) > 2 || Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]) > 2) different++;
        Assert.Equal(0, different);
        // The page has dark text near the top (not flipped upside down) and white elsewhere.
        Assert.Contains(Enumerable.Range(0, a.Width * 120).Select(p => a.Pixels[p * 4]), v => v < 100);
    }
}
