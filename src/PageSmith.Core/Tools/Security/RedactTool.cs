using System.Text.RegularExpressions;
using System.Windows;
using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;
using PageSmith.Core.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PageSmith.Core.Tools.Security;

/// <summary>An area to black out (display space).</summary>
public sealed record RedactionMark(int PageIndex, Rect Bounds, string? MatchedText = null, string? Reason = null);

public enum RedactionPattern
{
    Email,
    Phone,
    CreditCard,
    SocialSecurityNumber,
    Iban,
    IpAddress,
    Url,
    Date,
}

public sealed record RedactOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }
    public required IReadOnlyList<RedactionMark> Marks { get; init; }

    public string FillColor { get; init; } = "#000000";

    /// <summary>Optional label printed in each box, e.g. "REDACTED".</summary>
    public string? OverlayText { get; init; }

    /// <summary>Also clears the document properties (title, author, subject, keywords).</summary>
    public bool RemoveMetadata { get; init; } = true;

    /// <summary>Resolution used to rebuild redacted pages.</summary>
    public double Dpi { get; init; } = 200;
}

/// <summary>
/// Redact PDF. Pages with redactions are rebuilt from a rendered image with the boxes burnt in, so the
/// hidden text, images and vector content are really gone; the remaining text is re-added invisibly
/// so the page stays searchable. Pages without redactions are left untouched.
/// </summary>
public static partial class RedactTool
{
    public static async Task<ToolResult> RunAsync(RedactOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        if (options.Marks.Count == 0) throw new UserFacingException("Mark at least one area or search result to redact.");
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
        var byPage = options.Marks.GroupBy(m => m.PageIndex).ToDictionary(g => g.Key, g => g.Select(m => m.Bounds).ToList());
        var texts = TextExtractor.ReadPages(options.Input, byPage.Keys, ct).ToDictionary(p => p.Index);

        var images = new Dictionary<int, byte[]>();
        using (var renderer = await PdfRenderer.OpenAsync(options.Input.Path, options.Input.Password, ct).ConfigureAwait(false))
        {
            var n = 0;
            foreach (var (index, boxes) in byPage)
            {
                ct.ThrowIfCancellationRequested();
                scope.Slice(0, 0.7).Report(n++, byPage.Count, $"Redacting page {index + 1}");
                var bmp = await renderer.RenderAsync(index, options.Dpi, ct).ConfigureAwait(false);
                var px = ImageIO.ToPixels(bmp);
                var scale = options.Dpi / 72.0;
                var fill = TextStyle.ParseColor(options.FillColor);
                foreach (var b in boxes) FillRect(px, b, scale, fill);
                images[index] = ImageIO.EncodePng(ImageIO.FromPixels(px, options.Dpi));
            }
        }

        scope.Report(0.75, "Rebuilding pages");
        await Task.Run(() =>
        {
            using var doc = PdfDocs.Open(options.Input);
            foreach (var (index, png) in images)
            {
                var page = doc.Pages[index];
                var boxes = byPage[index];
                var keep = texts.TryGetValue(index, out var t) ? t.Words.Where(w => !boxes.Any(b => b.IntersectsWith(Inflate(w.Bounds, -0.5)))).ToList() : [];
                ReplaceWithImage(page, png, keep);
                if (!string.IsNullOrWhiteSpace(options.OverlayText))
                {
                    using var canvas = PageCanvas.Open(page);
                    foreach (var b in boxes)
                    {
                        var size = Math.Clamp(b.Height * 0.6, 4, 14);
                        var font = new XFont("Arial", size, XFontStyleEx.Bold);
                        canvas.Graphics.DrawString(options.OverlayText, font, XBrushes.White, new XRect(b.X, b.Y, b.Width, b.Height), XStringFormats.Center);
                    }
                }
                RemoveAnnotationsIn(page, boxes);
            }
            if (options.RemoveMetadata)
            {
                foreach (var key in new[] { "/Title", "/Author", "/Subject", "/Keywords", "/Creator" }) doc.Info.Elements.Remove(key);
                doc.Internals.Catalog.Elements.Remove("/Metadata");
            }
            PdfDocs.Save(doc, options.OutputPath);
        }, ct).ConfigureAwait(false);

        result.AddOutput(options.OutputPath);
        result.Summary = $"Permanently redacted {options.Marks.Count} area{(options.Marks.Count == 1 ? "" : "s")} on {byPage.Count} page{(byPage.Count == 1 ? "" : "s")}.";
        scope.Report(1);
        return result;
    }

