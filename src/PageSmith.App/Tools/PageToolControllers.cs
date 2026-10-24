using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Controls;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Tools.Edit;
using PageSmith.Core.Tools.Organize;

namespace PageSmith.App.Tools;

/// <summary>One page shown in the page grid.</summary>
public sealed partial class PageItem : ObservableObject
{
    public required FileEntry? Source { get; init; }
    public required int PageIndex { get; init; }
    public bool IsBlank => Source == null;
    public string SourceName => Source?.Name ?? "Blank page";
    public double BlankWidth { get; init; } = 595.28;
    public double BlankHeight { get; init; } = 841.89;

    [ObservableProperty] public partial BitmapSource? Thumbnail { get; set; }
    [ObservableProperty] public partial int Rotation { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial string Label { get; set; } = "";
    [ObservableProperty] public partial string? Badge { get; set; }

    private bool _loading;

    /// <summary>Loads the thumbnail the first time the page scrolls into view.</summary>
    public async void EnsureThumbnail()
    {
        if (_loading || Thumbnail != null || Source == null) return;
        _loading = true;
        Thumbnail = await AppServices.Thumbnails.GetAsync(Source.Path, PageIndex, 220, Source.Password);
    }
}

/// <summary>Base for tools whose workspace is a grid of pages.</summary>
public abstract partial class PageGridController(string id) : ToolController(id)
{
    public override WorkspaceKind Workspace => WorkspaceKind.Pages;
    public ObservableCollection<PageItem> Pages { get; } = [];

    public virtual bool AllowReorder => false;
    public virtual bool AllowRotate => false;
    public virtual bool AllowDelete => false;
    public virtual bool AllowInsert => false;
    public virtual bool ClickSelects => true;
    public virtual string Hint => "Click pages to select them. Shift+click selects a range.";
    public virtual string? SelectionVerb => null;

    public int SelectedCount => Pages.Count(p => p.IsSelected);

    public override FrameworkElement CreateWorkspace() => new PageGrid { DataContext = this };

    protected override async Task OnFilesAddedAsync(IReadOnlyList<FileEntry> added)
    {
        foreach (var f in added) AddPagesOf(f);
        Renumber();
        await Task.CompletedTask;
    }

    protected override void OnFilesChanged()
    {
        // Pages of removed files disappear with them.
        foreach (var p in Pages.Where(p => p.Source != null && !Files.Contains(p.Source)).ToList()) Pages.Remove(p);
        Renumber();
    }

    protected override void Reset() => Pages.Clear();

    protected void AddPagesOf(FileEntry file)
    {
        for (var i = 0; i < file.PageCount; i++)
        {
            var item = new PageItem { Source = file, PageIndex = i };
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PageItem.IsSelected)) OnSelectionChanged();
            };
            Pages.Add(item);
        }
    }

    public void Renumber()
    {
        var multi = Files.Count > 1;
        for (var i = 0; i < Pages.Count; i++)
        {
            var p = Pages[i];
            p.Label = p.IsBlank ? $"{i + 1} · blank" : multi ? $"{i + 1} · {p.Source!.Name} p.{p.PageIndex + 1}" : $"{p.PageIndex + 1}";
        }
        OnSelectionChanged();
    }

    protected virtual void OnSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionSummary));
    }

    public virtual string SelectionSummary => SelectedCount == 0 ? "No pages selected" : $"{SelectedCount} of {Pages.Count} pages selected";

    /// <summary>Selected pages as text ("1-3, 7"), using original page numbers of a single file.</summary>
    protected string SelectedSpec => PageRange.Format(Pages.Where(p => p.IsSelected && !p.IsBlank).Select(p => p.PageIndex));

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var p in Pages) p.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var p in Pages) p.IsSelected = false;
    }

    [RelayCommand]
    private void InvertSelection()
    {
        foreach (var p in Pages) p.IsSelected = !p.IsSelected;
    }

    [RelayCommand]
    private void RotateAllRight()
    {
        foreach (var p in Pages.Where(p => p.IsSelected || SelectedCount == 0)) p.Rotation = (p.Rotation + 90) % 360;
    }

    [RelayCommand]
    private void RotateAllLeft()
    {
        foreach (var p in Pages.Where(p => p.IsSelected || SelectedCount == 0)) p.Rotation = (p.Rotation + 270) % 360;
    }

    /// <summary>Applies a typed range ("1-3, 8") to the selection.</summary>
    public void SelectRange(string spec)
    {
        if (Files.Count == 0) return;
        var count = Files[0].PageCount;
        var set = PageRange.Parse(spec, count).ToHashSet();
        foreach (var p in Pages) p.IsSelected = !p.IsBlank && set.Contains(p.PageIndex);
    }
}

public sealed partial class RemovePagesController() : PageGridController("remove-pages")
{
    public override string Hint => "Click the pages to remove. Shift+click selects a range.";
    public override string? SelectionVerb => "remove";

    [ObservableProperty] public partial string PagesText { get; set; } = "";

    protected override void OnSelectionChanged()
    {
        base.OnSelectionChanged();
        var spec = SelectedSpec;
        if (spec != PagesText) PagesText = spec;
    }

