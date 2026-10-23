using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PageSmith.Core.Tools.Organize;

/// <summary>
/// One page of the organized document: page <see cref="PageIndex"/> of source <see cref="SourceIndex"/>,
/// or a blank page when <see cref="SourceIndex"/> is negative.
/// </summary>
public sealed record OrganizedPage(int SourceIndex, int PageIndex, int Rotation = 0, double BlankWidth = 595.28, double BlankHeight = 841.89)
{
    public static OrganizedPage Blank(double width = 595.28, double height = 841.89) => new(-1, -1, 0, width, height);
    public bool IsBlank => SourceIndex < 0;
}

public sealed record OrganizeOptions
{
    public required IReadOnlyList<InputFile> Sources { get; init; }
    public required IReadOnlyList<OrganizedPage> Pages { get; init; }
    public required string OutputPath { get; init; }
}

/// <summary>Builds a document from pages of one or more PDFs in any order, rotated, duplicated or blank.</summary>
public static class OrganizeTool
{
    public static Task<ToolResult> RunAsync(OrganizeOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (options.Pages.Count == 0) throw new UserFacingException("The document needs at least one page.");
            var scope = new ProgressScope(progress);
            var result = new ToolResult();
            foreach (var s in options.Sources) result.InputBytes += new FileInfo(s.Path).Length;

            using var copier = new PageCopier();
            using var output = PdfDocs.Create(Path.GetFileNameWithoutExtension(options.OutputPath));
            for (var i = 0; i < options.Pages.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                scope.Report(i, options.Pages.Count, "Arranging pages");
                var p = options.Pages[i];
                if (p.IsBlank)
                {
                    var blank = output.AddPage();
                    blank.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(p.BlankWidth, p.BlankHeight));
                    if (p.Rotation != 0) blank.Rotate = PageCopier.Normalize(p.Rotation);
                }
                else
                {
                    copier.Copy(options.Sources[p.SourceIndex], p.PageIndex, output, p.Rotation);
                }
            }
            PdfDocs.Save(output, options.OutputPath);
            result.AddOutput(options.OutputPath);
            result.Summary = $"Saved a {options.Pages.Count}-page document.";
            scope.Report(1);
            return result;
        }, ct);
}
