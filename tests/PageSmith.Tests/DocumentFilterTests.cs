using PageSmith.Core.Imaging;

namespace PageSmith.Tests;

public class DocumentFilterTests
{
    /// <summary>A "photo" of a page: paper that darkens from left to right (a shadow) with dark text bars on it.</summary>
    private static (PixelBuffer Photo, bool[] IsInk) ShadowedPage()
    {
        const int w = 600, h = 400;
        var photo = new PixelBuffer(w, h);
        var ink = new bool[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var paper = 250 - 110 * x / w;
            // Text lines: 6 px tall bars every 30 px, broken into "words".
            var isText = y % 30 is >= 12 and < 18 && x % 50 < 38 && x is > 20 and < 580;
            // A solid filled box, like a chart bar or logo.
            var isFill = x is >= 60 and < 180 && y is >= 250 and < 370;
            var isInk = isText || isFill;
            var v = isFill ? paper - 120 : isText ? paper - 100 : paper;
            var o = (y * w + x) * 4;
            photo.Pixels[o] = photo.Pixels[o + 1] = photo.Pixels[o + 2] = (byte)v;
            photo.Pixels[o + 3] = 255;
            ink[y * w + x] = isInk;
        }
        return (photo, ink);
    }

    [Fact]
    public void Black_and_white_removes_shadows_and_keeps_text()
    {
        var (photo, ink) = ShadowedPage();
        var bw = DocumentFilters.BlackAndWhite(photo);
        int inkBlack = 0, inkTotal = 0, paperWhite = 0, paperTotal = 0;
        for (var i = 0; i < ink.Length; i++)
        {
            var v = bw.Pixels[i * 4];
            Assert.True(v is 0 or 255);
            if (ink[i])
            {
                inkTotal++;
                if (v == 0) inkBlack++;
            }
            else
            {
                paperTotal++;
                if (v == 255) paperWhite++;
            }
        }
        Assert.True(inkBlack > inkTotal * 0.9, $"only {inkBlack}/{inkTotal} text pixels are black");
        Assert.True(paperWhite > paperTotal * 0.98, $"only {paperWhite}/{paperTotal} paper pixels are white");
    }

    [Fact]
    public void Grayscale_stretches_contrast()
    {
        var (photo, _) = ShadowedPage();
        var gray = DocumentFilters.Grayscale(photo);
        var values = Enumerable.Range(0, gray.Width * gray.Height).Select(i => gray.Pixels[i * 4]).ToList();
        Assert.True(values.Min() < 10 && values.Max() > 245);
        Assert.All(Enumerable.Range(0, gray.Width * gray.Height), i =>
            Assert.True(gray.Pixels[i * 4] == gray.Pixels[i * 4 + 1] && gray.Pixels[i * 4 + 1] == gray.Pixels[i * 4 + 2]));
    }
}
