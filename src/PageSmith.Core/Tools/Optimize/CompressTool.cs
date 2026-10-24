using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Filters;

namespace PageSmith.Core.Tools.Optimize;

public enum CompressionLevel
{
    /// <summary>Less quality, high compression.</summary>
    Extreme,

    /// <summary>Good quality, good compression.</summary>
    Recommended,

    /// <summary>High quality, less compression.</summary>
    Low,
}

public sealed record CompressOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }
    public CompressionLevel Level { get; init; } = CompressionLevel.Recommended;

    /// <summary>Overrides the level's target image resolution (DPI).</summary>
    public int? TargetDpi { get; init; }

    /// <summary>Overrides the level's JPEG quality (1-100).</summary>
    public int? JpegQuality { get; init; }
}

/// <summary>
/// Reduces PDF size: downsamples and re-encodes images to their displayed resolution, recompresses
/// streams with maximum Flate compression, drops private application data and unused objects.
/// </summary>
public static class CompressTool
{
    public sealed record LevelSettings(int TargetDpi, int JpegQuality, int MaxPixels);

    public static LevelSettings SettingsFor(CompressionLevel level) => level switch
    {
        CompressionLevel.Extreme => new(96, 40, 1400),
        CompressionLevel.Low => new(220, 82, 4000),
        _ => new(144, 65, 2400),
    };

    public static Task<ToolResult> RunAsync(CompressOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Run(options, progress, ct), ct);

    public static ToolResult Run(CompressOptions options, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var scope = new ProgressScope(progress);
        var level = SettingsFor(options.Level);
        var settings = level with
        {
            TargetDpi = options.TargetDpi ?? level.TargetDpi,
            JpegQuality = options.JpegQuality ?? level.JpegQuality,
        };
        var inputBytes = new FileInfo(options.Input.Path).Length;
        var result = new ToolResult { InputBytes = inputBytes };

        scope.Report(0, "Reading document");
        using var doc = PdfDocs.Open(options.Input);
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        doc.Options.CompressContentStreams = true;
        doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Never;

        scope.Report(0.05, "Measuring images");
        var placements = ImagePlacement.Measure(doc, ct);
        var objects = doc.Internals.GetAllObjects();
        var softMasks = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var obj in objects)
        {
            if (obj is PdfDictionary d && PdfImageCodec.IsImage(d) && d.Elements.GetDictionary("/SMask") is { } sm)
                softMasks.Add(sm);
        }

        var images = objects.OfType<PdfDictionary>().Where(PdfImageCodec.IsImage).ToList();
        int recompressed = 0;
        var imageScope = scope.Slice(0.1, 0.85);
        for (var i = 0; i < images.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            imageScope.Report(i, images.Count, $"Optimizing image {i + 1} of {images.Count}");
            if (softMasks.Contains(images[i])) continue;
            placements.TryGetValue(images[i], out var drawn);
            if (TryRecompress(images[i], drawn, settings)) recompressed++;
        }

        scope.Report(0.86, "Compressing streams");
        foreach (var obj in objects)
        {
            if (obj is not PdfDictionary d || d.Stream == null) continue;
            if (d.Elements.GetName("/Type") == "/Metadata") continue;
            if (!d.Stream.IsFiltered() && d.Stream.Length > 64)
            {
                try { d.Stream.Zip(); } catch { /* leave stream as is */ }
            }
        }
        StripPrivateData(doc);

        scope.Report(0.92, "Saving");
        var temp = options.OutputPath + ".tmp";
        PdfDocs.Save(doc, temp);
        var outputBytes = new FileInfo(temp).Length;
        if (outputBytes >= inputBytes)
        {
            // Already optimal: keep the original rather than produce a bigger file.
            File.Delete(temp);
            File.Copy(options.Input.Path, options.OutputPath, overwrite: true);
            result.Warnings.Add("This PDF is already well optimized; PageSmith could not make it smaller.");
        }
        else
        {
            File.Move(temp, options.OutputPath, overwrite: true);
        }

