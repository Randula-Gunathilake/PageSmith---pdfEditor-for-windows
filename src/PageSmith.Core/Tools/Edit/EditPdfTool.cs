using PageSmith.Core.Pdf;

namespace PageSmith.Core.Tools.Edit;

public sealed record EditPdfOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Elements in z-order: later elements are drawn on top.</summary>
    public required IReadOnlyList<PageElement> Elements { get; init; }
}

/// <summary>Adds text, images, shapes, freehand ink and white-out boxes to PDF pages.</summary>
public static class EditPdfTool
{
    public static Task<ToolResult> RunAsync(EditPdfOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (options.Elements.Count == 0) throw new UserFacingException("Add something to the pages first.");
            var scope = new ProgressScope(progress);
            var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
            using var doc = PdfDocs.Open(options.Input);
            Apply(doc, options.Elements, scope, ct);
            PdfDocs.Save(doc, options.OutputPath);
            result.AddOutput(options.OutputPath);
            var pages = options.Elements.Select(e => e.PageIndex).Distinct().Count();
            result.Summary = $"Applied {options.Elements.Count} edit{(options.Elements.Count == 1 ? "" : "s")} on {pages} page{(pages == 1 ? "" : "s")}.";
            scope.Report(1);
            return result;
        }, ct);

    public static void Apply(PdfSharp.Pdf.PdfDocument doc, IReadOnlyList<PageElement> elements, ProgressScope? scope = null, CancellationToken ct = default)
    {
        var byPage = elements.GroupBy(e => e.PageIndex).OrderBy(g => g.Key).ToList();
        for (var i = 0; i < byPage.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var group = byPage[i];
            scope?.Report(i, byPage.Count, $"Editing page {group.Key + 1}");
            if (group.Key < 0 || group.Key >= doc.PageCount) continue;
            using var canvas = PageCanvas.Open(doc.Pages[group.Key]);
            foreach (var element in group) PageElementRenderer.Draw(canvas.Graphics, element);
        }
    }
}
