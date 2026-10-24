using System.Windows.Media.Imaging;
using PageSmith.Core.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Writer;

namespace PageSmith.Core.Tools.Optimize;

public sealed record RepairOptions
{
    public required InputFile Input { get; init; }

    /// <summary>Output path; its extension is changed if the file turns out not to be a PDF.</summary>
    public required string OutputPath { get; init; }
}

/// <summary>
/// Recovers damaged PDFs. Strategies, from least to most invasive: strip junk before the header,
/// rebuild the cross-reference table (PDFsharp), rebuild page by page with a lenient parser (PdfPig),
/// and finally re-render every page that Windows can still display into a new PDF.
/// </summary>
public static class RepairTool
{
    public static async Task<ToolResult> RunAsync(RepairOptions options, IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        var scope = new ProgressScope(progress);
        var result = new ToolResult { InputBytes = new FileInfo(options.Input.Path).Length };
        var data = await File.ReadAllBytesAsync(options.Input.Path, ct).ConfigureAwait(false);
        if (data.Length == 0) throw new UserFacingException("The file is empty; there is nothing to recover.");

        var detected = DetectOtherFormat(data);
        if (detected != null)
        {
            var path = Path.ChangeExtension(options.OutputPath, detected.Value.Extension);
            await File.WriteAllBytesAsync(path, data, ct).ConfigureAwait(false);
            result.AddOutput(path);
            result.Summary = $"This file is not a PDF but a {detected.Value.Description}. It was saved with the correct extension.";
            return result;
        }

        // Some files have junk (e.g. HTTP headers or mail encoding) before %PDF.
        var start = IndexOf(data, "%PDF-"u8.ToArray());
        if (start < 0) throw new UserFacingException("No PDF data was found in this file.");
        if (start > 0) data = data[start..];
        var cleaned = Path.Combine(Path.GetTempPath(), $"pagesmith_repair_{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(cleaned, data, ct).ConfigureAwait(false);
        var input = options.Input with { Path = cleaned };

        try
        {
            scope.Report(0.1, "Rebuilding document structure");
            if (TryPdfSharp(input, options.OutputPath, out var pages))
            {
                result.AddOutput(options.OutputPath);
                result.Summary = $"Repaired: the document structure was rebuilt ({pages} pages recovered).";
                return result;
            }

            ct.ThrowIfCancellationRequested();
            scope.Report(0.4, "Recovering pages one by one");
            var (recovered, total) = TryPdfPig(input, options.OutputPath);
            if (recovered > 0)
            {
                result.AddOutput(options.OutputPath);
                result.Summary = $"Repaired: {recovered} of {total} pages were recovered.";
                if (recovered < total) result.Warnings.Add($"{total - recovered} damaged pages could not be recovered.");
                return result;
            }

            ct.ThrowIfCancellationRequested();
            scope.Report(0.6, "Recovering page images");
            var rendered = await TryRenderAsync(input, options.OutputPath, scope.Slice(0.6, 1), ct).ConfigureAwait(false);
            if (rendered > 0)
            {
                result.AddOutput(options.OutputPath);
                result.Summary = $"Recovered {rendered} pages as images.";
                result.Warnings.Add("The text in recovered pages is no longer selectable. Run OCR PDF to make it searchable again.");
                return result;
            }
            throw new UserFacingException("The file is too damaged to recover.");
        }
        finally
        {
            try { File.Delete(cleaned); } catch { /* temp file */ }
        }
    }

    private static bool TryPdfSharp(InputFile input, string output, out int pages)
    {
        pages = 0;
        try
        {
            PdfDocs.EnsureInitialized();
            var options = new PdfReaderOptions
            {
                InvalidStreamLength = PdfReaderProblemBehavior.SilentlyIgnore,
                ReferenceToUndefinedObject = PdfReaderProblemBehavior.SilentlyIgnore,
            };
            using var doc = string.IsNullOrEmpty(input.Password)
                ? PdfReader.Open(input.Path, PdfDocumentOpenMode.Modify, options)
                : PdfReader.Open(input.Path, input.Password, PdfDocumentOpenMode.Modify, options);
            if (doc.PageCount == 0) return false;
            pages = doc.PageCount;
            doc.Save(output);
            return true;
        }
        catch (Exception ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new PasswordRequiredException(input.Path, !string.IsNullOrEmpty(input.Password));
        }
        catch
        {
            return false;
        }
    }

    private static (int Recovered, int Total) TryPdfPig(InputFile input, string output)
    {
        try
        {
            var parsing = new ParsingOptions { UseLenientParsing = true, SkipMissingFonts = true };
            if (!string.IsNullOrEmpty(input.Password)) parsing.Password = input.Password;
            using var doc = UglyToad.PdfPig.PdfDocument.Open(input.Path, parsing);
            var builder = new PdfDocumentBuilder();
            var recovered = 0;
            for (var i = 1; i <= doc.NumberOfPages; i++)
            {
                try
                {
                    builder.AddPage(doc, i);
                    recovered++;
                }
                catch
                {
                    // Skip pages that cannot be parsed.
                }
            }
            if (recovered == 0) return (0, doc.NumberOfPages);
            File.WriteAllBytes(output, builder.Build());
            return (recovered, doc.NumberOfPages);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static async Task<int> TryRenderAsync(InputFile input, string output, ProgressScope scope, CancellationToken ct)
    {
        try
        {
            using var renderer = await PdfRenderer.OpenAsync(input.Path, input.Password, ct).ConfigureAwait(false);
            using var doc = PdfDocs.Create();
            var count = 0;
            for (var i = 0; i < renderer.PageCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                scope.Report(i, renderer.PageCount, $"Rendering page {i + 1}");
                BitmapSource bmp;
                try
                {
                    bmp = await renderer.RenderAsync(i, 200, ct).ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }
                var size = renderer.GetPageSize(i);
                var page = doc.AddPage();
                page.MediaBox = new PdfRectangle(new XPoint(0, 0), new XPoint(size.Width, size.Height));
                using var gfx = XGraphics.FromPdfPage(page);
                using var ms = new MemoryStream(Imaging.ImageIO.EncodeJpeg(bmp, 85));
                using var image = XImage.FromStream(ms);
                gfx.DrawImage(image, 0, 0, size.Width, size.Height);
                count++;
            }
            if (count > 0) PdfDocs.Save(doc, output);
            return count;
        }
        catch (PasswordRequiredException)
        {
            throw;
        }
        catch
        {
            return 0;
        }
    }

    internal static (string Extension, string Description)? DetectOtherFormat(byte[] d)
    {
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return (".jpg", "JPEG image");
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G') return (".png", "PNG image");
        if (d.Length >= 4 && d[0] == 'G' && d[1] == 'I' && d[2] == 'F') return (".gif", "GIF image");
        if (d.Length >= 4 && ((d[0] == 'I' && d[1] == 'I' && d[2] == 42) || (d[0] == 'M' && d[1] == 'M' && d[3] == 42))) return (".tif", "TIFF image");
        if (d.Length >= 4 && d[0] == 'P' && d[1] == 'K' && d[2] == 3 && d[3] == 4)
        {
            var head = Encoding.ASCII.GetString(d, 0, Math.Min(d.Length, 4096));
            if (head.Contains("word/")) return (".docx", "Word document");
            if (head.Contains("xl/")) return (".xlsx", "Excel workbook");
            if (head.Contains("ppt/")) return (".pptx", "PowerPoint presentation");
            if (IndexOf(d, "%PDF-"u8.ToArray()) < 0) return (".zip", "ZIP archive");
        }
        if (d.Length >= 8 && d[0] == 0xD0 && d[1] == 0xCF && d[2] == 0x11 && d[3] == 0xE0) return (".doc", "legacy Office document");
        return null;
    }

    private static int IndexOf(byte[] data, byte[] pattern)
    {
        var limit = Math.Min(data.Length - pattern.Length, 1024 * 1024);
        for (var i = 0; i <= limit; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length && match; j++) match = data[i + j] == pattern[j];
            if (match) return i;
        }
        return -1;
    }
}
