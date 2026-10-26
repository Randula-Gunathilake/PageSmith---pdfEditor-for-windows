using System.Windows;
using PageSmith.Core.Pdf;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PageSmith.Core.Text;

/// <summary>A word with its bounds in display space (see <see cref="PageGeometry"/>).</summary>
public sealed record TextWord(string Text, Rect Bounds, double FontSize, string FontName, bool Bold, bool Italic, uint Rgb)
{
    /// <summary>Display-space y of the text baseline, or NaN when unknown (e.g. OCR words).</summary>
    public double Baseline { get; init; } = double.NaN;
}

public sealed record TextLineInfo(string Text, Rect Bounds, IReadOnlyList<TextWord> Words)
{
    public double FontSize => Words.Count == 0 ? 0 : Words.Average(w => w.FontSize);
    public bool Bold => Words.Count > 0 && Words.Count(w => w.Bold) * 2 > Words.Count;
    public bool Italic => Words.Count > 0 && Words.Count(w => w.Italic) * 2 > Words.Count;
}

public sealed record TextBlockInfo(string Text, Rect Bounds, IReadOnlyList<TextLineInfo> Lines)
{
    public double FontSize => Lines.Count == 0 ? 0 : Lines.Average(l => l.FontSize);
}

/// <summary>The text of one page with positions in display space.</summary>
public sealed class PageText
{
    public required int Index { get; init; }
    public required PageGeometry Geometry { get; init; }
    public required IReadOnlyList<TextWord> Words { get; init; }
    public required int ImageCount { get; init; }
    public double Width => Geometry.Width;
    public double Height => Geometry.Height;
    public bool HasText => Words.Any(w => !string.IsNullOrWhiteSpace(w.Text));
}

/// <summary>Reads text and layout from PDFs with PdfPig.</summary>
public static class TextExtractor
{
    public static PigDocument Open(InputFile input) => Open(input, options => PigDocument.Open(input.Path, options));

    /// <summary>Opens a copy of the PDF held in memory, so the file itself is not kept open.</summary>
    public static PigDocument OpenInMemory(InputFile input) => Open(input, options => PigDocument.Open(File.ReadAllBytes(input.Path), options));

