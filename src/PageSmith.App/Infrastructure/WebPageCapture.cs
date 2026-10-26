using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;

namespace PageSmith.App.Infrastructure;

public enum WebPageSize
{
    A4,
    A3,
    A5,
    Letter,
    OneLongPage,
}

public enum WebMargin
{
    None,
    Small,
    Big,
}

public sealed record WebCaptureSettings
{
    /// <summary>Width of the browser window the page is laid out for, in CSS pixels.</summary>
    public int ScreenWidth { get; init; } = 1440;
    public WebPageSize PageSize { get; init; } = WebPageSize.A4;
    public bool Landscape { get; init; }
    public WebMargin Margin { get; init; } = WebMargin.Small;
    public bool BlockAds { get; init; } = true;
    public bool RemovePopups { get; init; } = true;
    public bool Backgrounds { get; init; } = true;
}

/// <summary>
/// Loads a web page or local HTML file in a hidden Microsoft Edge WebView2 and prints it to PDF or captures
/// a preview. WebView2 needs a window, so the page lives in a small window placed off screen.
/// </summary>
public sealed class WebPageCapture : IAsyncDisposable
{
    /// <summary>Ad and tracking networks blocked when "Block ads" is on.</summary>
    private static readonly string[] AdHosts =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.com", "google-analytics.com",
        "googletagmanager.com", "googletagservices.com", "adnxs.com", "taboola.com", "outbrain.com", "criteo.com", "criteo.net",
        "scorecardresearch.com", "amazon-adsystem.com", "pubmatic.com", "rubiconproject.com", "openx.net", "moatads.com",
        "adsrvr.org", "quantserve.com", "media.net", "adform.net", "smartadserver.com", "yieldmo.com", "sharethrough.com",
        "teads.tv", "zemanta.com", "revcontent.com", "mgid.com", "hotjar.com",
    ];

    /// <summary>Removes cookie banners, consent dialogs and other large overlays that would cover the content.</summary>
    private const string RemoveOverlaysScript = """
        (() => {
          const consent = '[id*="cookie" i],[class*="cookie" i],[id*="consent" i],[class*="consent" i],[id*="gdpr" i],[class*="gdpr" i],' +
                          '#onetrust-banner-sdk,#onetrust-consent-sdk,.fc-consent-root,[aria-modal="true"],[role="dialog"]';
          document.querySelectorAll(consent).forEach(e => {
            const s = getComputedStyle(e);
            if (s.position === 'fixed' || s.position === 'sticky' || e.getAttribute('aria-modal') === 'true') e.remove();
          });
          const area = innerWidth * innerHeight;
          for (const e of Array.from(document.querySelectorAll('body *'))) {
            const s = getComputedStyle(e);
            if ((s.position === 'fixed' || s.position === 'sticky') && (parseInt(s.zIndex) || 0) >= 100 && e.offsetWidth * e.offsetHeight > area * 0.25) e.remove();
          }
          for (const e of [document.documentElement, document.body]) if (e) e.style.setProperty('overflow', 'visible', 'important');
        })();
        """;

    private readonly Window _window;
    private readonly WebView2 _view;
    private CoreWebView2Environment? _environment;
    private string? _title;

    private WebPageCapture(int width)
    {
        _view = new WebView2();
        _window = new Window
        {
            Width = width,
            Height = 1000,
            Left = -32000,
            Top = -32000,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Content = _view,
        };
    }

    public string? Title => _title;

    /// <summary>Opens the page (asking for the internet first for web addresses). Must be called on the UI thread.</summary>
    public static async Task<WebPageCapture> OpenAsync(Uri address, WebCaptureSettings settings, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        if (!address.IsFile) await Core.Net.Connectivity.EnsureOnlineAsync("Converting web pages", ct);
        var capture = new WebPageCapture(Math.Clamp(settings.ScreenWidth, 320, 3840));
        try
        {
            await capture.LoadAsync(address, settings, progress, ct);
            return capture;
        }
        catch
        {
            await capture.DisposeAsync();
            throw;
        }
    }

    private async Task LoadAsync(Uri address, WebCaptureSettings settings, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        progress?.Report(new ToolProgress(0.05, "Starting Microsoft Edge WebView2"));
        _window.Show();
        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PageSmith", "WebView2");
            _environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await _view.EnsureCoreWebView2Async(_environment);
        }
        catch (WebView2RuntimeNotFoundException ex)
        {
            throw new UserFacingException("HTML to PDF needs the Microsoft Edge WebView2 Runtime, which is part of Windows 11. On Windows 10, install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again.", ex);
        }
        var core = _view.CoreWebView2;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        // Pop-up windows never open; pages stay in this one.
        core.NewWindowRequested += (_, e) => e.Handled = true;
        if (settings.BlockAds)
        {
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var u) && AdHosts.Any(h => u.Host == h || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
                    e.Response = _environment!.CreateWebResourceResponse(null, 403, "Blocked", "");
            };
        }
        // Lay the page out as on a screen of the chosen width, with screen (not print) styles.
        await core.CallDevToolsProtocolMethodAsync("Emulation.setDeviceMetricsOverride",
            JsonSerializer.Serialize(new { width = settings.ScreenWidth, height = 1000, deviceScaleFactor = 1, mobile = settings.ScreenWidth < 768 }));
        await core.CallDevToolsProtocolMethodAsync("Emulation.setEmulatedMedia", """{"media":"screen"}""");

        progress?.Report(new ToolProgress(0.15, address.IsFile ? "Opening the HTML file" : $"Loading {address.Host}"));
        var done = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(e);
        core.NavigationCompleted += OnCompleted;
        try
        {
            core.Navigate(address.AbsoluteUri);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await using (timeout.Token.Register(() => done.TrySetCanceled()))
            {
                CoreWebView2NavigationCompletedEventArgs result;
                try
                {
                    result = await done.Task;
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new UserFacingException("The page took more than a minute to load. Check the address, or try again later.");
                }
                if (!result.IsSuccess) throw NavigationError(result.WebErrorStatus, address);
            }
        }
        finally
        {
            core.NavigationCompleted -= OnCompleted;
        }

        // Let late content arrive, and scroll through the page so lazy-loaded pictures load too.
        progress?.Report(new ToolProgress(0.45, "Loading pictures"));
        await Task.Delay(800, ct);
        var screens = JsonSerializer.Deserialize<double>(await core.ExecuteScriptAsync("Math.ceil(document.documentElement.scrollHeight / Math.max(1, innerHeight))"));
        await core.ExecuteScriptAsync("""
            (async () => {
              const step = innerHeight;
              for (let y = 0; y < document.documentElement.scrollHeight && y < 60000; y += step) { scrollTo(0, y); await new Promise(r => setTimeout(r, 60)); }
              scrollTo(0, 0);
            })();
            """);
        // The scroll runs inside the page: wait for it (60 ms a screen) plus time for the pictures to arrive.
        await Task.Delay((int)Math.Min(8000, Math.Clamp(screens, 1, 60) * 70 + 1000), ct);
        if (settings.RemovePopups) await core.ExecuteScriptAsync(RemoveOverlaysScript);
        _title = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.title"));
    }

    private static UserFacingException NavigationError(CoreWebView2WebErrorStatus status, Uri address) => new(status switch
    {
        CoreWebView2WebErrorStatus.HostNameNotResolved => $"\"{address.Host}\" could not be found. Check the address.",
        CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset or CoreWebView2WebErrorStatus.Disconnected
            => "The connection was interrupted. Check your internet connection and try again.",
        CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ServerUnreachable => $"PageSmith could not reach {address.Host}.",
        CoreWebView2WebErrorStatus.Timeout => "The site took too long to answer.",
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
            or CoreWebView2WebErrorStatus.CertificateRevoked or CoreWebView2WebErrorStatus.CertificateIsInvalid => "The site's security certificate isn't valid, so the page was not opened.",
        _ when address.IsFile => "The HTML file could not be opened.",
        _ => $"The page could not be loaded ({status}).",
    });

    /// <summary>Paper size in inches (portrait).</summary>
    private static (double Width, double Height) Paper(WebPageSize size) => size switch
    {
        WebPageSize.A3 => (11.69, 16.54),
        WebPageSize.A5 => (5.83, 8.27),
        WebPageSize.Letter => (8.5, 11),
        _ => (8.27, 11.69),
    };

    /// <summary>Prints the page to a PDF file.</summary>
    public async Task SaveAsPdfAsync(string path, WebCaptureSettings settings, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(new ToolProgress(0.75, "Creating the PDF"));
        var core = _view.CoreWebView2;
        var margin = settings.Margin switch { WebMargin.Small => 0.4, WebMargin.Big => 0.8, _ => 0 };
        var (paperW, paperH) = Paper(settings.PageSize);
        if (settings.Landscape && settings.PageSize != WebPageSize.OneLongPage) (paperW, paperH) = (paperH, paperW);
        // Scale so the page is laid out at the chosen screen width rather than the paper's width.
        var printableWidth = (paperW - 2 * margin) * 96;
        var scale = Math.Clamp(printableWidth / settings.ScreenWidth, 0.1, 2.0);

        var print = _environment!.CreatePrintSettings();
        print.ShouldPrintBackgrounds = settings.Backgrounds;
        print.ShouldPrintHeaderAndFooter = false;
        print.ScaleFactor = scale;
        print.MarginTop = print.MarginBottom = print.MarginLeft = print.MarginRight = margin;
        print.Orientation = CoreWebView2PrintOrientation.Portrait;
        print.PageWidth = paperW;
        print.PageHeight = paperH;
        if (settings.PageSize == WebPageSize.OneLongPage)
        {
            var height = JsonSerializer.Deserialize<double>(await core.ExecuteScriptAsync("Math.max(document.body ? document.body.scrollHeight : 0, document.documentElement.scrollHeight)"));
            // PDF pages can be at most 200 inches long.
            print.PageHeight = Math.Clamp(height * scale / 96 + 2 * margin + 0.2, 2, 200);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (!await core.PrintToPdfAsync(path, print) || !File.Exists(path))
            throw new UserFacingException("Microsoft Edge could not create the PDF for this page.");
        progress?.Report(new ToolProgress(1));
    }

    /// <summary>A picture of the top of the page, as laid out at the chosen width.</summary>
    public async Task<BitmapSource> CapturePreviewAsync()
    {
        using var stream = new MemoryStream();
        await _view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
        return ImageIO.Decode(stream.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        _view.Dispose();
        _window.Close();
        return ValueTask.CompletedTask;
    }
}
