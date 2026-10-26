using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using PageSmith.App.Infrastructure;
using PageSmith.Core.Common;
using PageSmith.Core.Pdf;
using PageSmith.Core.Rendering;
using PageSmith.Core.Text;

namespace PageSmith.App.Documents;

/// <summary>
/// A PDF open in a tab. PageSmith works on private copies in a working folder: every change made with a
/// tool becomes a new version, so changes can be undone and the original file is only touched on Save.
/// </summary>
public sealed partial class DocumentSession : ObservableObject, IDisposable
{
    /// <summary>One version of the document. Each keeps its own password, since Protect and Unlock change it.</summary>
    private sealed record Version(string Path, string Description, string? Password);

    private readonly List<Version> _versions = [];
    private int _current = -1;
    private int _saved = -1;
    private int _outputs;
    private Task<TextIndex?>? _text;

    private DocumentSession(string title, string? filePath)
    {
        Title = title;
        FilePath = filePath;
        WorkFolder = Path.Combine(Path.GetTempPath(), "PageSmith", "Documents", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(WorkFolder);
    }

    /// <summary>The private folder holding this document's versions and tool results.</summary>
    public string WorkFolder { get; }

    /// <summary>Where the document is saved; null for a new document (made from images or Office files).</summary>
    [ObservableProperty] public partial string? FilePath { get; private set; }

    /// <summary>File name shown on the tab, e.g. "Report.pdf".</summary>
    [ObservableProperty] public partial string Title { get; private set; }

    /// <summary>Renders the current version; replaced whenever the version changes.</summary>
    [ObservableProperty] public partial PdfRenderer? Renderer { get; private set; }

    [ObservableProperty] public partial IReadOnlyList<Size> PageSizes { get; private set; } = [];

    /// <summary>The current version's open password, if it has one.</summary>
    public string? Password => _versions[_current].Password;

    public string CurrentPath => _versions[_current].Path;
    public InputFile CurrentInput => new(CurrentPath, Password);
    public int PageCount => PageSizes.Count;
    public bool IsDirty => _current != _saved;
    public bool CanUndo => _current > 0;
    public bool CanRedo => _current < _versions.Count - 1;
    public string? UndoDescription => CanUndo ? _versions[_current].Description : null;
    public string? RedoDescription => CanRedo ? _versions[_current + 1].Description : null;
    public string TabToolTip => FilePath ?? $"{Title} (not saved yet)";

    /// <summary>Where export tools put their files: next to the document, or the PageSmith folder for new ones.</summary>
    public string OutputFolder => FilePath != null && Path.GetDirectoryName(FilePath) is { } dir && CanWrite(dir) ? dir : AppServices.Settings.OutputFolder;

    /// <summary>Raised after the current version changed (a tool's result, undo or redo).</summary>
    public event EventHandler? VersionChanged;

    /// <summary>
    /// Opens a PDF. <paramref name="filePath"/> is where it saves (null for a new document, which is then
    /// unsaved from the start). Throws <see cref="PasswordRequiredException"/> when the password is missing or wrong.
    /// </summary>
    public static async Task<DocumentSession> OpenAsync(string pdf, string? filePath, string title, string? password)
    {
        var session = new DocumentSession(title, filePath);
        try
        {
            session.AddVersion(pdf, "Opened", password);
            await session.LoadCurrentAsync();
            if (filePath != null) session._saved = session._current;
            return session;
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// <summary>A new, empty folder in the working folder for a tool's results.</summary>
    public string NewOutputFolder()
    {
        var folder = Path.Combine(WorkFolder, "out", (++_outputs).ToString());
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Copies a file into the working folder as the next version (dropping any undone versions).</summary>
    private void AddVersion(string pdf, string description, string? password)
    {
        for (var i = _versions.Count - 1; i > _current; i--)
        {
            TryDelete(Path.GetDirectoryName(_versions[i].Path)!);
            _versions.RemoveAt(i);
            if (_saved == i) _saved = -1;
        }
        // Each version keeps the document's own file name, so tools name their results after it.
        var folder = Path.Combine(WorkFolder, $"v{_versions.Count}");
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Title.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? Title : Title + ".pdf");
        File.Copy(pdf, copy, overwrite: true);
        _versions.Add(new Version(copy, description, password));
        _current = _versions.Count - 1;
    }

    private async Task LoadCurrentAsync()
    {
        var renderer = await PdfRenderer.OpenAsync(CurrentPath, Password);
        var sizes = new Size[renderer.PageCount];
        for (var i = 0; i < sizes.Length; i++) sizes[i] = renderer.GetPageSize(i);
        var old = Renderer;
        Renderer = renderer;
        PageSizes = sizes;
        old?.Dispose();
        DropText();
        RaiseStateChanged();
        VersionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The current version's text, for find and selection, read in the background when first asked for.
    /// The result is null when the text can't be read at all.
    /// </summary>
    public Task<TextIndex?> GetTextAsync()
    {
        if (_text != null) return _text;
        var input = CurrentInput;
        return _text = Task.Run(() =>
        {
            try
            {
                return TextIndex.Open(input);
            }
            catch (Exception ex)
            {
                AppServices.LogError(ex);
                return (TextIndex?)null;
            }
        });
    }

    private void DropText()
    {
        var old = _text;
        _text = null;
        old?.ContinueWith(t => t.Result?.Dispose(), TaskScheduler.Default);
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoDescription));
        OnPropertyChanged(nameof(RedoDescription));
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(TabToolTip));
        OnPropertyChanged(nameof(Password));
    }

    /// <summary>
    /// Makes a tool's result the current version. A result without encryption needs no password (Unlock);
    /// one that was just protected opens with <paramref name="newPassword"/>, else <paramref name="askPassword"/>
    /// asks. Returns false when that was cancelled; the change is then dropped.
    /// </summary>
    public async Task<bool> CommitAsync(string pdf, string description, Func<bool, Task<string?>> askPassword, string? newPassword = null)
    {
        var previous = _current;
        var password = DocumentProperties.HasEncryptEntry(pdf) ? (string.IsNullOrEmpty(newPassword) ? Password : newPassword) : null;
        AddVersion(pdf, description, password);
        var wrong = false;
        while (true)
        {
            try
            {
                await LoadCurrentAsync();
                return true;
            }
            catch (PasswordRequiredException)
            {
                var typed = await askPassword(wrong);
                if (typed == null)
                {
                    // Keep the change out of the history rather than show a document that can't be opened.
                    TryDelete(Path.GetDirectoryName(_versions[_current].Path)!);
                    _versions.RemoveAt(_current);
                    _current = previous;
                    await LoadCurrentAsync();
                    return false;
                }
                _versions[_current] = _versions[_current] with { Password = typed };
                wrong = true;
            }
        }
    }

    public async Task UndoAsync()
    {
        if (!CanUndo) return;
        _current--;
        await LoadCurrentAsync();
    }

    public async Task RedoAsync()
    {
        if (!CanRedo) return;
        _current++;
        await LoadCurrentAsync();
    }

    /// <summary>Writes the current version to <paramref name="path"/>, which becomes the document's file.</summary>
    public async Task SaveToAsync(string path)
    {
        var full = Path.GetFullPath(path);
        var source = CurrentPath;
        await Task.Run(() =>
        {
            // Write next to the target first, so a failure never leaves a half-written document.
            var temp = full + ".pagesmith-saving";
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, full, overwrite: true);
        });
        FilePath = full;
        Title = Path.GetFileName(full);
        _saved = _current;
        RaiseStateChanged();
        AppServices.Settings.AddRecent([full]);
        AppServices.SaveSettings();
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".pagesmith_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch
        {
            // A working file still in use is cleaned up by Windows' temp cleanup later.
        }
    }

    public void Dispose()
    {
        Renderer?.Dispose();
        Renderer = null;
        DropText();
        TryDelete(WorkFolder);
    }
}
