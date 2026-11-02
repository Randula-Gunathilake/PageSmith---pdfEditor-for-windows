using System.IO;
using System.Windows.Media.Imaging;
using PageSmith.Core.Imaging;
using PageSmith.Core.Rendering;

namespace PageSmith.App.Infrastructure;

/// <summary>
/// Renders page thumbnails off the UI thread and keeps the most recent ones. Pages of the same file share
/// one open renderer (the file isn't re-read for every page), and few render at once, so a page grid of a
/// long document doesn't max out the CPU or memory.
/// </summary>
public sealed class ThumbnailCache
{
    /// <summary>About 40 MB of thumbnails.</summary>
    private const int MaxThumbnails = 400;

    private const int MaxOpenFiles = 3;

    private readonly object _lock = new();
    private readonly Dictionary<string, (Task<BitmapSource?> Task, long Used)> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Task<PdfRenderer> Renderer, long Used)> _renderers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(Math.Clamp(Environment.ProcessorCount / 4, 1, 3));
    private long _tick;

    public Task<BitmapSource?> GetAsync(string path, int pageIndex, int maxPixels, string? password = null)
    {
        var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
        var key = $"{path}|{pageIndex}|{maxPixels}|{stamp}|{(password == null ? 0 : password.GetHashCode())}";
        return Remember(key, () => RenderAsync(path, stamp, pageIndex, maxPixels, password));
    }

    /// <summary>Thumbnail for an image file (first frame).</summary>
    public Task<BitmapSource?> GetImageAsync(string path, int maxPixels) =>
        Remember($"img|{path}|{maxPixels}", () => Task.Run<BitmapSource?>(() =>
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

    private Task<BitmapSource?> Remember(string key, Func<Task<BitmapSource?>> make)
    {
        lock (_lock)
        {
            if (_thumbnails.TryGetValue(key, out var known))
            {
                _thumbnails[key] = (known.Task, ++_tick);
                return known.Task;
            }
            var task = make();
            _thumbnails[key] = (task, ++_tick);
            while (_thumbnails.Count > MaxThumbnails) _thumbnails.Remove(_thumbnails.MinBy(e => e.Value.Used).Key);
            return task;
        }
    }

    private async Task<BitmapSource?> RenderAsync(string path, long stamp, int pageIndex, int maxPixels, string? password)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            // A renderer can be closed by another file needing its place; then open it again once.
            for (var attempt = 0; ; attempt++)
            {
                var renderer = await RendererFor(path, stamp, password).ConfigureAwait(false);
                if (pageIndex >= renderer.PageCount) return null;
                try
                {
                    return await renderer.RenderThumbnailAsync(pageIndex, maxPixels).ConfigureAwait(false);
                }
                catch (ObjectDisposedException) when (attempt == 0)
                {
                }
            }
        }
        catch
        {
            // A file that couldn't be opened (a password, damage) is tried afresh next time.
            lock (_lock) _renderers.Remove(RendererKey(path, stamp, password));
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static string RendererKey(string path, long stamp, string? password) => $"{path}|{stamp}|{password}";

    private Task<PdfRenderer> RendererFor(string path, long stamp, string? password)
    {
        var key = RendererKey(path, stamp, password);
        lock (_lock)
        {
            if (_renderers.TryGetValue(key, out var open) && !open.Renderer.IsFaulted)
            {
                _renderers[key] = (open.Renderer, ++_tick);
                return open.Renderer;
            }
            var task = Task.Run(() => PdfRenderer.OpenAsync(path, password));
            _renderers[key] = (task, ++_tick);
            while (_renderers.Count > MaxOpenFiles)
            {
                var oldest = _renderers.MinBy(e => e.Value.Used);
                _renderers.Remove(oldest.Key);
                oldest.Value.Renderer.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully) t.Result.Dispose();
                }, TaskScheduler.Default);
            }
            return task;
        }
    }
}