    private static Rect Inflate(Rect r, double d)
    {
        r.Inflate(d, d);
        return r.IsEmpty ? new Rect(r.X, r.Y, 0, 0) : r;
    }

    private static void FillRect(PixelBuffer px, Rect b, double scale, XColor color)
    {
        var x0 = Math.Clamp((int)Math.Floor(b.Left * scale), 0, px.Width);
        var x1 = Math.Clamp((int)Math.Ceiling(b.Right * scale), 0, px.Width);
        var y0 = Math.Clamp((int)Math.Floor(b.Top * scale), 0, px.Height);
        var y1 = Math.Clamp((int)Math.Ceiling(b.Bottom * scale), 0, px.Height);
        for (var y = y0; y < y1; y++)
        for (var x = x0; x < x1; x++)
        {
            var i = (y * px.Width + x) * 4;
            px.Pixels[i] = color.B;
            px.Pixels[i + 1] = color.G;
            px.Pixels[i + 2] = color.R;
            px.Pixels[i + 3] = 255;
        }
    }

    /// <summary>Replaces a page's content with an image and an invisible layer of the words to keep.</summary>
    internal static void ReplaceWithImage(PdfPage page, byte[] image, IReadOnlyList<TextWord> keepWords)
    {
        page.Elements["/Resources"] = new PdfResources(page.Owner);
        page.Elements.Remove("/Group");
        page.Contents.Elements.Clear();
        using (var canvas = PageCanvas.Open(page))
        using (var ms = new MemoryStream(image))
        using (var x = XImage.FromStream(ms))
        {
            canvas.Graphics.DrawImage(x, 0, 0, canvas.Width, canvas.Height);
        }
        if (keepWords.Count > 0) InvisibleText.AddWords(page, keepWords);
    }

    private static void RemoveAnnotationsIn(PdfPage page, List<Rect> boxes)
    {
        if (!page.HasAnnotations) return;
        var g = PageGeometry.From(page);
        for (var i = page.Annotations.Count - 1; i >= 0; i--)
        {
            var a = page.Annotations[i];
            var r = a.Elements.GetArray("/Rect");
            if (r is not { Elements.Count: 4 }) continue;
            var display = g.ToDisplay(new Rect(new Point(r.Elements.GetReal(0), r.Elements.GetReal(1)), new Point(r.Elements.GetReal(2), r.Elements.GetReal(3))));
            if (boxes.Any(b => b.IntersectsWith(display))) page.Annotations.Remove(a);
        }
    }

    // ---------------------------------------------------------------- search

