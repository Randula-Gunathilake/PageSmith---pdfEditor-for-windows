using System.Windows.Media.Imaging;
using PageSmith.Core.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PageSmith.Core.Tools.Convert;

public enum PaperOrientation
{
    Portrait,
    Landscape,

    /// <summary>Follows each image's shape.</summary>
    Auto,
}

public enum ImagePageSize
{
    /// <summary>The page takes the size of the image.</summary>
    Fit,
    A4,
    Letter,
    Legal,
    A3,
    A5,
}

public enum PageMargin
{
    None,
    Small,
    Big,
}

public sealed record ImagesToPdfOptions
{
    public required IReadOnlyList<string> Images { get; init; }

    /// <summary>Output file when merging; otherwise the folder (or a file whose folder is used).</summary>
    public required string OutputPath { get; init; }

    public PaperOrientation Orientation { get; init; } = PaperOrientation.Auto;
    public ImagePageSize PageSize { get; init; } = ImagePageSize.Fit;
    public PageMargin Margin { get; init; } = PageMargin.None;

    /// <summary>One PDF with every image, or one PDF per image.</summary>
    public bool MergeIntoOne { get; init; } = true;

    /// <summary>Per-image clockwise rotation set by the user (in addition to EXIF orientation).</summary>
    public IReadOnlyDictionary<string, int>? Rotations { get; init; }
}

/// <summary>Converts JPG, PNG, TIFF, BMP, GIF… images into PDF pages (JPG to PDF and Scan to PDF tools).</summary>
public static class ImagesToPdfTool
{
    public static Task<ToolResult> RunAsync(ImagesToPdfOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Run(options, progress, ct), ct);

    public static ToolResult Run(ImagesToPdfOptions options, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        if (options.Images.Count == 0) throw new UserFacingException("Add at least one image.");
        var scope = new ProgressScope(progress);
        var result = new ToolResult();
        PdfDocument? merged = options.MergeIntoOne ? PdfDocs.Create(Path.GetFileNameWithoutExtension(options.OutputPath)) : null;
        var outDir = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath))!;
        var pageCount = 0;

        try
        {
            for (var i = 0; i < options.Images.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var image = options.Images[i];
                scope.Report(i, options.Images.Count, $"Adding {Path.GetFileName(image)}");
                result.InputBytes += new FileInfo(image).Length;
                var userRotation = options.Rotations?.GetValueOrDefault(image) ?? 0;

                var doc = merged ?? PdfDocs.Create(Path.GetFileNameWithoutExtension(image));
                pageCount += AddImagePages(doc, image, userRotation, options);
                if (merged == null)
                {
                    var path = OutputPaths.Unique(outDir, Path.GetFileNameWithoutExtension(image) + ".pdf");
                    PdfDocs.Save(doc, path);
                    doc.Dispose();
                    result.AddOutput(path);
                }
            }
            if (merged != null)
            {
                PdfDocs.Save(merged, options.OutputPath);
                result.AddOutput(options.OutputPath);
            }
        }
        finally
        {
            merged?.Dispose();
        }

        result.Summary = options.MergeIntoOne
            ? $"Created a {pageCount}-page PDF from {options.Images.Count} image{(options.Images.Count == 1 ? "" : "s")}."
            : $"Created {result.OutputFiles.Count} PDFs.";
        scope.Report(1);
        return result;
    }

    /// <summary>Adds one page per image frame; returns the number of pages added.</summary>
    public static int AddImagePages(PdfDocument doc, string imagePath, int userRotation, ImagesToPdfOptions options)
    {
        // JPEGs are embedded as-is (no recompression); orientation is applied when drawing.
        if (ImageIO.IsJpeg(imagePath))
        {
            var exif = ImageIO.GetExifOrientation(imagePath);
            var (exifRotation, mirror) = ImageIO.DescribeOrientation(exif);
            if (!mirror)
            {
                using var ximg = XImage.FromFile(imagePath);
                var dpi = DpiOf(imagePath);
                AddPage(doc, ximg, ximg.PixelWidth, ximg.PixelHeight, dpi, exifRotation + userRotation, options);
                return 1;
            }
        }

        var frames = ImageIO.LoadFrames(imagePath);
        foreach (var frame in frames)
        {
            using var ms = new MemoryStream(ImageIO.EncodePng(frame));
            using var ximg = XImage.FromStream(ms);
            AddPage(doc, ximg, frame.PixelWidth, frame.PixelHeight, frame.DpiX, userRotation, options);
        }
        return frames.Count;
    }

    private static void AddPage(PdfDocument doc, XImage image, int pixelWidth, int pixelHeight, double dpi, int rotation, ImagesToPdfOptions options)
    {
        rotation = ((rotation % 360) + 360) % 360;
        if (dpi is < 50 or > 1200 || double.IsNaN(dpi)) dpi = 96;
        // Size of the image as it appears (after rotation), in points.
        var w = pixelWidth * 72.0 / dpi;
        var h = pixelHeight * 72.0 / dpi;
        var sideways = rotation is 90 or 270;
        var (shownW, shownH) = sideways ? (h, w) : (w, h);

        var margin = options.Margin switch { PageMargin.Small => 20.0, PageMargin.Big => 50.0, _ => 0.0 };
        double pageW, pageH;
        if (options.PageSize == ImagePageSize.Fit)
        {
            pageW = shownW + 2 * margin;
            pageH = shownH + 2 * margin;
        }
        else
        {
            (pageW, pageH) = PaperSize(options.PageSize);
            var landscape = options.Orientation switch
            {
                PaperOrientation.Landscape => true,
                PaperOrientation.Portrait => false,
                _ => shownW > shownH,
            };
            if (landscape) (pageW, pageH) = (pageH, pageW);
        }

        var page = doc.AddPage();
        page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(pageW, pageH));
        using var gfx = XGraphics.FromPdfPage(page);

        // Fit inside the margins, keeping the aspect ratio.
        var boxW = pageW - 2 * margin;
        var boxH = pageH - 2 * margin;
        var scale = Math.Min(boxW / shownW, boxH / shownH);
        var drawW = shownW * scale;
        var drawH = shownH * scale;
        var cx = pageW / 2;
        var cy = pageH / 2;

        gfx.TranslateTransform(cx, cy);
        if (rotation != 0) gfx.RotateTransform(rotation);
        var (iw, ih) = sideways ? (drawH, drawW) : (drawW, drawH);
        gfx.DrawImage(image, -iw / 2, -ih / 2, iw, ih);
    }

    public static (double Width, double Height) PaperSize(ImagePageSize size) => size switch
    {
        ImagePageSize.Letter => (612, 792),
        ImagePageSize.Legal => (612, 1008),
        ImagePageSize.A3 => (841.89, 1190.55),
        ImagePageSize.A5 => (419.53, 595.28),
        _ => (595.28, 841.89),
    };

    private static double DpiOf(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return decoder.Frames[0].DpiX;
        }
        catch
        {
            return 96;
        }
    }
}
