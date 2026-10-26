using System.Globalization;
using System.Windows;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PageSmith.Core.Pdf;

public enum CommentKind
{
    Note,
    Highlight,
    Underline,
    StrikeOut,
    Squiggly,
    TextBox,
    Drawing,
    Shape,
    Stamp,
    Other,
}

/// <summary>
/// A comment (markup annotation) on a page: sticky notes, highlights, drawings and the like. Its
/// <see cref="Number"/> is its place in the page's annotation list, which identifies it for deleting.
/// Bounds are in display space (see <see cref="PageGeometry"/>).
/// </summary>
public sealed record PageComment(int PageIndex, int Number, CommentKind Kind, Rect Bounds, string? Text, string? Author, DateTime? Modified);

/// <summary>Reads, adds and deletes the comments of a document, as standard PDF annotations.</summary>
public static class DocumentComments
{
    private const int MaxComments = 20000;

    private static readonly Dictionary<string, CommentKind> Kinds = new()
    {
        ["/Text"] = CommentKind.Note,
        ["/Highlight"] = CommentKind.Highlight,
        ["/Underline"] = CommentKind.Underline,
        ["/StrikeOut"] = CommentKind.StrikeOut,
        ["/Squiggly"] = CommentKind.Squiggly,
        ["/FreeText"] = CommentKind.TextBox,
        ["/Ink"] = CommentKind.Drawing,
        ["/Square"] = CommentKind.Shape,
        ["/Circle"] = CommentKind.Shape,
        ["/Line"] = CommentKind.Shape,
        ["/Polygon"] = CommentKind.Shape,
        ["/PolyLine"] = CommentKind.Shape,
        ["/Stamp"] = CommentKind.Stamp,
        ["/Caret"] = CommentKind.Other,
        ["/FileAttachment"] = CommentKind.Other,
    };

    public static string Describe(CommentKind kind) => kind switch
    {
        CommentKind.Note => "Note",
        CommentKind.Highlight => "Highlight",
        CommentKind.Underline => "Underline",
        CommentKind.StrikeOut => "Strikethrough",
        CommentKind.Squiggly => "Squiggly underline",
        CommentKind.TextBox => "Text box",
        CommentKind.Drawing => "Drawing",
        CommentKind.Shape => "Shape",
        CommentKind.Stamp => "Stamp",
        _ => "Comment",
    };

    public static IReadOnlyList<PageComment> Read(InputFile input)
    {
        using var doc = PdfDocs.Open(input, PdfDocumentOpenMode.Import);
        return Read(doc);
    }

    public static IReadOnlyList<PageComment> Read(PdfDocument doc)
    {
        var comments = new List<PageComment>();
        for (var i = 0; i < doc.PageCount && comments.Count < MaxComments; i++)
        {
            var page = doc.Pages[i];
            if (page.Elements.GetArray("/Annots") is not { } annots) continue;
            var geometry = PageGeometry.From(page);
            for (var a = 0; a < annots.Elements.Count; a++)
            {
                if (annots.Elements.GetDictionary(a) is not { } annot || !Kinds.TryGetValue(annot.Elements.GetName("/Subtype"), out var kind)) continue;
                if (annot.Elements.GetArray("/Rect") is not { Elements.Count: 4 } rect) continue;
                var user = new Rect(new Point(rect.Elements.GetReal(0), rect.Elements.GetReal(1)), new Point(rect.Elements.GetReal(2), rect.Elements.GetReal(3)));
                comments.Add(new PageComment(i, a, kind, geometry.ToDisplay(user),
                    Clean(annot.Elements.GetString("/Contents")), Clean(annot.Elements.GetString("/T")), ParseDate(annot.Elements.GetString("/M"))));
            }
        }
        return comments;
    }

