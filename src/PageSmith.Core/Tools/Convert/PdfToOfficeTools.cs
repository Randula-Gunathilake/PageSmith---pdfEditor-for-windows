using ClosedXML.Excel;
using PageSmith.Core.Imaging;
using PageSmith.Core.Office;
using PageSmith.Core.Office.BuiltIn;
using PageSmith.Core.Rendering;
using PageSmith.Core.Text;

namespace PageSmith.Core.Tools.Convert;

public sealed record PdfToOfficeOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>Recognize text on scanned pages (iLovePDF Premium "OCR" option).</summary>
    public bool Ocr { get; init; }
    public string? OcrLanguage { get; init; }

    /// <summary>For PDF to Word: use Microsoft Word's PDF reflow when available.</summary>
    public ConversionEngine Engine { get; init; } = ConversionEngine.BuiltIn;

    /// <summary>For PDF to PowerPoint: pages as pictures (exact look) instead of editable text boxes.</summary>
    public bool SlidesAsImages { get; init; }

    /// <summary>For PDF to Excel: one worksheet per page instead of one sheet for everything.</summary>
    public bool SheetPerPage { get; init; }

    public string? Pages { get; init; }
}

/// <summary>PDF to Word (.docx).</summary>
public static class PdfToWordTool
{
    public static async Task<ToolResult> RunAsync(PdfToOfficeOptions o, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(o.Input.Path).Length };
        var useWord = o.Engine is ConversionEngine.MicrosoftOffice or ConversionEngine.Auto && OfficeDetection.IsMicrosoftOfficeAppInstalled(OfficeApp.Word);
        if (useWord && string.IsNullOrEmpty(o.Input.Password) && o.Pages == null)
        {
            var source = o.Input.Path;
            string? temp = null;
            try
            {
                if (o.Ocr)
                {
                    temp = Path.Combine(Path.GetTempPath(), $"pagesmith_ocr_{Guid.NewGuid():N}.pdf");
                    await Optimize.OcrTool.RunAsync(new Optimize.OcrOptions { Input = o.Input, OutputPath = temp, Language = o.OcrLanguage }, scope.Slice(0, 0.5).AsProgress(), ct).ConfigureAwait(false);
                    source = temp;
                }
                scope.Report(0.5, "Converting with Microsoft Word");
                await MicrosoftOffice.PdfToDocxAsync(source, o.OutputPath, ct).ConfigureAwait(false);
                result.AddOutput(o.OutputPath);
                result.Summary = "Converted to Word with Microsoft Word's PDF reflow.";
                return result;
            }
            catch (Exception ex) when (o.Engine == ConversionEngine.Auto && ex is not OperationCanceledException)
            {
                result.Warnings.Add($"Microsoft Word could not convert the file ({ex.Message}); the built-in converter was used.");
            }
            finally
            {
                if (temp != null) try { File.Delete(temp); } catch { /* temp */ }
            }
        }

        var pages = await LayoutAnalyzer.AnalyzeAsync(o.Input, new LayoutOptions { Ocr = o.Ocr, OcrLanguage = o.OcrLanguage, Pages = o.Pages }, scope.Slice(0, 0.8), ct).ConfigureAwait(false);
        scope.Report(0.85, "Writing Word document");
        await Task.Run(() => DocxWriter.Write(pages, o.OutputPath), ct).ConfigureAwait(false);
        result.AddOutput(o.OutputPath);
        result.Summary = $"Converted {pages.Count} page{(pages.Count == 1 ? "" : "s")} to an editable Word document.";
        WarnAboutScans(result, pages, o.Ocr);
        scope.Report(1);
        return result;
    }

    internal static void WarnAboutScans(ToolResult result, IReadOnlyList<LayoutPage> pages, bool ocr)
    {
        if (ocr) return;
        var scanned = pages.Count(p => !p.Elements.OfType<LayoutParagraph>().Any() && p.Elements.OfType<LayoutImage>().Any());
        if (scanned > 0)
            result.Warnings.Add($"{scanned} page{(scanned == 1 ? " looks" : "s look")} scanned. Turn on OCR to convert the text in {(scanned == 1 ? "it" : "them")}.");
    }
}

