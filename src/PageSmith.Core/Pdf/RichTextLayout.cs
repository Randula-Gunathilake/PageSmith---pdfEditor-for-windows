using System.Windows;
using PdfSharp.Drawing;

namespace PageSmith.Core.Pdf;

public enum TextAlign
{
    Left,
    Center,
    Right,
    Justify,
}

public enum VerticalAnchor
{
    Top,
    Middle,
    Bottom,
}

/// <summary>A run of text with one font and colour.</summary>
public sealed record TextRun(string Text, string Font, double Size, bool Bold = false, bool Italic = false, bool Underline = false, XColor? Color = null)
{
    public XFont ToFont()
    {
        var style = (Bold, Italic) switch
        {
            (true, true) => XFontStyleEx.BoldItalic,
            (true, false) => XFontStyleEx.Bold,
            (false, true) => XFontStyleEx.Italic,
            _ => XFontStyleEx.Regular,
        };
        if (Underline) style |= XFontStyleEx.Underline;
        return new XFont(Font, Math.Max(1, Size), style, new XPdfFontOptions(PdfSharp.Pdf.PdfFontEncoding.Unicode));
    }
}

/// <summary>A paragraph made of runs.</summary>
public sealed record RichParagraph(IReadOnlyList<TextRun> Runs, TextAlign Align = TextAlign.Left, double SpaceAfter = 0, string? Prefix = null, double Indent = 0);

/// <summary>
/// Lays out paragraphs of mixed fonts inside a rectangle with word wrapping, alignment and vertical
/// anchoring, then draws them with <see cref="XGraphics"/>. Optionally shrinks text until it fits.
/// </summary>
public static class RichTextLayout
{
    private sealed record Piece(string Text, TextRun Run, XFont Font, double Width, bool IsSpace);

    private sealed class Line
    {
        public List<Piece> Pieces { get; } = [];
        public double Width;
        public double Height;
        public double Ascent;
        public bool EndsParagraph;
        public TextAlign Align;
        public double Indent;
    }

    /// <summary>Draws the paragraphs; returns the height used.</summary>
    public static double Draw(XGraphics gfx, IReadOnlyList<RichParagraph> paragraphs, Rect box, VerticalAnchor anchor = VerticalAnchor.Top,
        bool shrinkToFit = false, double lineSpacing = 1.15)
    {
        var scale = 1.0;
        var lines = Layout(gfx, paragraphs, box.Width, scale, lineSpacing, out var height);
        if (shrinkToFit)
        {
            while (height > box.Height && scale > 0.3)
            {
                scale *= 0.9;
                lines = Layout(gfx, paragraphs, box.Width, scale, lineSpacing, out height);
            }
        }

        var y = anchor switch
        {
            VerticalAnchor.Middle => box.Top + (box.Height - height) / 2,
            VerticalAnchor.Bottom => box.Bottom - height,
            _ => box.Top,
        };
        foreach (var line in lines)
        {
            var available = box.Width - line.Indent;
            var x = box.Left + line.Indent + line.Align switch
            {
                TextAlign.Center => (available - line.Width) / 2,
                TextAlign.Right => available - line.Width,
                _ => 0,
            };
            var extraPerSpace = 0.0;
            if (line.Align == TextAlign.Justify && !line.EndsParagraph)
            {
                var spaces = line.Pieces.Count(p => p.IsSpace);
                if (spaces > 0) extraPerSpace = (available - line.Width) / spaces;
            }
            var baseline = y + line.Ascent;
            foreach (var piece in line.Pieces)
            {
                if (!piece.IsSpace)
                {
                    var brush = new XSolidBrush(piece.Run.Color ?? XColors.Black);
                    gfx.DrawString(piece.Text, piece.Font, brush, x, baseline, XStringFormats.BaseLineLeft);
                }
                x += piece.Width + (piece.IsSpace ? extraPerSpace : 0);
            }
            y += line.Height;
        }
        return height;
    }

    /// <summary>Measures the height the paragraphs need at the given width.</summary>
    public static double Measure(XGraphics gfx, IReadOnlyList<RichParagraph> paragraphs, double width, double lineSpacing = 1.15)
    {
        Layout(gfx, paragraphs, width, 1, lineSpacing, out var height);
        return height;
    }

