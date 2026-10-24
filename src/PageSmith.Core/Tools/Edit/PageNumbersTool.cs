using System.Windows;
using PageSmith.Core.Pdf;
using PdfSharp.Drawing;

namespace PageSmith.Core.Tools.Edit;

public enum NumberMargin
{
    Small,
    Recommended,
    Big,
}

public sealed record PageNumberOptions
{
    public required IReadOnlyList<InputFile> Files { get; init; }
    public required string OutputDirectory { get; init; }

    public PagePosition Position { get; init; } = PagePosition.BottomCenter;
    public NumberMargin Margin { get; init; } = NumberMargin.Recommended;

    /// <summary>Facing pages: left and right positions swap on even pages, as in a printed book.</summary>
    public bool FacingPages { get; init; }

    /// <summary>The first page is a cover: it gets no number and the numbering starts on page 2.</summary>
    public bool FirstPageIsCover { get; init; }

    public int FirstNumber { get; init; } = 1;

    /// <summary>Pages to number (1-based, inclusive); null means first/last page.</summary>
    public int? FromPage { get; init; }
    public int? ToPage { get; init; }

    /// <summary>Template: {n} number, {p} total pages, {r} pages in range, {rf} last number of the range, {f} file name.</summary>
    public string Template { get; init; } = "{n}";

    public TextStyle Style { get; init; } = new() { Size = 11 };
    public double Opacity { get; init; } = 1;
}

/// <summary>Adds page numbers with the position, range, template and typography options of iLovePDF.</summary>
public static class PageNumbersTool
{
    public static Task<ToolResult> RunAsync(PageNumberOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var scope = new ProgressScope(progress);
            var result = new ToolResult();
            for (var f = 0; f < options.Files.Count; f++)
            {
                ct.ThrowIfCancellationRequested();
                var file = options.Files[f];
                scope.Report(f, options.Files.Count, $"Numbering {file.Name}");
                result.InputBytes += new FileInfo(file.Path).Length;
                using var doc = PdfDocs.Open(file);
                Apply(doc, options, Path.GetFileNameWithoutExtension(file.Path));
                var output = OutputPaths.ForInput(file.Path, "numbered", ".pdf", options.OutputDirectory);
                PdfDocs.Save(doc, output);
                result.AddOutput(output);
            }
            result.Summary = $"Added page numbers to {options.Files.Count} file{(options.Files.Count == 1 ? "" : "s")}.";
            scope.Report(1);
            return result;
        }, ct);

    public static void Apply(PdfSharp.Pdf.PdfDocument doc, PageNumberOptions o, string fileName)
    {
        var total = doc.PageCount;
        var from = Math.Clamp(o.FromPage ?? (o.FirstPageIsCover ? 2 : 1), 1, total);
        var to = Math.Clamp(o.ToPage ?? total, from, total);
        var inRange = to - from + 1;
        var margin = o.Margin switch { NumberMargin.Small => 18.0, NumberMargin.Big => 54.0, _ => 36.0 };
        var font = o.Style.ToFont();
        var brush = new XSolidBrush(o.Style.ToColor(o.Opacity));

        for (var pageNo = from; pageNo <= to; pageNo++)
        {
            var number = o.FirstNumber + (pageNo - from);
            var text = Format(o.Template, number, total, inRange, o.FirstNumber + inRange - 1, fileName);
            var position = o.Position;
            // In a book the first page is a right-hand page, so even pages are on the left.
            if (o.FacingPages && pageNo % 2 == 0) position = PagePositions.Mirror(position);

            using var canvas = PageCanvas.Open(doc.Pages[pageNo - 1]);
            var size = canvas.Graphics.MeasureString(text, font);
            var origin = PagePositions.Place(position, new Size(canvas.Width, canvas.Height), new Size(size.Width, size.Height), margin);
            canvas.Graphics.DrawString(text, font, brush, new XRect(origin.X, origin.Y, size.Width, size.Height), XStringFormats.TopLeft);
        }
    }

    public static string Format(string template, int n, int total, int range, int rangeLast, string fileName) =>
        template.Replace("{rf}", rangeLast.ToString())
            .Replace("{n}", n.ToString())
            .Replace("{p}", total.ToString())
            .Replace("{r}", range.ToString())
            .Replace("{f}", fileName)
            .Replace("{d}", DateTime.Now.ToShortDateString());
}