    partial void OnPagesTextChanged(string value)
    {
        try { if (value != SelectedSpec) SelectRange(value); } catch (FormatException) { /* typing in progress */ }
    }

    public override string? Validate() => SelectedCount == 0 && Files.Count > 0 ? "Select the pages to remove." : base.Validate();

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        RemovePagesTool.RunAsync(new RemovePagesOptions { Input = Files[0].ToInput(), Pages = SelectedSpec, OutputPath = OutputFile(Files[0].Path, "removed", ".pdf") }, progress, ct);
}

public sealed partial class ExtractPagesController() : PageGridController("extract-pages")
{
    public override string Hint => "Click the pages to extract. Shift+click selects a range.";
    public override string? SelectionVerb => "extract";

    [ObservableProperty] public partial string PagesText { get; set; } = "";
    [ObservableProperty] public partial bool MergeIntoOne { get; set; } = true;

    protected override void OnSelectionChanged()
    {
        base.OnSelectionChanged();
        var spec = SelectedSpec;
        if (spec != PagesText) PagesText = spec;
    }

    partial void OnPagesTextChanged(string value)
    {
        try { if (value != SelectedSpec) SelectRange(value); } catch (FormatException) { /* typing in progress */ }
    }

    public override string? Validate() => SelectedCount == 0 && Files.Count > 0 ? "Select the pages to extract." : base.Validate();

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        SplitTool.RunAsync(new SplitOptions
        {
            Input = Files[0].ToInput(), OutputDirectory = OutputDirectory(), Mode = SplitMode.SelectPages,
            SelectedPages = SelectedSpec, MergeIntoOne = MergeIntoOne,
        }, progress, ct);
}

public sealed partial class OrganizeController() : PageGridController("organize")
{
    public override bool AllowReorder => true;
    public override bool AllowRotate => true;
    public override bool AllowDelete => true;
    public override bool AllowInsert => true;
    public override bool ClickSelects => true;
    public override string Hint => "Drag pages to reorder. Hover a page to rotate, duplicate, delete or insert a blank page after it.";

    [ObservableProperty] public partial string OutputName { get; set; } = "organized";

    [RelayCommand]
    private void AddBlankAtEnd() => InsertBlank(Pages.Count);

    [RelayCommand]
    private void DeleteSelected()
    {
        foreach (var p in Pages.Where(p => p.IsSelected).ToList()) Pages.Remove(p);
        Renumber();
    }

    public void InsertBlank(int index)
    {
        var item = new PageItem { Source = null, PageIndex = -1 };
        Pages.Insert(Math.Clamp(index, 0, Pages.Count), item);
        Renumber();
    }

    public void Duplicate(PageItem page)
    {
        var copy = new PageItem { Source = page.Source, PageIndex = page.PageIndex, Rotation = page.Rotation, Thumbnail = page.Thumbnail };
        Pages.Insert(Pages.IndexOf(page) + 1, copy);
        Renumber();
    }

    public override string? Validate() => Pages.Count == 0 && Files.Count > 0 ? "The document needs at least one page." : base.Validate();

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var sources = Files.ToList();
        var pages = Pages.Select(p => p.IsBlank
            ? OrganizedPage.Blank(p.BlankWidth, p.BlankHeight) with { Rotation = p.Rotation }
            : new OrganizedPage(sources.IndexOf(p.Source!), p.PageIndex, p.Rotation)).ToList();
        var name = OutputPaths.Sanitize(string.IsNullOrWhiteSpace(OutputName) ? "organized" : OutputName) + ".pdf";
        return OrganizeTool.RunAsync(new OrganizeOptions { Sources = sources.Select(s => s.ToInput()).ToList(), Pages = pages, OutputPath = OutputPaths.Unique(OutputDirectory(), name) }, progress, ct);
    }
}

public sealed partial class RotateController() : PageGridController("rotate")
{
    public override bool AllowRotate => true;
    public override bool ClickSelects => true;
    public override string Hint => "Hover a page to rotate it, or select pages and use Rotate left/right. With nothing selected, the buttons rotate every page.";

    [ObservableProperty] public partial PageFilter Filter { get; set; } = PageFilter.All;

    protected override async Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct)
    {
        var items = Files.Select(f => new RotateItem(f.ToInput(), 0,
            Pages.Where(p => p.Source == f && p.Rotation != 0).ToDictionary(p => p.PageIndex, p => p.Rotation))).ToList();
        if (items.All(i => i.PageRotations!.Count == 0)) throw new UserFacingException("Rotate at least one page first.");
        return await RotateTool.RunAsync(new RotateOptions { Items = items, OutputDirectory = OutputDirectory() }, progress, ct);
    }

    [RelayCommand]
    private void RotateFiltered(string direction)
    {
        var turn = direction == "left" ? 270 : 90;
        foreach (var p in Pages)
        {
            if (Filter != PageFilter.All && p.Thumbnail is { } t && (t.PixelWidth > t.PixelHeight) != (Filter == PageFilter.Landscape)) continue;
            p.Rotation = (p.Rotation + turn) % 360;
        }
    }
}
