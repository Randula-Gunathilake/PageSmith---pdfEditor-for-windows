using System.Windows;
using PageSmith.Core.Imaging;
using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;
using PageSmith.Core.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PageSmith.Core.Tools.Security;

public enum ChangeKind
{
    Inserted,
    Deleted,
    Replaced,
}

/// <summary>Where a word sits: page index and display-space bounds.</summary>
public readonly record struct WordLocation(int Page, Rect Bounds);

public sealed record TextChange(ChangeKind Kind, string OldText, string NewText, IReadOnlyList<WordLocation> OldLocations, IReadOnlyList<WordLocation> NewLocations)
{
    public int Page => NewLocations.Count > 0 ? NewLocations[0].Page : OldLocations.Count > 0 ? OldLocations[0].Page : 0;
}

public sealed record CompareReport(IReadOnlyList<TextChange> Changes, int OldWords, int NewWords, double Similarity);

public sealed record CompareOptions
{
    public required InputFile OldFile { get; init; }
    public required InputFile NewFile { get; init; }
    public required string OutputDirectory { get; init; }

    /// <summary>Text mode writes highlighted copies of both files; overlay mode writes a visual overlay PDF.</summary>
    public bool Overlay { get; init; }

    public bool IgnoreCase { get; init; }
    public bool IgnorePunctuation { get; init; }
}

/// <summary>Compare PDF: semantic text comparison with a change report, or a visual content overlay.</summary>
public static class CompareTool
{
    public static async Task<ToolResult> RunAsync(CompareOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(options.OldFile.Path).Length + new FileInfo(options.NewFile.Path).Length };
        if (options.Overlay)
        {
            var path = OutputPaths.Unique(options.OutputDirectory, $"{Path.GetFileNameWithoutExtension(options.NewFile.Path)}_overlay.pdf");
            var differing = await WriteOverlayAsync(options.OldFile, options.NewFile, path, scope, ct).ConfigureAwait(false);
            result.AddOutput(path);
            result.Summary = differing == 0 ? "The pages look identical." : $"{differing} page{(differing == 1 ? "" : "s")} differ. Red: only in the original, blue: only in the new version.";
            return result;
        }

        scope.Report(0.05, "Reading both documents");
        var report = await Task.Run(() => CompareText(options.OldFile, options.NewFile, options.IgnoreCase, options.IgnorePunctuation, ct), ct).ConfigureAwait(false);
        result.Data = report;
        result.Text = FormatReport(report);

        scope.Report(0.6, "Highlighting changes");
        await Task.Run(() =>
        {
            var oldOut = OutputPaths.ForInput(options.OldFile.Path, "compared", ".pdf", options.OutputDirectory);
            Highlight(options.OldFile, oldOut, report.Changes.SelectMany(c => c.OldLocations), "#E53935");
            result.AddOutput(oldOut);
            var newOut = OutputPaths.ForInput(options.NewFile.Path, "compared", ".pdf", options.OutputDirectory);
            Highlight(options.NewFile, newOut, report.Changes.SelectMany(c => c.NewLocations), "#43A047");
            result.AddOutput(newOut);
            var txt = OutputPaths.Unique(options.OutputDirectory, $"{Path.GetFileNameWithoutExtension(options.NewFile.Path)}_changes.txt");
            File.WriteAllText(txt, result.Text);
            result.AddOutput(txt);
        }, ct).ConfigureAwait(false);

