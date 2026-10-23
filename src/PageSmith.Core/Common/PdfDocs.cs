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

    private static bool IsPasswordProblem(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e.Message.Contains("password", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
