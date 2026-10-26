using System.Windows;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PageSmith.Core.Pdf;

/// <summary>
/// A link on a page: its area (display space, see <see cref="PageGeometry"/>) and where it goes, either a
/// page of the same document (<see cref="TargetPage"/>, 0-based) or a web or mail address (<see cref="Uri"/>).
/// </summary>
public sealed record PageLink(int PageIndex, Rect Bounds, int TargetPage, string? Uri);

/// <summary>Reads the link annotations of a document, for a viewer to follow.</summary>
public static class DocumentLinks
{
    private const int MaxLinks = 20000;

    public static IReadOnlyList<PageLink> Read(InputFile input)
    {
        using var doc = PdfDocs.Open(input, PdfDocumentOpenMode.Import);
        return Read(doc);
    }

    public static IReadOnlyList<PageLink> Read(PdfDocument doc)
    {
        var pages = DocumentOutline.PageNumbers(doc);
        var catalog = doc.Internals.Catalog;
        var links = new List<PageLink>();
        for (var i = 0; i < doc.PageCount && links.Count < MaxLinks; i++)
        {
            var page = doc.Pages[i];
            if (page.Elements.GetArray("/Annots") is not { } annots) continue;
            var geometry = PageGeometry.From(page);
            for (var a = 0; a < annots.Elements.Count; a++)
            {
                if (annots.Elements.GetDictionary(a) is not { } annot || annot.Elements.GetName("/Subtype") != "/Link") continue;
                if (annot.Elements.GetArray("/Rect") is not { Elements.Count: 4 } rect) continue;
                var user = new Rect(new Point(rect.Elements.GetReal(0), rect.Elements.GetReal(1)), new Point(rect.Elements.GetReal(2), rect.Elements.GetReal(3)));
                if (user.Width < 1 || user.Height < 1) continue;

                string? uri = null;
                var target = -1;
                var action = annot.Elements.GetDictionary("/A");
                switch (action?.Elements.GetName("/S"))
                {
                    case "/URI":
                        uri = action.Elements.GetString("/URI").Trim();
                        break;
                    case "/GoTo":
                        target = DocumentOutline.Resolve(action.Elements["/D"], catalog, pages);
                        break;
                    case null:
                        target = DocumentOutline.Resolve(annot.Elements["/Dest"], catalog, pages);
                        break;
                }
                if (target >= doc.PageCount) target = -1;
                if (target >= 0 || !string.IsNullOrEmpty(uri))
                    links.Add(new PageLink(i, geometry.ToDisplay(user), target, string.IsNullOrEmpty(uri) ? null : uri));
            }
        }
        return links;
    }

    /// <summary>Whether a link's address is one PageSmith opens (web pages and e-mail), not a program or file.</summary>
    public static bool IsSafeToOpen(string uri) =>
        System.Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "mailto";
}