        result.Summary = report.Changes.Count == 0
            ? "No text differences were found."
            : $"{report.Changes.Count} change{(report.Changes.Count == 1 ? "" : "s")} found; the documents are {report.Similarity:P0} similar.";
        scope.Report(1);
        return result;
    }

    public static CompareReport CompareText(InputFile oldFile, InputFile newFile, bool ignoreCase = false, bool ignorePunctuation = false, CancellationToken ct = default)
    {
        var a = ReadWords(oldFile, ct);
        var b = ReadWords(newFile, ct);
        string Norm(string s)
        {
            if (ignorePunctuation) s = new string(s.Where(c => !char.IsPunctuation(c)).ToArray());
            return ignoreCase ? s.ToLowerInvariant() : s;
        }
        var segments = WordDiff.Diff(a.Select(w => Norm(w.Word.Text)).ToList(), b.Select(w => Norm(w.Word.Text)).ToList(), StringComparer.Ordinal, ct);

        var changes = new List<TextChange>();
        var equal = 0;
        for (var i = 0; i < segments.Count; i++)
        {
            var s = segments[i];
            if (s.Op == DiffOp.Equal)
            {
                equal += s.OldLength;
                continue;
            }
            // A deletion directly followed by an insertion is a replacement.
            if (s.Op == DiffOp.Delete && i + 1 < segments.Count && segments[i + 1].Op == DiffOp.Insert)
            {
                var ins = segments[i + 1];
                changes.Add(Change(ChangeKind.Replaced, a, s.OldStart, s.OldLength, b, ins.NewStart, ins.NewLength));
                i++;
                continue;
            }
            changes.Add(s.Op == DiffOp.Delete
                ? Change(ChangeKind.Deleted, a, s.OldStart, s.OldLength, b, 0, 0)
                : Change(ChangeKind.Inserted, a, 0, 0, b, s.NewStart, s.NewLength));
        }
        var similarity = a.Count + b.Count == 0 ? 1 : 2.0 * equal / (a.Count + b.Count);
        return new CompareReport(changes, a.Count, b.Count, similarity);
    }

    private sealed record PlacedWord(int Page, TextWord Word);

    private static List<PlacedWord> ReadWords(InputFile file, CancellationToken ct)
    {
        var words = new List<PlacedWord>();
        foreach (var page in TextExtractor.ReadPages(file, null, ct))
            foreach (var block in TextExtractor.GetBlocks(page))
                foreach (var line in block.Lines)
                    words.AddRange(line.Words.Select(w => new PlacedWord(page.Index, w)));
        return words;
    }

    private static TextChange Change(ChangeKind kind, List<PlacedWord> a, int aStart, int aLen, List<PlacedWord> b, int bStart, int bLen)
    {
        var oldWords = a.Skip(aStart).Take(aLen).ToList();
        var newWords = b.Skip(bStart).Take(bLen).ToList();
        return new TextChange(kind,
            string.Join(" ", oldWords.Select(w => w.Word.Text)),
            string.Join(" ", newWords.Select(w => w.Word.Text)),
            oldWords.Select(w => new WordLocation(w.Page, w.Word.Bounds)).ToList(),
            newWords.Select(w => new WordLocation(w.Page, w.Word.Bounds)).ToList());
    }

    public static string FormatReport(CompareReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Change report — {report.Changes.Count} change(s), similarity {report.Similarity:P1}");
        sb.AppendLine($"Original: {report.OldWords} words; new version: {report.NewWords} words.");
        sb.AppendLine();
        foreach (var c in report.Changes)
        {
            var where = $"Page {c.Page + 1}";
            sb.AppendLine(c.Kind switch
            {
                ChangeKind.Inserted => $"{where}: added \"{Trim(c.NewText)}\"",
                ChangeKind.Deleted => $"{where}: removed \"{Trim(c.OldText)}\"",
                _ => $"{where}: \"{Trim(c.OldText)}\" → \"{Trim(c.NewText)}\"",
            });
        }
        return sb.ToString();
    }

    private static string Trim(string s) => s.Length > 200 ? s[..197] + "…" : s;

    private static void Highlight(InputFile file, string output, IEnumerable<WordLocation> locations, string color)
    {
        using var doc = PdfDocs.Open(file);
        var brush = new XSolidBrush(TextStyle.ParseColor(color, 0.35));
        foreach (var group in locations.GroupBy(l => l.Page))
        {
            if (group.Key >= doc.PageCount) continue;
            using var canvas = PageCanvas.Open(doc.Pages[group.Key]);
            foreach (var l in group)
            {
                var r = l.Bounds;
                canvas.Graphics.DrawRectangle(brush, r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2);
            }
        }
        PdfDocs.Save(doc, output);
    }

    /// <summary>Renders both versions and writes one page per pair: shared ink grey, original-only red, new-only blue.</summary>
    private static async Task<int> WriteOverlayAsync(InputFile oldFile, InputFile newFile, string output, ProgressScope scope, CancellationToken ct)
    {
        using var r1 = await PdfRenderer.OpenAsync(oldFile.Path, oldFile.Password, ct).ConfigureAwait(false);
        using var r2 = await PdfRenderer.OpenAsync(newFile.Path, newFile.Password, ct).ConfigureAwait(false);
        var pages = Math.Max(r1.PageCount, r2.PageCount);
        using var doc = PdfDocs.Create("Overlay comparison");
        var differing = 0;
        for (var i = 0; i < pages; i++)
        {
            ct.ThrowIfCancellationRequested();
            scope.Report(i, pages, $"Comparing page {i + 1}");
            var size = i < r2.PageCount ? r2.GetPageSize(i) : r1.GetPageSize(i);
            var a = i < r1.PageCount ? ImageIO.ToPixels(await r1.RenderAsync(i, 100, ct).ConfigureAwait(false)) : null;
            var b = i < r2.PageCount ? ImageIO.ToPixels(await r2.RenderAsync(i, 100, ct).ConfigureAwait(false)) : null;
            var (overlay, changed) = Overlay(a, b);
            if (changed > 0.0005) differing++;
            var page = doc.AddPage();
            page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(size.Width, size.Height));
            using var gfx = XGraphics.FromPdfPage(page);
            using var ms = new MemoryStream(ImageIO.EncodePng(ImageIO.FromPixels(overlay, 100)));
            using var img = XImage.FromStream(ms);
            gfx.DrawImage(img, 0, 0, size.Width, size.Height);
        }
        PdfDocs.Save(doc, output);
        return differing;
    }

    /// <summary>Combines two renderings; returns the overlay and the fraction of pixels that differ.</summary>
    public static (PixelBuffer Overlay, double ChangedFraction) Overlay(PixelBuffer? a, PixelBuffer? b)
    {
        var w = Math.Max(a?.Width ?? 0, b?.Width ?? 0);
        var h = Math.Max(a?.Height ?? 0, b?.Height ?? 0);
        var output = new PixelBuffer(Math.Max(1, w), Math.Max(1, h));
        long changed = 0;
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var la = Luma(a, x, y);
            var lb = Luma(b, x, y);
            var inkA = la < 200;
            var inkB = lb < 200;
            var o = (y * w + x) * 4;
            byte r, g, bl;
            if (inkA && inkB)
            {
                var v = (byte)Math.Min(la, lb);
                (r, g, bl) = ((byte)(v / 2 + 90), (byte)(v / 2 + 90), (byte)(v / 2 + 90));
            }
            else if (inkA)
            {
                (r, g, bl) = (220, 40, 40);
                changed++;
            }
            else if (inkB)
            {
                (r, g, bl) = (30, 90, 230);
                changed++;
            }
            else
            {
                (r, g, bl) = (255, 255, 255);
            }
            output.Pixels[o] = bl;
            output.Pixels[o + 1] = g;
            output.Pixels[o + 2] = r;
            output.Pixels[o + 3] = 255;
        }
        return (output, w * h == 0 ? 0 : (double)changed / (w * h));
    }

    private static int Luma(PixelBuffer? p, int x, int y)
    {
        if (p == null || x >= p.Width || y >= p.Height) return 255;
        var i = (y * p.Width + x) * 4;
        return (p.Pixels[i] * 114 + p.Pixels[i + 1] * 587 + p.Pixels[i + 2] * 299) / 1000;
    }
}
