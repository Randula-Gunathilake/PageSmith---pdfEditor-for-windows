using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using A = DocumentFormat.OpenXml.Drawing;
using MD = MigraDoc.DocumentObjectModel;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace PageSmith.Core.Office.BuiltIn;

/// <summary>
/// Renders Word documents (.docx) to PDF without Microsoft Office: paragraphs with styles, character
/// formatting, lists, tables, inline pictures, page breaks, sections, headers and footers.
/// </summary>
public sealed class DocxRenderer
{
    private sealed record RunFormat(string Font, double Size, bool Bold, bool Italic, bool Underline, bool Strike, string? Color, bool Superscript, bool Subscript);

    private readonly WordprocessingDocument _doc;
    private readonly Dictionary<string, W.Style> _styles = new(StringComparer.OrdinalIgnoreCase);
    private readonly RunFormat _defaults;
    private readonly string _minorFont = "Calibri";
    private readonly string _majorFont = "Calibri Light";
    private readonly double _defaultSpaceAfter;

    private DocxRenderer(WordprocessingDocument doc)
    {
        _doc = doc;
        var styles = doc.MainDocumentPart?.StyleDefinitionsPart?.Styles;
        if (styles != null)
            foreach (var s in styles.Elements<W.Style>())
                if (s.StyleId?.Value is { } id) _styles[id] = s;

        var theme = doc.MainDocumentPart?.ThemePart?.Theme?.ThemeElements?.FontScheme;
        if (theme?.MinorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } minor) _minorFont = minor;
        if (theme?.MajorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } major) _majorFont = major;

        var baseFormat = new RunFormat(_minorFont, 11, false, false, false, false, null, false, false);
        var docDefaults = styles?.DocDefaults;
        _defaults = Apply(baseFormat, docDefaults?.RunPropertiesDefault?.RunPropertiesBaseStyle);
        var pDefaults = docDefaults?.ParagraphPropertiesDefault?.ParagraphPropertiesBaseStyle?.SpacingBetweenLines;
        _defaultSpaceAfter = Twips(pDefaults?.After?.Value) ?? 0;
    }

    public static void Render(string docxPath, string pdfPath)
    {
        PdfDocs.EnsureInitialized();
        using var word = WordprocessingDocument.Open(docxPath, false);
        var renderer = new DocxRenderer(word);
        var document = renderer.Build();
        var pdf = new PdfDocumentRenderer { Document = document };
        pdf.RenderDocument();
        pdf.PdfDocument.Info.Creator = "PageSmith";
        pdf.PdfDocument.Save(pdfPath);
    }

    private Document Build()
    {
        var document = new Document();
        var normal = document.Styles[StyleNames.Normal]!;
        normal.Font.Name = _defaults.Font;
        normal.Font.Size = _defaults.Size;

        var body = _doc.MainDocumentPart?.Document?.Body ?? throw new UserFacingException("The document has no body.");
        // A paragraph's sectPr describes the section that ends with that paragraph.
        var sections = new List<(List<OpenXmlElement> Elements, W.SectionProperties? Props)>();
        var current = new List<OpenXmlElement>();
        foreach (var element in body.Elements())
        {
            if (element is W.SectionProperties finalProps)
            {
                sections.Add((current, finalProps));
                current = [];
                continue;
            }
            current.Add(element);
            if (element is W.Paragraph p && p.ParagraphProperties?.SectionProperties is { } sp)
            {
                sections.Add((current, sp));
                current = [];
            }
        }
        if (current.Count > 0 || sections.Count == 0) sections.Add((current, body.Elements<W.SectionProperties>().LastOrDefault()));

        foreach (var (elements, props) in sections)
        {
            var section = document.AddSection();
            SetupPage(section, props);
            AddHeadersFooters(section, props);
            foreach (var element in elements) AddBlock(section, element);
        }
        return document;
    }

    private static void SetupPage(Section section, W.SectionProperties? props)
    {
        var size = props?.GetFirstChild<W.PageSize>();
        var margin = props?.GetFirstChild<W.PageMargin>();
        var width = Twips(size?.Width?.Value) ?? 612;
        var height = Twips(size?.Height?.Value) ?? 792;
        section.PageSetup.PageWidth = Unit.FromPoint(width);
        section.PageSetup.PageHeight = Unit.FromPoint(height);
        section.PageSetup.TopMargin = Unit.FromPoint(Math.Abs(Twips(margin?.Top?.Value) ?? 72));
        section.PageSetup.BottomMargin = Unit.FromPoint(Math.Abs(Twips(margin?.Bottom?.Value) ?? 72));
        section.PageSetup.LeftMargin = Unit.FromPoint(Twips(margin?.Left?.Value) ?? 72);
        section.PageSetup.RightMargin = Unit.FromPoint(Twips(margin?.Right?.Value) ?? 72);
        section.PageSetup.HeaderDistance = Unit.FromPoint(Twips(margin?.Header?.Value) ?? 36);
        section.PageSetup.FooterDistance = Unit.FromPoint(Twips(margin?.Footer?.Value) ?? 36);
    }

    private void AddHeadersFooters(Section section, W.SectionProperties? props)
    {
        var main = _doc.MainDocumentPart!;
        var header = props?.Elements<W.HeaderReference>().FirstOrDefault(h => h.Type?.Value == W.HeaderFooterValues.Default || h.Type == null);
        if (header?.Id?.Value is { } hid && main.GetPartById(hid) is HeaderPart hp && hp.Header != null)
            foreach (var p in hp.Header.Elements<W.Paragraph>()) AddParagraph(section.Headers.Primary.AddParagraph(), p);

        var footer = props?.Elements<W.FooterReference>().FirstOrDefault(f => f.Type?.Value == W.HeaderFooterValues.Default || f.Type == null);
        if (footer?.Id?.Value is { } fid && main.GetPartById(fid) is FooterPart fp && fp.Footer != null)
            foreach (var p in fp.Footer.Elements<W.Paragraph>()) AddParagraph(section.Footers.Primary.AddParagraph(), p);
    }

    private void AddBlock(Section section, OpenXmlElement element)
    {
        switch (element)
        {
            case W.Paragraph p:
                if (p.ParagraphProperties?.PageBreakBefore != null) section.AddPageBreak();
                AddParagraph(section.AddParagraph(), p, section);
                break;
            case W.Table t:
                AddTable(section, t);
                break;
            case W.SdtBlock sdt:
                foreach (var child in sdt.SdtContentBlock?.Elements() ?? []) AddBlock(section, child);
                break;
        }
    }

    private void AddParagraph(MD.Paragraph target, W.Paragraph source, Section? section = null)
    {
        var styleChain = StyleChain(source.ParagraphProperties?.ParagraphStyleId?.Val?.Value);
        var pPr = styleChain.Select(s => s.StyleParagraphProperties).OfType<OpenXmlElement>().Append(source.ParagraphProperties).Where(x => x != null).ToList();
        var format = _defaults;
        foreach (var style in styleChain) format = Apply(format, style.StyleRunProperties);

        // Alignment, spacing and indentation: later sources (direct formatting) win.
        target.Format.SpaceAfter = Unit.FromPoint(_defaultSpaceAfter);
        foreach (var props in pPr)
        {
            var jc = props!.GetFirstChild<W.Justification>()?.Val?.Value;
            if (jc != null)
            {
                target.Format.Alignment =
                    jc == W.JustificationValues.Center ? ParagraphAlignment.Center :
                    jc == W.JustificationValues.Right || jc == W.JustificationValues.End ? ParagraphAlignment.Right :
                    jc == W.JustificationValues.Both || jc == W.JustificationValues.Distribute ? ParagraphAlignment.Justify :
                    ParagraphAlignment.Left;
            }
            if (props.GetFirstChild<W.SpacingBetweenLines>() is { } spacing)
            {
                if (Twips(spacing.Before?.Value) is { } before) target.Format.SpaceBefore = Unit.FromPoint(before);
                if (Twips(spacing.After?.Value) is { } after) target.Format.SpaceAfter = Unit.FromPoint(after);
                if (spacing.Line?.Value is { } line && double.TryParse(line, out var lineVal) &&
                    (spacing.LineRule == null || spacing.LineRule.Value == W.LineSpacingRuleValues.Auto))
                {
                    target.Format.LineSpacingRule = LineSpacingRule.Multiple;
                    target.Format.LineSpacing = lineVal / 240.0;
                }
            }
            if (props.GetFirstChild<W.Indentation>() is { } ind)
            {
                if (Twips(ind.Left?.Value ?? ind.Start?.Value) is { } left) target.Format.LeftIndent = Unit.FromPoint(left);
                if (Twips(ind.Right?.Value ?? ind.End?.Value) is { } right) target.Format.RightIndent = Unit.FromPoint(right);
                if (Twips(ind.FirstLine?.Value) is { } first) target.Format.FirstLineIndent = Unit.FromPoint(first);
                if (Twips(ind.Hanging?.Value) is { } hanging) target.Format.FirstLineIndent = Unit.FromPoint(-hanging);
            }
        }

        if (ListPrefix(source) is { } prefix)
        {
            target.Format.LeftIndent = Unit.FromPoint(Math.Max(target.Format.LeftIndent.Point, 18 * (prefix.Level + 1)));
            target.Format.FirstLineIndent = Unit.FromPoint(-14);
            target.AddFormattedText(prefix.Text + "\t", ToFont(format));
            target.Format.AddTabStop(Unit.FromPoint(target.Format.LeftIndent.Point));
        }

        AddInlines(target, source.Elements(), format, section, new FieldState());
    }

    private sealed class FieldState
    {
        public bool InInstruction;
        public bool SkipResult;
        public string Instruction = "";
    }

    private void AddInlines(MD.Paragraph target, IEnumerable<OpenXmlElement> elements, RunFormat paraFormat, Section? section, FieldState field)
    {
        foreach (var element in elements)
        {
            switch (element)
            {
                case W.Run run:
                    AddRun(target, run, paraFormat, section, field);
                    break;
                case W.Hyperlink link:
                    AddInlines(target, link.Elements(), paraFormat, section, field);
                    break;
                case W.SimpleField simple:
                    var instr = simple.Instruction?.Value?.Trim().ToUpperInvariant() ?? "";
                    if (instr.StartsWith("PAGE")) target.AddPageField();
                    else if (instr.StartsWith("NUMPAGES")) target.AddNumPagesField();
                    else AddInlines(target, simple.Elements(), paraFormat, section, field);
                    break;
                case W.SdtRun sdt:
                    AddInlines(target, sdt.SdtContentRun?.Elements() ?? [], paraFormat, section, field);
                    break;
                case W.InsertedRun ins:
                    AddInlines(target, ins.Elements(), paraFormat, section, field);
                    break;
                case W.CustomXmlRun custom:
                    AddInlines(target, custom.Elements(), paraFormat, section, field);
                    break;
            }
        }
    }

    private void AddRun(MD.Paragraph target, W.Run run, RunFormat paraFormat, Section? section, FieldState field)
    {
        var format = paraFormat;
        if (run.RunProperties?.RunStyle?.Val?.Value is { } charStyle)
            foreach (var style in StyleChain(charStyle)) format = Apply(format, style.StyleRunProperties);
        format = Apply(format, run.RunProperties);
        var font = ToFont(format);

        foreach (var child in run.Elements())
        {
            switch (child)
            {
                case W.FieldChar fc:
                    if (fc.FieldCharType?.Value == W.FieldCharValues.Begin)
                    {
                        field.InInstruction = true;
                        field.Instruction = "";
                    }
                    else if (fc.FieldCharType?.Value == W.FieldCharValues.Separate)
                    {
                        field.InInstruction = false;
                        var instr = field.Instruction.Trim().ToUpperInvariant();
                        field.SkipResult = instr.StartsWith("PAGE") || instr.StartsWith("NUMPAGES");
                        if (instr.StartsWith("PAGE")) target.AddPageField();
                        else if (instr.StartsWith("NUMPAGES")) target.AddNumPagesField();
                    }
                    else
                    {
                        field.InInstruction = false;
                        field.SkipResult = false;
                    }
                    break;
                case W.FieldCode code:
                    field.Instruction += code.Text;
                    break;
                case W.Text t when !field.InInstruction && !field.SkipResult:
                    target.AddFormattedText(t.Text, font);
                    break;
                case W.TabChar:
                    target.AddTab();
                    break;
                case W.Break br:
                    if (br.Type?.Value == W.BreakValues.Page && section != null) section.AddPageBreak();
                    else target.AddLineBreak();
                    break;
                case W.CarriageReturn:
                    target.AddLineBreak();
                    break;
                case W.NoBreakHyphen:
                    target.AddFormattedText("-", font);
                    break;
                case W.SymbolChar sym when sym.Char?.Value is { } hex && int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code):
                    target.AddFormattedText(((char)(code >= 0xF000 ? code - 0xF000 : code)).ToString(), font);
                    break;
                case W.Drawing drawing:
                    AddDrawing(target, drawing);
                    break;
            }
        }
    }

    private void AddDrawing(MD.Paragraph target, W.Drawing drawing)
    {
        var blip = drawing.Descendants<A.Blip>().FirstOrDefault();
        var extent = drawing.Descendants<DocumentFormat.OpenXml.Drawing.Wordprocessing.Extent>().FirstOrDefault();
        if (blip?.Embed?.Value is not { } rel || extent == null) return;
        if (_doc.MainDocumentPart!.GetPartById(rel) is not ImagePart part) return;
        using var stream = part.GetStream();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var data = ImageData(bytes, part.ContentType);
        if (data == null) return;
        var image = target.AddImage(data);
        image.Width = Unit.FromPoint((extent.Cx?.Value ?? 0) / 12700.0);
        image.Height = Unit.FromPoint((extent.Cy?.Value ?? 0) / 12700.0);
        image.LockAspectRatio = false;
    }

    /// <summary>MigraDoc accepts images as "base64:" strings; formats it cannot read are converted to PNG.</summary>
    internal static string? ImageData(byte[] bytes, string contentType)
    {
        if (contentType is "image/jpeg" or "image/png")
            return "base64:" + System.Convert.ToBase64String(bytes);
        try
        {
            var bmp = Imaging.ImageIO.Decode(bytes);
            return "base64:" + System.Convert.ToBase64String(Imaging.ImageIO.EncodePng(bmp));
        }
        catch
        {
            return null; // EMF/WMF and other vector formats are skipped.
        }
    }

    private void AddTable(Section section, W.Table source)
    {
        var table = section.AddTable();
        table.Borders.Width = 0.5;
        table.Borders.Color = Colors.Gray;
        table.Format.SpaceAfter = 0;

        var grid = source.GetFirstChild<W.TableGrid>()?.Elements<W.GridColumn>().Select(g => Twips(g.Width?.Value) ?? 72).ToList() ?? [];
        var rows = source.Elements<W.TableRow>().ToList();
        if (grid.Count == 0)
        {
            var maxCells = rows.Count == 0 ? 1 : rows.Max(r => r.Elements<W.TableCell>().Sum(c => c.TableCellProperties?.GridSpan?.Val?.Value ?? 1));
            var available = section.PageSetup.PageWidth.Point - section.PageSetup.LeftMargin.Point - section.PageSetup.RightMargin.Point;
            grid = Enumerable.Repeat(available / Math.Max(1, maxCells), maxCells).ToList();
        }
        foreach (var w in grid) table.AddColumn(Unit.FromPoint(Math.Max(10, w)));

        // Vertical merges: remember the cell that started a merge in each grid column.
        var mergeStarts = new Dictionary<int, Cell>();
        foreach (var sourceRow in rows)
        {
            var row = table.AddRow();
            row.TopPadding = 2;
            row.BottomPadding = 2;
            var col = 0;
            foreach (var sourceCell in sourceRow.Elements<W.TableCell>())
            {
                if (col >= grid.Count) break;
                var span = Math.Max(1, sourceCell.TableCellProperties?.GridSpan?.Val?.Value ?? 1);
                var vMerge = sourceCell.TableCellProperties?.VerticalMerge;
                var cell = row.Cells[col];
                if (vMerge != null && (vMerge.Val == null || vMerge.Val.Value == W.MergedCellValues.Continue))
                {
                    if (mergeStarts.TryGetValue(col, out var start)) start.MergeDown++;
                    col += span;
                    continue;
                }
                if (vMerge?.Val?.Value == W.MergedCellValues.Restart) mergeStarts[col] = cell;
                else mergeStarts.Remove(col);

                if (span > 1) cell.MergeRight = Math.Min(span, grid.Count - col) - 1;
                if (sourceCell.TableCellProperties?.Shading?.Fill?.Value is { } fill && TryColor(fill, out var shade)) cell.Shading.Color = shade;
                foreach (var block in sourceCell.Elements())
                {
                    if (block is W.Paragraph p) AddParagraph(cell.AddParagraph(), p);
                    else if (block is W.Table nested)
                        foreach (var np in nested.Descendants<W.Paragraph>()) AddParagraph(cell.AddParagraph(), np);
                }
                col += span;
            }
        }
        section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(4);
    }

    private (string Text, int Level)? ListPrefix(W.Paragraph p)
    {
        var numPr = p.ParagraphProperties?.NumberingProperties;
        if (numPr == null)
        {
            foreach (var style in StyleChain(p.ParagraphProperties?.ParagraphStyleId?.Val?.Value))
                numPr ??= style.StyleParagraphProperties?.NumberingProperties;
        }
        var numId = numPr?.NumberingId?.Val?.Value;
        if (numId is null or 0) return null;
        var level = numPr!.NumberingLevelReference?.Val?.Value ?? 0;

        var numbering = _doc.MainDocumentPart?.NumberingDefinitionsPart?.Numbering;
        var instance = numbering?.Elements<W.NumberingInstance>().FirstOrDefault(n => n.NumberID?.Value == numId);
        var abstractId = instance?.AbstractNumId?.Val?.Value;
        var abstractNum = numbering?.Elements<W.AbstractNum>().FirstOrDefault(a => a.AbstractNumberId?.Value == abstractId);
        var lvl = abstractNum?.Elements<W.Level>().FirstOrDefault(l => l.LevelIndex?.Value == level);
        var fmt = lvl?.NumberingFormat?.Val?.Value;
        if (fmt == null || fmt == W.NumberFormatValues.Bullet) return ("•", level);

        var key = $"{numId}:{level}";
        _counters[key] = _counters.GetValueOrDefault(key) + 1;
        // Deeper levels restart when a shallower level advances.
        foreach (var k in _counters.Keys.Where(k => k.StartsWith(numId + ":") && int.Parse(k[(k.IndexOf(':') + 1)..]) > level).ToList())
            _counters.Remove(k);
        var n = _counters[key];
        var text = fmt == W.NumberFormatValues.LowerLetter ? $"{(char)('a' + (n - 1) % 26)}." :
            fmt == W.NumberFormatValues.UpperLetter ? $"{(char)('A' + (n - 1) % 26)}." :
            fmt == W.NumberFormatValues.LowerRoman ? $"{Roman(n).ToLowerInvariant()}." :
            fmt == W.NumberFormatValues.UpperRoman ? $"{Roman(n)}." :
            $"{n}.";
        return (text, level);
    }

    private readonly Dictionary<string, int> _counters = [];

    private static string Roman(int n)
    {
        var map = new (int, string)[] { (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I") };
        var sb = new StringBuilder();
        foreach (var (v, s) in map)
            while (n >= v)
            {
                sb.Append(s);
                n -= v;
            }
        return sb.ToString();
    }

    /// <summary>The style and its basedOn ancestors, root first.</summary>
    private List<W.Style> StyleChain(string? styleId)
    {
        var chain = new List<W.Style>();
        if (styleId == null && _styles.Values.FirstOrDefault(s => s.Default?.Value == true && s.Type?.Value == W.StyleValues.Paragraph) is { } def)
            styleId = def.StyleId?.Value;
        var guard = 0;
        while (styleId != null && _styles.TryGetValue(styleId, out var style) && guard++ < 20)
        {
            chain.Insert(0, style);
            styleId = style.BasedOn?.Val?.Value;
        }
        return chain;
    }

    private RunFormat Apply(RunFormat f, OpenXmlElement? props)
    {
        if (props == null) return f;
        var fonts = props.GetFirstChild<W.RunFonts>();
        if (fonts != null)
        {
            if (fonts.Ascii?.Value is { Length: > 0 } ascii) f = f with { Font = ascii };
            else if (fonts.HighAnsi?.Value is { Length: > 0 } hansi) f = f with { Font = hansi };
            else if (fonts.AsciiTheme?.Value is { } theme)
                f = f with { Font = theme == W.ThemeFontValues.MajorHighAnsi || theme == W.ThemeFontValues.MajorAscii ? _majorFont : _minorFont };
        }
        if (props.GetFirstChild<W.FontSize>()?.Val?.Value is { } size && double.TryParse(size, out var halfPoints)) f = f with { Size = halfPoints / 2 };
        if (props.GetFirstChild<W.Bold>() is { } b) f = f with { Bold = b.Val?.Value ?? true };
        if (props.GetFirstChild<W.Italic>() is { } i) f = f with { Italic = i.Val?.Value ?? true };
        if (props.GetFirstChild<W.Strike>() is { } s) f = f with { Strike = s.Val?.Value ?? true };
        if (props.GetFirstChild<W.Underline>() is { } u) f = f with { Underline = u.Val?.Value != W.UnderlineValues.None };
        if (props.GetFirstChild<W.Color>()?.Val?.Value is { } c) f = f with { Color = c };
        if (props.GetFirstChild<W.VerticalTextAlignment>()?.Val?.Value is { } va)
            f = f with { Superscript = va == W.VerticalPositionValues.Superscript, Subscript = va == W.VerticalPositionValues.Subscript };
        return f;
    }

    private static MD.Font ToFont(RunFormat f)
    {
        var font = new MD.Font(f.Font, Unit.FromPoint(f.Size))
        {
            Bold = f.Bold,
            Italic = f.Italic,
            Underline = f.Underline ? Underline.Single : Underline.None,
            Superscript = f.Superscript,
            Subscript = f.Subscript,
        };
        if (f.Color != null && TryColor(f.Color, out var color)) font.Color = color;
        return font;
    }

    internal static bool TryColor(string hex, out MD.Color color)
    {
        color = Colors.Black;
        if (hex.Length != 6 || hex.Equals("auto", StringComparison.OrdinalIgnoreCase)) return false;
        if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return false;
        color = new MD.Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    private static double? Twips(string? value) =>
        value != null && double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v / 20.0 : null;

    private static double? Twips(uint? value) => value / 20.0;

    private static double? Twips(int? value) => value / 20.0;
}
