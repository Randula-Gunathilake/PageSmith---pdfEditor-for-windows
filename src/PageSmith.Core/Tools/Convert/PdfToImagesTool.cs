using System.Security.Cryptography;
using PageSmith.Core.Imaging;
using PageSmith.Core.Rendering;

namespace PageSmith.Core.Tools.Convert;

public enum PdfToImageMode
{
    /// <summary>Every page becomes an image.</summary>
    PageToImage,

    /// <summary>Images embedded in the PDF are extracted.</summary>
    ExtractImages,
}

public sealed record PdfToImagesOptions
{
    public required InputFile Input { get; init; }

    /// <summary>Folder that receives a sub-folder of images.</summary>
    public required string OutputDirectory { get; init; }

    public PdfToImageMode Mode { get; init; } = PdfToImageMode.PageToImage;
    public ImageFormatKind Format { get; init; } = ImageFormatKind.Jpeg;

    /// <summary>Rendering resolution: iLovePDF "Normal" is 150 DPI, "High" is 300 DPI.</summary>
    public double Dpi { get; init; } = 150;

    public int JpegQuality { get; init; } = 90;
    public string? Pages { get; init; }

    /// <summary>For TIFF: write all pages into one multi-page TIFF.</summary>
    public bool MultiPageTiff { get; init; }
}

/// <summary>Converts PDF pages to images, or extracts the images embedded in a PDF.</summary>
public static class PdfToImagesTool
{
    public static async Task<ToolResult> RunAsync(PdfToImagesOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
        var stem = Path.GetFileNameWithoutExtension(options.Input.Path);
        var dir = OutputPaths.UniqueDirectory(options.OutputDirectory, $"{stem} (images)");
        if (options.Mode == PdfToImageMode.PageToImage)
            await RenderPagesAsync(options, dir, stem, result, new ProgressScope(progress), ct).ConfigureAwait(false);
        else
            await Task.Run(() => ExtractImages(options, dir, stem, result, new ProgressScope(progress), ct), ct).ConfigureAwait(false);

        if (result.OutputFiles.Count == 0)
        {
            Directory.Delete(dir);
            throw new UserFacingException(options.Mode == PdfToImageMode.ExtractImages
                ? "This PDF does not contain any embedded images."
                : "No pages were converted.");
        }
        result.Summary = $"Created {result.OutputFiles.Count} image{(result.OutputFiles.Count == 1 ? "" : "s")} in \"{Path.GetFileName(dir)}\".";
        return result;
    }

    private static async Task RenderPagesAsync(PdfToImagesOptions o, string dir, string stem, ToolResult result, ProgressScope scope, CancellationToken ct)
    {
        using var renderer = await PdfRenderer.OpenAsync(o.Input.Path, o.Input.Password, ct).ConfigureAwait(false);
        var pages = PageRange.Parse(o.Pages, renderer.PageCount);
        var tiffFrames = new List<System.Windows.Media.Imaging.BitmapSource>();
        var digits = Math.Max(2, renderer.PageCount.ToString().Length);
        for (var i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            scope.Report(i, pages.Count, $"Rendering page {pages[i] + 1}");
            var bmp = await renderer.RenderAsync(pages[i], o.Dpi, ct).ConfigureAwait(false);
            bmp = WithDpi(bmp, o.Dpi);
            if (o.Format == ImageFormatKind.Tiff && o.MultiPageTiff)
            {
                tiffFrames.Add(bmp);
                continue;
            }
            var path = Path.Combine(dir, $"{stem}-{(pages[i] + 1).ToString().PadLeft(digits, '0')}{Extension(o.Format)}");
            await File.WriteAllBytesAsync(path, ImageIO.Encode(bmp, o.Format, o.JpegQuality), ct).ConfigureAwait(false);
            result.AddOutput(path);
        }
        if (tiffFrames.Count > 0)
        {
            var path = Path.Combine(dir, $"{stem}.tif");
            await File.WriteAllBytesAsync(path, ImageIO.EncodeTiff(tiffFrames), ct).ConfigureAwait(false);
            result.AddOutput(path);
        }
        scope.Report(1);
    }

    private static void ExtractImages(PdfToImagesOptions o, string dir, string stem, ToolResult result, ProgressScope scope, CancellationToken ct)
    {
        using var doc = Text.TextExtractor.Open(o.Input);
        var pages = PageRange.Parse(o.Pages, doc.NumberOfPages);
        var seen = new HashSet<string>();
        var n = 0;
        for (var i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            scope.Report(i, pages.Count, $"Extracting images from page {pages[i] + 1}");
            IEnumerable<UglyToad.PdfPig.Content.IPdfImage> images;
            try
            {
                images = doc.GetPage(pages[i] + 1).GetImages().ToList();
            }
            catch
            {
                result.Warnings.Add($"Images on page {pages[i] + 1} could not be read.");
                continue;
            }

            foreach (var image in images)
            {
                byte[] raw;
                try { raw = image.RawMemory.ToArray(); } catch { continue; }
                if (raw.Length == 0 || image.WidthInSamples < 8 || image.HeightInSamples < 8) continue;
                var hash = System.Convert.ToHexString(SHA1.HashData(raw));
                if (!seen.Add(hash)) continue;

                var isJpeg = IsDct(image);
                n++;
                var baseName = Path.Combine(dir, $"{stem}-image-{n:D3}");
                try
                {
                    if (isJpeg && o.Format == ImageFormatKind.Jpeg)
                    {
                        File.WriteAllBytes(baseName + ".jpg", raw);
                        result.AddOutput(baseName + ".jpg");
                        continue;
                    }
                    byte[]? png = null;
                    if (isJpeg) png = ImageIO.EncodePng(ImageIO.Decode(raw));
                    else if (!image.TryGetPng(out png)) png = null;
                    if (png == null)
                    {
                        n--;
                        continue;
                    }
                    var bmp = ImageIO.Decode(png);
                    var path = baseName + Extension(o.Format);
                    File.WriteAllBytes(path, o.Format == ImageFormatKind.Png ? png : ImageIO.Encode(bmp, o.Format, o.JpegQuality));
                    result.AddOutput(path);
                }
                catch
                {
                    n--;
                }
            }
        }
        scope.Report(1);
    }

    private static bool IsDct(UglyToad.PdfPig.Content.IPdfImage image)
    {
        if (!image.ImageDictionary.TryGet(UglyToad.PdfPig.Tokens.NameToken.Filter, out var token)) return false;
        return token switch
        {
            UglyToad.PdfPig.Tokens.NameToken n => n.Data == "DCTDecode",
            UglyToad.PdfPig.Tokens.ArrayToken a => a.Data.Count == 1 && a.Data[0] is UglyToad.PdfPig.Tokens.NameToken n2 && n2.Data == "DCTDecode",
            _ => false,
        };
    }

    private static System.Windows.Media.Imaging.BitmapSource WithDpi(System.Windows.Media.Imaging.BitmapSource bmp, double dpi)
    {
        if (Math.Abs(bmp.DpiX - dpi) < 0.5) return bmp;
        var px = ImageIO.ToPixels(bmp);
        return ImageIO.FromPixels(px, dpi);
    }

    public static string Extension(ImageFormatKind format) => format switch
    {
        ImageFormatKind.Png => ".png",
        ImageFormatKind.Tiff => ".tif",
        ImageFormatKind.Bmp => ".bmp",
        _ => ".jpg",
    };
}
