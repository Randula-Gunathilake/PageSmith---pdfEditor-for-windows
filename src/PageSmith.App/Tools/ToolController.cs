using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Net;
using PageSmith.Core.Tools.Optimize;

namespace PageSmith.App.Tools;

/// <summary>How a tool runs on a document open in a tab.</summary>
/// <param name="OutputFolder">Where results go: the document's working folder when it edits the document, else next to the file.</param>
/// <param name="EditsDocument">Whether the single PDF result replaces the open document.</param>
public sealed record DocumentToolContext(string OutputFolder, bool EditsDocument);

public enum WorkspaceKind
{
    /// <summary>A list of input files.</summary>
    Files,

    /// <summary>Page thumbnails (organize, remove, extract, rotate pages).</summary>
    Pages,

    /// <summary>One page at a time with drawable elements (edit, sign, redact, crop, forms).</summary>
    Editor,

    /// <summary>Tool-specific view returned by <see cref="ToolController.CreateWorkspace"/>.</summary>
    Custom,
}

/// <summary>An input file shown in a tool.</summary>
public sealed partial class FileEntry : ObservableObject
{
    public FileEntry(string path)
    {
        Path = path;
        var info = new FileInfo(path);
        Size = info.Exists ? info.Length : 0;
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public long Size { get; }
    public string SizeText => CompressTool.Format(Size);
    public bool IsPdf => System.IO.Path.GetExtension(Path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] public partial string? Password { get; set; }
    [ObservableProperty] public partial bool NeedsPassword { get; set; }
    [ObservableProperty] public partial int PageCount { get; set; }
    [ObservableProperty] public partial BitmapSource? Thumbnail { get; set; }

    /// <summary>Extra clockwise rotation chosen in the tool (multiples of 90).</summary>
    [ObservableProperty] public partial int Rotation { get; set; }

    [ObservableProperty] public partial string? Note { get; set; }

    public string Details => PageCount > 0 ? $"{PageCount} page{(PageCount == 1 ? "" : "s")} · {SizeText}" : SizeText;

    partial void OnPageCountChanged(int value) => OnPropertyChanged(nameof(Details));

    public InputFile ToInput() => new(Path, Password);
}

/// <summary>
/// The state and behaviour of one tool page: input files, options (as observable properties bound by
/// the tool's options template), running with progress/cancellation, and the result.
/// </summary>
public abstract partial class ToolController : ObservableObject
{
    private CancellationTokenSource? _cts;

