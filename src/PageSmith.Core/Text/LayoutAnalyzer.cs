using System.Text.RegularExpressions;
using System.Windows;
using PageSmith.Core.Ocr;
using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;

namespace PageSmith.Core.Text;

public enum ListKind
{
    None,
    Bullet,
    Numbered,
}

public abstract record LayoutElement(Rect Bounds);

public sealed record LayoutParagraph(
    Rect Bounds,
    IReadOnlyList<TextLineInfo> Lines,
    string Text,
    double FontSize,
    bool Bold,
    bool Italic,
    TextAlign Align,
    int HeadingLevel,
    ListKind List,
    string FontName,
    uint Rgb) : LayoutElement(Bounds);

public sealed record LayoutTable(Rect Bounds, IReadOnlyList<IReadOnlyList<string>> Rows) : LayoutElement(Bounds)
{
    public int ColumnCount => Rows.Count == 0 ? 0 : Rows.Max(r => r.Count);
}

public sealed record LayoutImage(Rect Bounds, byte[] Data, bool IsJpeg, int PixelWidth, int PixelHeight) : LayoutElement(Bounds);

public sealed record LayoutPage(int Index, double Width, double Height, IReadOnlyList<LayoutElement> Elements, bool FromOcr);

public sealed record LayoutOptions
{
    /// <summary>Recognize text on pages without a text layer.</summary>
    public bool Ocr { get; init; }
    public string? OcrLanguage { get; init; }
    public bool IncludeImages { get; init; } = true;
    public bool DetectTables { get; init; } = true;
    public string? Pages { get; init; }
}

/// <summary>
/// Turns positioned words into a document structure: paragraphs (with headings, lists, alignment
/// and dominant style), tables and images, in reading order. Used by the PDF to Office converters.
/// </summary>
public static partial class LayoutAnalyzer
{
    public static async Task<IReadOnlyList<LayoutPage>> AnalyzeAsync(InputFile input, LayoutOptions options, ProgressScope? scope = null, CancellationToken ct = default)
    {
        using var pig = TextExtractor.Open(input);
        var pages = PageRange.Parse(options.Pages, pig.NumberOfPages);
        PdfRenderer? renderer = null;
        Windows.Media.Ocr.OcrEngine? engine = null;
        var result = new List<LayoutPage>();
        try
        {
            for (var i = 0; i < pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                scope?.Report(i, pages.Count, $"Reading page {pages[i] + 1}");
                var page = pig.GetPage(pages[i] + 1);
                var text = TextExtractor.ReadPage(page, pages[i]);
                IReadOnlyList<TextWord> words = text.Words;
                var fromOcr = false;
                if (!text.HasText && options.Ocr)
                {
                    renderer ??= await PdfRenderer.OpenAsync(input.Path, input.Password, ct).ConfigureAwait(false);
                    engine ??= OcrService.CreateEngine(options.OcrLanguage);
                    var ocr = await OcrService.RecognizePageAsync(engine, renderer, pages[i], 300, ct).ConfigureAwait(false);
                    words = ocr.Words;
                    fromOcr = true;
                }

                var elements = new List<LayoutElement>();
                elements.AddRange(BuildText(words, text.Width, text.Height, options.DetectTables));
                if (options.IncludeImages && !fromOcr) elements.AddRange(ExtractImages(page, text.Geometry));
                // Scanned page without OCR: keep the page picture so content is not lost.
                if (fromOcr == false && !text.HasText && options.IncludeImages && elements.Count == 0)
                {
                    renderer ??= await PdfRenderer.OpenAsync(input.Path, input.Password, ct).ConfigureAwait(false);
                    var png = await renderer.RenderPngAsync(pages[i], 150, true, ct).ConfigureAwait(false);
                    var bmp = Imaging.ImageIO.Decode(png);
                    elements.Add(new LayoutImage(new Rect(0, 0, text.Width, text.Height), png, false, bmp.PixelWidth, bmp.PixelHeight));
                }
                result.Add(new LayoutPage(pages[i], text.Width, text.Height, Order(elements), fromOcr));
            }
        }
        finally
        {
            renderer?.Dispose();
        }
        return result;
    }

