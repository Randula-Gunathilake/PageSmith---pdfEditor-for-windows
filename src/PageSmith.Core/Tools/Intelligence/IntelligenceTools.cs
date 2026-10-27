using System.Windows;
using MigraDoc.DocumentObjectModel;
using MigraDoc.Rendering;
using PageSmith.Core.Imaging;
using PageSmith.Core.Intelligence;
using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;
using PageSmith.Core.Text;
using PdfSharp.Drawing;

namespace PageSmith.Core.Tools.Intelligence;

public sealed record SummarizeOptions
{
    public required InputFile Input { get; init; }

    /// <summary>.md, .txt or .pdf; null to only return the text.</summary>
    public string? OutputPath { get; init; }

    public SummaryLength Length { get; init; } = SummaryLength.Medium;

    /// <summary>An online AI service writes the summary; null summarizes on this PC (no internet).</summary>
    public AiService? Ai { get; init; }

    /// <summary>Language of an AI summary (the on-device summary keeps the document's sentences).</summary>
    public string OutputLanguage { get; init; } = "English";
    public string? Pages { get; init; }
}

public sealed record TranslateOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }
    public required ITranslator Translator { get; init; }

    /// <summary>ISO code, or null to detect.</summary>
    public string? SourceLanguage { get; init; }
    public required string TargetLanguage { get; init; }
    public string? Pages { get; init; }

    /// <summary>Also write the translated text to a .txt file.</summary>
    public bool ExportText { get; init; }
}

/// <summary>AI Summarizer.</summary>
public static class SummarizeTool
{
    public static async Task<ToolResult> RunAsync(SummarizeOptions o, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(o.Input.Path).Length };
        scope.Report(0.05, "Reading the document");
        var text = await Task.Run(() => DocumentText(o.Input, o.Pages, ct), ct).ConfigureAwait(false);
        if (NlpText.Tokenize(text).Count() < 30)
            throw new UserFacingException("This PDF has too little selectable text to summarize. If it is scanned, run OCR PDF first.");

        var title = Path.GetFileNameWithoutExtension(o.Input.Path);
        string markdown;
        if (o.Ai is { } ai)
        {
            scope.Report(0.3, $"Asking {ai.Name} for a summary");
            markdown = $"# {title}\n\n" + await ai.SummarizeAsync(text, o.Length, o.OutputLanguage, ct).ConfigureAwait(false);
            result.Summary = $"Summary written by {ai.Name} ({ai.Model}).";
        }
        else
        {
            scope.Report(0.4, "Finding the key sentences");
            var summary = await Task.Run(() => ExtractiveSummarizer.Summarize(text, o.Length), ct).ConfigureAwait(false);
            markdown = summary.ToMarkdown(title);
            result.Summary = $"Summarized {summary.SourceWords:N0} words on this PC.";
        }
        result.Text = markdown;

