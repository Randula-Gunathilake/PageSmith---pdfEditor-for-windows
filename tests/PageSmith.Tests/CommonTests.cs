using System.IO;
using PageSmith.Core.Common;
using PageSmith.Core.Rendering;

namespace PageSmith.Tests;

public class CommonTests
{
    [Theory]
    [InlineData("1-3,5", 10, new[] { 0, 1, 2, 4 })]
    [InlineData("8-", 10, new[] { 7, 8, 9 })]
    [InlineData("-2", 10, new[] { 0, 1 })]
    [InlineData("3-1", 5, new[] { 2, 1, 0 })]
    [InlineData("last", 4, new[] { 3 })]
    [InlineData("odd", 5, new[] { 0, 2, 4 })]
    [InlineData("even", 5, new[] { 1, 3 })]
    [InlineData("", 3, new[] { 0, 1, 2 })]
    [InlineData("2, 2, 1", 3, new[] { 1, 0 })]
    public void PageRange_parses_user_text(string spec, int count, int[] expected) =>
        Assert.Equal(expected, PageRange.Parse(spec, count));

    [Theory]
    [InlineData("0")]
    [InlineData("12")]
    [InlineData("a-b")]
    public void PageRange_rejects_invalid_pages(string spec) =>
        Assert.Throws<FormatException>(() => PageRange.Parse(spec, 10));

    [Fact]
    public void PageRange_formats_compactly() =>
        Assert.Equal("1-3, 5, 7-8", PageRange.Format([0, 1, 2, 4, 6, 7]));

    [Fact]
    public void OutputPaths_never_overwrite()
    {
        using var files = new TestFiles();
        var first = OutputPaths.Unique(files.Dir, "a.pdf");
        File.WriteAllText(first, "x");
        var second = OutputPaths.Unique(files.Dir, "a.pdf");
        Assert.EndsWith("a (2).pdf", second);
    }

    [Fact]
    public async Task Renderer_reports_point_sizes_and_renders()
    {
        using var files = new TestFiles();
        var pdf = files.CreatePdf("r.pdf", 2, 612, 792);
        using var renderer = await PdfRenderer.OpenAsync(pdf);
        Assert.Equal(2, renderer.PageCount);
        var size = renderer.GetPageSize(0);
        Assert.Equal(612, size.Width, 1);
        Assert.Equal(792, size.Height, 1);
        var bmp = await renderer.RenderAsync(0, 72);
        Assert.InRange(bmp.PixelWidth, 610, 614);
    }

    [Fact]
    public void Font_resolver_finds_windows_fonts()
    {
        var resolver = new WindowsFontResolver();
        var info = resolver.ResolveTypeface("Arial", true, false);
        Assert.NotNull(info);
        Assert.NotNull(resolver.GetFont(info!.FaceName));
        // Unknown families fall back instead of failing.
        Assert.NotNull(resolver.ResolveTypeface("No Such Font 123", false, false));
    }
}