    /// <summary>Most common font size, weighted by characters: the size of body text.</summary>
    public static double BodyFontSize(IEnumerable<TextWord> words)
    {
        var hist = new Dictionary<double, int>();
        foreach (var w in words)
        {
            var key = Math.Round(w.FontSize * 2) / 2;
            hist[key] = hist.GetValueOrDefault(key) + w.Text.Length;
        }
        return hist.Count == 0 ? 11 : hist.MaxBy(kv => kv.Value).Key;
    }

    private static IEnumerable<LayoutElement> BuildText(IReadOnlyList<TextWord> words, double pageWidth, double pageHeight, bool detectTables)
    {
        if (words.Count == 0) yield break;
        var body = BodyFontSize(words);
        var lines = TextExtractor.GetLines(words);

        var tableLines = new HashSet<TextLineInfo>(ReferenceEqualityComparer.Instance);
        if (detectTables)
        {
            foreach (var table in DetectTables(lines, body))
            {
                foreach (var l in table.Lines) tableLines.Add(l);
                yield return table.Table;
            }
        }

        var remaining = lines.Where(l => !tableLines.Contains(l)).ToList();
        var page = new PageText { Index = 0, Geometry = new PageGeometry(new Rect(0, 0, pageWidth, pageHeight), 0, pageHeight), Words = remaining.SelectMany(l => l.Words).ToList(), ImageCount = 0 };
        foreach (var block in TextExtractor.GetBlocks(page))
            foreach (var paragraph in SplitParagraphs(block, body, pageWidth))
                yield return paragraph;
    }

    /// <summary>Splits a block into paragraphs at list items and at lines that start indented after a short line.</summary>
    private static IEnumerable<LayoutParagraph> SplitParagraphs(TextBlockInfo block, double body, double pageWidth)
    {
        var current = new List<TextLineInfo>();
        foreach (var line in block.Lines)
        {
            var startsList = ListMarker().IsMatch(line.Text);
            var prev = current.LastOrDefault();
            var prevShort = prev != null && prev.Bounds.Right < block.Bounds.Right - Math.Max(30, body * 4);
            if (current.Count > 0 && (startsList || prevShort && !ListMarker().IsMatch(current[0].Text)))
            {
                yield return MakeParagraph(current, body, pageWidth, block.Bounds);
                current = [];
            }
            current.Add(line);
        }
        if (current.Count > 0) yield return MakeParagraph(current, body, pageWidth, block.Bounds);
    }

