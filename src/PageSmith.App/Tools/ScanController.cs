using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Controls;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Tools.Convert;

namespace PageSmith.App.Tools;

/// <summary>Scan to PDF: pages from a scanner, the camera or photos, cleaned up and combined into a PDF.</summary>
public sealed partial class ScanController() : ToolController("scan")
{
    public override WorkspaceKind Workspace => WorkspaceKind.Custom;
    public override bool CanReorder => true;
    public override bool CanRotateFiles => true;
    public override FrameworkElement CreateWorkspace() => new ScanWorkspace { DataContext = this };

    [ObservableProperty] public partial ScanColorMode ColorMode { get; set; } = ScanColorMode.Color;
    [ObservableProperty] public partial PaperOrientation Orientation { get; set; } = PaperOrientation.Portrait;
    [ObservableProperty] public partial ImagePageSize PageSize { get; set; } = ImagePageSize.A4;
    [ObservableProperty] public partial PageMargin Margin { get; set; } = PageMargin.None;
    [ObservableProperty] public partial bool MergeIntoOne { get; set; } = true;

    [RelayCommand]
    private async Task ScanAsync()
    {
        Error = null;
        try
        {
            if (ScanSources.ScanPage() is { } path) await AddFilesAsync([path]);
        }
        catch (UserFacingException ex)
        {
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private async Task TakePhotosAsync()
    {
        Error = null;
        var photos = CameraWindow.Show(AppServices.Window);
        if (photos.Count > 0) await AddFilesAsync(photos);
    }

    [RelayCommand]
    private async Task AddPhotosAsync()
    {
        var files = AppServices.PickFiles(Descriptor);
        if (files.Length > 0) await AddFilesAsync(files);
    }

    public override string? Validate() => Files.Count == 0 ? "Scan a page, take a photo or add images first." : base.Validate();

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var scope = new ProgressScope(progress);
        var images = Files.Select(f => f.Path).ToList();
        var rotations = Files.Where(f => f.Rotation != 0).ToDictionary(f => f.Path, f => f.Rotation);
        var cleaned = new List<string>();
        try
        {
            if (ColorMode != ScanColorMode.Color)
            {
                // Clean up copies of the pages; the originals stay as they were.
                var mode = ColorMode;
                var filtered = new List<string>();
                var filteredRotations = new Dictionary<string, int>();
                for (var i = 0; i < images.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    scope.Slice(0, 0.5).Report(i, images.Count, $"Cleaning up page {i + 1}");
                    var source = images[i];
                    var target = Path.Combine(ScanSources.Folder, $"{Guid.NewGuid():N}{(mode == ScanColorMode.BlackAndWhite ? ".png" : ".jpg")}");
                    await Task.Run(() =>
                    {
                        var pixels = DocumentFilters.Apply(ImageIO.ToPixels(ImageIO.Load(source)), mode);
                        var bitmap = ImageIO.FromPixels(pixels);
                        File.WriteAllBytes(target, mode == ScanColorMode.BlackAndWhite ? ImageIO.EncodePng(bitmap) : ImageIO.EncodeJpeg(bitmap, 85));
                    }, ct);
                    cleaned.Add(target);
                    filtered.Add(target);
                    if (rotations.TryGetValue(source, out var r)) filteredRotations[target] = r;
                }
                images = filtered;
                rotations = filteredRotations;
            }
            var name = Files.Count == 1 || !MergeIntoOne ? Path.GetFileNameWithoutExtension(Files[0].Path) : $"Scan {DateTime.Now:yyyy-MM-dd HH.mm}";
            var result = await ImagesToPdfTool.RunAsync(new ImagesToPdfOptions
            {
                Images = images,
                OutputPath = OutputPaths.Unique(ScanOutputDirectory(), name + ".pdf"),
                Orientation = PageSize == ImagePageSize.Fit ? PaperOrientation.Auto : Orientation,
                PageSize = PageSize,
                Margin = Margin,
                MergeIntoOne = MergeIntoOne,
                Rotations = rotations,
            }, scope.Slice(ColorMode == ScanColorMode.Color ? 0 : 0.5, 1).AsProgress(), ct);
            return result;
        }
        finally
        {
            foreach (var file in cleaned)
                try { File.Delete(file); } catch { /* temp file */ }
        }
    }

    /// <summary>
    /// Scanned and photographed pages live in a temporary folder, which isn't a useful place for the PDF:
    /// save next to the first real file, or in the PageSmith folder.
    /// </summary>
    private string ScanOutputDirectory()
    {
        var real = Files.FirstOrDefault(f => !f.Path.StartsWith(ScanSources.Folder, StringComparison.OrdinalIgnoreCase))?.Path;
        var dir = AppServices.OutputDirectoryFor(real) is { } d && !d.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ? d : AppServices.Settings.OutputFolder;
        Directory.CreateDirectory(dir);
        return dir;
    }

    public override Task SeedDemoAsync()
    {
        ColorMode = ScanColorMode.BlackAndWhite;
        return Task.CompletedTask;
    }
}
