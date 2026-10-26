using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using PageSmith.Core.Common;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace PageSmith.Core.Text;

/// <summary>A search match: one box per line it spans, in display space.</summary>
public sealed record TextHit(int PageIndex, IReadOnlyList<Rect> Boxes, string Text);

/// <summary>A word in a document's text, by page and position in reading order.</summary>
public readonly record struct TextPosition(int Page, int Word) : IComparable<TextPosition>
{
    public int CompareTo(TextPosition other) => Page != other.Page ? Page.CompareTo(other.Page) : Word.CompareTo(other.Word);

    public static bool operator <(TextPosition a, TextPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(TextPosition a, TextPosition b) => a.CompareTo(b) > 0;
}

/// <summary>The words of one page in reading order (columns, then lines), for selecting and searching.</summary>
public sealed class PageWords
{
    private readonly int[] _starts;
    private readonly int[] _lines;

    internal PageWords(int index, IReadOnlyList<TextBlockInfo> blocks)
    {
        Index = index;
        var words = new List<TextWord>();
        var starts = new List<int>();
        var lines = new List<int>();
        var sb = new StringBuilder();
        var line = 0;
        foreach (var l in blocks.SelectMany(b => b.Lines))
        {
            foreach (var w in l.Words)
            {
                if (sb.Length > 0) sb.Append(lines[^1] == line ? ' ' : '\n');
                starts.Add(sb.Length);
                sb.Append(w.Text);
                words.Add(w);
                lines.Add(line);
            }
            line++;
        }
        Words = words;
        Text = sb.ToString();
        _starts = [.. starts];
        _lines = [.. lines];
    }

    public int Index { get; }

    public IReadOnlyList<TextWord> Words { get; }

    /// <summary>The page's text: words separated by spaces, lines by line breaks.</summary>
    public string Text { get; }

    public bool HasText => Words.Count > 0;

    /// <summary>The word under <paramref name="p"/>, else the nearest one when <paramref name="nearest"/> is set; -1 if none.</summary>
    public int WordAt(Point p, bool nearest)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < Words.Count; i++)
        {
            var b = Words[i].Bounds;
            b.Inflate(1.5, 1.5);
            if (b.Contains(p)) return i;
            // Vertical distance counts more, so the nearest word is on the same line where possible.
            var dx = Math.Max(0, Math.Max(b.Left - p.X, p.X - b.Right));
            var dy = Math.Max(0, Math.Max(b.Top - p.Y, p.Y - b.Bottom));
            var d = dx + dy * 4;
            if (d < bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }
        return nearest ? best : -1;
    }

    /// <summary>The text of words <paramref name="first"/> to <paramref name="last"/>, inclusive.</summary>
    public string TextOf(int first, int last) => Text[_starts[first]..(_starts[last] + Words[last].Text.Length)];

    /// <summary>Highlight boxes for words <paramref name="first"/> to <paramref name="last"/>: one per line.</summary>
    public IReadOnlyList<Rect> BoxesOf(int first, int last)
    {
        var boxes = new List<Rect>();
        Rect? current = null;
        var line = -1;
        for (var i = first; i <= last && i < Words.Count; i++)
        {
            if (_lines[i] != line && current != null)
            {
                boxes.Add(current.Value);
                current = null;
            }
            line = _lines[i];
            current = current == null ? Words[i].Bounds : Rect.Union(current.Value, Words[i].Bounds);
        }
        if (current != null) boxes.Add(current.Value);
        return boxes;
    }

    /// <summary>The word holding character <paramref name="offset"/> of <see cref="Text"/>, or the next word after a separator.</summary>
    private int WordAtOffset(int offset)
    {
        var i = Array.BinarySearch(_starts, offset);
        if (i < 0) i = ~i - 1;
        if (i < 0) return 0;
        return offset < _starts[i] + Words[i].Text.Length ? i : i + 1;
    }

    internal IEnumerable<TextHit> Find(Regex rx)
    {
        foreach (Match m in rx.Matches(Text))
        {
            if (m.Length == 0) continue;
            var end = m.Index + m.Length;
            var boxes = new List<Rect>();
            Rect? current = null;
            var line = -1;
            for (var i = WordAtOffset(m.Index); i < Words.Count && _starts[i] < end; i++)
            {
                // Cover the matched part of each word; characters are assumed to be equally wide.
                var w = Words[i];
                var from = Math.Max(m.Index, _starts[i]) - _starts[i];
                var to = Math.Min(end, _starts[i] + w.Text.Length) - _starts[i];
                if (to <= from) continue;
                var charWidth = w.Bounds.Width / Math.Max(1, w.Text.Length);
                var part = new Rect(w.Bounds.Left + charWidth * from, w.Bounds.Top, charWidth * (to - from), w.Bounds.Height);
                if (_lines[i] != line && current != null)
                {
                    boxes.Add(current.Value);
                    current = null;
                }
                line = _lines[i];
                current = current == null ? part : Rect.Union(current.Value, part);
            }
            if (current != null) boxes.Add(current.Value);
            if (boxes.Count > 0) yield return new TextHit(Index, boxes, m.Value);
        }
    }
}

