using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Infrastructure;
using PageSmith.App.Tools.Editor;
using PageSmith.Core.Common;
using PageSmith.Core.Text;
using PageSmith.Core.Tools.Security;
using Wpf.Ui.Controls;

namespace PageSmith.App.Tools;

public sealed partial class PatternChoice(RedactionPattern pattern) : ObservableObject
{
    public RedactionPattern Pattern { get; } = pattern;
    public string Label { get; } = RedactTool.Describe(pattern);

    [ObservableProperty] public partial bool Selected { get; set; }
}

/// <summary>Redact PDF: mark areas, words, search results and personal-data patterns, then remove them for good.</summary>
public sealed partial class RedactController() : EditorController("redact")
{
    private static readonly EditorTool SelectTool = new("select", "Select", SymbolRegular.Cursor24, PointerAction.Select,
        "Click a mark to select it; drag to move, use the handles to resize, press Delete to remove it.");
    private static readonly EditorTool AreaTool = new("area", "Mark an area", SymbolRegular.RectangleLandscape24, PointerAction.DrawRect,
        "Drag over anything (text, pictures, signatures) to black it out.");
    private static readonly EditorTool WordTool = new("word", "Mark a word", SymbolRegular.TextT24, PointerAction.Pick,
        "Click a word to black it out.");

    private readonly Dictionary<int, IReadOnlyList<TextWord>> _words = [];

    public override IReadOnlyList<EditorTool> Tools { get; } = [AreaTool, WordTool, SelectTool];

    public IReadOnlyList<PatternChoice> Patterns { get; } = Enum.GetValues<RedactionPattern>().Select(p => new PatternChoice(p)).ToList();

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial bool MatchCase { get; set; }
    [ObservableProperty] public partial bool WholeWord { get; set; }
    [ObservableProperty] public partial bool UseRegex { get; set; }
    [ObservableProperty] public partial string FillColor { get; set; } = "#000000";
    [ObservableProperty] public partial string OverlayText { get; set; } = "";
    [ObservableProperty] public partial bool RemoveMetadata { get; set; } = true;
    [ObservableProperty] public partial string MarksSummary { get; set; } = "Nothing marked yet.";

    public IEnumerable<RedactItem> Marks => Items.OfType<RedactItem>().OrderBy(m => m.PageIndex).ThenBy(m => m.Bounds.Y).ThenBy(m => m.Bounds.X);

    public override bool KeepToolAfterCreate(EditorTool tool) => true;

    protected override void OnDocumentClosed() => _words.Clear();

    protected override void OnItemsChanged()
    {
        var marks = Items.OfType<RedactItem>().ToList();
        var pages = marks.Select(m => m.PageIndex).Distinct().Count();
        MarksSummary = marks.Count == 0 ? "Nothing marked yet." : $"{marks.Count} mark{(marks.Count == 1 ? "" : "s")} on {pages} page{(pages == 1 ? "" : "s")}.";
        OnPropertyChanged(nameof(Marks));
    }

    public override EditorItem? CreateFromRect(EditorTool tool, int page, Rect rect) =>
        tool == AreaTool && rect.Width >= 2 && rect.Height >= 2 ? new RedactItem { PageIndex = page, Bounds = rect, Reason = "Area" } : null;

    public override async Task PickAsync(EditorTool tool, int page, Point point)
    {
        if (tool != WordTool || Document is not { } doc) return;
        if (!_words.TryGetValue(page, out var words))
        {
            try
            {
                words = await Task.Run(() => TextExtractor.ReadPages(doc.ToInput(), [page]).FirstOrDefault()?.Words ?? []);
            }
            catch (Exception ex) when (ex is UserFacingException or PasswordRequiredException)
            {
                words = [];
            }
            _words[page] = words;
        }
        var word = words.Where(w => Grow(w.Bounds, 1.5).Contains(point)).OrderBy(w => w.Bounds.Width * w.Bounds.Height).FirstOrDefault();
        if (word == null)
        {
            AppServices.Notify("No text there", "Click directly on a word. For pictures or scanned pages, drag an area instead.");
            return;
        }
        if (Items.OfType<RedactItem>().Any(m => m.PageIndex == page && m.Bounds.Contains(word.Bounds))) return;
        AddItem(new RedactItem { PageIndex = page, Bounds = Grow(word.Bounds, 1), MatchedText = word.Text, Reason = "Word" }, select: false);
    }

