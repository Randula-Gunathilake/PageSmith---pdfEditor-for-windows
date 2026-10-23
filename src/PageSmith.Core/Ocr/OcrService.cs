using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows;
using PageSmith.Core.Rendering;
using PageSmith.Core.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PageSmith.Core.Ocr;

public sealed record OcrLanguage(string Tag, string DisplayName);

/// <summary>Words recognized on one page, in display space (points), plus the detected text skew.</summary>
public sealed record OcrPage(int PageIndex, double Width, double Height, IReadOnlyList<TextWord> Words, IReadOnlyList<TextLineInfo> Lines, double? TextAngle)
{
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
}

/// <summary>On-device text recognition with the OCR engine built into Windows.</summary>
public static class OcrService
{
    /// <summary>Languages with an OCR pack installed in Windows.</summary>
    public static IReadOnlyList<OcrLanguage> AvailableLanguages() =>
        OcrEngine.AvailableRecognizerLanguages.Select(l => new OcrLanguage(l.LanguageTag, l.DisplayName)).ToList();

    public static bool IsAvailable => OcrEngine.AvailableRecognizerLanguages.Count > 0;

    public static uint MaxImageDimension => OcrEngine.MaxImageDimension;

    /// <summary>Creates an engine for the language, or for the user's profile languages when null.</summary>
    public static OcrEngine CreateEngine(string? languageTag)
    {
        OcrEngine? engine = null;
        if (!string.IsNullOrEmpty(languageTag))
        {
            var language = new Language(languageTag);
            if (OcrEngine.IsLanguageSupported(language)) engine = OcrEngine.TryCreateFromLanguage(language);
        }
        engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine == null && OcrEngine.AvailableRecognizerLanguages.Count > 0)
            engine = OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);
        return engine ?? throw new UserFacingException(
            "No OCR language is installed in Windows. Add one in Settings › Time & language › Language & region (choose a language and install \"Optical character recognition\").");
    }

    /// <summary>Recognizes text in an encoded image (PNG/JPEG/BMP…). Coordinates are returned in pixels.</summary>
    public static async Task<OcrResult> RecognizeImageAsync(OcrEngine engine, byte[] encodedImage)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(encodedImage.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var width = decoder.PixelWidth;
        var height = decoder.PixelHeight;
        var max = OcrEngine.MaxImageDimension;
        SoftwareBitmap bitmap;
        if (width > max || height > max)
        {
            var scale = Math.Min((double)max / width, (double)max / height);
            var transform = new BitmapTransform
            {
                ScaledWidth = (uint)(width * scale),
                ScaledHeight = (uint)(height * scale),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        }
        else
        {
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        using (bitmap)
        {
            return await engine.RecognizeAsync(bitmap);
        }
    }

    /// <summary>Renders a PDF page and recognizes its text; word boxes are converted to display points.</summary>
    public static async Task<OcrPage> RecognizePageAsync(OcrEngine engine, PdfRenderer renderer, int pageIndex, double dpi = 300, CancellationToken ct = default)
    {
        var size = renderer.GetPageSize(pageIndex);
        var max = OcrEngine.MaxImageDimension;
        var longest = Math.Max(size.Width, size.Height) * dpi / 72.0;
        if (longest > max) dpi *= max / longest;
        var png = await renderer.RenderPngAsync(pageIndex, dpi, true, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var result = await RecognizeImageAsync(engine, png).ConfigureAwait(false);
        return ToPage(result, pageIndex, size.Width, size.Height, 72.0 / dpi);
    }

    /// <summary>Converts an OCR result in pixels to display-space words and lines.</summary>
    public static OcrPage ToPage(OcrResult result, int pageIndex, double width, double height, double pointsPerPixel)
    {
        var words = new List<TextWord>();
        var lines = new List<TextLineInfo>();
        foreach (var line in result.Lines)
        {
            var lineWords = new List<TextWord>();
            foreach (var w in line.Words)
            {
                var r = w.BoundingRect;
                var bounds = new Rect(r.X * pointsPerPixel, r.Y * pointsPerPixel, r.Width * pointsPerPixel, r.Height * pointsPerPixel);
                var word = new TextWord(w.Text, bounds, Math.Max(1, bounds.Height), "OCR", false, false, 0);
                lineWords.Add(word);
                words.Add(word);
            }
            if (lineWords.Count > 0)
                lines.Add(new TextLineInfo(line.Text, lineWords.Select(x => x.Bounds).Aggregate(Rect.Union), lineWords));
        }
        return new OcrPage(pageIndex, width, height, words, lines, result.TextAngle);
    }

    /// <summary>A font with glyphs for the language, used for the invisible text layer.</summary>
    public static string FontFor(string? languageTag)
    {
        var lang = (languageTag ?? "").ToLowerInvariant();
        if (lang.StartsWith("zh")) return "Microsoft YaHei";
        if (lang.StartsWith("ja")) return "Yu Gothic";
        if (lang.StartsWith("ko")) return "Malgun Gothic";
        if (lang.StartsWith("th")) return "Leelawadee UI";
        if (lang is var l && (l.StartsWith("hi") || l.StartsWith("mr") || l.StartsWith("bn") || l.StartsWith("ta") || l.StartsWith("te"))) return "Nirmala UI";
        if (lang.StartsWith("am")) return "Ebrima";
        return "Arial";
    }
}
