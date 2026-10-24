using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;
using PdfSharp.Pdf;

namespace PageSmith.Core.Tools.Organize;

public enum SplitMode
{
    /// <summary>User-defined ranges such as "1-3" and "4-9".</summary>
    CustomRanges,

    /// <summary>A new file every N pages.</summary>
    FixedRange,

    /// <summary>Every page becomes its own file.</summary>
    ExtractAll,

    /// <summary>Only the chosen pages, as one file or one file per page.</summary>
    SelectPages,

    /// <summary>Files no larger than a maximum size (iLovePDF Premium).</summary>
    BySize,

    /// <summary>Detects where one document ends and the next begins.</summary>
    Smart,
}

public enum SmartSplitStrategy
{
    /// <summary>Bookmarks if the PDF has them, otherwise blank separator pages.</summary>
    Auto,
    Bookmarks,
    BlankPages,
}

public sealed record SplitOptions
{
    public required InputFile Input { get; init; }
    public required string OutputDirectory { get; init; }
    public SplitMode Mode { get; init; } = SplitMode.CustomRanges;

    /// <summary>For <see cref="SplitMode.CustomRanges"/>: one entry per output, e.g. "1-3".</summary>
    public IReadOnlyList<string> Ranges { get; init; } = [];

    /// <summary>For <see cref="SplitMode.FixedRange"/>: pages per file.</summary>
    public int PagesPerFile { get; init; } = 1;

    /// <summary>For <see cref="SplitMode.SelectPages"/>: pages to extract, e.g. "1, 5-8".</summary>
    public string? SelectedPages { get; init; }

    /// <summary>Puts all ranges or selected pages into one file instead of one file each.</summary>
    public bool MergeIntoOne { get; init; }

    /// <summary>For <see cref="SplitMode.BySize"/>: maximum size per file in bytes.</summary>
    public long MaxBytesPerFile { get; init; } = 5 * 1024 * 1024;

    /// <summary>For <see cref="SplitMode.BySize"/>: compress the document first so fewer files are needed.</summary>
    public bool AllowCompression { get; init; }

    public SmartSplitStrategy SmartStrategy { get; init; } = SmartSplitStrategy.Auto;

    /// <summary>For smart splitting on blank pages: drop the blank separator pages.</summary>
    public bool DropSeparatorPages { get; init; } = true;
}

/// <summary>Splits one PDF into several (Split PDF and Extract pages tools).</summary>
public static class SplitTool
{
    public static async Task<ToolResult> RunAsync(SplitOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
        var input = options.Input;
        string? tempCompressed = null;

        try
        {
            if (options.Mode == SplitMode.BySize && options.AllowCompression)
            {
                scope.Report(0, "Compressing before splitting");
                tempCompressed = Path.Combine(Path.GetTempPath(), $"pagesmith_{Guid.NewGuid():N}.pdf");
                await Optimize.CompressTool.RunAsync(new Optimize.CompressOptions
                {
                    Input = input,
                    OutputPath = tempCompressed,
                    Level = Optimize.CompressionLevel.Recommended,
                }, scope.Slice(0, 0.4).AsProgress(), ct).ConfigureAwait(false);
                input = new InputFile(tempCompressed);
                scope = scope.Slice(0.4, 1);
            }

            using var copier = new PageCopier();
            var pageCount = copier.PageCount(input);
            var parts = await PlanAsync(options, input, copier, pageCount, scope.Slice(0, 0.3), ct).ConfigureAwait(false);
            if (parts.Count == 0) throw new UserFacingException("Nothing to split: no pages were selected.");

            var stem = Path.GetFileNameWithoutExtension(options.Input.Path);
            var single = parts.Count == 1;
            var dir = single ? options.OutputDirectory : OutputPaths.UniqueDirectory(options.OutputDirectory, $"{stem} (split)");
            var write = scope.Slice(0.3, 1);
            for (var i = 0; i < parts.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (label, pages) = parts[i];
                write.Report(i, parts.Count, $"Writing {label}");
                var path = OutputPaths.Unique(dir, $"{stem}_{label}.pdf");
                copier.WritePages(input, pages, path);
                result.AddOutput(path);
            }
            write.Report(1);
            result.Summary = single
                ? $"Created 1 PDF with {parts[0].Pages.Count} page{(parts[0].Pages.Count == 1 ? "" : "s")}."
                : $"Created {parts.Count} PDFs in \"{Path.GetFileName(dir)}\".";
            return result;
        }
        finally
        {
            if (tempCompressed != null) TryDelete(tempCompressed);
        }
    }

