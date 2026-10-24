namespace PageSmith.Core.Tools.Edit;

public enum PageFilter
{
    All,
    Portrait,
    Landscape,
}

/// <summary>A file to rotate: <see cref="Rotation"/> applies to every page; <see cref="PageRotations"/> adds per-page turns (0-based index → degrees).</summary>
public sealed record RotateItem(InputFile File, int Rotation, IReadOnlyDictionary<int, int>? PageRotations = null);

public sealed record RotateOptions
{
    public required IReadOnlyList<RotateItem> Items { get; init; }
    public required string OutputDirectory { get; init; }

    /// <summary>Limits the whole-file rotation to portrait or landscape pages.</summary>
    public PageFilter Filter { get; init; } = PageFilter.All;
}

/// <summary>Rotates whole documents or individual pages by multiples of 90°.</summary>
public static class RotateTool
{
    public static Task<ToolResult> RunAsync(RotateOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var scope = new ProgressScope(progress);
            var result = new ToolResult();
            var rotatedPages = 0;
            for (var f = 0; f < options.Items.Count; f++)
            {
                ct.ThrowIfCancellationRequested();
                var item = options.Items[f];
                scope.Report(f, options.Items.Count, $"Rotating {item.File.Name}");
                result.InputBytes += new FileInfo(item.File.Path).Length;
                using var doc = PdfDocs.Open(item.File);
                for (var i = 0; i < doc.PageCount; i++)
                {
                    var page = doc.Pages[i];
                    var turn = 0;
                    var geometry = Pdf.PageGeometry.From(page);
                    var landscape = geometry.Width > geometry.Height;
                    if (options.Filter == PageFilter.All || options.Filter == PageFilter.Landscape == landscape) turn += item.Rotation;
                    if (item.PageRotations?.TryGetValue(i, out var extra) == true) turn += extra;
                    turn = PageCopier.Normalize(turn);
                    if (turn == 0) continue;
                    page.Rotate = PageCopier.Normalize(page.Rotate + turn);
                    rotatedPages++;
                }
                var output = OutputPaths.ForInput(item.File.Path, "rotated", ".pdf", options.OutputDirectory);
                PdfDocs.Save(doc, output);
                result.AddOutput(output);
            }
            result.Summary = $"Rotated {rotatedPages} page{(rotatedPages == 1 ? "" : "s")} in {options.Items.Count} file{(options.Items.Count == 1 ? "" : "s")}.";
            scope.Report(1);
            return result;
        }, ct);
}
