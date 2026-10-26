using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PageSmith.Core.Pdf;

/// <summary>A bookmark: its title, the page it points to (0-based, -1 when unknown) and its children.</summary>
public sealed record OutlineItem(string Title, int PageIndex, IReadOnlyList<OutlineItem> Children);

/// <summary>Reads a document's bookmarks (outline), following the destination forms viewers support.</summary>
public static class DocumentOutline
{
    private const int MaxItems = 5000;

    public static IReadOnlyList<OutlineItem> Read(InputFile input)
    {
        using var doc = PdfDocs.Open(input, PdfDocumentOpenMode.Import);
        return Read(doc);
    }

    public static IReadOnlyList<OutlineItem> Read(PdfDocument doc)
    {
        var pages = PageNumbers(doc);
        var catalog = doc.Internals.Catalog;
        var root = catalog.Elements.GetDictionary("/Outlines");
        if (root == null) return [];
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var count = 0;
        return Children(root, 0);

        List<OutlineItem> Children(PdfDictionary parent, int depth)
        {
            var list = new List<OutlineItem>();
            if (depth > 32) return list;
            for (var item = parent.Elements.GetDictionary("/First"); item != null && count < MaxItems; item = item.Elements.GetDictionary("/Next"))
            {
                if (!seen.Add(item)) break; // a loop in a damaged file
                count++;
                var title = item.Elements.GetString("/Title").Replace('\r', ' ').Replace('\n', ' ').Trim();
                list.Add(new OutlineItem(title.Length == 0 ? "(untitled)" : title, PageOf(item), Children(item, depth + 1)));
            }
            return list;
        }

        int PageOf(PdfDictionary item)
        {
            var dest = item.Elements["/Dest"];
            if (dest == null && item.Elements.GetDictionary("/A") is { } action && action.Elements.GetName("/S") == "/GoTo")
                dest = action.Elements["/D"];
            return Resolve(dest, catalog, pages, 0);
        }
    }

    /// <summary>Page index by page object number, for resolving destinations.</summary>
    internal static Dictionary<int, int> PageNumbers(PdfDocument doc)
    {
        var pages = new Dictionary<int, int>();
        for (var i = 0; i < doc.PageCount; i++)
            if (doc.Pages[i].Reference is { } r) pages[r.ObjectNumber] = i;
        return pages;
    }

    /// <summary>The 0-based page a destination (explicit, named, or a GoTo action's) points to; -1 when unknown.</summary>
    internal static int Resolve(PdfItem? dest, PdfCatalog catalog, Dictionary<int, int> pages) => Resolve(dest, catalog, pages, 0);

    private static int Resolve(PdfItem? dest, PdfCatalog catalog, Dictionary<int, int> pages, int depth)
    {
        if (depth > 4) return -1;
        switch (dest)
        {
            case PdfReference r when r.Value is PdfArray array:
                return Resolve(array, catalog, pages, depth + 1);
            case PdfReference r when r.Value is PdfDictionary d:
                return Resolve(d.Elements["/D"], catalog, pages, depth + 1);
            case PdfArray array when array.Elements.Count > 0:
                return array.Elements[0] switch
                {
                    PdfReference page => pages.GetValueOrDefault(page.ObjectNumber, -1),
                    PdfInteger index => index.Value,
                    _ => -1,
                };
            case PdfDictionary d:
                return Resolve(d.Elements["/D"], catalog, pages, depth + 1);
            case PdfString s:
                return Resolve(Named(catalog, s.Value), catalog, pages, depth + 1);
            case PdfName n:
                return Resolve(Named(catalog, n.Value.TrimStart('/')), catalog, pages, depth + 1);
            default:
                return -1;
        }
    }

    /// <summary>Looks up a named destination in the catalog's /Dests dictionary or the /Names /Dests tree.</summary>
    private static PdfItem? Named(PdfCatalog catalog, string name)
    {
        if (catalog.Elements.GetDictionary("/Dests") is { } dests && dests.Elements["/" + name] is { } direct) return direct;
        var tree = catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests");
        return tree == null ? null : SearchNameTree(tree, name, 0);
    }

    private static PdfItem? SearchNameTree(PdfDictionary node, string name, int depth)
    {
        if (depth > 16) return null;
        if (node.Elements.GetArray("/Names") is { } names)
            for (var i = 0; i + 1 < names.Elements.Count; i += 2)
                if (names.Elements[i] is PdfString key && key.Value == name) return names.Elements[i + 1];
        if (node.Elements.GetArray("/Kids") is { } kids)
            for (var i = 0; i < kids.Elements.Count; i++)
                if (kids.Elements.GetDictionary(i) is { } kid && SearchNameTree(kid, name, depth + 1) is { } found) return found;
        return null;
    }
}
