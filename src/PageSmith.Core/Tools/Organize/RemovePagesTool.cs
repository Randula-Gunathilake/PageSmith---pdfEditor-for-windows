namespace PageSmith.Core.Tools.Organize;

public sealed record RemovePagesOptions
{
    public required InputFile Input { get; init; }

    /// <summary>Pages to delete, e.g. "2, 5-7".</summary>
    public required string Pages { get; init; }

    public required string OutputPath { get; init; }
}

/// <summary>Deletes pages from a PDF.</summary>
public static class RemovePagesTool
{
    public static Task<ToolResult> RunAsync(RemovePagesOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var scope = new ProgressScope(progress);
            using var copier = new PageCopier();
            var count = copier.PageCount(options.Input);
            var remove = PageRange.Parse(options.Pages, count).ToHashSet();
            if (remove.Count == 0) throw new UserFacingException("Select the pages to remove.");
            if (remove.Count >= count) throw new UserFacingException("You cannot remove every page of the document.");

            scope.Report(0.3, "Removing pages");
            ct.ThrowIfCancellationRequested();
            var keep = Enumerable.Range(0, count).Where(i => !remove.Contains(i)).ToList();
            copier.WritePages(options.Input, keep, options.OutputPath);

            var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
            result.AddOutput(options.OutputPath);
            result.Summary = $"Removed {remove.Count} page{(remove.Count == 1 ? "" : "s")}; {keep.Count} remain.";
            scope.Report(1);
            return result;
        }, ct);
}
