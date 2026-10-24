using System.Globalization;
using System.Windows;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PageSmith.Core.Pdf;

/// <summary>
/// Turns form fields into ordinary page content: each widget's current appearance is stamped onto
/// its page; choice fields (whose appearance viewers usually generate) get their value drawn as text.
/// The widgets and the AcroForm are then removed.
/// </summary>
public static class FormFlattener
{
    public static int Flatten(PdfDocument doc)
    {
        var flattened = 0;
        var counter = 0;
        foreach (var page in doc.Pages)
        {
            var annots = page.Elements.GetArray("/Annots");
            if (annots == null) continue;
            var keep = new List<PdfItem>();
            var stamps = new StringBuilder();
            var texts = new List<(Rect Bounds, string Text, double Size)>();
            var geometry = PageGeometry.From(page);
            for (var i = 0; i < annots.Elements.Count; i++)
            {
                var widget = annots.Elements.GetDictionary(i);
                if (widget == null || widget.Elements.GetName("/Subtype") != "/Widget")
                {
                    keep.Add(annots.Elements[i]);
                    continue;
                }
                flattened++;
                if ((widget.Elements.GetInteger("/F") & 2) != 0) continue; // hidden

                var rectArray = widget.Elements.GetArray("/Rect");
                if (rectArray is not { Elements.Count: 4 }) continue;
                var rect = new Rect(new Point(rectArray.Elements.GetReal(0), rectArray.Elements.GetReal(1)),
                    new Point(rectArray.Elements.GetReal(2), rectArray.Elements.GetReal(3)));

                var field = Inherited(widget, "/FT") is PdfName ft ? ft.Value : "";
                if (field == "/Ch")
                {
                    var value = ValueText(Inherited(widget, "/V"));
                    if (value.Length > 0) texts.Add((geometry.ToDisplay(rect), value, FontSize(widget, rect.Height)));
                    continue;
                }

                var appearance = Appearance(widget);
                // Text fields whose appearance does not show the value (none, or just a box) get the value drawn.
                if (field == "/Tx" && !ShowsText(appearance) && ValueText(Inherited(widget, "/V")) is { Length: > 0 } text)
                    texts.Add((geometry.ToDisplay(rect), text, FontSize(widget, rect.Height)));
                if (appearance == null) continue;
                var name = $"/PSFlat{++counter}";
                var xobjects = Resources(page).Elements.GetDictionary("/XObject");
                if (xobjects == null)
                {
                    xobjects = new PdfDictionary(doc);
                    Resources(page).Elements["/XObject"] = xobjects;
                }
                appearance.Elements.SetName("/Type", "/XObject");
                appearance.Elements.SetName("/Subtype", "/Form");
                if (appearance.Reference == null) doc.Internals.AddObject(appearance);
                xobjects.Elements.SetReference(name, appearance);

                // Map the appearance's bounding box onto the widget rectangle.
                var bbox = appearance.Elements.GetArray("/BBox");
                double bx = 0, by = 0, bw = rect.Width, bh = rect.Height;
                if (bbox is { Elements.Count: 4 })
                {
                    bx = Math.Min(bbox.Elements.GetReal(0), bbox.Elements.GetReal(2));
                    by = Math.Min(bbox.Elements.GetReal(1), bbox.Elements.GetReal(3));
                    bw = Math.Max(0.001, Math.Abs(bbox.Elements.GetReal(2) - bbox.Elements.GetReal(0)));
                    bh = Math.Max(0.001, Math.Abs(bbox.Elements.GetReal(3) - bbox.Elements.GetReal(1)));
                }
                var sx = rect.Width / bw;
                var sy = rect.Height / bh;
                stamps.Append(CultureInfo.InvariantCulture, $"q {sx:0.#####} 0 0 {sy:0.#####} {rect.Left - bx * sx:0.###} {rect.Top - by * sy:0.###} cm {name} Do Q\n");
            }

            if (stamps.Length > 0)
            {
                var content = new PdfContent(doc);
                content.CreateStream(Encoding.ASCII.GetBytes("q\n" + stamps + "Q\n"));
                doc.Internals.AddObject(content);
                page.Contents.Elements.Add(content.Reference!);
            }
            if (texts.Count > 0)
            {
                using var canvas = PageCanvas.Open(page);
                foreach (var (bounds, text, size) in texts)
                {
                    var font = new XFont("Arial", size, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
                    canvas.Graphics.DrawString(text, font, XBrushes.Black, new XRect(bounds.X + 2, bounds.Y, Math.Max(1, bounds.Width - 4), bounds.Height), XStringFormats.CenterLeft);
                }
            }
            if (keep.Count == 0) page.Elements.Remove("/Annots");
            else page.Elements["/Annots"] = new PdfArray(doc, keep.ToArray());
        }
        doc.Internals.Catalog.Elements.Remove("/AcroForm");
        return flattened;
    }

    private static PdfDictionary Resources(PdfPage page) => page.Resources;

    private static PdfItem? Inherited(PdfDictionary dict, string key)
    {
        for (var d = dict; d != null; d = d.Elements.GetDictionary("/Parent"))
        {
            var item = d.Elements[key];
            if (item != null) return item is PdfReference r ? r.Value : item;
        }
        return null;
    }

    private static PdfDictionary? Appearance(PdfDictionary widget)
    {
        var normal = widget.Elements.GetDictionary("/AP")?.Elements["/N"];
        if (normal is PdfReference r) normal = r.Value;
        if (normal is PdfDictionary { Stream: not null } stream) return stream;
        if (normal is PdfDictionary states)
        {
            var state = widget.Elements.GetName("/AS");
            if (string.IsNullOrEmpty(state)) return null;
            return states.Elements.GetDictionary(state);
        }
        return null;
    }

    private static bool ShowsText(PdfDictionary? appearance)
    {
        if (appearance?.Stream == null) return false;
        try
        {
            var content = Encoding.Latin1.GetString(appearance.Stream.UnfilteredValue);
            return content.Contains("Tj") || content.Contains("TJ") || content.Contains('\'');
        }
        catch
        {
            return false;
        }
    }

    private static string ValueText(PdfItem? item) => item switch
    {
        PdfString s => s.Value,
        PdfStringObject so => so.Value,
        PdfName n => n.Value.TrimStart('/'),
        PdfArray a when a.Elements.Count > 0 => ValueText(a.Elements[0]),
        _ => "",
    };

    private static double FontSize(PdfDictionary widget, double height)
    {
        var da = (Inherited(widget, "/DA") as PdfString)?.Value ?? "";
        var parts = da.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tf = Array.IndexOf(parts, "Tf");
        if (tf > 0 && double.TryParse(parts[tf - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size > 0) return size;
        return Math.Clamp(height * 0.6, 6, 14);
    }
}