/// <summary>
/// The text of an open document, read page by page when first needed, for find and text selection.
/// The document is read from a copy in memory, so the file stays free to be replaced. Thread-safe.
/// </summary>
public sealed class TextIndex : IDisposable
{
    private readonly PigDocument _doc;
    private readonly PageWords?[] _pages;
    private readonly object _gate = new();
    private bool _disposed;

    private TextIndex(PigDocument doc)
    {
        _doc = doc;
        _pages = new PageWords?[doc.NumberOfPages];
    }

    public static TextIndex Open(InputFile input) => new(TextExtractor.OpenInMemory(input));

    public int PageCount => _pages.Length;

    /// <summary>The page's words if they have been read already, without waiting (for hover feedback).</summary>
    public PageWords? Loaded(int index) => Volatile.Read(ref _pages[index]);

    public PageWords GetPage(int index)
    {
        lock (_gate)
        {
            if (_pages[index] is { } cached) return cached;
            ObjectDisposedException.ThrowIf(_disposed, this);
            PageWords words;
            try
            {
                words = new PageWords(index, TextExtractor.GetBlocks(TextExtractor.ReadPage(_doc.GetPage(index + 1), index)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A page whose text can't be read is treated as having none.
                words = new PageWords(index, []);
            }
            return _pages[index] = words;
        }
    }

    /// <summary>The search pattern for a query: spaces match any white space, including line breaks.</summary>
    public static Regex Pattern(string query, bool matchCase = false, bool wholeWord = false)
    {
        var pattern = Regex.Escape(query.Trim()).Replace("\\ ", "\\s+");
        if (wholeWord) pattern = $@"(?<!\w){pattern}(?!\w)";
        return new Regex(pattern, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    }

    /// <summary>Every match of <paramref name="query"/>, in reading order.</summary>
    public IReadOnlyList<TextHit> Find(string query, bool matchCase = false, bool wholeWord = false, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var rx = Pattern(query, matchCase, wholeWord);
        var hits = new List<TextHit>();
        for (var i = 0; i < PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            hits.AddRange(GetPage(i).Find(rx));
            progress?.Report((i + 1.0) / PageCount);
        }
        return hits;
    }

    /// <summary>The text from one word to another (either order, inclusive); pages are separated by blank lines.</summary>
    public string TextBetween(TextPosition a, TextPosition b)
    {
        var (from, to) = a < b ? (a, b) : (b, a);
        var sb = new StringBuilder();
        for (var p = from.Page; p <= to.Page && p < PageCount; p++)
        {
            var page = GetPage(p);
            if (!page.HasText) continue;
            var first = p == from.Page ? from.Word : 0;
            var last = p == to.Page ? Math.Min(to.Word, page.Words.Count - 1) : page.Words.Count - 1;
            if (first > last) continue;
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(page.TextOf(first, last));
        }
        return sb.ToString();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _doc.Dispose();
        }
    }
}
