using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PageSmith.Core.Tools;

/// <summary>Copies pages from source documents into new documents, opening each source once.</summary>
public sealed class PageCopier : IDisposable
{
    private readonly Dictionary<string, PdfDocument> _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Appends page <paramref name="pageIndex"/> of <paramref name="source"/> to <paramref name="target"/>.</summary>
    public PdfPage Copy(InputFile source, int pageIndex, PdfDocument target, int extraRotation = 0)
    {
        var page = target.AddPage(Source(source).Pages[pageIndex]);
        if (extraRotation != 0) page.Rotate = Normalize(page.Rotate + extraRotation);
        return page;
    }

    /// <summary>Writes the given pages of <paramref name="source"/> to a new PDF file.</summary>
    public void WritePages(InputFile source, IEnumerable<int> pages, string path, string? title = null)
    {
        using var doc = PdfDocs.Create(title ?? Path.GetFileNameWithoutExtension(path));
        foreach (var p in pages) Copy(source, p, doc);
        PdfDocs.Save(doc, path);
    }

    public int PageCount(InputFile source) => Source(source).PageCount;

    /// <summary>The opened source document, for reading outlines and metadata.</summary>
    public PdfDocument Source(InputFile source)
    {
        if (!_sources.TryGetValue(source.Path, out var doc))
        {
            doc = PdfDocs.Open(source, PdfDocumentOpenMode.Import);
            _sources[source.Path] = doc;
        }
        return doc;
    }

    public static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;

    public void Dispose()
    {
        foreach (var doc in _sources.Values) doc.Dispose();
        _sources.Clear();
    }
}
