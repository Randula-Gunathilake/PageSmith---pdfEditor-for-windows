using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Controls;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Rendering;
using Wpf.Ui.Controls;

namespace PageSmith.App.Tools.Editor;

/// <summary>What a click or drag on the page does while a tool is active.</summary>
public enum PointerAction
{
    /// <summary>Select, move and resize items.</summary>
    Select,

    /// <summary>Drag out a rectangle; the controller turns it into an item.</summary>
    DrawRect,

    /// <summary>Click to place an item of a default size.</summary>
    Click,

    /// <summary>Freehand drawing.</summary>
    Ink,

    /// <summary>Click something on the page itself (e.g. existing text to edit).</summary>
    Pick,

    /// <summary>The page is interactive (filling form fields); clicks go to the fields.</summary>
    Interact,
}

public sealed record EditorTool(string Id, string Label, SymbolRegular Icon, PointerAction Action, string? Hint = null);

/// <summary>A page of the open document.</summary>
public sealed partial class EditorPage : ObservableObject
{
    public required int Index { get; init; }
    public required Size Size { get; init; }
    public string Label => (Index + 1).ToString();

    [ObservableProperty] public partial BitmapSource? Thumbnail { get; set; }
    [ObservableProperty] public partial int ItemCount { get; set; }
}

/// <summary>Undo/redo of adding, deleting, moving and resizing items.</summary>
public sealed class UndoStack
{
    private readonly Stack<(Action Undo, Action Redo)> _undo = new();
    private readonly Stack<(Action Undo, Action Redo)> _redo = new();

    public event EventHandler? Changed;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Push(Action undo, Action redo)
    {
        _undo.Push((undo, redo));
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var step)) return;
        step.Undo();
        _redo.Push(step);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var step)) return;
        step.Redo();
        _undo.Push(step);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Base for tools that work on one document page by page (Crop, Edit, Forms, Sign, Redact). Until a file
/// is added the workspace is the usual drop zone; afterwards it is a <see cref="PageEditor"/>.
/// </summary>
public abstract partial class EditorController : ToolController
{
    private PdfRenderer? _renderer;
    private readonly Dictionary<(int Page, int Dpi), BitmapSource> _renders = [];
    private readonly SemaphoreSlim _openGate = new(1, 1);

    protected EditorController(string toolId) : base(toolId)
    {
        Items.CollectionChanged += OnItemsChanged;
        Undo.Changed += (_, _) =>
        {
            UndoCommand.NotifyCanExecuteChanged();
            RedoCommand.NotifyCanExecuteChanged();
        };
    }

    public override WorkspaceKind Workspace => HasDocument ? WorkspaceKind.Editor : WorkspaceKind.Files;
    public override FrameworkElement? CreateWorkspace() => HasDocument ? new PageEditor { DataContext = this } : null;

    public ObservableCollection<EditorPage> Pages { get; } = [];
    public ObservableCollection<EditorItem> Items { get; } = [];
    public UndoStack Undo { get; } = new();

    /// <summary>The tools in the editor toolbar; the first one is active after opening a file.</summary>
    public abstract IReadOnlyList<EditorTool> Tools { get; }

    [ObservableProperty] public partial bool HasDocument { get; set; }
    [ObservableProperty] public partial int CurrentPageIndex { get; set; }
    [ObservableProperty] public partial EditorItem? SelectedItem { get; set; }
    [ObservableProperty] public partial EditorTool? ActiveTool { get; set; }

    public FileEntry? Document => Files.FirstOrDefault();
    public EditorPage? CurrentPage => CurrentPageIndex >= 0 && CurrentPageIndex < Pages.Count ? Pages[CurrentPageIndex] : null;
    public string PageText => Pages.Count == 0 ? "" : $"{CurrentPageIndex + 1} / {Pages.Count}";
    public string? ToolHint => ActiveTool?.Hint;

    partial void OnHasDocumentChanged(bool value) => OnPropertyChanged(nameof(Workspace));

