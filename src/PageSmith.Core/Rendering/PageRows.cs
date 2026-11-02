using System.Windows;

namespace PageSmith.Core.Rendering;

/// <summary>How a reader arranges pages in rows.</summary>
public enum PageLayout
{
    /// <summary>One page per row, and more side by side once they are small enough to fit, like Word.</summary>
    Auto,

    /// <summary>One page per row.</summary>
    Single,

    /// <summary>Two pages per row.</summary>
    Two,

    /// <summary>Two pages per row with the first page on its own at the right, like an open book.</summary>
    Book,
}

/// <summary>
/// Where pages go when they are laid out in rows, in the units of the sizes given (display units). Auto puts as
/// many pages in a row as fit across, like Word; two-page layouts are spreads whose pages meet in the middle.
/// Rows are centred across, and pages vertically in their row.
/// </summary>
public sealed class PageRows
{
    /// <summary>More pages in a row than this would be too small to tell apart.</summary>
    public const int MaxPerRow = 16;

    private readonly double[] _tops;
    private readonly double[] _x;
    private readonly int[] _row;
    private readonly int[] _rowStart;
    private readonly double[] _rowTops;
    private readonly double[] _rowHeights;
    private readonly double[] _rowWidths;

    private PageRows(double[] tops, double[] x, int[] row, int[] rowStart, double[] rowTops, double[] rowHeights, double[] rowWidths, double height)
    {
        _tops = tops;
        _x = x;
        _row = row;
        _rowStart = rowStart;
        _rowTops = rowTops;
        _rowHeights = rowHeights;
        _rowWidths = rowWidths;
        Height = height;
        PagesWidth = rowWidths.Length == 0 ? 0 : rowWidths.Max();
        for (var r = 0; r < rowTops.Length; r++) MostPerRow = Math.Max(MostPerRow, rowStart[r + 1] - rowStart[r]);
    }

    public int PageCount => _tops.Length;
    public int RowCount => _rowTops.Length;

    /// <summary>The most pages side by side in any row.</summary>
    public int MostPerRow { get; }

    /// <summary>The width of the widest row, without the edge around the pages.</summary>
    public double PagesWidth { get; }

    /// <summary>The height of everything, edges included.</summary>
    public double Height { get; }

    public double TopOf(int page) => _tops[page];
    public int RowOf(int page) => _row[page];
    public double RowTop(int row) => _rowTops[row];
    public double RowHeight(int row) => _rowHeights[row];
    public int RowStart(int row) => _rowStart[row];

    /// <summary>The page after the last one of the row.</summary>
    public int RowEnd(int row) => _rowStart[row + 1];

    /// <summary>The left of a page when the left of the widest row is at <paramref name="pagesLeft"/>.</summary>
    public double LeftOf(int page, double pagesLeft) => pagesLeft + (PagesWidth - _rowWidths[_row[page]]) / 2 + _x[page];

    /// <summary>The row at a height (the one above, in the gap between two).</summary>
    public int RowAt(double y)
    {
        if (_rowTops.Length == 0) return 0;
        var i = Array.BinarySearch(_rowTops, y);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, _rowTops.Length - 1);
    }

    /// <summary>Whether another arrangement puts the same pages in each row.</summary>
    public bool SameRowsAs(PageRows other) => _rowStart.AsSpan().SequenceEqual(other._rowStart);

    /// <summary>The pages that share a row with <paramref name="page"/> in a two-page layout, or just the page.</summary>
    public static (int First, int Count) RowAround(PageLayout layout, int page, int pageCount)
    {
        if (pageCount <= 1) return (page, 1);
        switch (layout)
        {
            case PageLayout.Two:
                var first = page - page % 2;
                return (first, Math.Min(2, pageCount - first));
            case PageLayout.Book when page == 0:
                return (0, 1);
            case PageLayout.Book:
                var start = page - (page - 1) % 2;
                return (start, Math.Min(2, pageCount - start));
            default:
                return (page, 1);
        }
    }

    /// <summary>
    /// Lays pages out top to bottom. <paramref name="width"/> is the room across, which decides how many pages
    /// Auto puts in a row; <paramref name="edge"/> is the space above the first row and below the last.
    /// </summary>
    public static PageRows Arrange(IReadOnlyList<Size> sizes, PageLayout layout, double width, double gap, double edge)
    {
        var count = sizes.Count;
        var spreads = layout is PageLayout.Two or PageLayout.Book && count > 1;
        // Spreads have two columns as wide as the widest page, meeting in the middle.
        var cell = 0.0;
        foreach (var s in sizes) cell = Math.Max(cell, s.Width);

        var tops = new double[count];
        var x = new double[count];
        var row = new int[count];
        var rowStart = new List<int>();
        var rowTops = new List<double>();
        var rowHeights = new List<double>();
        var rowWidths = new List<double>();
        var y = edge;
        for (var start = 0; start < count;)
        {
            int end;
            double rowWidth;
            if (spreads)
            {
                // An open book has its cover on its own, on the right.
                var cover = layout == PageLayout.Book && start == 0;
                end = Math.Min(count, start + (cover ? 1 : 2));
                for (var p = start; p < end; p++)
                    x[p] = cover || p > start ? cell + gap : cell - sizes[p].Width;
                rowWidth = 2 * cell + gap;
            }
            else
            {
                // As many as fit across (one at least); one per row for the single layout.
                end = start + 1;
                rowWidth = sizes[start].Width;
                while (layout == PageLayout.Auto && end < count && end - start < MaxPerRow && rowWidth + gap + sizes[end].Width <= width + 0.5)
                    rowWidth += gap + sizes[end++].Width;
                var across = 0.0;
                for (var p = start; p < end; p++)
                {
                    x[p] = across;
                    across += sizes[p].Width + gap;
                }
            }
            var height = 0.0;
            for (var p = start; p < end; p++) height = Math.Max(height, sizes[p].Height);
            for (var p = start; p < end; p++)
            {
                row[p] = rowStart.Count;
                tops[p] = y + (height - sizes[p].Height) / 2;
            }
            rowStart.Add(start);
            rowTops.Add(y);
            rowHeights.Add(height);
            rowWidths.Add(rowWidth);
            y += height + gap;
            start = end;
        }
        rowStart.Add(count);
        var total = count == 0 ? 0 : y - gap + edge;
        return new PageRows(tops, x, row, [.. rowStart], [.. rowTops], [.. rowHeights], [.. rowWidths], total);
    }
}
