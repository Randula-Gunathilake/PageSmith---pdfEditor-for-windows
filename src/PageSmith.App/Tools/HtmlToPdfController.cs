using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Controls;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Net;

namespace PageSmith.App.Tools;

public sealed record ScreenWidthChoice(int Width, string Label);

/// <summary>HTML to PDF: a web address or a local HTML file, laid out at a chosen screen width and printed by Edge.</summary>
public sealed partial class HtmlToPdfController() : ToolController("html-to-pdf")
{
    public override WorkspaceKind Workspace => WorkspaceKind.Custom;
    public override bool NeedsFiles => false;
    public override FrameworkElement CreateWorkspace() => new HtmlWorkspace { DataContext = this };

    public IReadOnlyList<ScreenWidthChoice> ScreenWidths { get; } =
    [
        new(320, "Phone (320 px)"), new(768, "Tablet (768 px)"), new(1024, "Small laptop (1024 px)"),
        new(1280, "Laptop (1280 px)"), new(1440, "Desktop (1440 px)"), new(1920, "Full HD (1920 px)"),
    ];

    [ObservableProperty] public partial string Address { get; set; } = "";
    [ObservableProperty] public partial ScreenWidthChoice? ScreenWidth { get; set; }
    [ObservableProperty] public partial WebPageSize PageSize { get; set; } = WebPageSize.A4;
    [ObservableProperty] public partial bool Landscape { get; set; }
    [ObservableProperty] public partial WebMargin Margin { get; set; } = WebMargin.Small;
    [ObservableProperty] public partial bool BlockAds { get; set; } = true;
    [ObservableProperty] public partial bool RemovePopups { get; set; } = true;
    [ObservableProperty] public partial bool Backgrounds { get; set; } = true;
    [ObservableProperty] public partial BitmapSource? Preview { get; set; }
    [ObservableProperty] public partial bool IsPreviewing { get; set; }

    public bool HasPreview => Preview != null;
    public bool UsesFile => Files.Count > 0;
    public bool IsOneLongPage => PageSize == WebPageSize.OneLongPage;

    partial void OnPreviewChanged(BitmapSource? value) => OnPropertyChanged(nameof(HasPreview));
    partial void OnPageSizeChanged(WebPageSize value) => OnPropertyChanged(nameof(IsOneLongPage));

    partial void OnAddressChanged(string value)
    {
        // Typing an address replaces a chosen file, and the old preview no longer applies.
        if (!string.IsNullOrWhiteSpace(value) && Files.Count > 0) Files.Clear();
        Preview = null;
    }

    partial void OnScreenWidthChanged(ScreenWidthChoice? value) => Preview = null;

    public override Task OnShownAsync()
    {
        ScreenWidth ??= ScreenWidths.First(w => w.Width == 1440);
        return Task.CompletedTask;
    }

    protected override void OnFilesChanged()
    {
        OnPropertyChanged(nameof(UsesFile));
        if (Files.Count > 0 && Address.Length > 0) Address = "";
        Preview = null;
    }

    /// <summary>The page to convert, or null with the reason.</summary>
    private (Uri? Uri, string? Problem) Target()
    {
        if (Files.Count > 0) return (new Uri(Files[0].Path), null);
        var text = Address.Trim();
        if (text.Length == 0) return (null, "Type a web address, or add an HTML file.");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && uri.Host.Contains('.')
            ? (uri, null)
            : (null, "That doesn't look like a web address. Try something like example.com.");
    }

    private WebCaptureSettings Settings() => new()
    {
        ScreenWidth = ScreenWidth?.Width ?? 1440,
        PageSize = PageSize,
        Landscape = Landscape,
        Margin = Margin,
        BlockAds = BlockAds,
        RemovePopups = RemovePopups,
        Backgrounds = Backgrounds,
    };

    [RelayCommand]
    private async Task ShowPreviewAsync()
    {
        var (uri, problem) = Target();
        if (uri == null)
        {
            Error = problem;
            return;
        }
        Error = null;
        IsOffline = false;
        IsPreviewing = true;
        try
        {
            await using var page = await WebPageCapture.OpenAsync(uri, Settings(), null, CancellationToken.None);
            Preview = await page.CapturePreviewAsync();
        }
        catch (OfflineException ex)
        {
            IsOffline = true;
            Error = ex.Message;
        }
        catch (UserFacingException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsPreviewing = false;
        }
    }

    [RelayCommand]
    private void ClosePreview() => Preview = null;

    public override string? Validate() => Target().Problem;

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var (uri, problem) = Target();
        if (uri == null) throw new UserFacingException(problem!);
        var settings = Settings();
        await using var page = await WebPageCapture.OpenAsync(uri, settings, progress, ct);
        var name = uri.IsFile
            ? Path.GetFileNameWithoutExtension(uri.LocalPath)
            : string.IsNullOrWhiteSpace(page.Title) ? uri.Host : page.Title!;
        var dir = uri.IsFile ? OutputDirectory(uri.LocalPath) : OutputDirectory(null);
        var output = OutputPaths.Unique(dir, OutputPaths.Sanitize(name.Length > 80 ? name[..80] : name) + ".pdf");
        await page.SaveAsPdfAsync(output, settings, progress, ct);
        var result = new ToolResult { InputBytes = uri.IsFile ? new FileInfo(uri.LocalPath).Length : 0 };
        result.AddOutput(output);
        result.Summary = $"Saved {(uri.IsFile ? Path.GetFileName(uri.LocalPath) : uri.Host)} as a PDF{(settings.PageSize == WebPageSize.OneLongPage ? " on one long page" : "")}.";
        return result;
    }

    public override async Task SeedDemoAsync()
    {
        if (Files.Count == 0) Address = "example.com";
        await ShowPreviewAsync();
    }
}