    private static LayoutParagraph MakeParagraph(List<TextLineInfo> lines, double body, double pageWidth, Rect blockBounds)
    {
        var bounds = lines.Select(l => l.Bounds).Aggregate(Rect.Union);
        var words = lines.SelectMany(l => l.Words).ToList();
        var chars = Math.Max(1, words.Sum(w => w.Text.Length));
        var size = words.Sum(w => w.FontSize * w.Text.Length) / chars;
        var bold = words.Where(w => w.Bold).Sum(w => w.Text.Length) * 2 > chars;
        var italic = words.Where(w => w.Italic).Sum(w => w.Text.Length) * 2 > chars;
        var font = words.GroupBy(w => w.FontName).MaxBy(g => g.Sum(w => w.Text.Length))?.Key ?? "";
        var rgb = words.GroupBy(w => w.Rgb).MaxBy(g => g.Sum(w => w.Text.Length))?.Key ?? 0;

        var text = JoinLines(lines);
        var list = ListKind.None;
        var m = ListMarker().Match(text);
        if (m.Success)
        {
            list = char.IsDigit(m.Value[0]) || char.IsLetter(m.Value[0]) ? ListKind.Numbered : ListKind.Bullet;
            text = text[m.Length..].TrimStart();
        }

        var heading = 0;
        if (lines.Count <= 3 && text.Length < 200 && list == ListKind.None)
        {
            if (size >= body * 1.6) heading = 1;
            else if (size >= body * 1.3) heading = 2;
            else if (size >= body * 1.12 || (bold && lines.Count == 1 && text.Length < 80 && !text.TrimEnd().EndsWith('.'))) heading = 3;
        }

        var leftGap = bounds.Left;
        var rightGap = pageWidth - bounds.Right;
        var align = TextAlign.Left;
        if (lines.Count >= 2 && lines.Take(lines.Count - 1).All(l => Math.Abs(l.Bounds.Right - blockBounds.Right) < 3 && Math.Abs(l.Bounds.Left - blockBounds.Left) < body * 2.5))
            align = TextAlign.Justify;
        else if (Math.Abs(leftGap - rightGap) < pageWidth * 0.04 && bounds.Width < pageWidth * 0.7 && lines.All(l => Math.Abs((l.Bounds.Left + l.Bounds.Right) / 2 - pageWidth / 2) < pageWidth * 0.05))
            align = TextAlign.Center;
        else if (rightGap < leftGap * 0.5 && bounds.Width < pageWidth * 0.5 && lines.All(l => Math.Abs(l.Bounds.Right - bounds.Right) < 3))
            align = TextAlign.Right;

        return new LayoutParagraph(bounds, lines, text, size, bold, italic, align, heading, list, font, rgb);
    }

