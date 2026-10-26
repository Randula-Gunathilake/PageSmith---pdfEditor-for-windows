namespace PageSmith.Core.Imaging;

public enum ScanColorMode
{
    /// <summary>Keep the photo as it is.</summary>
    Color,

    /// <summary>Grey with the contrast stretched, like a photocopy.</summary>
    Grayscale,

    /// <summary>Pure black ink on white paper; shadows and uneven light are evened out.</summary>
    BlackAndWhite,
}

/// <summary>Clean-up filters for photos and scans of documents.</summary>
public static class DocumentFilters
{
    public static PixelBuffer Apply(PixelBuffer source, ScanColorMode mode) => mode switch
    {
        ScanColorMode.Grayscale => Grayscale(source),
        ScanColorMode.BlackAndWhite => BlackAndWhite(source),
        _ => source,
    };

    /// <summary>Luminance of each pixel, 0-255.</summary>
    public static byte[] Luminance(PixelBuffer p)
    {
        var lum = new byte[p.Width * p.Height];
        for (var i = 0; i < lum.Length; i++)
        {
            var o = i * 4;
            lum[i] = (byte)((p.Pixels[o] * 29 + p.Pixels[o + 1] * 150 + p.Pixels[o + 2] * 77) >> 8);
        }
        return lum;
    }

    /// <summary>Greyscale with the darkest 1% mapped to black and the brightest 1% to white.</summary>
    public static PixelBuffer Grayscale(PixelBuffer source)
    {
        var lum = Luminance(source);
        var (low, high) = Percentiles(lum, 0.01, 0.99);
        var range = Math.Max(1, high - low);
        var output = new PixelBuffer(source.Width, source.Height);
        for (var i = 0; i < lum.Length; i++)
        {
            var v = (byte)Math.Clamp((lum[i] - low) * 255 / range, 0, 255);
            var o = i * 4;
            output.Pixels[o] = output.Pixels[o + 1] = output.Pixels[o + 2] = v;
            output.Pixels[o + 3] = 255;
        }
        return output;
    }

    /// <summary>
    /// Adaptive threshold: each pixel is compared with the average brightness around it, so a page photographed
    /// with a shadow across it still comes out as clean black text on white.
    /// </summary>
    public static PixelBuffer BlackAndWhite(PixelBuffer source)
    {
        var w = source.Width;
        var h = source.Height;
        var lum = Luminance(source);
        // Integral image for fast window averages.
        var integral = new long[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            long row = 0;
            for (var x = 0; x < w; x++)
            {
                row += lum[y * w + x];
                integral[(y + 1) * (w + 1) + x + 1] = integral[y * (w + 1) + x + 1] + row;
            }
        }
        double Mean(int x, int y, int r)
        {
            int x0 = Math.Max(0, x - r), x1 = Math.Min(w, x + r + 1), y0 = Math.Max(0, y - r), y1 = Math.Min(h, y + r + 1);
            var sum = integral[y1 * (w + 1) + x1] - integral[y0 * (w + 1) + x1] - integral[y1 * (w + 1) + x0] + integral[y0 * (w + 1) + x0];
            return sum / (double)((x1 - x0) * (y1 - y0));
        }

        var radius = Math.Max(8, Math.Min(w, h) / 24);
        var paper = PaperBrightness(lum, w, h);
        var output = new PixelBuffer(w, h);
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var v0 = lum[y * w + x];
                // Text: noticeably darker than its close surroundings, which follow any shadow on the paper.
                var mean = Mean(x, y, radius);
                var ink = v0 < mean * 0.85 - 4 || mean < 60 && v0 < 100;
                // Solid fills (boxes, charts) are as dark as their close surroundings, but much darker than
                // the paper around them.
                if (!ink && v0 < 160) ink = v0 < paper(x, y) * 0.62;
                var v = ink ? (byte)0 : (byte)255;
                var o = (y * w + x) * 4;
                output.Pixels[o] = output.Pixels[o + 1] = output.Pixels[o + 2] = v;
                output.Pixels[o + 3] = 255;
            }
        }
        return output;
    }

    /// <summary>
    /// Estimates how bright the paper is at each point, following shadows: the bright level of each block of the
    /// image, spread to neighbouring blocks so that large dark areas take the paper level from around them.
    /// </summary>
    private static Func<int, int, double> PaperBrightness(byte[] lum, int w, int h)
    {
        var block = Math.Max(16, Math.Min(w, h) / 40);
        var bw = (w + block - 1) / block;
        var bh = (h + block - 1) / block;
        var bright = new double[bw * bh];
        var histogram = new int[256];
        for (var by = 0; by < bh; by++)
        for (var bx = 0; bx < bw; bx++)
        {
            Array.Clear(histogram);
            var count = 0;
            for (var y = by * block; y < Math.Min(h, (by + 1) * block); y++)
            for (var x = bx * block; x < Math.Min(w, (bx + 1) * block); x++)
            {
                histogram[lum[y * w + x]]++;
                count++;
            }
            // The 90th percentile ignores a few specks of glare.
            var seen = 0;
            var level = 255;
            for (var v = 0; v < 256; v++)
            {
                seen += histogram[v];
                if (seen >= count * 0.9)
                {
                    level = v;
                    break;
                }
            }
            bright[by * bw + bx] = level;
        }
        const int spread = 4;
        var paper = new double[bw * bh];
        for (var by = 0; by < bh; by++)
        for (var bx = 0; bx < bw; bx++)
        {
            var best = 0.0;
            for (var dy = -spread; dy <= spread; dy++)
            for (var dx = -spread; dx <= spread; dx++)
            {
                int nx = bx + dx, ny = by + dy;
                if (nx >= 0 && ny >= 0 && nx < bw && ny < bh) best = Math.Max(best, bright[ny * bw + nx]);
            }
            paper[by * bw + bx] = best;
        }
        // Bilinear between block centres so shadows change smoothly.
        return (x, y) =>
        {
            var fx = Math.Clamp((x + 0.5) / block - 0.5, 0, bw - 1);
            var fy = Math.Clamp((y + 0.5) / block - 0.5, 0, bh - 1);
            int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(bw - 1, x0 + 1), y1 = Math.Min(bh - 1, y0 + 1);
            double tx = fx - x0, ty = fy - y0;
            var top = paper[y0 * bw + x0] * (1 - tx) + paper[y0 * bw + x1] * tx;
            var bottom = paper[y1 * bw + x0] * (1 - tx) + paper[y1 * bw + x1] * tx;
            return top * (1 - ty) + bottom * ty;
        };
    }

    private static (int Low, int High) Percentiles(byte[] values, double low, double high)
    {
        var histogram = new int[256];
        foreach (var v in values) histogram[v]++;
        int Find(double fraction)
        {
            var target = (long)(values.Length * fraction);
            long seen = 0;
            for (var i = 0; i < 256; i++)
            {
                seen += histogram[i];
                if (seen > target) return i;
            }
            return 255;
        }
        return (Find(low), Find(high));
    }
}