        if (o.OutputPath != null)
        {
            scope.Report(0.9, "Saving");
            if (Path.GetExtension(o.OutputPath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
                await Task.Run(() => MarkdownToPdf(markdown, o.OutputPath), ct).ConfigureAwait(false);
            else
                await File.WriteAllTextAsync(o.OutputPath, markdown, ct).ConfigureAwait(false);
            result.AddOutput(o.OutputPath);
        }
        scope.Report(1);
        return result;
    }

    public static string DocumentText(InputFile input, string? pages, CancellationToken ct)
    {
        using var pig = TextExtractor.Open(input);
        var indices = PageRange.Parse(pages, pig.NumberOfPages);
        var sb = new StringBuilder();
        foreach (var i in indices)
        {
            ct.ThrowIfCancellationRequested();
            var page = TextExtractor.ReadPage(pig.GetPage(i + 1), i);
            foreach (var block in TextExtractor.GetBlocks(page))
                sb.AppendLine(LayoutAnalyzer.JoinLines(block.Lines)).AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Renders simple Markdown (headings, bullets, paragraphs) to a PDF.</summary>
    public static void MarkdownToPdf(string markdown, string path)
    {
        PdfDocs.EnsureInitialized();
        var document = new Document();
        document.Styles[StyleNames.Normal]!.Font.Name = "Segoe UI";
        document.Styles[StyleNames.Normal]!.Font.Size = 11;
        var section = document.AddSection();
        section.PageSetup.PageWidth = Unit.FromPoint(595.28);
        section.PageSetup.PageHeight = Unit.FromPoint(841.89);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromPoint(64);
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith('#'))
            {
                var level = line.TakeWhile(c => c == '#').Count();
                var p = section.AddParagraph(line[level..].Trim());
                p.Format.Font.Size = level == 1 ? 20 : level == 2 ? 15 : 13;
                p.Format.Font.Bold = true;
                p.Format.SpaceBefore = Unit.FromPoint(level == 1 ? 0 : 12);
                p.Format.SpaceAfter = Unit.FromPoint(6);
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                var p = section.AddParagraph("•\t" + line[2..].Replace("**", ""));
                p.Format.LeftIndent = Unit.FromPoint(16);
                p.Format.FirstLineIndent = Unit.FromPoint(-12);
                p.Format.SpaceAfter = Unit.FromPoint(4);
            }
            else
            {
                var p = section.AddParagraph(line.Replace("**", ""));
                p.Format.SpaceAfter = Unit.FromPoint(6);
                p.Format.Alignment = ParagraphAlignment.Justify;
            }
        }
        var renderer = new PdfDocumentRenderer { Document = document };
        renderer.RenderDocument();
        renderer.PdfDocument.Info.Creator = "PageSmith";
        renderer.PdfDocument.Save(path);
    }
}

/// <summary>
/// Translate PDF: each text block is translated and drawn back in place over its original
/// (covered with the sampled background colour), so pictures, tables and layout stay as they were.
/// </summary>
public static class TranslateTool
{
    public static async Task<ToolResult> RunAsync(TranslateOptions o, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(o.Input.Path).Length };
        scope.Report(0.02, "Reading the document");
        var pages = await Task.Run(() => TextExtractor.ReadPages(o.Input, PageRange.Parse(o.Pages, PdfDocs.CountPages(o.Input)), ct), ct).ConfigureAwait(false);
        var blocks = pages.SelectMany(p => TextExtractor.GetBlocks(p).Select(b => (Page: p, Block: b))).Where(x => x.Block.Text.Any(char.IsLetter)).ToList();
        if (blocks.Count == 0) throw new UserFacingException("This PDF has no selectable text to translate. If it is scanned, run OCR PDF first.");