/// <summary>PDF to PowerPoint (.pptx): one slide per page.</summary>
public static class PdfToPowerPointTool
{
    public static async Task<ToolResult> RunAsync(PdfToOfficeOptions o, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(o.Input.Path).Length };
        using var renderer = await PdfRenderer.OpenAsync(o.Input.Path, o.Input.Password, ct).ConfigureAwait(false);
        var indices = PageRange.Parse(o.Pages, renderer.PageCount);
        var first = renderer.GetPageSize(indices[0]);
        var slides = new List<SlideContent>();

        IReadOnlyList<LayoutPage>? layout = null;
        if (!o.SlidesAsImages)
            layout = await LayoutAnalyzer.AnalyzeAsync(o.Input, new LayoutOptions { Ocr = o.Ocr, OcrLanguage = o.OcrLanguage, Pages = o.Pages, DetectTables = false }, scope.Slice(0, 0.5), ct).ConfigureAwait(false);

        var build = scope.Slice(o.SlidesAsImages ? 0 : 0.5, 0.95);
        for (var i = 0; i < indices.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            build.Report(i, indices.Count, $"Building slide {i + 1}");
            var size = renderer.GetPageSize(indices[i]);
            // Slides share one size; pages of other sizes are fitted and centred.
            var scale = Math.Min(first.Width / size.Width, first.Height / size.Height);
            var ox = (first.Width - size.Width * scale) / 2;
            var oy = (first.Height - size.Height * scale) / 2;

            if (o.SlidesAsImages || layout == null)
            {
                var png = await renderer.RenderPngAsync(indices[i], 200, true, ct).ConfigureAwait(false);
                var jpg = ImageIO.EncodeJpeg(ImageIO.Decode(png), 88);
                slides.Add(new SlideContent([new SlidePicture(jpg, true, ox, oy, size.Width * scale, size.Height * scale)], []));
                continue;
            }

            var page = layout[i];
            var pictures = new List<SlidePicture>();
            var boxes = new List<SlideTextBox>();
            foreach (var element in page.Elements)
            {
                var r = element.Bounds;
                double X(double v) => ox + v * scale;
                double Y(double v) => oy + v * scale;
                switch (element)
                {
                    case LayoutImage img:
                        pictures.Add(new SlidePicture(img.Data, img.IsJpeg, X(r.Left), Y(r.Top), r.Width * scale, r.Height * scale));
                        break;
                    case LayoutParagraph p:
                        var align = p.Align switch { Pdf.TextAlign.Center => "ctr", Pdf.TextAlign.Right => "r", Pdf.TextAlign.Justify => "just", _ => "l" };
                        var text = string.Join("\n", p.Lines.Select(l => l.Text));
                        boxes.Add(new SlideTextBox(text, X(r.Left), Y(r.Top), r.Width * scale * 1.04 + 2, r.Height * scale + 2,
                            p.FontSize * scale, p.Bold, p.Italic, DocxWriter.FontFamily(p.FontName), p.Rgb, align));
                        break;
                }
            }
            slides.Add(new SlideContent(pictures, boxes));
        }

        scope.Report(0.96, "Writing presentation");
        await Task.Run(() => PptxWriter.Write(o.OutputPath, first.Width, first.Height, slides), ct).ConfigureAwait(false);
        result.AddOutput(o.OutputPath);
        result.Summary = $"Created a presentation with {slides.Count} slide{(slides.Count == 1 ? "" : "s")}.";
        if (layout != null) PdfToWordTool.WarnAboutScans(result, layout, o.Ocr);
        scope.Report(1);
        return result;
    }
}

