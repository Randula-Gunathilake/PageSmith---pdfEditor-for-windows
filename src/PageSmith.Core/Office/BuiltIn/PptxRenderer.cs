using System.Windows;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using PageSmith.Core.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace PageSmith.Core.Office.BuiltIn;

/// <summary>
/// Renders PowerPoint presentations (.pptx) to PDF without Microsoft Office: one page per slide with
/// backgrounds, shapes, text boxes (inheriting placeholder positions and sizes), pictures, tables and groups.
/// </summary>
public sealed class PptxRenderer
{
    private const double EmuPerPoint = 12700;
    private readonly PresentationDocument _pres;
    private readonly Dictionary<string, XColor> _themeColors = new(StringComparer.OrdinalIgnoreCase);
    private string _majorFont = "Calibri Light";
    private string _minorFont = "Calibri";

    private PptxRenderer(PresentationDocument pres)
    {
        _pres = pres;
    }

    /// <summary>Maps a group's child coordinate space to slide points.</summary>
    private delegate Rect Transform(Rect emuRect);

    public static void Render(string pptxPath, string pdfPath)
    {
        PdfDocs.EnsureInitialized();
        using var pres = PresentationDocument.Open(pptxPath, false);
        new PptxRenderer(pres).RenderTo(pdfPath);
    }

