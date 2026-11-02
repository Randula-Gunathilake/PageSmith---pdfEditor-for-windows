using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;
using WinBitmapEncoder = Windows.Graphics.Imaging.BitmapEncoder;
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
    private volatile bool _disposed;

    private PdfRenderer(WinPdfDocument doc, IRandomAccessStream stream)
    {
        _doc = doc;
        _stream = stream;
    }

    public int PageCount => (int)_doc.PageCount;
    public bool IsPasswordProtected => _doc.IsPasswordProtected;

    /// <summary>
    /// Opens a PDF file. It is copied into native memory in chunks (no large managed buffer, and the file is
    /// not kept open), which loads and renders several times faster than reading the file through a stream adapter.
    /// </summary>
    public static async Task<PdfRenderer> OpenAsync(string path, string? password = null, CancellationToken ct = default)
    {
        var stream = new InMemoryRandomAccessStream();
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                await stream.WriteAsync(buffer.AsBuffer(0, read)).AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stream.Dispose();
            throw new UserFacingException($"\"{Path.GetFileName(path)}\" couldn't be read ({ex.Message}).", ex);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
        stream.Seek(0);
        return await LoadAsync(stream, path, password).ConfigureAwait(false);
    }

    public static async Task<PdfRenderer> OpenAsync(byte[] pdf, string displayName, string? password = null)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(pdf.AsBuffer());
        stream.Seek(0);
        return await LoadAsync(stream, displayName, password).ConfigureAwait(false);
    }

    private static async Task<PdfRenderer> LoadAsync(IRandomAccessStream stream, string displayName, string? password)
    {
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

    /// <summary>Every page's size in points, as displayed.</summary>
    public System.Windows.Size[] GetPageSizes()
    {
        var sizes = new System.Windows.Size[PageCount];
        for (var i = 0; i < sizes.Length; i++) sizes[i] = GetPageSize(i);
        return sizes;
    }

    // Windows.Data.Pdf multiplies the destination size by the display scale of the process.
    // The factor is measured on the first render and divided out so output sizes are exact.
    private static double _displayScale;

    /// <summary>Renders a page to PNG bytes at the given resolution.</summary>
    public Task<byte[]> RenderPngAsync(int index, double dpi, bool whiteBackground = true, CancellationToken ct = default) =>
        RenderBytesAsync(index, dpi, whiteBackground, null, ct);

    /// <summary>
    /// Renders a page to encoded image bytes: PNG, or with <paramref name="encoder"/> another format. Pages
    /// shown on screen use BMP, which skips compression and renders about a third faster.
    /// </summary>
    private async Task<byte[]> RenderBytesAsync(int index, double dpi, bool whiteBackground, Guid? encoder, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var page = _doc.GetPage((uint)index);
            var wanted = Math.Max(1, Math.Round(page.Size.Width * dpi / 96.0));
            var wantedHeight = Math.Max(1, Math.Round(page.Size.Height * dpi / 96.0));
            var known = _displayScale > 0;
            var factor = known ? _displayScale : 1.0;
            var bytes = await RenderAsync(page, wanted / factor, wantedHeight / factor, whiteBackground, encoder).ConfigureAwait(false);
            var actual = ImageWidth(bytes);
            if (actual > 0 && Math.Abs(actual - wanted) > 1)
            {
                _displayScale = actual / (wanted / factor);
                bytes = await RenderAsync(page, wanted / _displayScale, wantedHeight / _displayScale, whiteBackground, encoder).ConfigureAwait(false);
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

    private static async Task<byte[]> RenderAsync(PdfPage page, double width, double height, bool whiteBackground, Guid? encoder)
    {
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(width)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(height)),
            BackgroundColor = whiteBackground ? Windows.UI.Color.FromArgb(255, 255, 255, 255) : Windows.UI.Color.FromArgb(0, 255, 255, 255),
        };
        if (encoder is { } id) options.BitmapEncoderId = id;
        using var output = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(output, options);
        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    /// <summary>Pixel width from a PNG or BMP header; 0 when unknown.</summary>
    private static int ImageWidth(byte[] image)
    {
        if (image.Length > 24 && image[1] == 'P' && image[2] == 'N' && image[3] == 'G')
            return image[16] << 24 | image[17] << 16 | image[18] << 8 | image[19];
        if (image.Length > 26 && image[0] == 'B' && image[1] == 'M')
            return Math.Abs(BitConverter.ToInt32(image, 18));
        return 0;
    }

    private double ThumbnailDpi(int index, int maxPixels)
    {
        var size = GetPageSize(index);
        return maxPixels * 72.0 / Math.Max(1, Math.Max(size.Width, size.Height));
    }

    /// <summary>Renders a page so that its longer side is <paramref name="maxPixels"/> pixels.</summary>
    public Task<byte[]> RenderThumbnailPngAsync(int index, int maxPixels, CancellationToken ct = default) =>
        RenderPngAsync(index, ThumbnailDpi(index, maxPixels), true, ct);

    /// <summary>Renders a thumbnail to a frozen WPF bitmap, its longer side <paramref name="maxPixels"/> pixels.</summary>
    public Task<BitmapSource> RenderThumbnailAsync(int index, int maxPixels, CancellationToken ct = default) =>
        RenderAsync(index, ThumbnailDpi(index, maxPixels), ct);

    /// <summary>Renders a page, on white, to a frozen WPF bitmap for the screen.</summary>
    public async Task<BitmapSource> RenderAsync(int index, double dpi, CancellationToken ct = default)
    {
        var bmp = await RenderBytesAsync(index, dpi, true, WinBitmapEncoder.BmpEncoderId, ct).ConfigureAwait(false);
        return FromBmp(bmp) ?? Imaging.ImageIO.Decode(bmp);
    }

    /// <summary>
    /// Makes a frozen bitmap straight from a 32-bit BMP's pixels, without the image decoder's extra copies
    /// (it took most of the time of drawing a page). Pages are drawn on white, so alpha is left out: Bgr32 is
    /// also the quickest format for WPF to draw. Null for any other kind of BMP.
    /// </summary>
    internal static BitmapSource? FromBmp(byte[] bmp)
    {
        if (bmp.Length < 66 || bmp[0] != 'B' || bmp[1] != 'M') return null;
        var offset = BitConverter.ToInt32(bmp, 10);
        var width = BitConverter.ToInt32(bmp, 18);
        var height = BitConverter.ToInt32(bmp, 22);
        var bits = BitConverter.ToInt16(bmp, 28);
        var compression = BitConverter.ToInt32(bmp, 30);
        // Uncompressed, or with colour masks that must be the usual blue-green-red order.
        var plainMasks = compression == 0 ||
                         compression == 3 && BitConverter.ToUInt32(bmp, 54) == 0x00FF0000 && BitConverter.ToUInt32(bmp, 58) == 0x0000FF00 && BitConverter.ToUInt32(bmp, 62) == 0x000000FF;
        if (bits != 32 || !plainMasks || width <= 0 || height == 0) return null;
        var rows = Math.Abs(height);
        var stride = width * 4;
        if (offset < 54 || offset + (long)stride * rows > bmp.Length) return null;
        BitmapSource bitmap;
        if (height < 0)
        {
            // Top-down rows can be used where they are.
            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(bmp, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                bitmap = BitmapSource.Create(width, rows, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null,
                    pinned.AddrOfPinnedObject() + offset, stride * rows, stride);
            }
            finally
            {
                pinned.Free();
            }
        }
        else
        {
            // Bottom-up rows are turned the right way up.
            var pixels = new byte[stride * rows];
            for (var y = 0; y < rows; y++) System.Buffer.BlockCopy(bmp, offset + (rows - 1 - y) * stride, pixels, y * stride, stride);
            bitmap = BitmapSource.Create(width, rows, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, pixels, stride);
        }
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// Releases the document once any render in progress has finished; renders asked for later throw
    /// <see cref="ObjectDisposedException"/>. A viewer can therefore swap renderers while pages are drawing.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.WaitAsync().ContinueWith(_ =>
        {
            _stream.Dispose();
            _gate.Release();
        }, TaskScheduler.Default);
    }
}