    private static PigDocument Open(InputFile input, Func<ParsingOptions, PigDocument> open)
    {
        var options = new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true };
        if (!string.IsNullOrEmpty(input.Password)) options.Password = input.Password;
        try
        {
            return open(options);
        }
        catch (Exception ex) when (ex.GetType().Name.Contains("Encrypt") || ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new PasswordRequiredException(input.Path, !string.IsNullOrEmpty(input.Password));
        }
        catch (Exception ex) when (ex is not PasswordRequiredException)
        {
            throw new UserFacingException($"The text of \"{input.Name}\" could not be read ({ex.Message}).", ex);
        }
    }

    /// <summary>Reads every page (or the given 0-based pages).</summary>
    public static IReadOnlyList<PageText> ReadPages(InputFile input, IEnumerable<int>? pages = null, CancellationToken ct = default)
    {
        using var doc = Open(input);
        var indices = pages?.ToList() ?? Enumerable.Range(0, doc.NumberOfPages).ToList();
        var result = new List<PageText>(indices.Count);
        foreach (var i in indices)
        {
            ct.ThrowIfCancellationRequested();
            result.Add(ReadPage(doc.GetPage(i + 1), i));
        }
        return result;
    }

    public static PageText ReadPage(Page page, int index)
    {
        var geometry = PageGeometry.From(page);
        var words = new List<TextWord>();
        IEnumerable<Word> pigWords;
        try
        {
            pigWords = page.GetWords(NearestNeighbourWordExtractor.Instance);
        }
        catch
        {
            pigWords = page.GetWords();
        }
        foreach (var w in pigWords)
        {
            if (string.IsNullOrWhiteSpace(w.Text)) continue;
            words.Add(ToWord(w, geometry));
        }
        int images;
        try { images = page.NumberOfImages; } catch { images = 0; }
        return new PageText { Index = index, Geometry = geometry, Words = words, ImageCount = images };
    }

    /// <summary>Plain text of the whole document in reading order, pages separated by blank lines.</summary>
    public static string GetPlainText(InputFile input, CancellationToken ct = default)
    {
        var pages = ReadPages(input, null, ct);
        return string.Join("\n\n", pages.Select(p => string.Join("\n\n", GetBlocks(p).Select(b => b.Text))));
    }

    /// <summary>Groups words into lines (by baseline) in display space, top to bottom.</summary>
    public static IReadOnlyList<TextLineInfo> GetLines(IReadOnlyList<TextWord> words)
    {
        var lines = new List<List<TextWord>>();
        static double Key(TextWord w) => double.IsNaN(w.Baseline) ? w.Bounds.Bottom : w.Baseline;
        foreach (var w in words.OrderBy(Key).ThenBy(w => w.Bounds.Left))
        {
            var target = lines.LastOrDefault(l =>
            {
                var last = l[^1];
                // Same baseline means same line, even when glyph boxes differ (underscores, sub/superscripts).
                if (!double.IsNaN(last.Baseline) && !double.IsNaN(w.Baseline))
                    return Math.Abs(last.Baseline - w.Baseline) <= Math.Max(1.5, 0.3 * Math.Min(last.FontSize, w.FontSize));
                var overlap = Math.Min(last.Bounds.Bottom, w.Bounds.Bottom) - Math.Max(last.Bounds.Top, w.Bounds.Top);
                return overlap > 0.5 * Math.Min(last.Bounds.Height, w.Bounds.Height);
            });
            if (target == null) lines.Add([w]);
            else target.Add(w);
        }
        return lines.Select(MakeLine).OrderBy(l => l.Bounds.Top).ToList();
    }

    /// <summary>Text blocks (paragraphs, columns) in reading order, using the Docstrum segmenter.</summary>
    public static IReadOnlyList<TextBlockInfo> GetBlocks(PageText page)
    {
        if (page.Words.Count == 0) return [];
        var lines = GetLines(page.Words);
        var blocks = new List<List<TextLineInfo>>();
        foreach (var line in lines)
        {
            var block = blocks.LastOrDefault(b => BelongsTo(b, line));
            if (block == null) blocks.Add([line]);
            else block.Add(line);
        }
        var infos = blocks.Select(b => new TextBlockInfo(
            string.Join("\n", b.Select(l => l.Text)),
            b.Select(l => l.Bounds).Aggregate(Rect.Union),
            b)).ToList();
        return OrderBlocks(infos);
    }

    private static bool BelongsTo(List<TextLineInfo> block, TextLineInfo line)
    {
        var last = block[^1];
        var gap = line.Bounds.Top - last.Bounds.Bottom;
        var size = Math.Max(1, Math.Min(last.FontSize, line.FontSize));
        var horizontalOverlap = Math.Min(last.Bounds.Right, line.Bounds.Right) - Math.Max(last.Bounds.Left, line.Bounds.Left);
        var similarSize = Math.Abs(last.FontSize - line.FontSize) < 0.25 * Math.Max(last.FontSize, line.FontSize) + 0.5;
        return gap < 0.9 * size && gap > -0.5 * size && horizontalOverlap > 0 && similarSize;
    }

    /// <summary>Orders blocks column by column: blocks that overlap horizontally read top to bottom.</summary>
    private static IReadOnlyList<TextBlockInfo> OrderBlocks(List<TextBlockInfo> blocks) =>
        blocks.Count <= 1
            ? blocks
            : ColumnAwareOrder(blocks.OrderBy(b => Math.Round(b.Bounds.Top / 4)).ThenBy(b => b.Bounds.Left).ToList());

    private static IReadOnlyList<TextBlockInfo> ColumnAwareOrder(List<TextBlockInfo> sorted)
    {
        var result = new List<TextBlockInfo>();
        var remaining = new List<TextBlockInfo>(sorted);
        while (remaining.Count > 0)
        {
            var current = remaining[0];
            remaining.RemoveAt(0);
            result.Add(current);
            // Continue down the same column while the next block below is horizontally aligned.
            while (true)
            {
                var next = remaining
                    .Where(b => b.Bounds.Top >= current.Bounds.Top &&
                                Math.Min(b.Bounds.Right, current.Bounds.Right) - Math.Max(b.Bounds.Left, current.Bounds.Left) > 0.5 * Math.Min(b.Bounds.Width, current.Bounds.Width))
                    .OrderBy(b => b.Bounds.Top)
                    .FirstOrDefault();
                if (next == null) break;
                // Stop if another block sits between them in a different column at the same height.
                var blocker = remaining.Any(b => b != next && b.Bounds.Top < next.Bounds.Top && b.Bounds.Top > current.Bounds.Top &&
                                                 b.Bounds.Left < current.Bounds.Left);
                if (blocker) break;
                remaining.Remove(next);
                result.Add(next);
                current = next;
            }
        }
        return result;
    }

    private static TextLineInfo MakeLine(List<TextWord> words)
    {
        var ordered = words.OrderBy(w => w.Bounds.Left).ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i > 0)
            {
                var gap = ordered[i].Bounds.Left - ordered[i - 1].Bounds.Right;
                sb.Append(gap > ordered[i].FontSize * 2.5 ? "  " : " ");
            }
            sb.Append(ordered[i].Text);
        }
        return new TextLineInfo(sb.ToString(), ordered.Select(w => w.Bounds).Aggregate(Rect.Union), ordered);
    }

    private static TextWord ToWord(Word w, PageGeometry geometry)
    {
        var b = w.BoundingBox;
        var user = new Rect(new Point(b.Left, b.Bottom), new Point(b.Right, b.Top));
        var display = geometry.ToDisplay(user);
        var letter = w.Letters.Count > 0 ? w.Letters[0] : null;
        var fontName = w.FontName ?? letter?.FontName ?? "";
        var size = letter != null ? (letter.PointSize > 0 ? letter.PointSize : letter.FontSize) : display.Height;
        if (size <= 0 || double.IsNaN(size)) size = Math.Max(1, display.Height);
        var lower = fontName.ToLowerInvariant();
        var bold = lower.Contains("bold") || lower.Contains("black") || lower.Contains("heavy") || lower.Contains("semibold") ||
                   (letter?.FontDetails?.IsBold ?? false);
        var italic = lower.Contains("italic") || lower.Contains("oblique") || (letter?.FontDetails?.IsItalic ?? false);
        uint rgb = 0;
        try
        {
            if (letter?.Color != null)
            {
                var (r, g, bl) = letter.Color.ToRGBValues();
                rgb = (uint)((int)(r * 255) << 16 | (int)(g * 255) << 8 | (int)(bl * 255));
            }
        }
        catch
        {
            rgb = 0;
        }
        // Baselines only order lines when text runs horizontally on screen.
        var baseline = letter != null && geometry.Rotation is 0 or 180 && letter.TextOrientation == TextOrientation.Horizontal
            ? geometry.ToDisplay(new Point(letter.StartBaseLine.X, letter.StartBaseLine.Y)).Y
            : double.NaN;
        return new TextWord(w.Text, display, size, CleanFontName(fontName), bold, italic, rgb) { Baseline = baseline };
    }

    /// <summary>Strips the subset prefix ("ABCDEF+Arial-Bold" → "Arial-Bold").</summary>
    public static string CleanFontName(string name)
    {
        var plus = name.IndexOf('+');
        return plus is > 0 and <= 7 ? name[(plus + 1)..] : name;
    }
}