/// <summary>PDF to Excel (.xlsx): rows and columns rebuilt from word positions.</summary>
public static class PdfToExcelTool
{
    public static async Task<ToolResult> RunAsync(PdfToOfficeOptions o, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(o.Input.Path).Length };
        var pages = await LayoutAnalyzer.AnalyzeAsync(o.Input, new LayoutOptions { Ocr = o.Ocr, OcrLanguage = o.OcrLanguage, Pages = o.Pages, IncludeImages = false, DetectTables = false },
            scope.Slice(0, 0.7), ct).ConfigureAwait(false);

        scope.Report(0.75, "Building spreadsheet");
        var cellCount = 0;
        await Task.Run(() =>
        {
            using var workbook = new XLWorkbook();
            IXLWorksheet? sheet = null;
            var row = 1;
            foreach (var page in pages)
            {
                if (sheet == null || o.SheetPerPage)
                {
                    sheet = workbook.AddWorksheet(o.SheetPerPage ? $"Page {page.Index + 1}" : "Sheet1");
                    row = 1;
                }
                else
                {
                    row++; // blank row between pages
                }
                var lines = TextExtractor.GetLines(page.Elements.OfType<LayoutParagraph>().SelectMany(p => p.Lines.SelectMany(l => l.Words)).ToList());
                cellCount += WriteRows(sheet, lines, ref row);
            }
            if (sheet == null) workbook.AddWorksheet("Sheet1");
            foreach (var ws in workbook.Worksheets)
            {
                ws.Columns().AdjustToContents(1, 400, 8, 60);
                ws.SheetView.FreezeRows(0);
            }
            workbook.SaveAs(o.OutputPath);
        }, ct).ConfigureAwait(false);

        result.AddOutput(o.OutputPath);
        result.Summary = $"Extracted {cellCount:N0} cells from {pages.Count} page{(pages.Count == 1 ? "" : "s")}.";
        PdfToWordTool.WarnAboutScans(result, pages, o.Ocr);
        scope.Report(1);
        return result;
    }

    /// <summary>Writes lines as rows, splitting cells at wide gaps and aligning them to shared column positions.</summary>
    internal static int WriteRows(IXLWorksheet sheet, IReadOnlyList<TextLineInfo> lines, ref int row)
    {
        if (lines.Count == 0) return 0;
        var body = LayoutAnalyzer.BodyFontSize(lines.SelectMany(l => l.Words));
        var split = lines.Select(l => LayoutAnalyzer.SplitCells(l, body)).ToList();
        var columns = LayoutAnalyzer.ColumnStarts(split, body);
        var cells = 0;
        foreach (var cellsInLine in split)
        {
            foreach (var (text, bounds) in cellsInLine)
            {
                var col = 0;
                for (var c = 0; c < columns.Count; c++)
                    if (columns[c] <= bounds.Left + 6) col = c;
                var cell = sheet.Cell(row, col + 1);
                var existing = cell.GetString();
                SetValue(cell, existing.Length > 0 ? existing + " " + text : text);
                cells++;
            }
            row++;
        }
        return cells;
    }

    /// <summary>Stores numbers, percentages and dates as typed values so Excel can calculate with them.</summary>
    internal static void SetValue(IXLCell cell, string text)
    {
        var t = text.Trim();
        var negative = t.StartsWith('(') && t.EndsWith(')');
        var core = negative ? t[1..^1] : t;
        var percent = core.EndsWith('%');
        if (percent) core = core[..^1];
        core = core.TrimStart('$', '€', '£', '¥', '₹').Replace(",", "").Replace(" ", "");
        if (core.Length > 0 && core.Length < 20 && double.TryParse(core, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            && !(t.Length > 1 && t[0] == '0' && !t.Contains('.') && !t.Contains(',')))
        {
            if (negative) number = -number;
            if (percent)
            {
                cell.Value = number / 100;
                cell.Style.NumberFormat.Format = "0.##%";
            }
            else
            {
                cell.Value = number;
            }
            return;
        }
        if (t.Length is >= 8 and <= 10 && DateTime.TryParseExact(t, ["yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "d/M/yyyy", "dd.MM.yyyy"],
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
        {
            cell.Value = date;
            return;
        }
        cell.Value = text;
    }
}