    /// <summary>Finds every occurrence of <paramref name="query"/> (or a regular expression) and returns marks covering it.</summary>
    public static IReadOnlyList<RedactionMark> FindText(InputFile input, string query, bool matchCase = false, bool wholeWord = false, bool regex = false, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var pattern = regex ? query : Regex.Escape(query.Trim()).Replace("\\ ", "\\s+");
        if (wholeWord) pattern = $@"(?<!\w){pattern}(?!\w)";
        var rx = new Regex(pattern, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        return Find(input, rx, null, query, ct);
    }

    public static IReadOnlyList<RedactionMark> FindPatterns(InputFile input, IEnumerable<RedactionPattern> patterns, CancellationToken ct = default)
    {
        var marks = new List<RedactionMark>();
        foreach (var p in patterns.Distinct())
            marks.AddRange(Find(input, PatternRegex(p), p == RedactionPattern.CreditCard ? IsLuhnValid : null, Describe(p), ct));
        return marks;
    }

    public static string Describe(RedactionPattern p) => p switch
    {
        RedactionPattern.Email => "Email address",
        RedactionPattern.Phone => "Phone number",
        RedactionPattern.CreditCard => "Credit card number",
        RedactionPattern.SocialSecurityNumber => "Social security number",
        RedactionPattern.Iban => "Bank account (IBAN)",
        RedactionPattern.IpAddress => "IP address",
        RedactionPattern.Url => "Web address",
        _ => "Date",
    };

    public static Regex PatternRegex(RedactionPattern p) => p switch
    {
        RedactionPattern.Email => EmailRx(),
        RedactionPattern.Phone => PhoneRx(),
        RedactionPattern.CreditCard => CardRx(),
        RedactionPattern.SocialSecurityNumber => SsnRx(),
        RedactionPattern.Iban => IbanRx(),
        RedactionPattern.IpAddress => IpRx(),
        RedactionPattern.Url => UrlRx(),
        _ => DateRx(),
    };

    private static IReadOnlyList<RedactionMark> Find(InputFile input, Regex rx, Func<string, bool>? validate, string reason, CancellationToken ct)
    {
        var marks = new List<RedactionMark>();
        foreach (var page in TextExtractor.ReadPages(input, null, ct))
        {
            // Page text with each character mapped back to its word.
            var sb = new StringBuilder();
            var owners = new List<TextWord?>();
            var starts = new List<int>();
            foreach (var line in TextExtractor.GetLines(page.Words))
            {
                foreach (var word in line.Words)
                {
                    if (sb.Length > 0 && owners[^1] != null)
                    {
                        sb.Append(' ');
                        owners.Add(null);
                        starts.Add(-1);
                    }
                    var start = sb.Length;
                    sb.Append(word.Text);
                    for (var i = 0; i < word.Text.Length; i++)
                    {
                        owners.Add(word);
                        starts.Add(start);
                    }
                }
                sb.Append('\n');
                owners.Add(null);
                starts.Add(-1);
            }
            var text = sb.ToString();
            foreach (Match m in rx.Matches(text))
            {
                if (m.Length == 0 || validate?.Invoke(m.Value) == false) continue;
                // One box per line the match spans; partial words are covered proportionally.
                var byLine = new Dictionary<double, Rect>();
                for (var i = m.Index; i < m.Index + m.Length; i++)
                {
                    var w = owners[i];
                    if (w == null) continue;
                    var offset = i - starts[i];
                    var charWidth = w.Bounds.Width / Math.Max(1, w.Text.Length);
                    var r = new Rect(w.Bounds.Left + charWidth * offset, w.Bounds.Top, charWidth, w.Bounds.Height);
                    var key = Math.Round(w.Bounds.Top);
                    byLine[key] = byLine.TryGetValue(key, out var existing) ? Rect.Union(existing, r) : r;
                }
                foreach (var r in byLine.Values)
                {
                    var padded = r;
                    padded.Inflate(1, 1);
                    marks.Add(new RedactionMark(page.Index, padded, m.Value, reason));
                }
            }
        }
        return marks;
    }

    /// <summary>Luhn checksum, used to keep only real card numbers.</summary>
    public static bool IsLuhnValid(string number)
    {
        var digits = number.Where(char.IsDigit).Select(c => c - '0').ToList();
        if (digits.Count is < 13 or > 19) return false;
        var sum = 0;
        for (var i = 0; i < digits.Count; i++)
        {
            var d = digits[digits.Count - 1 - i];
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRx();

    [GeneratedRegex(@"(?<![\w+])(?:\+?\d{1,3}[\s.\-]?)?(?:\(\d{2,4}\)[\s.\-]?)?\d{2,4}[\s.\-]?\d{3,4}[\s.\-]?\d{3,4}(?!\w)")]
    private static partial Regex PhoneRx();

    [GeneratedRegex(@"(?<!\d)(?:\d[ \-]?){12,18}\d(?!\d)")]
    private static partial Regex CardRx();

    [GeneratedRegex(@"(?<!\d)\d{3}-\d{2}-\d{4}(?!\d)")]
    private static partial Regex SsnRx();

    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?:\s?[A-Z0-9]{4}){2,7}(?:\s?[A-Z0-9]{1,3})?\b")]
    private static partial Regex IbanRx();

    [GeneratedRegex(@"(?<![\d.])(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)(?![\d.])")]
    private static partial Regex IpRx();

    [GeneratedRegex(@"(?:https?://|www\.)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRx();

    [GeneratedRegex(@"(?<!\d)(?:\d{1,2}[/.\-]\d{1,2}[/.\-]\d{2,4}|\d{4}-\d{2}-\d{2}|\d{1,2}\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{2,4}|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.?\s+\d{1,2},?\s+\d{2,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex DateRx();
}