    /// <summary>Adds a sticky note with its top-left corner at <paramref name="at"/> (display space).</summary>
    public static Task<ToolResult> AddNoteAsync(InputFile input, string outputPath, int pageIndex, Point at, string text, string? author = null) =>
        Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(text)) throw new UserFacingException("Type the note's text first.");
            using var doc = PdfDocs.Open(input);
            if (pageIndex < 0 || pageIndex >= doc.PageCount) throw new UserFacingException("That page is not in the document.");
            var page = doc.Pages[pageIndex];
            var geometry = PageGeometry.From(page);
            // A 20-point icon, kept inside the page.
            var display = new Rect(Math.Clamp(at.X, 0, Math.Max(0, geometry.Width - 20)), Math.Clamp(at.Y, 0, Math.Max(0, geometry.Height - 20)), 20, 20);
            var user = geometry.ToUser(display);

            var note = new PdfDictionary(doc);
            note.Elements.SetName("/Type", "/Annot");
            note.Elements.SetName("/Subtype", "/Text");
            note.Elements["/Rect"] = Array(doc, user.Left, user.Top, user.Right, user.Bottom);
            note.Elements["/Contents"] = new PdfString(text.Trim(), PdfStringEncoding.Unicode);
            note.Elements["/T"] = new PdfString(author ?? Environment.UserName, PdfStringEncoding.Unicode);
            note.Elements["/M"] = new PdfString(PdfDate(DateTime.Now));
            note.Elements.SetName("/Name", "/Comment");
            note.Elements["/C"] = Array(doc, 1, 0.84, 0);
            // Print, and keep the icon's size and orientation when zooming or rotating.
            note.Elements.SetInteger("/F", 4 | 8 | 16);
            note.Elements.SetBoolean("/Open", false);
            note.Elements.SetReference("/P", page);
            var ap = new PdfDictionary(doc);
            ap.Elements.SetReference("/N", NoteIcon(doc, user));
            note.Elements["/AP"] = ap;
            doc.Internals.AddObject(note);

            var annots = page.Elements.GetArray("/Annots");
            if (annots == null)
            {
                annots = new PdfArray(doc);
                page.Elements["/Annots"] = annots;
            }
            annots.Elements.Add(note.Reference!);
            PdfDocs.Save(doc, outputPath);
            var result = new ToolResult { InputBytes = new FileInfo(input.Path).Length, Summary = "Added a note." };
            return result.AddOutput(outputPath);
        });

    /// <summary>Deletes a comment (and its pop-up window) found by <see cref="Read(InputFile)"/>.</summary>
    public static Task<ToolResult> DeleteAsync(InputFile input, string outputPath, PageComment comment) =>
        Task.Run(() =>
        {
            using var doc = PdfDocs.Open(input);
            if (comment.PageIndex >= doc.PageCount || doc.Pages[comment.PageIndex].Elements.GetArray("/Annots") is not { } annots ||
                comment.Number >= annots.Elements.Count || annots.Elements.GetDictionary(comment.Number) is not { } annot ||
                !Kinds.ContainsKey(annot.Elements.GetName("/Subtype")))
                throw new UserFacingException("That comment is no longer in the document.");
            var popup = annot.Elements.GetDictionary("/Popup");
            annots.Elements.RemoveAt(comment.Number);
            if (popup != null)
            {
                for (var i = annots.Elements.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(annots.Elements.GetDictionary(i), popup)) annots.Elements.RemoveAt(i);
            }
            PdfDocs.Save(doc, outputPath);
            var result = new ToolResult { InputBytes = new FileInfo(input.Path).Length, Summary = $"Deleted a {Describe(comment.Kind).ToLowerInvariant()}." };
            return result.AddOutput(outputPath);
        });

    /// <summary>A yellow speech bubble with lines of "text", drawn in the page's coordinates.</summary>
    private static PdfDictionary NoteIcon(PdfDocument doc, Rect user)
    {
        double x = user.Left, y = user.Top;
        string P(double dx, double dy) => $"{N(x + dx)} {N(y + dy)}";
        var content = $"q 1 0.84 0 rg 0.55 0.42 0 RG 0.8 w {P(1.5, 6)} m {P(18.5, 6)} l {P(18.5, 18.5)} l {P(1.5, 18.5)} l h {P(4.5, 6)} m {P(4.5, 1.5)} l {P(9, 6)} l h B " +
                      $"0.45 0.33 0 RG 1 w {P(4.5, 15)} m {P(15.5, 15)} l S {P(4.5, 12)} m {P(15.5, 12)} l S {P(4.5, 9)} m {P(12, 9)} l S Q";
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = Array(doc, user.Left, user.Top, user.Right, user.Bottom);
        form.CreateStream(Encoding.ASCII.GetBytes(content));
        doc.Internals.AddObject(form);
        return form;
    }

    private static string? Clean(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>Parses a PDF date ("D:20261027143000+05'30'"), ignoring the time zone.</summary>
    internal static DateTime? ParseDate(string s)
    {
        var digits = new string(s.StartsWith("D:", StringComparison.Ordinal) ? s[2..].TakeWhile(char.IsDigit).ToArray() : s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 8) return null;
        digits = digits.PadRight(14, '0')[..14];
        return DateTime.TryParseExact(digits, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static string PdfDate(DateTime t)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(t);
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"D:{t:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
    }

    private static PdfArray Array(PdfDocument doc, params double[] values) => new(doc, values.Select(v => (PdfItem)new PdfReal(v)).ToArray());

    private static string N(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
