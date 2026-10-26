using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Controls;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Imaging;
using PageSmith.Core.Rendering;
using PageSmith.Core.Tools.Security;

namespace PageSmith.App.Tools;

/// <summary>A coloured box over a changed word.</summary>
public sealed record CompareHighlight(Rect Bounds, string Color);

/// <summary>One page in the side-by-side or overlay view. Its picture loads while it is on screen.</summary>
public sealed partial class ComparePage : ObservableObject
{
    private readonly Func<int, Task<BitmapSource?>> _render;
    private int _version;

    public ComparePage(int index, Size size, Func<int, Task<BitmapSource?>> render)
    {
        Index = index;
        Size = size;
        _render = render;
    }

    public int Index { get; }
    public Size Size { get; }
    public string Label => $"Page {Index + 1}";
    public List<CompareHighlight> Highlights { get; } = [];

    [ObservableProperty] public partial BitmapSource? Image { get; set; }

    public async void Load()
    {
        var version = ++_version;
        var image = await _render(Index);
        if (version == _version) Image = image;
    }

    /// <summary>Lets go of the picture when the page scrolls far out of view.</summary>
    public void Unload()
    {
        _version++;
        Image = null;
    }
}

/// <summary>A change for the list in the options panel.</summary>
public sealed record ChangeRow(TextChange Change)
{
    public int Page => Change.Page + 1;
    public string KindText => Change.Kind switch { ChangeKind.Inserted => "Added", ChangeKind.Deleted => "Removed", _ => "Changed" };
    public string Color => Change.Kind switch { ChangeKind.Inserted => "#43A047", ChangeKind.Deleted => "#E53935", _ => "#F59E0B" };
    public string Text => Change.Kind switch
    {
        ChangeKind.Inserted => Short(Change.NewText),
        ChangeKind.Deleted => Short(Change.OldText),
        _ => $"{Short(Change.OldText)} → {Short(Change.NewText)}",
    };

    private static string Short(string s) => s.Length > 80 ? s[..77] + "…" : s;
}

/// <summary>Compare PDF: two versions side by side with the changed words highlighted, or a visual overlay.</summary>
public sealed partial class CompareController() : ToolController("compare")
{
    private PdfRenderer? _oldRenderer, _newRenderer;
    private CancellationTokenSource? _compareCts;

    public override WorkspaceKind Workspace => WorkspaceKind.Custom;
    public override bool CanReorder => true;
    public override FrameworkElement CreateWorkspace() => new CompareView { DataContext = this };

    public ObservableCollection<ComparePage> OldPages { get; } = [];
    public ObservableCollection<ComparePage> NewPages { get; } = [];
    public ObservableCollection<ComparePage> OverlayPages { get; } = [];
    public ObservableCollection<ChangeRow> Changes { get; } = [];

    [ObservableProperty] public partial bool Overlay { get; set; }
    [ObservableProperty] public partial bool IgnoreCase { get; set; }
    [ObservableProperty] public partial bool IgnorePunctuation { get; set; }
    [ObservableProperty] public partial bool IsComparing { get; set; }
    [ObservableProperty] public partial bool HasPair { get; set; }
    [ObservableProperty] public partial string? Summary { get; set; }
    [ObservableProperty] public partial ChangeRow? SelectedChange { get; set; }

    public string OldName => Files.ElementAtOrDefault(0)?.Name ?? "";
    public string NewName => Files.ElementAtOrDefault(1)?.Name ?? "";
    public bool HasNoChanges => HasPair && !IsComparing && Changes.Count == 0 && !Overlay;

    partial void OnIgnoreCaseChanged(bool value) => _ = CompareAsync();
    partial void OnIgnorePunctuationChanged(bool value) => _ = CompareAsync();
    partial void OnOverlayChanged(bool value) => OnPropertyChanged(nameof(HasNoChanges));
    partial void OnIsComparingChanged(bool value) => OnPropertyChanged(nameof(HasNoChanges));

    protected override async Task OnFilesAddedAsync(IReadOnlyList<FileEntry> added)
    {
        if (Files.Count > 2)
        {
            AppServices.Notify("Two files at a time", "Compare PDF compares an original with one new version. Extra files were left out.");
            while (Files.Count > 2) Files.RemoveAt(Files.Count - 1);
        }
        foreach (var file in Files.Where(f => f.NeedsPassword).ToList())
        {
            var wrong = false;
            while (file.NeedsPassword)
            {
                var password = await AppServices.AskPasswordAsync(file.Name, wrong);
                if (password == null)
                {
                    Files.Remove(file);
                    break;
                }
                file.Password = password;
                file.Note = null;
                await LoadDetailsAsync(file);
                wrong = true;
            }
        }
        await CompareAsync();
    }

    protected override void OnFilesChanged()
    {
        OnPropertyChanged(nameof(OldName));
        OnPropertyChanged(nameof(NewName));
        // Reordering or removing a file starts a new comparison.
        if (!_suspendRecompare) _ = CompareAsync();
    }

    private bool _suspendRecompare;

    protected override void Reset() => Clear();

    [RelayCommand]
    private void Swap()
    {
        if (Files.Count < 2) return;
        _suspendRecompare = true;
        Files.Move(1, 0);
        _suspendRecompare = false;
        _ = CompareAsync();
    }

    private void Clear()
    {
        _compareCts?.Cancel();
        ClearView();
    }

