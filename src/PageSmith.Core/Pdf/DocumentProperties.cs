using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PageSmith.Core.Pdf;

/// <summary>What the Properties dialog shows about a PDF.</summary>
public sealed record DocumentProperties
{
    public string? Title { get; init; }
    public string? Author { get; init; }
    public string? Subject { get; init; }
    public string? Keywords { get; init; }
    public string? Creator { get; init; }
    public string? Producer { get; init; }
    public DateTime? Created { get; init; }
    public DateTime? Modified { get; init; }
    public string PdfVersion { get; init; } = "";
    public int PageCount { get; init; }
    public long FileSize { get; init; }
    public bool Encrypted { get; init; }
    public bool HasForm { get; init; }
    public bool HasBookmarks { get; init; }

    /// <summary>Size of the first page in millimetres (as displayed).</summary>
    public (double Width, double Height) PageSizeMm { get; init; }

    public static DocumentProperties Read(InputFile input)
    {
        using var doc = PdfDocs.Open(input, PdfDocumentOpenMode.Import);
        var info = doc.Info;
        static string? Text(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        static DateTime? Date(DateTime d) => d == DateTime.MinValue ? null : d;
        var first = doc.PageCount > 0 ? PageGeometry.From(doc.Pages[0]) : null;
        return new DocumentProperties
        {
            Title = Text(info.Title),
            Author = Text(info.Author),
            Subject = Text(info.Subject),
            Keywords = Text(info.Keywords),
            Creator = Text(info.Creator),
            Producer = Text(info.Producer),
            Created = Date(info.CreationDate),
            Modified = Date(info.ModificationDate),
            PdfVersion = $"{doc.Version / 10}.{doc.Version % 10}",
            PageCount = doc.PageCount,
            FileSize = new FileInfo(input.Path).Length,
            Encrypted = !string.IsNullOrEmpty(input.Password) || HasEncryptEntry(input.Path),
            HasForm = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is { Elements.Count: > 0 },
            HasBookmarks = doc.Internals.Catalog.Elements.GetDictionary("/Outlines")?.Elements.ContainsKey("/First") == true,
            PageSizeMm = first == null ? (0, 0) : (first.Width * 25.4 / 72, first.Height * 25.4 / 72),
        };
    }

    /// <summary>
    /// Encrypted files name an /Encrypt dictionary in their trailer (or cross-reference stream), which sits
    /// in plain text near the end of the file. PDFsharp doesn't expose the trailer, so look there directly.
    /// </summary>
    public static bool HasEncryptEntry(string path)
    {
        using var stream = File.OpenRead(path);
        var length = (int)Math.Min(stream.Length, 256 * 1024);
        stream.Seek(-length, SeekOrigin.End);
        var buffer = new byte[length];
        stream.ReadExactly(buffer);
        return buffer.AsSpan().IndexOf("/Encrypt"u8) >= 0;
    }
}
