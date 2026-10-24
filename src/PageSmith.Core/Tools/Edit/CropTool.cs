using System.Windows;
using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PageSmith.Core.Tools.Edit;

public sealed record CropOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>
    /// Area to keep as fractions (0..1) of the displayed page: X, Y from the top-left, width, height.
    /// Fractions let one selection apply to pages of different sizes.
    /// </summary>
    public Rect Area { get; init; } = new(0, 0, 1, 1);

    /// <summary>Pages to crop, e.g. "3" for the current page only; null for all pages.</summary>
    public string? Pages { get; init; }

    /// <summary>Per-page areas (0-based page → fractional rect) that override <see cref="Area"/>.</summary>
    public IReadOnlyDictionary<int, Rect>? PageAreas { get; init; }
}

/// <summary>Crops pages by setting their visible box (CropBox) to the selected area.</summary>
public static class CropTool
{
    public static Task<ToolResult> RunAsync(CropOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var scope = new ProgressScope(progress);
            var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
            using var doc = PdfDocs.Open(options.Input);
            var pages = PageRange.Parse(options.Pages, doc.PageCount).ToHashSet();
            if (options.PageAreas != null) pages.UnionWith(options.PageAreas.Keys);
            var n = 0;
            foreach (var index in pages.Order())
            {
                ct.ThrowIfCancellationRequested();
                scope.Report(n++, pages.Count, $"Cropping page {index + 1}");
                var area = options.PageAreas?.GetValueOrDefault(index) ?? options.Area;
                Crop(doc.Pages[index], area);
            }
            PdfDocs.Save(doc, options.OutputPath);
            result.AddOutput(options.OutputPath);
            result.Summary = $"Cropped {pages.Count} page{(pages.Count == 1 ? "" : "s")}.";
            scope.Report(1);
            return result;
        }, ct);

    public static void Crop(PdfPage page, Rect fraction)
    {
        fraction.Intersect(new Rect(0, 0, 1, 1));
        if (fraction.IsEmpty || fraction.Width < 0.01 || fraction.Height < 0.01) throw new UserFacingException("The crop area is too small.");
        var g = PageGeometry.From(page);
        var display = new Rect(fraction.X * g.Width, fraction.Y * g.Height, fraction.Width * g.Width, fraction.Height * g.Height);
        var user = g.ToUser(display);
        var box = new PdfRectangle(new XPoint(user.Left, user.Top), new XPoint(user.Right, user.Bottom));
        page.CropBox = box;
        page.TrimBox = box;
        page.ArtBox = box;
        page.BleedBox = box;
    }

    /// <summary>Finds the area that contains ink on a page, as fractions, with a small padding.</summary>
    public static async Task<Rect> DetectContentAsync(PdfRenderer renderer, int pageIndex, double padding = 0.015, CancellationToken ct = default)
    {
        var px = ImageIO.ToPixels(await renderer.RenderAsync(pageIndex, 50, ct).ConfigureAwait(false));
        int minX = px.Width, minY = px.Height, maxX = -1, maxY = -1;
        for (var y = 0; y < px.Height; y++)
        for (var x = 0; x < px.Width; x++)
        {
            var i = (y * px.Width + x) * 4;
            if ((px.Pixels[i] + px.Pixels[i + 1] + px.Pixels[i + 2]) / 3 > 235) continue;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        if (maxX < 0) return new Rect(0, 0, 1, 1);
        var r = new Rect((double)minX / px.Width - padding, (double)minY / px.Height - padding,
            (double)(maxX - minX + 1) / px.Width + 2 * padding, (double)(maxY - minY + 1) / px.Height + 2 * padding);
        r.Intersect(new Rect(0, 0, 1, 1));
        return r;
    }
}
