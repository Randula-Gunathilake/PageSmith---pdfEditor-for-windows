using System.Globalization;
using System.Text;
using System.Windows;
using PageSmith.Core.Pdf;
using PdfSharp.Pdf;

namespace PageSmith.Core.Tools.Edit;

public enum MarkupKind
{
    Highlight,
    Underline,
    StrikeOut,
}

/// <summary>Text to mark on one page: one box per line, in display space (see <see cref="PageGeometry"/>).</summary>
public sealed record MarkupMark(int PageIndex, IReadOnlyList<Rect> Boxes, string? Text = null);

public sealed record MarkupOptions
{
    public required InputFile Input { get; init; }
    public required string OutputPath { get; init; }
    public required MarkupKind Kind { get; init; }
    public required IReadOnlyList<MarkupMark> Marks { get; init; }

    /// <summary>Colour as #RRGGBB; null picks yellow for highlights and red for the others.</summary>
    public string? Color { get; init; }

    /// <summary>Shown as the annotation's author in PDF readers.</summary>
    public string Author { get; init; } = Environment.UserName;
}

/// <summary>
/// Adds text markup annotations (highlight, underline, strikethrough) as standard PDF annotations with
/// appearance streams, so every PDF reader shows them and they can be edited or deleted there.
/// </summary>
public static class MarkupTool
{
    public static Task<ToolResult> RunAsync(MarkupOptions options, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (options.Marks.Count == 0 || options.Marks.All(m => m.Boxes.Count == 0))
                throw new UserFacingException("Select some text first.");
            var color = TextStyle.ParseColor(options.Color ?? (options.Kind == MarkupKind.Highlight ? "#FFD400" : "#E5484D"));
            var (r, g, b) = (color.R / 255.0, color.G / 255.0, color.B / 255.0);
            var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
            using var doc = PdfDocs.Open(options.Input);
            foreach (var mark in options.Marks.Where(m => m.Boxes.Count > 0))
            {
                ct.ThrowIfCancellationRequested();
                if (mark.PageIndex < 0 || mark.PageIndex >= doc.PageCount) continue;
                var page = doc.Pages[mark.PageIndex];
                AddAnnotation(doc, page, PageGeometry.From(page), mark, options.Kind, (r, g, b), options.Author);
            }
            PdfDocs.Save(doc, options.OutputPath);
            result.AddOutput(options.OutputPath);
            var lines = options.Marks.Sum(m => m.Boxes.Count);
            result.Summary = $"Added a {Describe(options.Kind).ToLowerInvariant()} over {lines} line{(lines == 1 ? "" : "s")} of text.";
            return result;
        }, ct);

    public static string Describe(MarkupKind kind) => kind switch
    {
        MarkupKind.Highlight => "Highlight",
        MarkupKind.Underline => "Underline",
        _ => "Strikethrough",
    };

    private static void AddAnnotation(PdfDocument doc, PdfPage page, PageGeometry geometry, MarkupMark mark, MarkupKind kind, (double R, double G, double B) color, string author)
    {
        // Corners of each line in user space, in the order QuadPoints wants (upper left, upper right,
        // lower left, lower right relative to the text). Going through display space keeps that order
        // right on rotated pages.
        var quads = new List<Point[]>();
        foreach (var box in mark.Boxes)
        {
            var padded = box;
            padded.Inflate(0.5, Math.Max(0.5, box.Height * 0.08));
            quads.Add([
                geometry.ToUser(padded.TopLeft), geometry.ToUser(padded.TopRight),
                geometry.ToUser(padded.BottomLeft), geometry.ToUser(padded.BottomRight),
            ]);
        }
        var all = quads.SelectMany(q => q).ToList();
        var bounds = new Rect(new Point(all.Min(p => p.X), all.Min(p => p.Y)), new Point(all.Max(p => p.X), all.Max(p => p.Y)));
        bounds.Inflate(1, 1);

        var annot = new PdfDictionary(doc);
        annot.Elements.SetName("/Type", "/Annot");
        annot.Elements.SetName("/Subtype", kind switch { MarkupKind.Highlight => "/Highlight", MarkupKind.Underline => "/Underline", _ => "/StrikeOut" });
        annot.Elements["/Rect"] = Array(doc, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        annot.Elements["/QuadPoints"] = Array(doc, quads.SelectMany(q => q).SelectMany(p => new[] { p.X, p.Y }).ToArray());
        annot.Elements["/C"] = Array(doc, color.R, color.G, color.B);
        annot.Elements.SetInteger("/F", 4);
        annot.Elements.SetReference("/P", page);
        annot.Elements["/T"] = new PdfString(author, PdfStringEncoding.Unicode);
        if (!string.IsNullOrWhiteSpace(mark.Text)) annot.Elements["/Contents"] = new PdfString(mark.Text, PdfStringEncoding.Unicode);
        annot.Elements["/M"] = new PdfString(PdfDate(DateTime.Now));
        annot.Elements["/CreationDate"] = new PdfString(PdfDate(DateTime.Now));

        var ap = new PdfDictionary(doc);
        ap.Elements.SetReference("/N", Appearance(doc, bounds, mark.Boxes, geometry, kind, color));
        annot.Elements["/AP"] = ap;
        doc.Internals.AddObject(annot);

        var annots = page.Elements.GetArray("/Annots");
        if (annots == null)
        {
            annots = new PdfArray(doc);
            page.Elements["/Annots"] = annots;
        }
        annots.Elements.Add(annot.Reference!);
    }

    /// <summary>The look of the annotation: a multiplied fill for highlights, a line for the others.</summary>
    private static PdfDictionary Appearance(PdfDocument doc, Rect bounds, IReadOnlyList<Rect> boxes, PageGeometry geometry, MarkupKind kind, (double R, double G, double B) color)
    {
        var sb = new StringBuilder();
        sb.Append("q /GS0 gs ");
        if (kind == MarkupKind.Highlight)
        {
            sb.Append($"{N(color.R)} {N(color.G)} {N(color.B)} rg ");
            foreach (var box in boxes)
            {
                var padded = box;
                padded.Inflate(0.5, Math.Max(0.5, box.Height * 0.08));
                var p = new[] { padded.TopLeft, padded.TopRight, padded.BottomRight, padded.BottomLeft }.Select(geometry.ToUser).ToArray();
                sb.Append($"{N(p[0].X)} {N(p[0].Y)} m {N(p[1].X)} {N(p[1].Y)} l {N(p[2].X)} {N(p[2].Y)} l {N(p[3].X)} {N(p[3].Y)} l h f ");
            }
        }
        else
        {
            sb.Append($"{N(color.R)} {N(color.G)} {N(color.B)} RG ");
            foreach (var box in boxes)
            {
                var y = kind == MarkupKind.Underline ? box.Bottom - box.Height * 0.08 : box.Top + box.Height * 0.55;
                var a = geometry.ToUser(new Point(box.Left, y));
                var b = geometry.ToUser(new Point(box.Right, y));
                sb.Append($"{N(Math.Max(0.6, box.Height * 0.07))} w {N(a.X)} {N(a.Y)} m {N(b.X)} {N(b.Y)} l S ");
            }
        }
        sb.Append('Q');

        var state = new PdfDictionary(doc);
        state.Elements.SetName("/Type", "/ExtGState");
        if (kind == MarkupKind.Highlight) state.Elements.SetName("/BM", "/Multiply");
        state.Elements["/CA"] = new PdfReal(1);
        state.Elements["/ca"] = new PdfReal(1);
        var states = new PdfDictionary(doc);
        states.Elements["/GS0"] = state;
        var resources = new PdfDictionary(doc);
        resources.Elements["/ExtGState"] = states;

        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        // The appearance is drawn in page coordinates: its box is the annotation's rectangle.
        form.Elements["/BBox"] = Array(doc, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        form.Elements["/Resources"] = resources;
        form.CreateStream(Encoding.ASCII.GetBytes(sb.ToString()));
        doc.Internals.AddObject(form);
        return form;
    }

    private static PdfArray Array(PdfDocument doc, params double[] values) => new(doc, values.Select(v => (PdfItem)new PdfReal(v)).ToArray());

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string PdfDate(DateTime t)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(t);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"D:{t:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
    }
}