        var source = o.SourceLanguage ?? NlpText.DetectLanguage(string.Join(" ", blocks.Take(40).Select(b => b.Block.Text)));
        if (string.Equals(source, o.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            throw new UserFacingException($"The document already appears to be in {Languages.NameOf(o.TargetLanguage)}.");

        // Translate in batches that fit the service.
        var texts = blocks.Select(b => LayoutAnalyzer.JoinLines(b.Block.Lines)).ToList();
        var translated = new string[texts.Count];
        var translateScope = scope.Slice(0.05, 0.8);
        var start = 0;
        while (start < texts.Count)
        {
            ct.ThrowIfCancellationRequested();
            var end = start;
            var chars = 0;
            while (end < texts.Count && (end == start || chars + texts[end].Length <= o.Translator.MaxBatchCharacters) && end - start < 60)
            {
                chars += texts[end].Length;
                end++;
            }
            translateScope.Report(start, texts.Count, $"Translating with {o.Translator.Name} ({start}/{texts.Count} blocks)");
            var batch = await o.Translator.TranslateAsync(texts.GetRange(start, end - start), source, o.TargetLanguage, ct).ConfigureAwait(false);
            for (var i = 0; i < batch.Count; i++) translated[start + i] = batch[i];
            start = end;
        }

        scope.Report(0.82, "Sampling page colours");
        var backgrounds = await SampleBackgroundsAsync(o.Input, blocks.Select(b => (b.Page.Index, b.Block.Bounds)).ToList(), ct).ConfigureAwait(false);

        scope.Report(0.9, "Writing the translated PDF");
        await Task.Run(() =>
        {
            using var doc = PdfDocs.Open(o.Input);
            var font = FontFor(o.TargetLanguage);
            foreach (var group in blocks.Select((b, i) => (b.Page, b.Block, Index: i)).GroupBy(x => x.Page.Index))
            {
                using var canvas = PageCanvas.Open(doc.Pages[group.Key]);
                foreach (var (_, block, index) in group)
                {
                    var bounds = block.Bounds;
                    bounds.Inflate(1, 1);
                    canvas.Graphics.DrawRectangle(new XSolidBrush(backgrounds[index]), bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    var style = block.Lines.SelectMany(l => l.Words).FirstOrDefault();
                    var color = style == null || style.Rgb == 0 ? XColors.Black : XColor.FromArgb((int)(style.Rgb >> 16 & 0xFF), (int)(style.Rgb >> 8 & 0xFF), (int)(style.Rgb & 0xFF));
                    var bold = block.Lines.Count(l => l.Bold) * 2 > block.Lines.Count;
                    var run = new TextRun(translated[index] ?? "", font, block.FontSize, bold, false, false, color);
                    // Allow the translation to use a little more room below when it runs long.
                    var room = new Rect(bounds.X, bounds.Y, Math.Max(10, bounds.Width), bounds.Height + Math.Min(8, block.FontSize * 0.6));
                    RichTextLayout.Draw(canvas.Graphics, [new RichParagraph([run])], room, VerticalAnchor.Top, shrinkToFit: true, lineSpacing: 1.05);
                }
            }
            doc.Info.Title = $"{doc.Info.Title} ({Languages.NameOf(o.TargetLanguage)})".Trim();
            PdfDocs.Save(doc, o.OutputPath);
        }, ct).ConfigureAwait(false);
        result.AddOutput(o.OutputPath);

        result.Text = string.Join("\n\n", translated);
        if (o.ExportText)
        {
            var txt = Path.ChangeExtension(o.OutputPath, ".txt");
            await File.WriteAllTextAsync(txt, result.Text, ct).ConfigureAwait(false);
            result.AddOutput(txt);
        }
        result.Summary = $"Translated {blocks.Count} text block{(blocks.Count == 1 ? "" : "s")} from {Languages.NameOf(source)} to {Languages.NameOf(o.TargetLanguage)} with {o.Translator.Name}.";
        scope.Report(1);
        return result;
    }

    /// <summary>A font that has glyphs for the target language.</summary>
    public static string FontFor(string language) => language.ToLowerInvariant() switch
    {
        "zh" or "zh-tw" => "Microsoft YaHei",
        "ja" => "Yu Gothic",
        "ko" => "Malgun Gothic",
        "th" => "Leelawadee UI",
        "si" => "Nirmala UI",
        "ta" or "hi" or "bn" => "Nirmala UI",
        "ar" or "fa" or "ur" or "he" => "Segoe UI",
        _ => "Arial",
    };

    /// <summary>Median colour around each block, so the cover matches tinted backgrounds.</summary>
    private static async Task<XColor[]> SampleBackgroundsAsync(InputFile input, List<(int Page, Rect Bounds)> blocks, CancellationToken ct)
    {
        var colors = new XColor[blocks.Count];
        using var renderer = await PdfRenderer.OpenAsync(input.Path, input.Password, ct).ConfigureAwait(false);
        const double dpi = 50;
        foreach (var pageGroup in blocks.Select((b, i) => (b, i)).GroupBy(x => x.b.Page))
        {
            var px = ImageIO.ToPixels(await renderer.RenderAsync(pageGroup.Key, dpi, ct).ConfigureAwait(false));
            foreach (var (b, i) in pageGroup)
            {
                var samples = new List<(byte R, byte G, byte B)>();
                var scale = dpi / 72.0;
                var r = b.Bounds;
                r.Inflate(3, 3);
                for (var t = 0.0; t <= 1.0; t += 0.05)
                {
                    foreach (var (x, y) in new[] { (r.Left + t * r.Width, r.Top), (r.Left + t * r.Width, r.Bottom), (r.Left, r.Top + t * r.Height), (r.Right, r.Top + t * r.Height) })
                    {
                        var px0 = (int)(x * scale);
                        var py0 = (int)(y * scale);
                        if (px0 < 0 || py0 < 0 || px0 >= px.Width || py0 >= px.Height) continue;
                        var o = (py0 * px.Width + px0) * 4;
                        samples.Add((px.Pixels[o + 2], px.Pixels[o + 1], px.Pixels[o]));
                    }
                }
                if (samples.Count == 0)
                {
                    colors[i] = XColors.White;
                    continue;
                }
                var median = samples.OrderBy(s => s.R + s.G + s.B).ElementAt(samples.Count * 2 / 3);
                colors[i] = XColor.FromArgb(median.R, median.G, median.B);
            }
        }
        return colors;
    }
}
