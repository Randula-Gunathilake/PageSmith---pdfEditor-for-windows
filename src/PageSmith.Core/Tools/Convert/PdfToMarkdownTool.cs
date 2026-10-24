using System.Windows;
using PageSmith.Core.Text;

namespace PageSmith.Core.Tools.Convert;

public sealed record PdfToMarkdownOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }
    public bool Ocr { get; init; }
    public string? OcrLanguage { get; init; }

    /// <summary>Saves images next to the .md file and links them.</summary>
    public bool ExportImages { get; init; } = true;

    public string? Pages { get; init; }
}

/// <summary>PDF to Markdown: headings, paragraphs, lists, tables, links and images.</summary>
public static class PdfToMarkdownTool
{
    public static async Task<ToolResult> RunAsync(PdfToMarkdownOptions o, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(o.Input.Path).Length };
        var pages = await LayoutAnalyzer.AnalyzeAsync(o.Input, new LayoutOptions { Ocr = o.Ocr, OcrLanguage = o.OcrLanguage, Pages = o.Pages, IncludeImages = o.ExportImages },
            scope.Slice(0, 0.8), ct).ConfigureAwait(false);
        var links = ReadLinks(o.Input);

        var md = new StringBuilder();
        var imageDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(o.OutputPath))!, Path.GetFileNameWithoutExtension(o.OutputPath) + "_images");
        var imageCount = 0;
        foreach (var page in pages)
        {
            foreach (var element in page.Elements)
            {
                switch (element)
                {
                    case LayoutParagraph p:
                        var text = Linkify(Escape(p.Text), links.GetValueOrDefault(page.Index), p);
                        if (p.HeadingLevel > 0) md.Append(new string('#', p.HeadingLevel)).Append(' ').AppendLine(text.Replace("**", ""));
                        else if (p.List == ListKind.Bullet) md.Append("- ").AppendLine(Emphasis(text, p));
                        else if (p.List == ListKind.Numbered) md.Append("1. ").AppendLine(Emphasis(text, p));
                        else md.AppendLine(Emphasis(text, p));
                        md.AppendLine();
                        break;
                    case LayoutTable t:
                        AppendTable(md, t);
                        md.AppendLine();
                        break;
                    case LayoutImage img when o.ExportImages:
                        Directory.CreateDirectory(imageDir);
                        imageCount++;
                        var name = $"image-{imageCount:D3}.{(img.IsJpeg ? "jpg" : "png")}";
                        await File.WriteAllBytesAsync(Path.Combine(imageDir, name), img.Data, ct).ConfigureAwait(false);
                        md.AppendLine($"![Image {imageCount}]({Path.GetFileName(imageDir)}/{name})").AppendLine();
                        break;
                }
            }
        }

        await File.WriteAllTextAsync(o.OutputPath, Tidy(md.ToString()), ct).ConfigureAwait(false);
        result.AddOutput(o.OutputPath);
        result.Text = md.ToString();
        result.Summary = $"Converted {pages.Count} page{(pages.Count == 1 ? "" : "s")} to Markdown" + (imageCount > 0 ? $" with {imageCount} image{(imageCount == 1 ? "" : "s")}." : ".");
        PdfToWordTool.WarnAboutScans(result, pages, o.Ocr);
        scope.Report(1);
        return result;
    }

    private static string Emphasis(string text, LayoutParagraph p) =>
        p.Bold && p.Italic ? $"***{text}***" : p.Bold ? $"**{text}**" : p.Italic ? $"*{text}*" : text;

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c is '*' or '_' or '`' or '[' or ']' or '<' or '>' or '|' or '#') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static void AppendTable(StringBuilder md, LayoutTable t)
    {
        var cols = t.ColumnCount;
        string Row(IReadOnlyList<string> cells) =>
            "| " + string.Join(" | ", Enumerable.Range(0, cols).Select(i => i < cells.Count ? Escape(cells[i]).Replace("\n", " ") : "")) + " |";
        md.AppendLine(Row(t.Rows[0]));
        md.AppendLine("|" + string.Concat(Enumerable.Repeat(" --- |", cols)));
        foreach (var row in t.Rows.Skip(1)) md.AppendLine(Row(row));
    }

    /// <summary>Link annotations per page: target URL and its rectangle in display space.</summary>
    private static Dictionary<int, List<(Rect Bounds, string Url)>> ReadLinks(InputFile input)
    {
        var map = new Dictionary<int, List<(Rect, string)>>();
        try
        {
            using var doc = TextExtractor.Open(input);
            for (var i = 0; i < doc.NumberOfPages; i++)
            {
                var page = doc.GetPage(i + 1);
                var geometry = Pdf.PageGeometry.From(page);
                foreach (var link in page.GetHyperlinks())
                {
                    if (string.IsNullOrEmpty(link.Uri)) continue;
                    var b = link.Bounds;
                    var rect = geometry.ToDisplay(new Rect(new Point(b.Left, b.Bottom), new Point(b.Right, b.Top)));
                    if (!map.TryGetValue(i, out var list)) map[i] = list = [];
                    list.Add((rect, link.Uri));
                }
            }
        }
        catch
        {
            // Links are a bonus.
        }
        return map;
    }

    private static string Linkify(string text, List<(Rect Bounds, string Url)>? links, LayoutParagraph p)
    {
        if (links == null) return text;
        foreach (var (bounds, url) in links)
        {
            var words = p.Lines.SelectMany(l => l.Words).Where(w => bounds.IntersectsWith(w.Bounds)).Select(w => w.Text).ToList();
            if (words.Count == 0) continue;
            var anchor = Escape(string.Join(" ", words));
            var index = text.IndexOf(anchor, StringComparison.Ordinal);
            if (index >= 0) text = text[..index] + $"[{anchor}]({url})" + text[(index + anchor.Length)..];
        }
        return text;
    }

    private static string Tidy(string md)
    {
        while (md.Contains("\n\n\n")) md = md.Replace("\n\n\n", "\n\n");
        return md.Trim() + "\n";
    }
}
