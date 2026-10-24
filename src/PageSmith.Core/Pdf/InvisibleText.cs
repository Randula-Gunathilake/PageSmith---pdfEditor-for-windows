using System.Text.RegularExpressions;
using PageSmith.Core.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PageSmith.Core.Pdf;

/// <summary>
/// Adds an invisible but selectable and searchable text layer to a page (text render mode 3),
/// the technique used by OCR software and PDF/A archives.
/// </summary>
public static partial class InvisibleText
{
    public static void AddWords(PdfPage page, IEnumerable<TextWord> words, string fontFamily = "Arial")
    {
        var list = words.Where(w => !string.IsNullOrWhiteSpace(w.Text) && w.Bounds.Width > 0 && w.Bounds.Height > 0).ToList();
        if (list.Count == 0) return;

        var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
        var fonts = new Dictionary<double, XFont>();
        var before = ExistingContents(page);
        using (var canvas = PageCanvas.Open(page))
        {
            var gfx = canvas.Graphics;
            foreach (var w in list)
            {
                var size = Math.Round(Math.Max(1, w.Bounds.Height * 0.86), 1);
                if (!fonts.TryGetValue(size, out var font))
                {
                    font = new XFont(fontFamily, size, XFontStyleEx.Regular, options);
                    fonts[size] = font;
                }
                var measured = gfx.MeasureString(w.Text, font).Width;
                if (measured <= 0) continue;
                var state = gfx.Save();
                gfx.TranslateTransform(w.Bounds.Left, w.Bounds.Top);
                gfx.ScaleTransform(w.Bounds.Width / measured, 1);
                gfx.DrawString(w.Text, font, XBrushes.Black, 0, 0, XStringFormats.TopLeft);
                gfx.Restore(state);
            }
        }
        MakeTextInvisible(page, before);
    }

    /// <summary>The content streams a page has now, to tell them apart from streams drawn later.</summary>
    internal static HashSet<PdfDictionary> ExistingContents(PdfPage page)
    {
        var set = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var elements = page.Contents.Elements;
        for (var i = 0; i < elements.Count; i++)
            if (elements.GetDictionary(i) is { } d) set.Add(d);
        return set;
    }

    /// <summary>Switches every text object in content streams not in <paramref name="existing"/> to render mode 3.</summary>
    internal static void MakeTextInvisible(PdfPage page, HashSet<PdfDictionary> existing)
    {
        var elements = page.Contents.Elements;
        for (var i = 0; i < elements.Count; i++)
        {
            if (elements.GetDictionary(i) is not PdfContent content || content.Stream == null) continue;
            if (existing.Contains(content)) continue;
            var bytes = content.Stream.IsFiltered() ? content.Stream.UnfilteredValue : content.Stream.Value;
            var text = Encoding.Latin1.GetString(bytes);
            if (!text.Contains("BT")) continue;
            var patched = BeginText().Replace(text, "BT\n3 Tr\n");
            content.Stream.Value = Encoding.Latin1.GetBytes(patched);
            content.Elements.Remove("/Filter");
            content.Elements.Remove("/DecodeParms");
        }
    }

    [GeneratedRegex(@"(?<![A-Za-z])BT(?![A-Za-z])\s*")]
    private static partial Regex BeginText();
}