    protected ToolController(string toolId)
    {
        Descriptor = ToolRegistry.Get(toolId);
        Files.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasFiles));
            OnFilesChanged();
            RunCommand.NotifyCanExecuteChanged();
        };
    }

    public ToolDescriptor Descriptor { get; }
    public ObservableCollection<FileEntry> Files { get; } = [];
    public bool HasFiles => Files.Count > 0;

    public virtual WorkspaceKind Workspace => WorkspaceKind.Files;

    /// <summary>Whether files can be reordered (merge, images to PDF).</summary>
    public virtual bool CanReorder => false;

    /// <summary>Whether each file can be rotated from the file list.</summary>
    public virtual bool CanRotateFiles => false;

    /// <summary>For tools that work without input files (HTML to PDF from a URL, scanning).</summary>
    public virtual bool NeedsFiles => Descriptor.Input != InputKind.None;

    public virtual string ActionText => Descriptor.ActionText;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RunCommand))] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }
    [ObservableProperty] public partial ToolResult? Result { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial bool IsOffline { get; set; }

    public bool HasResult => Result != null;
    partial void OnResultChanged(ToolResult? value) => OnPropertyChanged(nameof(HasResult));

    /// <summary>Validation message shown instead of running, or null when ready.</summary>
    public virtual string? Validate() => NeedsFiles && Files.Count == 0 ? $"Add {(Descriptor.MultipleFiles ? "files" : "a file")} first." : null;

    protected abstract Task<ToolResult> RunCoreAsync(IProgress<ToolProgress> progress, CancellationToken ct);

    protected virtual void OnFilesChanged() { }

    /// <summary>Called when the tool page is shown.</summary>
    public virtual Task OnShownAsync() => Task.CompletedTask;

    public virtual FrameworkElement? CreateWorkspace() => null;

    /// <summary>
    /// Development aid for the --snapshot --demo mode: sets the tool up through the same entry points a
    /// user's clicks use (placing items, picking options), so screenshots and --run exercise real code paths.
    /// </summary>
    public virtual Task SeedDemoAsync() => Task.CompletedTask;

    /// <summary>Development aid for --demo-alt: shows the tool's dialog or alternative view.</summary>
    public virtual void ShowDemoAlternative() { }

    /// <summary>
    /// Set when the tool works on a document open in a tab: where results go, and whether the result
    /// replaces the document (then results land in the document's private working folder).
    /// </summary>
    public DocumentToolContext? Context { get; set; }

    /// <summary>Passwords already known for input files (an open document's), so tools don't ask again.</summary>
    public Dictionary<string, string> KnownPasswords { get; } = new(StringComparer.OrdinalIgnoreCase);

    private bool CanRun() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        Error = null;
        IsOffline = false;
        var problem = Validate();
        if (problem != null)
        {
            Error = problem;
            return;
        }
        if (Context == null && AppServices.Settings.Output == OutputLocation.Ask && !AppServices.AskOutputFolder())
            return;

        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        Status = "Starting…";
        var progress = new Progress<ToolProgress>(p =>
        {
            Progress = p.Fraction * 100;
            if (p.Status != null) Status = p.Status;
        });
        var sw = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                try
                {
                    Result = await RunCoreAsync(progress, _cts.Token);
                    break;
                }
                catch (PasswordRequiredException ex)
                {
                    var file = Files.FirstOrDefault(f => string.Equals(f.Path, ex.FilePath, StringComparison.OrdinalIgnoreCase));
                    var password = await AppServices.AskPasswordAsync(Path.GetFileName(ex.FilePath), ex.PasswordWasWrong);
                    if (password == null || file == null) throw;
                    file.Password = password;
                }
            }
            // An open document's working copy is private; its real file is already in Recent.
            if (Context == null)
            {
                AppServices.Settings.AddRecent(Files.Select(f => f.Path));
                AppServices.SaveSettings();
            }
            if (AppServices.Settings.OpenFolderAfterSaving && Context?.EditsDocument != true && Result?.OutputFiles.Count > 0)
                AppServices.ShowInFolder(Result.OutputFiles[0]);
            Status = $"Done in {sw.Elapsed.TotalSeconds:0.#} s";
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
        }
        catch (OfflineException ex)
        {
            IsOffline = true;
            Error = ex.Message;
        }
        catch (PasswordRequiredException ex)
        {
            Error = ex.Message;
        }
        catch (UserFacingException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = $"Something went wrong: {ex.Message}";
            AppServices.LogError(ex);
        }
        finally
        {
            if (Error != null) Status = null;
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void StartOver()
    {
        Result = null;
        Error = null;
        Status = null;
        Progress = 0;
        Files.Clear();
        Reset();
    }

    [RelayCommand]
    private void BackToOptions()
    {
        Result = null;
        Status = null;
    }

    [RelayCommand]
    private static void OpenNetworkSettings() => AppServices.OpenUri("ms-settings:network-status");

    /// <summary>Clears tool-specific state when starting over.</summary>
    protected virtual void Reset() { }

    /// <summary>Adds files the tool accepts; loads page counts and thumbnails in the background.</summary>
    public async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var accepted = paths.Where(p => File.Exists(p) && Descriptor.Accepts(p)).ToList();
        var rejected = paths.Count() - accepted.Count;
        if (!Descriptor.MultipleFiles)
        {
            accepted = accepted.Take(1).ToList();
            if (accepted.Count > 0) Files.Clear();
        }
        Result = null;
        var added = new List<FileEntry>();
        foreach (var path in accepted)
        {
            if (Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var entry = new FileEntry(path) { Password = KnownPasswords.GetValueOrDefault(path) };
            Files.Add(entry);
            added.Add(entry);
        }
        if (rejected > 0)
            AppServices.Notify("Some files were skipped", $"{Descriptor.Title} accepts {string.Join(", ", Descriptor.Extensions.Take(8))} files.", Wpf.Ui.Controls.ControlAppearance.Caution);
        foreach (var entry in added) await LoadDetailsAsync(entry);
        await OnFilesAddedAsync(added);
    }

    protected virtual Task OnFilesAddedAsync(IReadOnlyList<FileEntry> added) => Task.CompletedTask;

    public async Task LoadDetailsAsync(FileEntry entry)
    {
        if (entry.IsPdf)
        {
            try
            {
                entry.PageCount = await Task.Run(() => PdfDocs.CountPages(entry.ToInput()));
                entry.NeedsPassword = false;
            }
            catch (PasswordRequiredException)
            {
                entry.NeedsPassword = true;
                entry.Note = "Password protected — click the lock to unlock";
                return;
            }
            catch (UserFacingException ex)
            {
                entry.Note = ex.Message;
            }
            entry.Thumbnail = await AppServices.Thumbnails.GetAsync(entry.Path, 0, 160, entry.Password);
        }
        else if (Core.Imaging.ImageIO.IsSupported(entry.Path))
        {
            entry.Thumbnail = await AppServices.Thumbnails.GetImageAsync(entry.Path, 160);
        }
    }

    /// <summary>Folder for outputs, per the user's output setting (or the open document's, see <see cref="Context"/>).</summary>
    protected string OutputDirectory(string? firstInput = null)
    {
        var dir = Context?.OutputFolder ?? AppServices.OutputDirectoryFor(firstInput ?? Files.FirstOrDefault()?.Path);
        Directory.CreateDirectory(dir);
        return dir;
    }

    protected string OutputFile(string input, string suffix, string extension) =>
        OutputPaths.ForInput(input, suffix, extension, OutputDirectory(input));
}