        result.AddOutput(options.OutputPath);
        var saved = 1.0 - (double)result.OutputBytes / Math.Max(1, inputBytes);
        result.Summary = saved > 0.005
            ? $"Your PDF is now {saved:P0} smaller ({Format(inputBytes)} → {Format(result.OutputBytes)}); {recompressed} image{(recompressed == 1 ? "" : "s")} optimized."
            : $"Size unchanged ({Format(inputBytes)}).";
        scope.Report(1);
        return result;
    }

    /// <summary>Downsamples an image to the target resolution and re-encodes it when that makes it smaller.</summary>
    internal static bool TryRecompress(PdfDictionary image, (double Width, double Height) drawn, LevelSettings settings)
    {
        var info = PdfImageCodec.Inspect(image);
        if (info == null || info.HasColorKeyMask) return false;
        if (info.Width * info.Height < 64 * 64) return false;

        // Resolution the image is displayed at; fall back to a pixel cap when it is never drawn on a page.
        double scale = 1;
        if (drawn.Width > 0.5 && drawn.Height > 0.5)
        {
            var dpiX = info.Width / (drawn.Width / 72.0);
            var dpiY = info.Height / (drawn.Height / 72.0);
            var dpi = Math.Min(dpiX, dpiY);
            if (dpi > settings.TargetDpi * 1.15) scale = settings.TargetDpi / dpi;
        }
        var longest = Math.Max(info.Width, info.Height) * scale;
        if (longest > settings.MaxPixels) scale *= settings.MaxPixels / longest;

        var bitmap = PdfImageCodec.Decode(info);
        if (bitmap == null) return false;

        var newWidth = Math.Max(1, (int)Math.Round(info.Width * scale));
        var newHeight = Math.Max(1, (int)Math.Round(info.Height * scale));
        var resized = scale < 0.98 ? ImageIO.Resize(bitmap, newWidth, newHeight) : bitmap;
        if (scale >= 0.98)
        {
            newWidth = info.Width;
            newHeight = info.Height;
        }

        var oldLength = image.Stream!.Value.Length;
        var photographic = info.IsJpeg || PdfImageCodec.EstimateColorCount(resized) >= 256;
        byte[] encoded;
        string filter;
        if (photographic)
        {
            var source = info.Components == 1
                ? new System.Windows.Media.Imaging.FormatConvertedBitmap(resized, System.Windows.Media.PixelFormats.Gray8, null, 0)
                : resized;
            encoded = ImageIO.EncodeJpeg(source, settings.JpegQuality);
            filter = "/DCTDecode";
        }
        else
        {
            // Graphics with few colours stay lossless; only resampling can help them.
            if (scale >= 0.98) return false;
            var flate = Filtering.Encode(PdfImageCodec.ToRawSamples(resized, info.Components), "/FlateDecode");
            if (flate == null) return false;
            encoded = flate;
            filter = "/FlateDecode";
        }

        var shrankEnough = encoded.Length < oldLength * 0.9;
        if (!shrankEnough) return false;

        image.Stream!.Value = encoded;
        image.Elements.SetName("/Filter", filter);
        image.Elements.Remove("/DecodeParms");
        image.Elements.SetInteger("/Width", newWidth);
        image.Elements.SetInteger("/Height", newHeight);
        image.Elements.SetInteger("/BitsPerComponent", 8);
        image.Elements.SetInteger("/Length", encoded.Length);
        return true;
    }

    /// <summary>Removes data viewers never display: page thumbnails and application private data.</summary>
    private static void StripPrivateData(PdfDocument doc)
    {
        foreach (var page in doc.Pages)
        {
            page.Elements.Remove("/Thumb");
            page.Elements.Remove("/PieceInfo");
        }
        doc.Internals.Catalog.Elements.Remove("/PieceInfo");
    }

    public static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.##} MB",
        >= 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B",
    };
}
