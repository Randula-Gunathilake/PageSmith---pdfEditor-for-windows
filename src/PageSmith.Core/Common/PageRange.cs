namespace PageSmith.Core.Common;

/// <summary>
/// Parses page selections typed by users, such as <c>1-3, 5, 8-</c>, <c>last</c>, <c>odd</c> or <c>even</c>.
/// Page numbers in the text are 1-based; results are 0-based page indices.
/// </summary>
public static class PageRange
{
    /// <summary>Returns the selected page indices in the order written. Duplicates are dropped unless allowed.</summary>
    public static IReadOnlyList<int> Parse(string? spec, int pageCount, bool allowDuplicates = false)
    {
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var (start, end) in ParseRanges(spec, pageCount))
        {
            var step = start <= end ? 1 : -1;
            for (var i = start; ; i += step)
            {
                if (allowDuplicates || seen.Add(i)) result.Add(i);
                if (i == end) break;
            }
        }
        return result;
    }

    /// <summary>Returns the ranges as inclusive 0-based (start, end) pairs. A blank spec selects every page.</summary>
    public static IReadOnlyList<(int Start, int End)> ParseRanges(string? spec, int pageCount)
    {
        if (pageCount <= 0) return [];
        if (string.IsNullOrWhiteSpace(spec)) return [(0, pageCount - 1)];

        var ranges = new List<(int, int)>();
        foreach (var raw in spec.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim().ToLowerInvariant();
            if (token.Length == 0) continue;
            switch (token)
            {
                case "all" or "*":
                    ranges.Add((0, pageCount - 1));
                    continue;
                case "odd":
                    for (var i = 0; i < pageCount; i += 2) ranges.Add((i, i));
                    continue;
                case "even":
                    for (var i = 1; i < pageCount; i += 2) ranges.Add((i, i));
                    continue;
            }

            var dash = token.IndexOf('-');
            if (dash < 0)
            {
                var page = ParseNumber(token, pageCount, raw);
                ranges.Add((page, page));
                continue;
            }

            var left = token[..dash].Trim();
            var right = token[(dash + 1)..].Trim();
            var start = left.Length == 0 ? 0 : ParseNumber(left, pageCount, raw);
            var end = right.Length == 0 ? pageCount - 1 : ParseNumber(right, pageCount, raw);
            ranges.Add((start, end));
        }
        return ranges;
    }

    /// <summary>Formats 0-based indices as compact 1-based text, e.g. <c>1-3, 5</c>.</summary>
    public static string Format(IEnumerable<int> indices)
    {
        var sorted = indices.Distinct().Order().ToList();
        var parts = new List<string>();
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            parts.Add(i == j ? $"{sorted[i] + 1}" : $"{sorted[i] + 1}-{sorted[j] + 1}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private static int ParseNumber(string text, int pageCount, string token)
    {
        if (text is "last" or "end" or "z") return pageCount - 1;
        if (!int.TryParse(text, out var n) || n < 1)
            throw new FormatException($"\"{token.Trim()}\" is not a valid page number or range.");
        if (n > pageCount)
            throw new FormatException($"Page {n} does not exist; the document has {pageCount} page{(pageCount == 1 ? "" : "s")}.");
        return n - 1;
    }
}
