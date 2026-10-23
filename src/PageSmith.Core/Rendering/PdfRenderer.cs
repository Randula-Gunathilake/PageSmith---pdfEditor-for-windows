using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace PageSmith.Core.Rendering;

/// <summary>
/// Renders PDF pages with the PDF engine built into Windows (Windows.Data.Pdf).
/// Page sizes are reported in points (1/72 inch), with /Rotate already applied.
/// </summary>
public sealed class PdfRenderer : IDisposable
{
    private readonly WinPdfDocument _doc;
    private readonly IRandomAccessStream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private PdfRenderer(WinPdfDocument doc, IRandomAccessStream stream)
    {
        _doc = doc;
        _stream = stream;
    }

    public int PageCount => (int)_doc.PageCount;
    public bool IsPasswordProtected => _doc.IsPasswordProtected;

    public static async Task<PdfRenderer> OpenAsync(string path, string? password = null, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return await OpenAsync(bytes, path, password).ConfigureAwait(false);
    }

    public static async Task<PdfRenderer> OpenAsync(byte[] pdf, string displayName, string? password = null)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pdf.AsBuffer());
        stream.Seek(0);
        try
        {
            var doc = string.IsNullOrEmpty(password)
                ? await WinPdfDocument.LoadFromStreamAsync(stream)
                : await WinPdfDocument.LoadFromStreamAsync(stream, password);
            return new PdfRenderer(doc, stream);
        }
        catch (Exception ex) when ((uint)ex.HResult == 0x8007052B /* ERROR_WRONG_PASSWORD */)
        {
            stream.Dispose();
            throw new PasswordRequiredException(displayName, !string.IsNullOrEmpty(password));
        }
        catch (Exception ex) when (ex is not PasswordRequiredException)
        {
            stream.Dispose();
            throw new UserFacingException($"Windows could not render \"{Path.GetFileName(displayName)}\" ({ex.Message}).", ex);
        }
    }

    /// <summary>Page size in points, as displayed (rotation applied).</summary>
    public System.Windows.Size GetPageSize(int index)
    {
        using var page = _doc.GetPage((uint)index);
        // Windows.Data.Pdf reports sizes in DIPs (1/96 inch).
        return new System.Windows.Size(page.Size.Width * 72.0 / 96.0, page.Size.Height * 72.0 / 96.0);
    }

    // Windows.Data.Pdf multiplies the destination size by the display scale of the process.
    // The factor is measured on the first render and divided out so output sizes are exact.
    private static double _displayScale;

    /// <summary>Renders a page to PNG bytes at the given resolution.</summary>
    public async Task<byte[]> RenderPngAsync(int index, double dpi, bool whiteBackground = true, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var page = _doc.GetPage((uint)index);
            var wanted = Math.Max(1, Math.Round(page.Size.Width * dpi / 96.0));
            var wantedHeight = Math.Max(1, Math.Round(page.Size.Height * dpi / 96.0));
            var known = _displayScale > 0;
            var factor = known ? _displayScale : 1.0;
            var bytes = await RenderAsync(page, wanted / factor, wantedHeight / factor, whiteBackground).ConfigureAwait(false);
            var actual = PngWidth(bytes);
            if (actual > 0 && Math.Abs(actual - wanted) > 1)
            {
                _displayScale = actual / (wanted / factor);
                bytes = await RenderAsync(page, wanted / _displayScale, wantedHeight / _displayScale, whiteBackground).ConfigureAwait(false);
            }
            else if (!known && actual > 0)
            {
                _displayScale = 1.0;
            }
            return bytes;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<byte[]> RenderAsync(PdfPage page, double width, double height, bool whiteBackground)
    {
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(width)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(height)),
            BackgroundColor = whiteBackground ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(0, 255, 255, 255),
        };
        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, options);
        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    private static int PngWidth(byte[] png) =>
        png.Length > 24 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G'
            ? png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19]
            : 0;

    /// <summary>Renders a page so that its longer side is <paramref name="maxPixels"/> pixels.</summary>
    public Task<byte[]> RenderThumbnailPngAsync(int index, int maxPixels, CancellationToken ct = default)
    {
        var size = GetPageSize(index);
        var longest = Math.Max(size.Width, size.Height);
        var dpi = maxPixels * 72.0 / Math.Max(1, longest);
        return RenderPngAsync(index, dpi, true, ct);
    }

    /// <summary>Renders a page to a frozen WPF bitmap.</summary>
    public async Task<BitmapSource> RenderAsync(int index, double dpi, CancellationToken ct = default)
    {
        var png = await RenderPngAsync(index, dpi, true, ct).ConfigureAwait(false);
        return Imaging.ImageIO.Decode(png);
    }

    public void Dispose()
    {
        _stream.Dispose();
        _gate.Dispose();
    }
}