    private static Rect Grow(Rect r, double by)
    {
        r.Inflate(by, by);
        return r;
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (Document is not { } doc || string.IsNullOrWhiteSpace(SearchText)) return;
        if (UseRegex)
        {
            try { _ = new Regex(SearchText); }
            catch (ArgumentException ex)
            {
                Error = $"That pattern isn't valid: {ex.Message}";
                return;
            }
        }
        var (text, matchCase, whole, regex) = (SearchText, MatchCase, WholeWord, UseRegex);
        await AddFoundAsync(() => RedactTool.FindText(doc.ToInput(), text, matchCase, whole, regex), $"\"{text}\"");
    }

    [RelayCommand]
    private async Task FindPatternsAsync()
    {
        if (Document is not { } doc) return;
        var chosen = Patterns.Where(p => p.Selected).Select(p => p.Pattern).ToList();
        if (chosen.Count == 0)
        {
            Error = "Tick at least one kind of information to look for.";
            return;
        }
        await AddFoundAsync(() => RedactTool.FindPatterns(doc.ToInput(), chosen), "personal information");
    }

    private async Task AddFoundAsync(Func<IReadOnlyList<RedactionMark>> find, string what)
    {
        Error = null;
        Status = $"Searching for {what}…";
        try
        {
            var found = await Task.Run(find);
            var existing = Items.OfType<RedactItem>().ToList();
            var added = found
                .Where(f => !existing.Any(e => e.PageIndex == f.PageIndex && e.Bounds.Contains(f.Bounds)))
                .Select(f => (EditorItem)new RedactItem { PageIndex = f.PageIndex, Bounds = Grow(f.Bounds, 1), MatchedText = f.MatchedText, Reason = f.Reason ?? "Search" })
                .ToList();
            AddItems(added);
            Status = found.Count == 0 ? $"No {what} found. Scanned pages need OCR PDF first." : $"Marked {added.Count} place{(added.Count == 1 ? "" : "s")} with {what}.";
            if (added.Count > 0 && !added.Any(a => a.PageIndex == CurrentPageIndex)) CurrentPageIndex = added[0].PageIndex;
        }
        catch (Exception ex) when (ex is UserFacingException or RegexMatchTimeoutException or PasswordRequiredException)
        {
            Status = null;
            Error = ex.Message;
        }
    }

    [RelayCommand]
    private void GoToMark(RedactItem? mark)
    {
        if (mark == null) return;
        CurrentPageIndex = mark.PageIndex;
        SelectedItem = mark;
    }

    [RelayCommand]
    private void RemoveMark(RedactItem? mark)
    {
        if (mark != null) RemoveItem(mark);
    }

    [RelayCommand]
    private void ClearMarks() => RemoveItems(Items.OfType<RedactItem>().ToList());

    public override string? Validate()
    {
        if (base.Validate() is { } problem) return problem;
        return Items.OfType<RedactItem>().Any() ? null : "Mark something to redact: drag over an area, click a word, or search.";
    }

    protected override Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct) =>
        RedactTool.RunAsync(new RedactOptions
        {
            Input = Document!.ToInput(),
            OutputPath = OutputFor("redacted"),
            Marks = Items.OfType<RedactItem>().Select(m => new RedactionMark(m.PageIndex, m.Bounds, m.MatchedText, m.Reason)).ToList(),
            FillColor = FillColor,
            OverlayText = string.IsNullOrWhiteSpace(OverlayText) ? null : OverlayText.Trim(),
            RemoveMetadata = RemoveMetadata,
        }, progress, ct);

    public override async Task SeedDemoAsync()
    {
        CurrentPageIndex = 0;
        foreach (var p in Patterns.Where(p => p.Pattern is RedactionPattern.Email or RedactionPattern.Phone)) p.Selected = true;
        await FindPatternsAsync();
        SearchText = "Americas";
        await SearchAsync();
        CurrentPageIndex = 0;
        if (CreateFromRect(AreaTool, 0, new Rect(415, 595, 130, 130)) is { } area) AddItem(area);
        OverlayText = "REDACTED";
    }
}
