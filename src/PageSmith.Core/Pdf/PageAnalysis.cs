using PageSmith.Core.Imaging;
using PageSmith.Core.Rendering;

namespace PageSmith.Core.Pdf;

/// <summary>Visual checks on rendered pages.</summary>
public static class PageAnalysis
{
    /// <summary>
    /// Fraction of "ink" (non-near-white) pixels of a page rendered at low resolution.
    /// Values below ~0.002 mean the page is visually blank.
    /// </summary>
    public static async Task<double> InkCoverageAsync(PdfRenderer renderer, int pageIndex, CancellationToken ct = default)
    {
        var bmp = await renderer.RenderAsync(pageIndex, 36, ct).ConfigureAwait(false);
        return InkCoverage(ImageIO.ToPixels(bmp));
    }

    public static double InkCoverage(PixelBuffer px, int threshold = 235)
    {
        long ink = 0;
        var p = px.Pixels;
        // Ignore a 3% border where scanners leave shadows.
        int mx = px.Width * 3 / 100, my = px.Height * 3 / 100;
        long total = 0;
        for (var y = my; y < px.Height - my; y++)
        for (var x = mx; x < px.Width - mx; x++)
        {
            var i = (y * px.Width + x) * 4;
            var lum = (p[i] * 114 + p[i + 1] * 587 + p[i + 2] * 299) / 1000;
            if (lum < threshold) ink++;
            total++;
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    public static async Task<bool> IsBlankAsync(PdfRenderer renderer, int pageIndex, double maxCoverage = 0.002, CancellationToken ct = default) =>
        await InkCoverageAsync(renderer, pageIndex, ct).ConfigureAwait(false) <= maxCoverage;
}
