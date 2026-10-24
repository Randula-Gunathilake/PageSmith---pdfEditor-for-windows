using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using PageSmith.Core.Imaging;
using PageSmith.Core.Rendering;

namespace PageSmith.App.Infrastructure;

/// <summary>Renders and caches page thumbnails off the UI thread.</summary>
public sealed class ThumbnailCache
{
    private readonly ConcurrentDictionary<string, Task<BitmapSource?>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(Math.Max(2, Environment.ProcessorCount / 2));

    public Task<BitmapSource?> GetAsync(string path, int pageIndex, int maxPixels, string? password = null)
    {
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        var key = $"{path}|{pageIndex}|{maxPixels}|{stamp}";
        return _cache.GetOrAdd(key, _ => Task.Run(() => RenderAsync(path, pageIndex, maxPixels, password)));
    }

    /// <summary>Thumbnail for an image file (first frame).</summary>
    public Task<BitmapSource?> GetImageAsync(string path, int maxPixels)
    {
        var key = $"img|{path}|{maxPixels}";
        return _cache.GetOrAdd(key, _ => Task.Run<BitmapSource?>(() =>
        {
            try
            {
                var image = ImageIO.Load(path);
                var scale = Math.Min(1.0, maxPixels / (double)Math.Max(image.PixelWidth, image.PixelHeight));
                return scale < 1 ? ImageIO.Resize(image, (int)(image.PixelWidth * scale), (int)(image.PixelHeight * scale)) : image;
            }
            catch
            {
                return null;
            }
        }));
    }

    private async Task<BitmapSource?> RenderAsync(string path, int pageIndex, int maxPixels, string? password)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            using var renderer = await PdfRenderer.OpenAsync(path, password).ConfigureAwait(false);
            if (pageIndex >= renderer.PageCount) return null;
            var png = await renderer.RenderThumbnailPngAsync(pageIndex, maxPixels).ConfigureAwait(false);
            return ImageIO.Decode(png);
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Forget(string path)
    {
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(path + "|", StringComparison.OrdinalIgnoreCase) || k.Contains("|" + path + "|", StringComparison.OrdinalIgnoreCase)).ToList())
            _cache.TryRemove(key, out _);
    }
}
