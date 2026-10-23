using PageSmith.Core.Ocr;
using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;
using PageSmith.Core.Text;

namespace PageSmith.Core.Tools.Optimize;

public sealed record OcrOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>BCP-47 tag of an installed Windows OCR language; null uses the user's languages.</summary>
    public string? Language { get; init; }

    /// <summary>Pages to process, e.g. "1-5"; null for all.</summary>
    public string? Pages { get; init; }

    /// <summary>Leaves pages that already contain selectable text unchanged.</summary>
    public bool SkipPagesWithText { get; init; } = true;

    /// <summary>Rendering resolution for recognition.</summary>
    public double Dpi { get; init; } = 300;

    /// <summary>Also writes the recognized text to a .txt file next to the PDF.</summary>
    public bool ExportText { get; init; }
}

/// <summary>Makes scanned PDFs searchable and selectable by adding an invisible OCR text layer.</summary>
public static class OcrTool
{
    public static async Task<ToolResult> RunAsync(OcrOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
        var engine = OcrService.CreateEngine(options.Language);
        var font = OcrService.FontFor(engine.RecognizerLanguage.LanguageTag);

        using var renderer = await PdfRenderer.OpenAsync(options.Input.Path, options.Input.Password, ct).ConfigureAwait(false);
        var pages = PageRange.Parse(options.Pages, renderer.PageCount);
        var existingText = options.SkipPagesWithText
            ? TextExtractor.ReadPages(options.Input, pages, ct).ToDictionary(p => p.Index, p => p.HasText)
            : [];

        var recognized = new List<OcrPage>();
        for (var i = 0; i < pages.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var index = pages[i];
            scope.Report(i, pages.Count + 1, $"Recognizing page {index + 1} of {renderer.PageCount}");
            if (existingText.TryGetValue(index, out var hasText) && hasText) continue;
            recognized.Add(await OcrService.RecognizePageAsync(engine, renderer, index, options.Dpi, ct).ConfigureAwait(false));
        }

        scope.Report(pages.Count, pages.Count + 1, "Adding text layer");
        var wordCount = 0;
        await Task.Run(() =>
        {
            using var doc = PdfDocs.Open(options.Input);
            foreach (var page in recognized)
            {
                ct.ThrowIfCancellationRequested();
                InvisibleText.AddWords(doc.Pages[page.PageIndex], page.Words, font);
                wordCount += page.Words.Count;
            }
            PdfDocs.Save(doc, options.OutputPath);
        }, ct).ConfigureAwait(false);
        result.AddOutput(options.OutputPath);

        if (options.ExportText)
        {
            var txt = Path.ChangeExtension(options.OutputPath, ".txt");
            await File.WriteAllTextAsync(txt, string.Join("\n\n", recognized.Select(p => p.Text)), ct).ConfigureAwait(false);
            result.AddOutput(txt);
        }

        var skipped = pages.Count - recognized.Count;
        result.Summary = recognized.Count == 0
            ? "Every page already had selectable text; nothing needed OCR."
            : $"Recognized {wordCount:N0} words on {recognized.Count} page{(recognized.Count == 1 ? "" : "s")} ({engine.RecognizerLanguage.DisplayName}).";
        if (skipped > 0 && recognized.Count > 0)
            result.Warnings.Add($"{skipped} page{(skipped == 1 ? "" : "s")} already had text and {(skipped == 1 ? "was" : "were")} left unchanged.");
        result.Text = string.Join("\n\n", recognized.Select(p => p.Text));
        scope.Report(1);
        return result;
    }
}
