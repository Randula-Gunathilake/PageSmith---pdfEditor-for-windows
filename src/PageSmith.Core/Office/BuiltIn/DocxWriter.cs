using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PageSmith.Core.Pdf;
using PageSmith.Core.Text;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace PageSmith.Core.Office.BuiltIn;

/// <summary>Writes analyzed PDF pages as an editable Word document.</summary>
public static class DocxWriter
{
    public static void Write(IReadOnlyList<LayoutPage> pages, string path)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        main.Document = new W.Document(new W.Body());
        AddStyles(main);
        var body = main.Document.Body!;
        var imageId = 1u;
        var body0 = LayoutAnalyzer.BodyFontSize(pages.SelectMany(p => p.Elements.OfType<LayoutParagraph>().SelectMany(e => e.Lines.SelectMany(l => l.Words))));

        for (var pi = 0; pi < pages.Count; pi++)
        {
            var page = pages[pi];
            var margins = Margins(page);
            var textWidth = Math.Max(72, page.Width - margins.Left - margins.Right);
            foreach (var element in page.Elements)
            {
                switch (element)
                {
                    case LayoutParagraph p:
                        body.Append(Paragraph(p, margins.Left, body0));
                        break;
                    case LayoutTable t:
                        body.Append(Table(t, textWidth));
                        body.Append(new W.Paragraph());
                        break;
                    case LayoutImage img:
                        var para = Image(main, img, textWidth, imageId++);
                        if (para != null) body.Append(para);
                        break;
                }
            }

            // Each PDF page ends a Word section with the same page size, so pages line up.
            var sectPr = new W.SectionProperties(
                new W.PageSize { Width = (UInt32Value)(uint)(page.Width * 20), Height = (UInt32Value)(uint)(page.Height * 20), Orient = page.Width > page.Height ? W.PageOrientationValues.Landscape : W.PageOrientationValues.Portrait },
                new W.PageMargin
                {
                    Top = (int)(margins.Top * 20), Bottom = (int)(margins.Bottom * 20),
                    Left = (UInt32Value)(uint)(margins.Left * 20), Right = (UInt32Value)(uint)(margins.Right * 20),
                    Header = 360, Footer = 360, Gutter = 0,
                });
            if (pi < pages.Count - 1)
                body.Append(new W.Paragraph(new W.ParagraphProperties(sectPr)));
            else
                body.Append(sectPr);
        }
        main.Document.Save();
    }

    private static (double Left, double Right, double Top, double Bottom) Margins(LayoutPage page)
    {
        if (page.Elements.Count == 0) return (72, 72, 72, 72);
        var bounds = page.Elements.Select(e => e.Bounds).Aggregate(System.Windows.Rect.Union);
        double Clamp(double v) => Math.Clamp(v, 18, 108);
        return (Clamp(bounds.Left), Clamp(page.Width - bounds.Right), Clamp(bounds.Top - 4), Clamp(page.Height - bounds.Bottom - 4));
    }

    private static W.Paragraph Paragraph(LayoutParagraph p, double leftMargin, double body)
    {
        var props = new W.ParagraphProperties();
        if (p.HeadingLevel > 0) props.Append(new W.ParagraphStyleId { Val = $"Heading{p.HeadingLevel}" });
        else if (p.List != ListKind.None) props.Append(new W.ParagraphStyleId { Val = "ListParagraph" });
        props.Append(new W.SpacingBetweenLines { After = ((int)(Math.Max(2, p.FontSize * 0.5) * 20)).ToString(), Line = "252", LineRule = W.LineSpacingRuleValues.Auto });
        var indent = Math.Max(0, p.Bounds.Left - leftMargin);
        if (p.List != ListKind.None) props.Append(new W.Indentation { Left = ((int)((indent + 18) * 20)).ToString(), Hanging = "360" });
        else if (indent > 12 && p.Align == TextAlign.Left) props.Append(new W.Indentation { Left = ((int)(indent * 20)).ToString() });
        var jc = p.Align switch
        {
            TextAlign.Center => W.JustificationValues.Center,
            TextAlign.Right => W.JustificationValues.Right,
            TextAlign.Justify => W.JustificationValues.Both,
            _ => W.JustificationValues.Left,
        };
        props.Append(new W.Justification { Val = jc });
        var para = new W.Paragraph(props);

        // Words with an end-of-line flag, so wrapped lines flow and hyphenation is undone.
        var tokens = new List<(string Text, TextWord Style, bool Space)>();
        foreach (var line in p.Lines)
        {
            for (var i = 0; i < line.Words.Count; i++)
                tokens.Add((line.Words[i].Text, line.Words[i], true));
        }
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var (text, style, _) = tokens[i];
            var next = tokens[i + 1].Text;
            if (text.Length > 1 && text[^1] == '-' && char.IsLetter(text[^2]) && next.Length > 0 && char.IsLower(next[0]) && IsLineEnd(p, style))
                tokens[i] = (text[..^1], style, false);
        }

        if (p.List != ListKind.None && tokens.Count > 0)
        {
            var marker = p.List == ListKind.Bullet ? "•" : tokens[0].Text;
            para.Append(MakeRun(marker + "\t", tokens[0].Style, p.HeadingLevel > 0));
            tokens.RemoveAt(0);
        }

        // Consecutive words with the same look become one run.
        var sb = new StringBuilder();
        TextWord? current = null;
        foreach (var (text, style, space) in tokens)
        {
            if (current != null && !SameStyle(current, style))
            {
                para.Append(MakeRun(sb.ToString(), current, p.HeadingLevel > 0));
                sb.Clear();
            }
            current = style;
            sb.Append(text);
            if (space) sb.Append(' ');
        }
        if (current != null && sb.Length > 0) para.Append(MakeRun(sb.ToString().TrimEnd(), current, p.HeadingLevel > 0));
        return para;
    }

    private static bool IsLineEnd(LayoutParagraph p, TextWord word) => p.Lines.Any(l => l.Words.Count > 0 && ReferenceEquals(l.Words[^1], word));

    private static bool SameStyle(TextWord a, TextWord b) =>
        a.Bold == b.Bold && a.Italic == b.Italic && Math.Abs(a.FontSize - b.FontSize) < 0.6 && a.Rgb == b.Rgb && a.FontName == b.FontName;

    private static W.Run MakeRun(string text, TextWord style, bool inHeading)
    {
        var rPr = new W.RunProperties();
        var family = FontFamily(style.FontName);
        if (family != null) rPr.Append(new W.RunFonts { Ascii = family, HighAnsi = family, ComplexScript = family });
        if (style.Bold) rPr.Append(new W.Bold());
        if (style.Italic) rPr.Append(new W.Italic());
        if (style.Rgb != 0) rPr.Append(new W.Color { Val = style.Rgb.ToString("X6") });
        else if (inHeading) rPr.Append(new W.Color { Val = "000000" });
        rPr.Append(new W.FontSize { Val = ((int)Math.Round(style.FontSize * 2)).ToString() });
        var parts = text.Split('\t');
        var run = new W.Run(rPr);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) run.Append(new W.TabChar());
            if (parts[i].Length > 0) run.Append(new W.Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
        return run;
    }

    /// <summary>Maps PDF font names ("ABCDEF+Arial-BoldMT", "TimesNewRomanPS-ItalicMT") to installed families.</summary>
    public static string? FontFamily(string pdfFont)
    {
        if (string.IsNullOrWhiteSpace(pdfFont) || pdfFont == "OCR") return null;
        var name = TextExtractor.CleanFontName(pdfFont);
        var cut = name.IndexOfAny([',', '-']);
        if (cut > 0) name = name[..cut];
        foreach (var suffix in new[] { "PSMT", "MT", "PS", "Std", "Pro", "Regular", "Bold", "Italic" })
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length + 2) name = name[..^suffix.Length];
        name = name switch
        {
            "Helvetica" or "Arial" => "Arial",
            "Times" or "TimesNewRoman" or "TimesNewRomanPS" or "Times-Roman" => "Times New Roman",
            "Courier" or "CourierNew" or "CourierNewPS" => "Courier New",
            "Symbol" => "Symbol",
            _ => System.Text.RegularExpressions.Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " "),
        };
        return WindowsFontResolver.InstalledFamilies().Contains(name, StringComparer.OrdinalIgnoreCase) ? name : null;
    }

    private static W.Table Table(LayoutTable t, double textWidth)
    {
        var columns = Math.Max(1, t.ColumnCount);
        var colWidth = (int)(textWidth * 20 / columns);
        var table = new W.Table(new W.TableProperties(
            new W.TableStyle { Val = "TableGrid" },
            new W.TableWidth { Type = W.TableWidthUnitValues.Auto, Width = "0" },
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.LeftBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.RightBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4 },
                new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4 })));
        var grid = new W.TableGrid();
        for (var c = 0; c < columns; c++) grid.Append(new W.GridColumn { Width = colWidth.ToString() });
        table.Append(grid);
        foreach (var row in t.Rows)
        {
            var tr = new W.TableRow();
            for (var c = 0; c < columns; c++)
            {
                var text = c < row.Count ? row[c] : "";
                tr.Append(new W.TableCell(
                    new W.TableCellProperties(new W.TableCellWidth { Type = W.TableWidthUnitValues.Dxa, Width = colWidth.ToString() }),
                    new W.Paragraph(new W.Run(new W.Text(text) { Space = SpaceProcessingModeValues.Preserve }))));
            }
            table.Append(tr);
        }
        return table;
    }

    private static W.Paragraph? Image(MainDocumentPart main, LayoutImage img, double textWidth, uint id)
    {
        byte[] data;
        PartTypeInfo type;
        if (img.IsJpeg)
        {
            data = img.Data;
            type = ImagePartType.Jpeg;
        }
        else
        {
            data = img.Data;
            type = ImagePartType.Png;
        }
        var part = main.AddImagePart(type);
        using (var ms = new MemoryStream(data)) part.FeedData(ms);
        var rel = main.GetIdOfPart(part);

        var scale = Math.Min(1, textWidth / Math.Max(1, img.Bounds.Width));
        var cx = (long)(img.Bounds.Width * scale * 12700);
        var cy = (long)(img.Bounds.Height * scale * 12700);
        var drawing = new W.Drawing(
            new DW.Inline(
                new DW.Extent { Cx = cx, Cy = cy },
                new DW.EffectExtent { LeftEdge = 0, TopEdge = 0, RightEdge = 0, BottomEdge = 0 },
                new DW.DocProperties { Id = id, Name = $"Picture {id}" },
                new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
                new A.Graphic(new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = id, Name = $"image{id}" },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(new A.Blip { Embed = rel }, new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(new A.Offset { X = 0, Y = 0 }, new A.Extents { Cx = cx, Cy = cy }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle })))
                { Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture" }))
            { DistanceFromTop = 0, DistanceFromBottom = 0, DistanceFromLeft = 0, DistanceFromRight = 0 });
        return new W.Paragraph(new W.ParagraphProperties(new W.Justification { Val = W.JustificationValues.Center }), new W.Run(drawing));
    }

    private static void AddStyles(MainDocumentPart main)
    {
        var styles = main.AddNewPart<StyleDefinitionsPart>();
        var root = new W.Styles(
            new W.DocDefaults(
                new W.RunPropertiesDefault(new W.RunPropertiesBaseStyle(new W.RunFonts { Ascii = "Calibri", HighAnsi = "Calibri", ComplexScript = "Calibri" }, new W.FontSize { Val = "22" })),
                new W.ParagraphPropertiesDefault(new W.ParagraphPropertiesBaseStyle(new W.SpacingBetweenLines { After = "120", Line = "252", LineRule = W.LineSpacingRuleValues.Auto }))),
            new W.Style(new W.StyleName { Val = "Normal" }, new W.PrimaryStyle()) { Type = W.StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new W.Style(new W.StyleName { Val = "List Paragraph" }, new W.BasedOn { Val = "Normal" }) { Type = W.StyleValues.Paragraph, StyleId = "ListParagraph" },
            new W.Style(new W.StyleName { Val = "Table Grid" }) { Type = W.StyleValues.Table, StyleId = "TableGrid" });
        for (var level = 1; level <= 3; level++)
        {
            root.Append(new W.Style(
                new W.StyleName { Val = $"heading {level}" },
                new W.BasedOn { Val = "Normal" },
                new W.NextParagraphStyle { Val = "Normal" },
                new W.PrimaryStyle(),
                new W.StyleParagraphProperties(new W.KeepNext(), new W.SpacingBetweenLines { Before = "240", After = "120" }, new W.OutlineLevel { Val = level - 1 }),
                new W.StyleRunProperties(new W.Bold()))
            { Type = W.StyleValues.Paragraph, StyleId = $"Heading{level}" });
        }
        styles.Styles = root;
    }
}