    /// <summary>Works out the output files: a label and the 0-based pages of each.</summary>
    internal static async Task<List<(string Label, IReadOnlyList<int> Pages)>> PlanAsync(
        SplitOptions o, InputFile input, PageCopier copier, int pageCount, ProgressScope scope, CancellationToken ct)
    {
        var parts = new List<(string, IReadOnlyList<int>)>();
        switch (o.Mode)
        {
            case SplitMode.CustomRanges:
            {
                var ranges = o.Ranges.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
                if (ranges.Count == 0) throw new UserFacingException("Add at least one page range.");
                var all = ranges.Select(r => (Label: Label(r), Pages: PageRange.Parse(r, pageCount, allowDuplicates: true))).ToList();
                if (o.MergeIntoOne)
                    parts.Add(("ranges", all.SelectMany(a => a.Pages).ToList()));
                else
                    parts.AddRange(all.Select(a => (a.Label, a.Pages)));
                break;
            }
            case SplitMode.FixedRange:
            {
                var n = Math.Max(1, o.PagesPerFile);
                for (var start = 0; start < pageCount; start += n)
                {
                    var end = Math.Min(pageCount, start + n) - 1;
                    parts.Add((start == end ? $"{start + 1}" : $"{start + 1}-{end + 1}", Enumerable.Range(start, end - start + 1).ToList()));
                }
                break;
            }
            case SplitMode.ExtractAll:
                for (var i = 0; i < pageCount; i++) parts.Add(($"page_{i + 1}", [i]));
                break;
            case SplitMode.SelectPages:
            {
                var pages = PageRange.Parse(o.SelectedPages, pageCount);
                if (o.MergeIntoOne) parts.Add(("extracted", pages));
                else parts.AddRange(pages.Select(p => ($"page_{p + 1}", (IReadOnlyList<int>)[p])));
                break;
            }
            case SplitMode.BySize:
                parts.AddRange(PlanBySize(input, copier, pageCount, o.MaxBytesPerFile, scope, ct));
                break;
            case SplitMode.Smart:
                parts.AddRange(await PlanSmartAsync(o, input, copier, pageCount, scope, ct).ConfigureAwait(false));
                break;
        }
        return parts;
    }

    /// <summary>Greedy packing: each file takes as many pages as fit, found with galloping + binary search.</summary>
    private static List<(string, IReadOnlyList<int>)> PlanBySize(InputFile input, PageCopier copier, int pageCount, long maxBytes, ProgressScope scope, CancellationToken ct)
    {
        if (maxBytes < 1024) throw new UserFacingException("The maximum file size must be at least 1 KB.");
        var parts = new List<(string, IReadOnlyList<int>)>();
        var start = 0;
        while (start < pageCount)
        {
            ct.ThrowIfCancellationRequested();
            scope.Report(start, pageCount, "Measuring page sizes");
            bool Fits(int count) => MeasureBytes(input, copier, start, count) <= maxBytes;

            var good = 1;
            if (Fits(1))
            {
                var probe = 2;
                while (start + probe <= pageCount && Fits(probe))
                {
                    good = probe;
                    probe *= 2;
                }
                var bad = Math.Min(probe, pageCount - start + 1);
                while (bad - good > 1)
                {
                    var mid = (good + bad) / 2;
                    if (Fits(mid)) good = mid;
                    else bad = mid;
                }
            }
            var end = start + good - 1;
            parts.Add((start == end ? $"{start + 1}" : $"{start + 1}-{end + 1}", Enumerable.Range(start, good).ToList()));
            start = end + 1;
        }
        return parts;
    }

    private static long MeasureBytes(InputFile input, PageCopier copier, int start, int count)
    {
        using var doc = new PdfDocument();
        for (var i = start; i < start + count; i++) copier.Copy(input, i, doc);
        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.Length;
    }

    private static async Task<List<(string, IReadOnlyList<int>)>> PlanSmartAsync(
        SplitOptions o, InputFile input, PageCopier copier, int pageCount, ProgressScope scope, CancellationToken ct)
    {
        var starts = new SortedSet<int> { 0 };
        var skip = new HashSet<int>();
        var strategy = o.SmartStrategy;

        if (strategy is SmartSplitStrategy.Auto or SmartSplitStrategy.Bookmarks)
        {
            var doc = copier.Source(input);
            foreach (var outline in doc.Outlines)
            {
                var page = outline.DestinationPage;
                if (page == null) continue;
                var index = IndexOf(doc, page);
                if (index >= 0) starts.Add(index);
            }
            if (starts.Count > 1 || strategy == SmartSplitStrategy.Bookmarks)
                return Ranges(starts, skip, pageCount);
        }

        // Blank separator pages: a page with no text and (almost) no ink ends the current document.
        var hasText = Text.TextExtractor.ReadPages(input, null, ct).Select(p => p.HasText).ToList();
        using var renderer = await PdfRenderer.OpenAsync(input.Path, input.Password, ct).ConfigureAwait(false);
        for (var i = 0; i < pageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            scope.Report(i, pageCount, "Looking for separator pages");
            if (i < hasText.Count && hasText[i]) continue;
            if (!await PageAnalysis.IsBlankAsync(renderer, i, ct: ct).ConfigureAwait(false)) continue;
            if (o.DropSeparatorPages) skip.Add(i);
            if (i + 1 < pageCount) starts.Add(i + 1);
        }
        return Ranges(starts, skip, pageCount);
    }

    private static List<(string, IReadOnlyList<int>)> Ranges(SortedSet<int> starts, HashSet<int> skip, int pageCount)
    {
        var list = starts.ToList();
        var parts = new List<(string, IReadOnlyList<int>)>();
        for (var i = 0; i < list.Count; i++)
        {
            var end = i + 1 < list.Count ? list[i + 1] : pageCount;
            var pages = Enumerable.Range(list[i], end - list[i]).Where(p => !skip.Contains(p)).ToList();
            if (pages.Count == 0) continue;
            parts.Add(($"part_{parts.Count + 1}", pages));
        }
        return parts;
    }

    private static int IndexOf(PdfDocument doc, PdfPage page)
    {
        for (var i = 0; i < doc.PageCount; i++)
            if (ReferenceEquals(doc.Pages[i], page) || doc.Pages[i].Reference == page.Reference) return i;
        return -1;
    }

    private static string Label(string range) => OutputPaths.Sanitize(range.Replace(" ", "").Replace(',', '_'));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* temp file */ }
    }
}