    partial void OnCurrentPageIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentPage));
        OnPropertyChanged(nameof(PageText));
        NextPageCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        if (SelectedItem != null && SelectedItem.PageIndex != value) SelectedItem = null;
        OnCurrentPageChanged();
    }

    partial void OnSelectedItemChanged(EditorItem? oldValue, EditorItem? newValue)
    {
        if (oldValue != null)
        {
            oldValue.IsSelected = false;
            if (oldValue is TextItem t)
            {
                t.IsEditing = false;
                // A text box left empty is dropped rather than saved as nothing.
                if (string.IsNullOrWhiteSpace(t.Text) && Items.Contains(t)) Items.Remove(t);
            }
        }
        if (newValue == null && DefaultSelection is { } fallback && Items.Contains(fallback))
        {
            SelectedItem = fallback;
            return;
        }
        if (newValue != null) newValue.IsSelected = true;
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        BringForwardCommand.NotifyCanExecuteChanged();
        SendBackwardCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelection));
    }

    partial void OnActiveToolChanged(EditorTool? value) => OnPropertyChanged(nameof(ToolHint));

    public bool HasSelection => SelectedItem != null;

    /// <summary>Item that stays selected when nothing else is (the crop frame).</summary>
    protected virtual EditorItem? DefaultSelection => null;

    protected PdfRenderer? Renderer => _renderer;

    protected virtual void OnCurrentPageChanged() { }

    // ------------------------------------------------------------------ document

    protected override async Task OnFilesAddedAsync(IReadOnlyList<FileEntry> added)
    {
        if (added.Count > 0) await OpenDocumentAsync(added[0]);
    }

    protected override void OnFilesChanged()
    {
        if (Files.Count == 0) CloseDocument();
    }

    protected override void Reset() => CloseDocument();

    private async Task OpenDocumentAsync(FileEntry file)
    {
        await _openGate.WaitAsync();
        try
        {
            var wrong = false;
            while (file.NeedsPassword)
            {
                var password = await AppServices.AskPasswordAsync(file.Name, wrong);
                if (password == null)
                {
                    Files.Remove(file);
                    return;
                }
                file.Password = password;
                file.Note = null;
                await LoadDetailsAsync(file);
                wrong = true;
            }
            CloseDocument();
            _renderer = await PdfRenderer.OpenAsync(file.Path, file.Password);
            for (var i = 0; i < _renderer.PageCount; i++) Pages.Add(new EditorPage { Index = i, Size = _renderer.GetPageSize(i) });
            CurrentPageIndex = 0;
            OnPropertyChanged(nameof(PageText));
            ActiveTool = Tools.FirstOrDefault();
            await OnDocumentOpenedAsync();
            HasDocument = true;
            _ = LoadThumbnailsAsync(file);
        }
        catch (UserFacingException ex)
        {
            Error = ex.Message;
            Files.Remove(file);
        }
        finally
        {
            _openGate.Release();
        }
    }

    private async Task LoadThumbnailsAsync(FileEntry file)
    {
        foreach (var page in Pages.ToList())
        {
            if (!ReferenceEquals(Document, file)) return;
            page.Thumbnail = await AppServices.Thumbnails.GetAsync(file.Path, page.Index, 140, file.Password);
        }
    }

    private void CloseDocument()
    {
        HasDocument = false;
        SelectedItem = null;
        Items.Clear();
        Pages.Clear();
        Undo.Clear();
        _renders.Clear();
        _renderer?.Dispose();
        _renderer = null;
        OnDocumentClosed();
    }

    /// <summary>Called after a document opens, before the editor shows (load fields, place the crop box…).</summary>
    protected virtual Task OnDocumentOpenedAsync() => Task.CompletedTask;

    protected virtual void OnDocumentClosed() { }

    /// <summary>Renders a page for the editor, caching a few recent renders.</summary>
    public async Task<BitmapSource?> RenderPageAsync(int index, double dpi)
    {
        if (_renderer is not { } renderer || index < 0 || index >= renderer.PageCount) return null;
        var key = (index, (int)Math.Round(dpi));
        if (_renders.TryGetValue(key, out var cached)) return cached;
        try
        {
            var bitmap = await Task.Run(() => renderer.RenderAsync(index, key.Item2));
            if (!ReferenceEquals(renderer, _renderer)) return null;
            if (_renders.Count > 12) _renders.Clear();
            _renders[key] = bitmap;
            return bitmap;
        }
        catch (Exception ex) when (ex is UserFacingException or ObjectDisposedException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ pointer actions

    /// <summary>Turns a rectangle dragged with <see cref="PointerAction.DrawRect"/> into an item (or null to ignore).</summary>
    public virtual EditorItem? CreateFromRect(EditorTool tool, int page, Rect rect) => null;

    /// <summary>Creates an item for a click with <see cref="PointerAction.Click"/>.</summary>
    public virtual EditorItem? CreateAt(EditorTool tool, int page, Point point) => null;

    /// <summary>Handles a click with <see cref="PointerAction.Pick"/>.</summary>
    public virtual Task PickAsync(EditorTool tool, int page, Point point) => Task.CompletedTask;

    /// <summary>Ink colour and width for <see cref="PointerAction.Ink"/>.</summary>
    public virtual (string Color, double Width) InkPen => ("#1F3A93", 2);

    /// <summary>Whether the tool stays active after creating an item (else the editor returns to Select).</summary>
    public virtual bool KeepToolAfterCreate(EditorTool tool) => false;

    // ------------------------------------------------------------------ items

    public IEnumerable<EditorItem> ItemsOn(int page) => Items.Where(i => i.PageIndex == page);

    public void AddItem(EditorItem item, bool select = true)
    {
        Items.Add(item);
        Undo.Push(() => RemoveQuietly(item), () => Items.Add(item));
        if (select) SelectedItem = item;
    }

    public void AddItems(IReadOnlyList<EditorItem> items)
    {
        if (items.Count == 0) return;
        foreach (var item in items) Items.Add(item);
        Undo.Push(() => { foreach (var i in items) RemoveQuietly(i); }, () => { foreach (var i in items) Items.Add(i); });
    }

    public void RemoveItem(EditorItem item)
    {
        if (!item.CanDelete) return;
        var index = Items.IndexOf(item);
        if (index < 0) return;
        RemoveQuietly(item);
        Undo.Push(() => Items.Insert(Math.Min(index, Items.Count), item), () => RemoveQuietly(item));
    }

    public void RemoveItems(IReadOnlyList<EditorItem> items)
    {
        var removable = items.Where(i => i.CanDelete && Items.Contains(i)).ToList();
        if (removable.Count == 0) return;
        foreach (var item in removable) RemoveQuietly(item);
        Undo.Push(() => { foreach (var i in removable) Items.Add(i); }, () => { foreach (var i in removable) RemoveQuietly(i); });
    }

    private void RemoveQuietly(EditorItem item)
    {
        if (ReferenceEquals(SelectedItem, item)) SelectedItem = null;
        Items.Remove(item);
    }

    /// <summary>Records a finished move or resize so it can be undone.</summary>
    public void RecordBoundsChange(EditorItem item, Rect before, Rect after)
    {
        if (before == after) return;
        Undo.Push(() => item.SetBounds(before), () => item.SetBounds(after));
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var page in Pages) page.ItemCount = 0;
        foreach (var group in Items.GroupBy(i => i.PageIndex))
            if (group.Key >= 0 && group.Key < Pages.Count) Pages[group.Key].ItemCount = group.Count(CountsAsEdit);
        OnItemsChanged();
    }

    /// <summary>Whether an item counts towards a page's badge (existing form fields don't).</summary>
    protected virtual bool CountsAsEdit(EditorItem item) => true;

    protected virtual void OnItemsChanged() { }

    // ------------------------------------------------------------------ commands

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private void NextPage() => CurrentPageIndex++;

    private bool CanGoNext() => CurrentPageIndex < Pages.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousPage() => CurrentPageIndex--;

    private bool CanGoPrevious() => CurrentPageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private void DeleteSelected()
    {
        if (SelectedItem is { } item) RemoveItem(item);
    }

    private bool CanDeleteSelected() => SelectedItem?.CanDelete == true;

    [RelayCommand(CanExecute = nameof(CanUndoEdit))]
    private void UndoEdit() => Undo.Undo();

    private bool CanUndoEdit() => Undo.CanUndo;

    [RelayCommand(CanExecute = nameof(CanRedoEdit))]
    private void RedoEdit() => Undo.Redo();

    private bool CanRedoEdit() => Undo.CanRedo;

    public IRelayCommand UndoCommand => UndoEditCommand;
    public IRelayCommand RedoCommand => RedoEditCommand;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void BringForward()
    {
        if (SelectedItem is not { } item) return;
        var index = Items.IndexOf(item);
        if (index < Items.Count - 1) Items.Move(index, Items.Count - 1);
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void SendBackward()
    {
        if (SelectedItem is not { } item) return;
        var index = Items.IndexOf(item);
        if (index > 0) Items.Move(index, 0);
    }

    [RelayCommand]
    private void CloseFile()
    {
        Result = null;
        Error = null;
        Status = null;
        Files.Clear();
    }

    public override string? Validate()
    {
        if (!HasDocument) return NeedsFiles ? "Add a PDF first." : null;
        return null;
    }

    protected string OutputFor(string suffix) => OutputFile(Document!.Path, suffix, ".pdf");

    /// <summary>
    /// Development aid for the --snapshot --demo mode: places sample items through the same entry points
    /// a user's clicks and drags use, so screenshots and --run exercise the real code paths.
    /// </summary>
    public virtual Task SeedDemoAsync() => Task.CompletedTask;

    /// <summary>Default box size for something placed with a click, centred on the point and kept on the page.</summary>
    protected Rect PlaceBox(int page, Point centre, Size size)
    {
        var pageSize = Pages[page].Size;
        var x = Math.Clamp(centre.X - size.Width / 2, 0, Math.Max(0, pageSize.Width - size.Width));
        var y = Math.Clamp(centre.Y - size.Height / 2, 0, Math.Max(0, pageSize.Height - size.Height));
        return new Rect(x, y, size.Width, size.Height);
    }
}