    private void ClearView()
    {
        OldPages.Clear();
        NewPages.Clear();
        OverlayPages.Clear();
        Changes.Clear();
        Summary = null;
        _oldRenderer?.Dispose();
        _newRenderer?.Dispose();
        _oldRenderer = _newRenderer = null;
        HasPair = false;
    }

    /// <summary>Loads both documents and works out the text changes.</summary>
    private async Task CompareAsync()
    {
        var ready = Files.Count == 2 && Files.All(f => f.IsPdf && !f.NeedsPassword && f.PageCount > 0);
        if (!ready)
        {
            Clear();
            return;
        }
        _compareCts?.Cancel();
        var cts = _compareCts = new CancellationTokenSource();
        var (oldFile, newFile) = (Files[0], Files[1]);
        IsComparing = true;
        Error = null;
        try
        {
            var oldRenderer = await PdfRenderer.OpenAsync(oldFile.Path, oldFile.Password);
            var newRenderer = await PdfRenderer.OpenAsync(newFile.Path, newFile.Password);
            var (ignoreCase, ignorePunctuation) = (IgnoreCase, IgnorePunctuation);
            var report = await Task.Run(() => CompareTool.CompareText(oldFile.ToInput(), newFile.ToInput(), ignoreCase, ignorePunctuation, cts.Token), cts.Token);
            if (cts.IsCancellationRequested)
            {
                oldRenderer.Dispose();
                newRenderer.Dispose();
                return;
            }

            ClearView();
            _oldRenderer = oldRenderer;
            _newRenderer = newRenderer;
            for (var i = 0; i < oldRenderer.PageCount; i++) OldPages.Add(new ComparePage(i, oldRenderer.GetPageSize(i), p => RenderAsync(oldRenderer, p)));
            for (var i = 0; i < newRenderer.PageCount; i++) NewPages.Add(new ComparePage(i, newRenderer.GetPageSize(i), p => RenderAsync(newRenderer, p)));
            var overlayCount = Math.Max(oldRenderer.PageCount, newRenderer.PageCount);
            for (var i = 0; i < overlayCount; i++)
                OverlayPages.Add(new ComparePage(i, i < newRenderer.PageCount ? newRenderer.GetPageSize(i) : oldRenderer.GetPageSize(i), p => RenderOverlayAsync(oldRenderer, newRenderer, p)));

            foreach (var change in report.Changes)
            {
                var color = new ChangeRow(change).Color;
                foreach (var l in change.OldLocations) if (l.Page < OldPages.Count) OldPages[l.Page].Highlights.Add(new CompareHighlight(Grow(l.Bounds), color));
                foreach (var l in change.NewLocations) if (l.Page < NewPages.Count) NewPages[l.Page].Highlights.Add(new CompareHighlight(Grow(l.Bounds), color));
                Changes.Add(new ChangeRow(change));
            }
            Summary = report.Changes.Count == 0
                ? "No text differences. Try Visual overlay to spot changes in pictures or layout."
                : $"{report.Changes.Count} change{(report.Changes.Count == 1 ? "" : "s")} · {report.Similarity:P0} similar";
            HasPair = true;
        }
        catch (OperationCanceledException)
        {
            // A newer comparison replaced this one.
        }
        catch (Exception ex) when (ex is UserFacingException or PasswordRequiredException)
        {
            Error = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(cts, _compareCts)) IsComparing = false;
            OnPropertyChanged(nameof(HasNoChanges));
        }
    }

    private static Rect Grow(Rect r)
    {
        r.Inflate(1, 1);
        return r;
    }

    private static async Task<BitmapSource?> RenderAsync(PdfRenderer renderer, int page)
    {
        try
        {
            return await Task.Run(() => renderer.RenderAsync(page, 110));
        }
        catch (Exception ex) when (ex is ObjectDisposedException or UserFacingException)
        {
            return null;
        }
    }

    private static async Task<BitmapSource?> RenderOverlayAsync(PdfRenderer oldRenderer, PdfRenderer newRenderer, int page)
    {
        try
        {
            return await Task.Run(async () =>
            {
                var a = page < oldRenderer.PageCount ? ImageIO.ToPixels(await oldRenderer.RenderAsync(page, 100)) : null;
                var b = page < newRenderer.PageCount ? ImageIO.ToPixels(await newRenderer.RenderAsync(page, 100)) : null;
                return ImageIO.FromPixels(CompareTool.Overlay(a, b).Overlay, 100);
            });
        }
        catch (Exception ex) when (ex is ObjectDisposedException or UserFacingException)
        {
            return null;
        }
    }

    public override async Task SeedDemoAsync()
    {
        for (var i = 0; i < 40 && IsComparing; i++) await Task.Delay(250);
        if (Changes.Count > 0) SelectedChange = Changes[Math.Min(Changes.Count - 1, Changes.Count * 2 / 3)];
    }

    public override void ShowDemoAlternative() => Overlay = true;

    public override string? Validate()
    {
        if (Files.Count < 2) return "Add two PDFs: the original first, then the new version.";
        if (IsComparing) return "Still comparing; try again in a moment.";
        return base.Validate();
    }

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        CompareTool.RunAsync(new CompareOptions
        {
            OldFile = Files[0].ToInput(),
            NewFile = Files[1].ToInput(),
            OutputDirectory = OutputDirectory(Files[1].Path),
            Overlay = Overlay,
            IgnoreCase = IgnoreCase,
            IgnorePunctuation = IgnorePunctuation,
        }, progress, ct);
}