    private void RenderTo(string pdfPath)
    {
        var part = _pres.PresentationPart ?? throw new UserFacingException("The presentation is empty.");
        var size = part.Presentation?.SlideSize;
        var width = (size?.Cx?.Value ?? 9144000) / EmuPerPoint;
        var height = (size?.Cy?.Value ?? 6858000) / EmuPerPoint;
        LoadTheme(part);

        using var doc = PdfDocs.Create();
        var slideIds = part.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList() ?? [];
        foreach (var id in slideIds)
        {
            if (id.RelationshipId?.Value is not { } rel || part.GetPartById(rel) is not SlidePart slidePart) continue;
            if (slidePart.Slide?.Show?.Value == false) continue;
            var page = doc.AddPage();
            page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(width, height));
            using var gfx = XGraphics.FromPdfPage(page);
            DrawBackground(gfx, slidePart, width, height);
            // Layout and master shapes that are not placeholders show through (logos, footers, decorations).
            if (slidePart.Slide?.CommonSlideData?.ShapeTree is { } tree)
            {
                var layout = slidePart.SlideLayoutPart;
                var master = layout?.SlideMasterPart;
                if (master?.SlideMaster?.CommonSlideData?.ShapeTree is { } mtree && layout?.SlideLayout?.ShowMasterShapes?.Value != false)
                    DrawTree(gfx, mtree, master, slidePart, Identity, nonPlaceholdersOnly: true);
                if (layout?.SlideLayout?.CommonSlideData?.ShapeTree is { } ltree)
                    DrawTree(gfx, ltree, layout, slidePart, Identity, nonPlaceholdersOnly: true);
                DrawTree(gfx, tree, slidePart, slidePart, Identity, nonPlaceholdersOnly: false);
            }
        }
        if (doc.PageCount == 0) throw new UserFacingException("The presentation has no visible slides.");
        PdfDocs.Save(doc, pdfPath);
    }

    private static Rect Identity(Rect r) => new(r.X / EmuPerPoint, r.Y / EmuPerPoint, r.Width / EmuPerPoint, r.Height / EmuPerPoint);

    private void LoadTheme(PresentationPart part)
    {
        var theme = part.SlideMasterParts.FirstOrDefault()?.ThemePart?.Theme?.ThemeElements;
        if (theme?.FontScheme?.MajorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } major) _majorFont = major;
        if (theme?.FontScheme?.MinorFont?.LatinFont?.Typeface?.Value is { Length: > 0 } minor) _minorFont = minor;
        var scheme = theme?.ColorScheme;
        if (scheme == null) return;
        void Add(string name, OpenXmlCompositeElement? el)
        {
            var hex = el?.GetFirstChild<A.RgbColorModelHex>()?.Val?.Value ?? el?.GetFirstChild<A.SystemColor>()?.LastColor?.Value;
            if (hex != null && TryHex(hex, out var c)) _themeColors[name] = c;
        }
        Add("dk1", scheme.Dark1Color);
        Add("lt1", scheme.Light1Color);
        Add("dk2", scheme.Dark2Color);
        Add("lt2", scheme.Light2Color);
        Add("accent1", scheme.Accent1Color);
        Add("accent2", scheme.Accent2Color);
        Add("accent3", scheme.Accent3Color);
        Add("accent4", scheme.Accent4Color);
        Add("accent5", scheme.Accent5Color);
        Add("accent6", scheme.Accent6Color);
        Add("hlink", scheme.Hyperlink);
        if (_themeColors.TryGetValue("dk1", out var dk1)) _themeColors["tx1"] = dk1;
        if (_themeColors.TryGetValue("lt1", out var lt1)) _themeColors["bg1"] = lt1;
        if (_themeColors.TryGetValue("dk2", out var dk2)) _themeColors["tx2"] = dk2;
        if (_themeColors.TryGetValue("lt2", out var lt2)) _themeColors["bg2"] = lt2;
    }

    private void DrawBackground(XGraphics gfx, SlidePart slide, double width, double height)
    {
        var sources = new OpenXmlElement?[]
        {
            slide.Slide?.CommonSlideData?.Background,
            slide.SlideLayoutPart?.SlideLayout?.CommonSlideData?.Background,
            slide.SlideLayoutPart?.SlideMasterPart?.SlideMaster?.CommonSlideData?.Background,
        };
        var color = XColors.White;
        foreach (var bg in sources)
        {
            if (bg == null) continue;
            var fill = bg.Descendants<A.SolidFill>().FirstOrDefault();
            if (fill != null && ResolveColor(fill) is { } c)
            {
                color = c;
                break;
            }
            var gradient = bg.Descendants<A.GradientStop>().FirstOrDefault();
            if (gradient != null && ResolveColor(gradient) is { } g)
            {
                color = g;
                break;
            }
            if (bg.Descendants<A.Blip>().FirstOrDefault()?.Embed?.Value is { } rel)
            {
                var owner = bg == sources[0] ? (OpenXmlPart)slide : bg == sources[1] ? slide.SlideLayoutPart! : slide.SlideLayoutPart!.SlideMasterPart!;
                if (LoadImage(owner, rel) is { } img)
                {
                    gfx.DrawImage(img, 0, 0, width, height);
                    img.Dispose();
                    return;
                }
            }
        }
        gfx.DrawRectangle(new XSolidBrush(color), 0, 0, width, height);
    }

    private void DrawTree(XGraphics gfx, OpenXmlCompositeElement tree, OpenXmlPart owner, SlidePart slide, Transform transform, bool nonPlaceholdersOnly)
    {
        foreach (var element in tree.Elements())
        {
            try
            {
                switch (element)
                {
                    case P.Shape shape:
                        var ph = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
                        if (nonPlaceholdersOnly && ph != null) continue;
                        DrawShape(gfx, shape, owner, slide, transform);
                        break;
                    case P.Picture picture:
                        if (nonPlaceholdersOnly && picture.NonVisualPictureProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape != null) continue;
                        DrawPicture(gfx, picture, owner, transform);
                        break;
                    case P.GroupShape group:
                        DrawGroup(gfx, group, owner, slide, transform, nonPlaceholdersOnly);
                        break;
                    case P.GraphicFrame frame:
                        DrawGraphicFrame(gfx, frame, transform);
                        break;
                    case P.ConnectionShape connector:
                        DrawConnector(gfx, connector, transform);
                        break;
                }
            }
            catch
            {
                // One broken shape should not lose the slide.
            }
        }
    }

    private void DrawGroup(XGraphics gfx, P.GroupShape group, OpenXmlPart owner, SlidePart slide, Transform parent, bool nonPlaceholdersOnly)
    {
        var xfrm = group.GroupShapeProperties?.TransformGroup;
        Transform transform = parent;
        if (xfrm?.Offset != null && xfrm.Extents != null && xfrm.ChildOffset != null && xfrm.ChildExtents != null)
        {
            double ox = xfrm.Offset.X?.Value ?? 0, oy = xfrm.Offset.Y?.Value ?? 0;
            double ex = xfrm.Extents.Cx?.Value ?? 1, ey = xfrm.Extents.Cy?.Value ?? 1;
            double cx = xfrm.ChildOffset.X?.Value ?? 0, cy = xfrm.ChildOffset.Y?.Value ?? 0;
            double cex = Math.Max(1, xfrm.ChildExtents.Cx?.Value ?? 1), cey = Math.Max(1, xfrm.ChildExtents.Cy?.Value ?? 1);
            double sx = ex / cex, sy = ey / cey;
            transform = r => parent(new Rect(ox + (r.X - cx) * sx, oy + (r.Y - cy) * sy, r.Width * sx, r.Height * sy));
        }
        DrawTree(gfx, group, owner, slide, transform, nonPlaceholdersOnly);
    }

    private void DrawShape(XGraphics gfx, P.Shape shape, OpenXmlPart owner, SlidePart slide, Transform transform)
    {
        var ph = shape.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
        var (layoutShape, masterShape) = FindPlaceholderSources(slide, ph);
        var xfrm = shape.ShapeProperties?.Transform2D ?? layoutShape?.ShapeProperties?.Transform2D ?? masterShape?.ShapeProperties?.Transform2D;
        if (xfrm?.Offset == null || xfrm.Extents == null) return;
        var rect = transform(new Rect(xfrm.Offset.X?.Value ?? 0, xfrm.Offset.Y?.Value ?? 0, xfrm.Extents.Cx?.Value ?? 0, xfrm.Extents.Cy?.Value ?? 0));
        var rotation = (xfrm.Rotation?.Value ?? 0) / 60000.0;

        var state = gfx.Save();
        if (Math.Abs(rotation) > 0.01) gfx.RotateAtTransform(rotation, new XPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));

        var props = shape.ShapeProperties;
        var geometry = props?.GetFirstChild<A.PresetGeometry>()?.Preset?.Value;
        var fill = props?.GetFirstChild<A.SolidFill>() is { } sf ? ResolveColor(sf) : null;
        if (fill == null && props?.GetFirstChild<A.NoFill>() == null && props?.GetFirstChild<A.GradientFill>()?.Descendants<A.GradientStop>().FirstOrDefault() is { } gs)
            fill = ResolveColor(gs);
        if (fill == null && props?.GetFirstChild<A.NoFill>() == null && shape.ShapeStyle?.FillReference is { } fillRef && (fillRef.Index?.Value ?? 0) > 0)
            fill = ResolveColor(fillRef);
        XPen? pen = null;
        var outline = props?.GetFirstChild<A.Outline>();
        if (outline?.GetFirstChild<A.SolidFill>() is { } lf && ResolveColor(lf) is { } lc)
            pen = new XPen(lc, Math.Max(0.5, (outline.Width?.Value ?? 9525) / EmuPerPoint));
        else if (outline?.GetFirstChild<A.NoFill>() == null && shape.ShapeStyle?.LineReference is { } lineRef && (lineRef.Index?.Value ?? 0) > 0 && ResolveColor(lineRef) is { } rc)
            pen = new XPen(rc, 0.75);

        if (fill != null || pen != null)
        {
            var brush = fill != null ? new XSolidBrush(fill.Value) : null;
            var r = new XRect(rect.X, rect.Y, rect.Width, rect.Height);
            if (geometry == A.ShapeTypeValues.Ellipse)
                DrawEllipse(gfx, pen, brush, r);
            else if (geometry == A.ShapeTypeValues.RoundRectangle)
                DrawRounded(gfx, pen, brush, r, Math.Min(r.Width, r.Height) * 0.16);
            else if (geometry == A.ShapeTypeValues.Line)
                gfx.DrawLine(pen ?? new XPen(XColors.Black, 1), r.Left, r.Top, r.Right, r.Bottom);
            else
                DrawRect(gfx, pen, brush, r);
        }

        if (shape.TextBody != null) DrawText(gfx, shape.TextBody, rect, ph, layoutShape, masterShape, slide);
        gfx.Restore(state);
    }

    private (P.Shape? Layout, P.Shape? Master) FindPlaceholderSources(SlidePart slide, P.PlaceholderShape? ph)
    {
        if (ph == null) return (null, null);
        static P.Shape? Find(OpenXmlCompositeElement? tree, P.PlaceholderShape ph) =>
            tree?.Descendants<P.Shape>().FirstOrDefault(s =>
            {
                var other = s.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties?.PlaceholderShape;
                if (other == null) return false;
                if (ph.Index?.Value != null && other.Index?.Value == ph.Index.Value) return true;
                var t1 = ph.Type?.Value ?? P.PlaceholderValues.Body;
                var t2 = other.Type?.Value ?? P.PlaceholderValues.Body;
                return ph.Index?.Value == null && t1 == t2 || (t1 == P.PlaceholderValues.CenteredTitle && t2 == P.PlaceholderValues.Title);
            });
        var layout = Find(slide.SlideLayoutPart?.SlideLayout?.CommonSlideData?.ShapeTree, ph);
        var master = Find(slide.SlideLayoutPart?.SlideMasterPart?.SlideMaster?.CommonSlideData?.ShapeTree, ph);
        return (layout, master);
    }

    private void DrawText(XGraphics gfx, P.TextBody body, Rect rect, P.PlaceholderShape? ph, P.Shape? layoutShape, P.Shape? masterShape, SlidePart slide)
    {
        var bodyPr = body.BodyProperties;
        double l = (bodyPr?.LeftInset?.Value ?? 91440) / EmuPerPoint, r = (bodyPr?.RightInset?.Value ?? 91440) / EmuPerPoint;
        double t = (bodyPr?.TopInset?.Value ?? 45720) / EmuPerPoint, b = (bodyPr?.BottomInset?.Value ?? 45720) / EmuPerPoint;
        var box = new Rect(rect.X + l, rect.Y + t, Math.Max(1, rect.Width - l - r), Math.Max(1, rect.Height - t - b));
        var anchorValue = bodyPr?.Anchor?.Value ?? layoutShape?.TextBody?.BodyProperties?.Anchor?.Value ?? masterShape?.TextBody?.BodyProperties?.Anchor?.Value;
        var anchor = anchorValue == A.TextAnchoringTypeValues.Center ? VerticalAnchor.Middle :
            anchorValue == A.TextAnchoringTypeValues.Bottom ? VerticalAnchor.Bottom : VerticalAnchor.Top;
        var fontScale = (bodyPr?.GetFirstChild<A.NormalAutoFit>()?.FontScale?.Value ?? 100000) / 100000.0;

        var type = ph?.Type?.Value;
        var isTitle = type == P.PlaceholderValues.Title || type == P.PlaceholderValues.CenteredTitle;
        var isBody = ph != null && !isTitle && type != P.PlaceholderValues.SlideNumber && type != P.PlaceholderValues.Footer && type != P.PlaceholderValues.DateAndTime;
        var masterStyles = slide.SlideLayoutPart?.SlideMasterPart?.SlideMaster?.TextStyles;
        OpenXmlCompositeElement? styleList = isTitle ? masterStyles?.TitleStyle : isBody ? masterStyles?.BodyStyle : masterStyles?.OtherStyle;

        var paragraphs = new List<RichParagraph>();
        foreach (var p in body.Elements<A.Paragraph>())
        {
            var level = p.ParagraphProperties?.Level?.Value ?? 0;
            var levelStyle = LevelProperties(styleList, level);
            var layoutLevel = LevelProperties(layoutShape?.TextBody?.ListStyle, level);
            var defSize = (layoutLevel?.GetFirstChild<A.DefaultRunProperties>()?.FontSize?.Value
                           ?? levelStyle?.GetFirstChild<A.DefaultRunProperties>()?.FontSize?.Value
                           ?? (isTitle ? 4400 : isBody ? 2800 - level * 400 : 1800)) / 100.0;
            var defColor = levelStyle?.GetFirstChild<A.DefaultRunProperties>()?.GetFirstChild<A.SolidFill>() is { } dfill ? ResolveColor(dfill) : null;
            var font = isTitle ? _majorFont : _minorFont;

            var runs = new List<TextRun>();
            foreach (var child in p.Elements())
            {
                switch (child)
                {
                    case A.Run run:
                        runs.Add(MakeRun(run.Text?.Text ?? "", run.RunProperties, font, defSize, defColor, fontScale));
                        break;
                    case A.Break:
                        runs.Add(new TextRun("\n", font, defSize * fontScale));
                        break;
                    case A.Field fld:
                        var text = fld.Type?.Value == "slidenum" ? (SlideNumber(slide)?.ToString() ?? fld.Text?.Text ?? "") : fld.Text?.Text ?? "";
                        runs.Add(MakeRun(text, fld.RunProperties, font, defSize, defColor, fontScale));
                        break;
                }
            }
            if (runs.Count == 0) runs.Add(new TextRun(" ", font, (p.GetFirstChild<A.EndParagraphRunProperties>()?.FontSize?.Value / 100.0 ?? defSize) * fontScale));

            var algn = p.ParagraphProperties?.Alignment?.Value ?? layoutLevel?.Alignment?.Value ?? levelStyle?.Alignment?.Value;
            var align = algn == A.TextAlignmentTypeValues.Center ? TextAlign.Center :
                algn == A.TextAlignmentTypeValues.Right ? TextAlign.Right :
                algn == A.TextAlignmentTypeValues.Justified ? TextAlign.Justify : TextAlign.Left;
            var bullet = isBody && p.ParagraphProperties?.GetFirstChild<A.NoBullet>() == null &&
                         (levelStyle?.GetFirstChild<A.CharacterBullet>() != null || p.ParagraphProperties?.GetFirstChild<A.CharacterBullet>() != null) &&
                         layoutLevel?.GetFirstChild<A.NoBullet>() == null && runs.Any(x => x.Text.Trim().Length > 0)
                ? "• " : null;
            paragraphs.Add(new RichParagraph(runs, align, defSize * 0.2 * fontScale, bullet, level * 24));
        }

        RichTextLayout.Draw(gfx, paragraphs, box, anchor, shrinkToFit: true, lineSpacing: 1.1);
    }

    private static A.TextParagraphPropertiesType? LevelProperties(OpenXmlCompositeElement? list, int level) => list?.Elements<A.TextParagraphPropertiesType>()
        .FirstOrDefault(e => e.LocalName == $"lvl{level + 1}pPr");

    private TextRun MakeRun(string text, A.TextCharacterPropertiesType? props, string font, double defSize, XColor? defColor, double scale)
    {
        var size = (props?.FontSize?.Value / 100.0 ?? defSize) * scale;
        var face = props?.GetFirstChild<A.LatinFont>()?.Typeface?.Value;
        if (face == "+mj-lt") face = _majorFont;
        else if (face == "+mn-lt") face = _minorFont;
        var color = props?.GetFirstChild<A.SolidFill>() is { } fill ? ResolveColor(fill) : defColor;
        return new TextRun(text, string.IsNullOrEmpty(face) ? font : face, size,
            props?.Bold?.Value ?? false, props?.Italic?.Value ?? false,
            props?.Underline?.Value is { } u && u != A.TextUnderlineValues.None,
            color ?? (_themeColors.TryGetValue("tx1", out var tx1) ? tx1 : XColors.Black));
    }

    private static int? SlideNumber(SlidePart slide)
    {
        var pres = slide.GetParentParts().OfType<PresentationPart>().FirstOrDefault();
        var ids = pres?.Presentation?.SlideIdList?.Elements<P.SlideId>().ToList();
        if (ids == null) return null;
        var rel = pres!.GetIdOfPart(slide);
        var index = ids.FindIndex(i => i.RelationshipId?.Value == rel);
        return index >= 0 ? index + 1 : null;
    }

    private void DrawPicture(XGraphics gfx, P.Picture picture, OpenXmlPart owner, Transform transform)
    {
        var xfrm = picture.ShapeProperties?.Transform2D;
        if (xfrm?.Offset == null || xfrm.Extents == null) return;
        var rel = picture.BlipFill?.Blip?.Embed?.Value;
        if (rel == null) return;
        var rect = transform(new Rect(xfrm.Offset.X?.Value ?? 0, xfrm.Offset.Y?.Value ?? 0, xfrm.Extents.Cx?.Value ?? 0, xfrm.Extents.Cy?.Value ?? 0));
        using var image = LoadImage(owner, rel);
        if (image == null) return;
        var rotation = (xfrm.Rotation?.Value ?? 0) / 60000.0;
        var state = gfx.Save();
        if (Math.Abs(rotation) > 0.01) gfx.RotateAtTransform(rotation, new XPoint(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
        gfx.DrawImage(image, rect.X, rect.Y, rect.Width, rect.Height);
        gfx.Restore(state);
    }

    private void DrawGraphicFrame(XGraphics gfx, P.GraphicFrame frame, Transform transform)
    {
        var table = frame.Descendants<A.Table>().FirstOrDefault();
        var xfrm = frame.Transform;
        if (table == null || xfrm?.Offset == null) return;
        var origin = transform(new Rect(xfrm.Offset.X?.Value ?? 0, xfrm.Offset.Y?.Value ?? 0, 1, 1));
        var widths = table.TableGrid?.Elements<A.GridColumn>().Select(c => transform(new Rect(0, 0, c.Width?.Value ?? 0, 1)).Width).ToList() ?? [];
        var y = origin.Y;
        var pen = new XPen(XColors.Gray, 0.5);
        var firstRow = true;
        foreach (var row in table.Elements<A.TableRow>())
        {
            var h = transform(new Rect(0, 0, 1, row.Height?.Value ?? 370840)).Height;
            var x = origin.X;
            var col = 0;
            foreach (var cell in row.Elements<A.TableCell>())
            {
                if (col >= widths.Count) break;
                var span = Math.Max(1, cell.GridSpan?.Value ?? 1);
                var w = widths.Skip(col).Take(span).Sum();
                var r = new XRect(x, y, w, h);
                if (cell.TableCellProperties?.GetFirstChild<A.SolidFill>() is { } fill && ResolveColor(fill) is { } c)
                    gfx.DrawRectangle(new XSolidBrush(c), r);
                else if (firstRow && _themeColors.TryGetValue("accent1", out var accent))
                    gfx.DrawRectangle(new XSolidBrush(accent), r);
                gfx.DrawRectangle(pen, r);
                var text = string.Join("\n", cell.TextBody?.Elements<A.Paragraph>().Select(p => string.Concat(p.Descendants<A.Text>().Select(t => t.Text))) ?? []);
                var size = cell.TextBody?.Descendants<A.RunProperties>().FirstOrDefault()?.FontSize?.Value / 100.0 ?? 14;
                var color = firstRow ? XColors.White : XColors.Black;
                RichTextLayout.Draw(gfx, [new RichParagraph([new TextRun(text, _minorFont, size, firstRow, Color: color)])],
                    new Rect(x + 4, y + 2, Math.Max(1, w - 8), Math.Max(1, h - 4)), VerticalAnchor.Middle, shrinkToFit: true);
                x += w;
                col += span;
            }
            y += h;
            firstRow = false;
        }
    }

    private void DrawConnector(XGraphics gfx, P.ConnectionShape connector, Transform transform)
    {
        var xfrm = connector.ShapeProperties?.Transform2D;
        if (xfrm?.Offset == null || xfrm.Extents == null) return;
        var rect = transform(new Rect(xfrm.Offset.X?.Value ?? 0, xfrm.Offset.Y?.Value ?? 0, xfrm.Extents.Cx?.Value ?? 0, xfrm.Extents.Cy?.Value ?? 0));
        var outline = connector.ShapeProperties?.GetFirstChild<A.Outline>();
        var color = outline?.GetFirstChild<A.SolidFill>() is { } f ? ResolveColor(f) ?? XColors.Black : XColors.Black;
        var pen = new XPen(color, Math.Max(0.5, (outline?.Width?.Value ?? 12700) / EmuPerPoint));
        double x1 = rect.Left, y1 = rect.Top, x2 = rect.Right, y2 = rect.Bottom;
        if (xfrm.HorizontalFlip?.Value == true) (x1, x2) = (x2, x1);
        if (xfrm.VerticalFlip?.Value == true) (y1, y2) = (y2, y1);
        gfx.DrawLine(pen, x1, y1, x2, y2);
    }

    private static XImage? LoadImage(OpenXmlPart owner, string rel)
    {
        try
        {
            if (owner.GetPartById(rel) is not ImagePart part) return null;
            using var s = part.GetStream();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();
            if (part.ContentType is not ("image/jpeg" or "image/png"))
                bytes = Imaging.ImageIO.EncodePng(Imaging.ImageIO.Decode(bytes));
            return XImage.FromStream(new MemoryStream(bytes));
        }
        catch
        {
            return null;
        }
    }

    private XColor? ResolveColor(OpenXmlElement fill)
    {
        if (fill.GetFirstChild<A.RgbColorModelHex>() is { } rgb && rgb.Val?.Value is { } hex && TryHex(hex, out var c))
            return ApplyMods(c, rgb);
        if (fill.GetFirstChild<A.SchemeColor>() is { } scheme && scheme.Val?.Value is { } val)
        {
            var name = val.ToString() switch
            {
                "tx1" or "TextDark1" => "tx1",
                "bg1" or "BackgroundLight1" => "bg1",
                _ => ((IEnumValue)val).Value,
            };
            if (_themeColors.TryGetValue(name, out var themed)) return ApplyMods(themed, scheme);
        }
        if (fill.GetFirstChild<A.SystemColor>()?.LastColor?.Value is { } sys && TryHex(sys, out var s)) return s;
        if (fill.GetFirstChild<A.PresetColor>()?.Val?.Value is { } preset)
        {
            var known = System.Drawing.Color.FromName(((IEnumValue)preset).Value);
            if (known.IsKnownColor) return XColor.FromArgb(known.R, known.G, known.B);
        }
        return null;
    }

    /// <summary>Applies lumMod/lumOff/tint/shade adjustments approximately.</summary>
    private static XColor ApplyMods(XColor c, OpenXmlElement color)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        foreach (var mod in color.Elements())
        {
            var v = (mod.GetAttributes().FirstOrDefault(a => a.LocalName == "val").Value is { } s && int.TryParse(s, out var n) ? n : 100000) / 100000.0;
            switch (mod.LocalName)
            {
                case "lumMod": r *= v; g *= v; b *= v; break;
                case "lumOff": r += v; g += v; b += v; break;
                case "shade": r *= v; g *= v; b *= v; break;
                case "tint": r += (1 - r) * (1 - v); g += (1 - g) * (1 - v); b += (1 - b) * (1 - v); break;
            }
        }
        return XColor.FromArgb((int)(Math.Clamp(r, 0, 1) * 255), (int)(Math.Clamp(g, 0, 1) * 255), (int)(Math.Clamp(b, 0, 1) * 255));
    }

    private static bool TryHex(string hex, out XColor color)
    {
        color = XColors.Black;
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = XColor.FromArgb((int)(v >> 16 & 0xFF), (int)(v >> 8 & 0xFF), (int)(v & 0xFF));
        return true;
    }

    private static void DrawRect(XGraphics gfx, XPen? pen, XBrush? brush, XRect r)
    {
        if (brush != null && pen != null) gfx.DrawRectangle(pen, brush, r);
        else if (brush != null) gfx.DrawRectangle(brush, r);
        else if (pen != null) gfx.DrawRectangle(pen, r);
    }

    private static void DrawEllipse(XGraphics gfx, XPen? pen, XBrush? brush, XRect r)
    {
        if (brush != null && pen != null) gfx.DrawEllipse(pen, brush, r);
        else if (brush != null) gfx.DrawEllipse(brush, r);
        else if (pen != null) gfx.DrawEllipse(pen, r);
    }

    private static void DrawRounded(XGraphics gfx, XPen? pen, XBrush? brush, XRect r, double radius)
    {
        var size = new XSize(radius * 2, radius * 2);
        if (brush != null && pen != null) gfx.DrawRoundedRectangle(pen, brush, r, size);
        else if (brush != null) gfx.DrawRoundedRectangle(brush, r, size);
        else if (pen != null) gfx.DrawRoundedRectangle(pen, r, size);
    }
}