    private static List<Line> Layout(XGraphics gfx, IReadOnlyList<RichParagraph> paragraphs, double width, double scale, double lineSpacing, out double height)
    {
        var lines = new List<Line>();
        height = 0;
        foreach (var para in paragraphs)
        {
            var runs = para.Runs.Where(r => r.Text.Length > 0).ToList();
            if (para.Prefix != null && runs.Count > 0) runs.Insert(0, runs[0] with { Text = para.Prefix });
            var current = NewLine(para);
            var maxWidth = Math.Max(1, width - para.Indent);
            var defaultSize = (para.Runs.FirstOrDefault()?.Size ?? 12) * scale;

            foreach (var run in runs)
            {
                var font = (run with { Size = run.Size * scale }).ToFont();
                foreach (var token in Tokenize(run.Text))
                {
                    if (token == "\n")
                    {
                        Finish(current, font, lineSpacing, lines, ref height);
                        current = NewLine(para);
                        continue;
                    }
                    var isSpace = token.Trim().Length == 0;
                    var w = gfx.MeasureString(token, font).Width;
                    if (!isSpace && current.Width + w > maxWidth && current.Pieces.Any(p => !p.IsSpace))
                    {
                        TrimTrailingSpaces(current);
                        Finish(current, font, lineSpacing, lines, ref height);
                        current = NewLine(para);
                    }
                    if (isSpace && current.Pieces.Count == 0) continue;
                    // Break words longer than the line on character boundaries.
                    if (!isSpace && w > maxWidth)
                    {
                        foreach (var chunk in SplitLongWord(gfx, token, font, maxWidth))
                        {
                            var cw = gfx.MeasureString(chunk, font).Width;
                            if (current.Width + cw > maxWidth && current.Pieces.Count > 0)
                            {
                                Finish(current, font, lineSpacing, lines, ref height);
                                current = NewLine(para);
                            }
                            Add(current, new Piece(chunk, run, font, cw, false), lineSpacing);
                        }
                        continue;
                    }
                    Add(current, new Piece(token, run, font, w, isSpace), lineSpacing);
                }
            }
            TrimTrailingSpaces(current);
            current.EndsParagraph = true;
            var fallback = new XFont(para.Runs.FirstOrDefault()?.Font ?? "Arial", Math.Max(1, defaultSize));
            Finish(current, fallback, lineSpacing, lines, ref height);
            height += para.SpaceAfter * scale;
            if (lines.Count > 0) lines[^1].Height += para.SpaceAfter * scale;
        }
        return lines;
    }

    private static Line NewLine(RichParagraph p) => new() { Align = p.Align, Indent = p.Indent };

    private static void Add(Line line, Piece piece, double lineSpacing)
    {
        line.Pieces.Add(piece);
        line.Width += piece.Width;
        var h = piece.Font.GetHeight() * lineSpacing / 1.15;
        line.Height = Math.Max(line.Height, h);
        line.Ascent = Math.Max(line.Ascent, piece.Font.Size * 0.9);
    }

    private static void Finish(Line line, XFont fallback, double lineSpacing, List<Line> lines, ref double height)
    {
        if (line.Height <= 0)
        {
            line.Height = fallback.GetHeight() * lineSpacing / 1.15;
            line.Ascent = fallback.Size * 0.9;
        }
        lines.Add(line);
        height += line.Height;
    }

    private static void TrimTrailingSpaces(Line line)
    {
        while (line.Pieces.Count > 0 && line.Pieces[^1].IsSpace)
        {
            line.Width -= line.Pieces[^1].Width;
            line.Pieces.RemoveAt(line.Pieces.Count - 1);
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        var sb = new StringBuilder();
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            if (ch == '\n')
            {
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
                yield return "\n";
            }
            else if (ch is ' ' or '\t')
            {
                if (sb.Length > 0) yield return sb.ToString();
                sb.Clear();
                yield return ch == '\t' ? "    " : " ";
            }
            else
            {
                sb.Append(ch);
                // CJK text has no spaces: allow a break after every ideograph.
                if (ch >= 0x2E80 && ch <= 0x9FFF || ch >= 0xAC00 && ch <= 0xD7AF || ch >= 0x3040 && ch <= 0x30FF)
                {
                    yield return sb.ToString();
                    sb.Clear();
                }
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static IEnumerable<string> SplitLongWord(XGraphics gfx, string word, XFont font, double maxWidth)
    {
        var start = 0;
        while (start < word.Length)
        {
            var len = 1;
            while (start + len < word.Length && gfx.MeasureString(word.Substring(start, len + 1), font).Width <= maxWidth) len++;
            yield return word.Substring(start, len);
            start += len;
        }
    }
}