    /// <summary>Joins wrapped lines into flowing text, removing end-of-line hyphenation.</summary>
    public static string JoinLines(IEnumerable<TextLineInfo> lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            var t = line.Text.Trim();
            if (sb.Length > 0)
            {
                if (sb[^1] == '-' && sb.Length > 1 && char.IsLetter(sb[^2]) && t.Length > 0 && char.IsLower(t[0])) sb.Length--;
                else sb.Append(' ');
            }
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>Splits a line into cells wherever the gap between words is wide.</summary>
    public static List<(string Text, Rect Bounds)> SplitCells(TextLineInfo line, double body)
    {
        var cells = new List<(string, Rect)>();
        var current = new List<TextWord>();
        foreach (var w in line.Words)
        {
            if (current.Count > 0)
            {
                var gap = w.Bounds.Left - current[^1].Bounds.Right;
                if (gap > Math.Max(body * 1.2, w.FontSize * 1.2))
                {
                    cells.Add((string.Join(" ", current.Select(x => x.Text)), current.Select(x => x.Bounds).Aggregate(Rect.Union)));
                    current = [];
                }
            }
            current.Add(w);
        }
        if (current.Count > 0) cells.Add((string.Join(" ", current.Select(x => x.Text)), current.Select(x => x.Bounds).Aggregate(Rect.Union)));
        return cells;
    }

    /// <summary>
    /// Finds runs of consecutive lines with two or more cells whose column boundaries line up.
    /// Returns each table and the lines it consumed.
    /// </summary>
    public static IEnumerable<(LayoutTable Table, IReadOnlyList<TextLineInfo> Lines)> DetectTables(IReadOnlyList<TextLineInfo> lines, double body)
    {
        var split = lines.Select(l => SplitCells(l, body)).ToList();
        var i = 0;
        while (i < lines.Count)
        {
            if (split[i].Count < 2)
            {
                i++;
                continue;
            }
            var j = i;
            while (j + 1 < lines.Count && split[j + 1].Count >= 2 && lines[j + 1].Bounds.Top - lines[j].Bounds.Bottom < body * 2.5 && Aligned(split[j], split[j + 1], body))
                j++;
            var count = j - i + 1;
            if (count >= 2 && split.Skip(i).Take(count).Sum(c => c.Count) >= 6)
            {
                var columns = ColumnStarts(split.Skip(i).Take(count), body);
                var rows = new List<IReadOnlyList<string>>();
                for (var k = i; k <= j; k++)
                {
                    var row = new string[columns.Count];
                    foreach (var (text, bounds) in split[k])
                    {
                        var col = Nearest(columns, bounds.Left);
                        row[col] = string.IsNullOrEmpty(row[col]) ? text : row[col] + " " + text;
                    }
                    rows.Add(row.Select(c => c ?? "").ToList());
                }
                var used = lines.Skip(i).Take(count).ToList();
                yield return (new LayoutTable(used.Select(l => l.Bounds).Aggregate(Rect.Union), rows), used);
                i = j + 1;
            }
            else
            {
                i++;
            }
        }
    }

    private static bool Aligned(List<(string Text, Rect Bounds)> a, List<(string Text, Rect Bounds)> b, double body)
    {
        var tolerance = Math.Max(6, body);
        var matches = b.Count(cb => a.Any(ca => Math.Abs(ca.Bounds.Left - cb.Bounds.Left) < tolerance || Math.Abs(ca.Bounds.Right - cb.Bounds.Right) < tolerance));
        return matches >= Math.Max(2, Math.Min(a.Count, b.Count) * 0.6);
    }

    /// <summary>Clusters the left edges of cells into column start positions.</summary>
    public static List<double> ColumnStarts(IEnumerable<List<(string Text, Rect Bounds)>> rows, double body)
    {
        var lefts = rows.SelectMany(r => r.Select(c => c.Bounds.Left)).Order().ToList();
        var columns = new List<double>();
        var tolerance = Math.Max(6, body * 1.2);
        foreach (var x in lefts)
        {
            if (columns.Count == 0 || x - columns[^1] > tolerance) columns.Add(x);
        }
        return columns;
    }

    private static int Nearest(List<double> columns, double x)
    {
        var best = 0;
        for (var c = 0; c < columns.Count; c++)
            if (columns[c] <= x + 6) best = c;
        return best;
    }

    private static IEnumerable<LayoutImage> ExtractImages(UglyToad.PdfPig.Content.Page page, PageGeometry geometry)
    {
        IEnumerable<UglyToad.PdfPig.Content.IPdfImage> images;
        try
        {
            images = page.GetImages().ToList();
        }
        catch
        {
            yield break;
        }
        foreach (var image in images)
        {
            var b = image.Bounds;
            var bounds = geometry.ToDisplay(new Rect(new Point(b.Left, b.Bottom), new Point(b.Right, b.Top)));
            if (bounds.Width < 8 || bounds.Height < 8) continue;
            byte[]? data = null;
            var isJpeg = false;
            try
            {
                if (image.ImageDictionary.TryGet(UglyToad.PdfPig.Tokens.NameToken.Filter, out var f) && f is UglyToad.PdfPig.Tokens.NameToken { Data: "DCTDecode" })
                {
                    data = image.RawMemory.ToArray();
                    isJpeg = true;
                }
                else if (!image.TryGetPng(out data))
                {
                    data = null;
                }
            }
            catch
            {
                data = null;
            }
            if (data == null || data.Length == 0) continue;
            yield return new LayoutImage(bounds, data, isJpeg, image.WidthInSamples, image.HeightInSamples);
        }
    }

    /// <summary>
    /// Keeps paragraphs in their (column-aware) reading order and slots tables and images in
    /// before the first paragraph that starts below them.
    /// </summary>
    private static IReadOnlyList<LayoutElement> Order(List<LayoutElement> elements)
    {
        var flow = elements.Where(e => e is LayoutParagraph).ToList();
        foreach (var e in elements.Where(e => e is not LayoutParagraph).OrderBy(e => e.Bounds.Top))
        {
            var index = flow.FindIndex(p => p is LayoutParagraph && p.Bounds.Top > e.Bounds.Top - 1);
            if (index < 0) flow.Add(e);
            else flow.Insert(index, e);
        }
        return flow;
    }

    [GeneratedRegex(@"^\s*([•●○◦▪■□➢►\-–—*]|\(?\d{1,3}[.)]|\(?[a-zA-Z][.)](?=\s)|[ivxIVX]{1,4}[.)](?=\s))\s+")]
    private static partial Regex ListMarker();
}
