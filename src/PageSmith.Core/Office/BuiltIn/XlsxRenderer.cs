using ClosedXML.Excel;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

namespace PageSmith.Core.Office.BuiltIn;

/// <summary>
/// Renders spreadsheets (.xlsx, .xlsm, .csv) to PDF without Microsoft Office. Each visible worksheet
/// starts on a new page; wide sheets switch to landscape and are scaled to fit the page width.
/// </summary>
public static class XlsxRenderer
{
    public static void Render(string path, string pdfPath)
    {
        PdfDocs.EnsureInitialized();
        using var workbook = Path.GetExtension(path).Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? LoadCsv(path)
            : new XLWorkbook(path);

        var document = new Document();
        document.Styles[StyleNames.Normal]!.Font.Name = "Calibri";
        var rendered = 0;
        foreach (var sheet in workbook.Worksheets)
        {
            if (sheet.Visibility != XLWorksheetVisibility.Visible) continue;
            var used = sheet.RangeUsed(XLCellsUsedOptions.Contents | XLCellsUsedOptions.MergedRanges);
            if (used == null) continue;
            AddSheet(document, sheet, used);
            rendered++;
        }
        if (rendered == 0)
        {
            var section = document.AddSection();
            section.AddParagraph("(This workbook is empty.)");
        }

        var pdf = new PdfDocumentRenderer { Document = document };
        pdf.RenderDocument();
        pdf.PdfDocument.Info.Creator = "PageSmith";
        pdf.PdfDocument.Save(pdfPath);
    }

    private static XLWorkbook LoadCsv(string path)
    {
        var wb = new XLWorkbook();
        var ws = wb.AddWorksheet(OutputPaths.Sanitize(Path.GetFileNameWithoutExtension(path)).Truncate(31));
        var row = 1;
        foreach (var line in File.ReadLines(path))
        {
            var col = 1;
            foreach (var value in SplitCsv(line)) ws.Cell(row, col++).Value = value;
            row++;
        }
        return wb;
    }

