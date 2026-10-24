using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PageSmith.Core.Common;

/// <summary>Opens PDF documents with PDFsharp and turns password failures into <see cref="PasswordRequiredException"/>.</summary>
public static class PdfDocs
{
    private static int _initialized;

    /// <summary>Configures PDFsharp once per process (font resolution through Windows).</summary>
    public static void EnsureInitialized()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1) return;
        GlobalFontSettings.FontResolver = new WindowsFontResolver();
    }

    public static PdfDocument Open(InputFile input, PdfDocumentOpenMode mode = PdfDocumentOpenMode.Modify)
    {
        EnsureInitialized();
        if (!File.Exists(input.Path)) throw new UserFacingException($"File not found: {input.Path}");
        var options = new PdfReaderOptions();
        try
        {
            return string.IsNullOrEmpty(input.Password)
                ? PdfReader.Open(input.Path, mode, options)
                : PdfReader.Open(input.Path, input.Password, mode, options);
        }
        catch (Exception ex) when (IsPasswordProblem(ex) && mode == PdfDocumentOpenMode.Modify)
        {
            // PDFsharp modifies encrypted files only with the owner password. The user password (or none,
            // for files that only restrict permissions) still allows importing, so work on a decrypted copy.
            PdfDocument imported;
            try
            {
                imported = string.IsNullOrEmpty(input.Password)
                    ? PdfReader.Open(input.Path, PdfDocumentOpenMode.Import, options)
                    : PdfReader.Open(input.Path, input.Password, PdfDocumentOpenMode.Import, options);
            }
            catch (Exception inner) when (IsPasswordProblem(inner))
            {
                throw new PasswordRequiredException(input.Path, !string.IsNullOrEmpty(input.Password));
            }
            using (imported) return DecryptedCopy(imported);
        }
        catch (Exception ex) when (IsPasswordProblem(ex))
        {
            throw new PasswordRequiredException(input.Path, !string.IsNullOrEmpty(input.Password));
        }
        catch (Exception ex) when (ex is not UserFacingException and not PasswordRequiredException)
        {
            throw new UserFacingException(
                $"\"{input.Name}\" could not be opened ({ex.Message}). Try the Repair PDF tool.", ex);
        }
    }

    /// <summary>Opens the document for page import into another document.</summary>
    public static PdfDocument OpenForImport(InputFile input) => Open(input, PdfDocumentOpenMode.Import);

    public static int CountPages(InputFile input)
    {
        using var doc = Open(input, PdfDocumentOpenMode.Import);
        return doc.PageCount;
    }

    /// <summary>True when the file cannot be opened without a user password.</summary>
    public static bool RequiresPassword(string path)
    {
        try
        {
            using var doc = Open(new InputFile(path), PdfDocumentOpenMode.Import);
            return false;
        }
        catch (PasswordRequiredException)
        {
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Creates an empty document with PageSmith metadata.</summary>
    public static PdfDocument Create(string? title = null)
    {
        EnsureInitialized();
        var doc = new PdfDocument();
        doc.Info.Creator = "PageSmith";
        if (title != null) doc.Info.Title = title;
        return doc;
    }

    public static void Save(PdfDocument doc, string path)
    {
        doc.Info.Creator = string.IsNullOrEmpty(doc.Info.Creator) ? "PageSmith" : doc.Info.Creator;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        doc.Save(path);
    }

    /// <summary>Marks documents that were rebuilt without their original encryption.</summary>
    public const string DecryptedCopyTag = "PageSmith:decrypted-copy";

    /// <summary>
    /// Rebuilds an import-mode document as a new, unencrypted, modifiable one: pages (with their
    /// annotations), bookmarks, document information and the interactive form.
    /// </summary>
    public static PdfDocument DecryptedCopy(PdfDocument source)
    {
        var copy = new PdfDocument { Tag = DecryptedCopyTag };
        foreach (var page in source.Pages) copy.AddPage(page);

        foreach (var key in new[] { "/Title", "/Author", "/Subject", "/Keywords", "/Creator" })
            if (source.Info.Elements.ContainsKey(key)) copy.Info.Elements.SetString(key, source.Info.Elements.GetString(key));

        CopyOutlines(source, source.Outlines, copy, copy.Outlines);

        // Imported widget annotations keep their /Parent chains; their roots are the form's fields.
        var roots = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in copy.Pages)
        {
            var annots = page.Elements.GetArray("/Annots");
            if (annots == null) continue;
            for (var i = 0; i < annots.Elements.Count; i++)
            {
                var a = annots.Elements.GetDictionary(i);
                if (a?.Elements.GetName("/Subtype") != "/Widget") continue;
                var root = a;
                while (root.Elements.GetDictionary("/Parent") is { } parent) root = parent;
                if (seen.Add(root)) roots.Add(root);
            }
        }
        if (roots.Count > 0)
        {
            var form = new PdfDictionary(copy);
            var fields = new PdfArray(copy);
            foreach (var r in roots)
            {
                if (r.Reference == null) copy.Internals.AddObject(r);
                fields.Elements.Add(r.Reference!);
            }
            form.Elements["/Fields"] = fields;
            if (source.Internals.Catalog.Elements.GetDictionary("/AcroForm") is { } original && original.Elements.GetString("/DA") is { Length: > 0 } da)
                form.Elements.SetString("/DA", da);
            form.Elements["/NeedAppearances"] = new PdfBoolean(true);
            copy.Internals.AddObject(form);
            copy.Internals.Catalog.Elements.SetReference("/AcroForm", form);
        }
        return copy;
    }

    private static void CopyOutlines(PdfDocument source, PdfSharp.Pdf.PdfOutlineCollection from, PdfDocument target, PdfSharp.Pdf.PdfOutlineCollection to)
    {
        foreach (var outline in from)
        {
            var index = -1;
            if (outline.DestinationPage != null)
                for (var i = 0; i < source.PageCount; i++)
                    if (ReferenceEquals(source.Pages[i], outline.DestinationPage) || source.Pages[i].Reference == outline.DestinationPage.Reference) { index = i; break; }
            if (index < 0) continue;
            var added = to.Add(outline.Title, target.Pages[index], outline.Opened);
            if (outline.HasChildren) CopyOutlines(source, outline.Outlines, target, added.Outlines);
        }
    }

    private static bool IsPasswordProblem(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
