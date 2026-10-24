namespace PageSmith.Core.Tools.Organize;

/// <summary>One document to merge, optionally rotated or limited to some pages.</summary>
public sealed record MergeItem(InputFile File, int Rotation = 0, string? Pages = null);

public sealed record MergeOptions
{
    public required IReadOnlyList<MergeItem> Items { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Adds one bookmark per merged file, named after the file.</summary>
    public bool AddBookmarks { get; init; } = true;
}

/// <summary>Combines several PDFs into one, in the given order.</summary>
public static class MergeTool
{
    public static Task<ToolResult> RunAsync(MergeOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() => Run(options, progress, ct), ct);

    public static ToolResult Run(MergeOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        if (options.Items.Count == 0) throw new UserFacingException("Add at least one PDF to merge.");
        var scope = new ProgressScope(progress);
        var result = new ToolResult();
        using var copier = new PageCopier();
        using var output = PdfDocs.Create(Path.GetFileNameWithoutExtension(options.OutputPath));

        for (var f = 0; f < options.Items.Count; f++)
        {
            ct.ThrowIfCancellationRequested();
            var item = options.Items[f];
            scope.Report(f, options.Items.Count, $"Adding {item.File.Name}");
            result.InputBytes += new FileInfo(item.File.Path).Length;

            var count = copier.PageCount(item.File);
            var pages = PageRange.Parse(item.Pages, count, allowDuplicates: true);
            var firstPage = output.PageCount;
            foreach (var index in pages)
                copier.Copy(item.File, index, output, item.Rotation);

            if (options.AddBookmarks && output.PageCount > firstPage)
                output.Outlines.Add(Path.GetFileNameWithoutExtension(item.File.Path), output.Pages[firstPage], true);
        }

        scope.Report(0.95, "Saving");
        var pageCount = output.PageCount;
        PdfDocs.Save(output, options.OutputPath);
        result.AddOutput(options.OutputPath);
        result.Summary = $"Merged {options.Items.Count} file{(options.Items.Count == 1 ? "" : "s")} into {pageCount} pages.";
        scope.Report(1);
        return result;
    }
}