    internal static IEnumerable<string> SplitCsv(string line)
    {
        var sb = new StringBuilder();
        var quoted = false;
        var separator = line.Count(c => c == ';') > line.Count(c => c == ',') ? ';' : ',';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else sb.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == separator) { yield return sb.ToString(); sb.Clear(); }
            else sb.Append(c);
        }
        yield return sb.ToString();
    }

    private static void AddSheet(Document document, IXLWorksheet sheet, IXLRange used)
    {
        var firstCol = used.RangeAddress.FirstAddress.ColumnNumber;
        var lastCol = used.RangeAddress.LastAddress.ColumnNumber;
        var firstRow = used.RangeAddress.FirstAddress.RowNumber;
        var lastRow = used.RangeAddress.LastAddress.RowNumber;
        var widths = Enumerable.Range(firstCol, lastCol - firstCol + 1)
            .Select(c => sheet.Column(c).IsHidden ? 0 : Math.Max(12, sheet.Column(c).Width * 5.6 + 4))
            .ToList();
        var total = widths.Sum();

        var section = document.AddSection();
        var setup = section.PageSetup;
        setup.PageWidth = Unit.FromPoint(595.28);
        setup.PageHeight = Unit.FromPoint(841.89);
        setup.TopMargin = setup.BottomMargin = Unit.FromPoint(40);
        setup.LeftMargin = setup.RightMargin = Unit.FromPoint(36);
        var available = 595.28 - 72;
        if (total > available)
        {
            setup.PageWidth = Unit.FromPoint(841.89);
            setup.PageHeight = Unit.FromPoint(595.28);
            available = 841.89 - 72;
        }
        var scale = Math.Min(1, available / Math.Max(1, total));

        var title = section.Headers.Primary.AddParagraph(sheet.Name);
        title.Format.Font.Size = 8;
        title.Format.Font.Color = Colors.Gray;
        var pageField = section.Footers.Primary.AddParagraph();
        pageField.Format.Alignment = ParagraphAlignment.Center;
        pageField.Format.Font.Size = 8;
        pageField.AddPageField();

        var table = section.AddTable();
        table.Borders.Width = 0.25;
        table.Borders.Color = new Color(0xD0, 0xD0, 0xD0);
        foreach (var w in widths) table.AddColumn(Unit.FromPoint(Math.Max(0.5, w * scale)));

        var merged = sheet.MergedRanges.ToList();
        for (var r = firstRow; r <= lastRow; r++)
        {
            var xlRow = sheet.Row(r);
            var row = table.AddRow();
            if (xlRow.IsHidden)
            {
                row.Height = Unit.FromPoint(0.1);
                row.HeightRule = RowHeightRule.Exactly;
                continue;
            }
            row.Height = Unit.FromPoint(Math.Max(8, xlRow.Height * scale));
            row.HeightRule = RowHeightRule.AtLeast;
            row.TopPadding = row.BottomPadding = Unit.FromPoint(1);

            for (var c = firstCol; c <= lastCol; c++)
            {
                var xlCell = sheet.Cell(r, c);
                var cell = row.Cells[c - firstCol];
                var merge = merged.FirstOrDefault(m => m.FirstCell().Address.RowNumber <= r && m.LastCell().Address.RowNumber >= r &&
                                                       m.FirstCell().Address.ColumnNumber <= c && m.LastCell().Address.ColumnNumber >= c);
                if (merge != null)
                {
                    var first = merge.FirstCell().Address;
                    if (first.RowNumber != r || first.ColumnNumber != c) continue;
                    cell.MergeRight = Math.Min(lastCol, merge.LastCell().Address.ColumnNumber) - c;
                    cell.MergeDown = Math.Min(lastRow, merge.LastCell().Address.RowNumber) - r;
                }
                FillCell(cell, xlCell, scale);
            }
        }
    }

    private static void FillCell(Cell cell, IXLCell xl, double scale)
    {
        string text;
        try
        {
            text = xl.GetFormattedString();
        }
        catch
        {
            text = xl.Value.ToString();
        }
        var style = xl.Style;
        if (TryColor(style.Fill.BackgroundColor, out var fill) && style.Fill.PatternType != XLFillPatternValues.None)
            cell.Shading.Color = fill;
        cell.VerticalAlignment = style.Alignment.Vertical switch
        {
            XLAlignmentVerticalValues.Top => VerticalAlignment.Top,
            XLAlignmentVerticalValues.Center => VerticalAlignment.Center,
            _ => VerticalAlignment.Bottom,
        };
        if (text.Length == 0) return;

        var p = cell.AddParagraph();
        var isNumber = xl.DataType is XLDataType.Number or XLDataType.DateTime or XLDataType.TimeSpan;
        p.Format.Alignment = style.Alignment.Horizontal switch
        {
            XLAlignmentHorizontalValues.Center or XLAlignmentHorizontalValues.CenterContinuous => ParagraphAlignment.Center,
            XLAlignmentHorizontalValues.Right => ParagraphAlignment.Right,
            XLAlignmentHorizontalValues.Justify => ParagraphAlignment.Justify,
            XLAlignmentHorizontalValues.Left => ParagraphAlignment.Left,
            _ => isNumber ? ParagraphAlignment.Right : ParagraphAlignment.Left,
        };
        var font = new Font(string.IsNullOrEmpty(style.Font.FontName) ? "Calibri" : style.Font.FontName, Unit.FromPoint(Math.Max(4, style.Font.FontSize * scale)))
        {
            Bold = style.Font.Bold,
            Italic = style.Font.Italic,
            Underline = style.Font.Underline != XLFontUnderlineValues.None ? Underline.Single : Underline.None,
        };
        if (TryColor(style.Font.FontColor, out var color)) font.Color = color;
        p.AddFormattedText(text, font);
    }

    private static bool TryColor(XLColor xl, out Color color)
    {
        color = Colors.Black;
        try
        {
            if (xl.ColorType != XLColorType.Color) return false;
            var c = xl.Color;
            if (c.A == 0) return false;
            color = new Color(c.R, c.G, c.B);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max];
}
