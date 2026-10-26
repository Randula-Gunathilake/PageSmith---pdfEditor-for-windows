using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PageSmith.Core.Workflows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace PageSmith.App.Infrastructure;

/// <summary>App-wide services: settings, navigation, dialogs, notifications and shell helpers.</summary>
public static class AppServices
{
    public static AppSettings Settings { get; } = AppSettings.Load();
    public static ThumbnailCache Thumbnails { get; } = new();
    public static WorkflowStore Workflows { get; } = new(WorkflowStore.DefaultPath);

    /// <summary>Files waiting to be handed to a tool when its page opens (tool id → files).</summary>
    private static readonly Dictionary<string, List<string>> PendingFiles = [];

    internal static NavigationView? Navigation { get; set; }
    internal static SnackbarPresenter? Snackbars { get; set; }
    internal static ContentDialogHost? DialogHost { get; set; }
    internal static Window? Window { get; set; }

    /// <summary>A folder chosen with the "Ask each time" output setting, valid for the current run.</summary>
    private static string? _askedFolder;

    /// <summary>
    /// True for --snapshot runs (screenshots and automated checks). They must leave no trace in the user's
    /// settings: no theme, recent files or window position.
    /// </summary>
    public static bool IsSnapshotRun { get; set; }

    public static void SaveSettings()
    {
        if (IsSnapshotRun) return;
        try { Settings.Save(); } catch (Exception ex) { LogError(ex); }
    }

    public static void ApplyTheme()
    {
        if (Window == null) return;
        if (Settings.Theme == ThemePreference.System)
        {
            ApplicationThemeManager.ApplySystemTheme(true);
            SystemThemeWatcher.Watch(Window, WindowBackdropType.Mica, true);
        }
        else
        {
            SystemThemeWatcher.UnWatch(Window);
            ApplicationThemeManager.Apply(Settings.Theme == ThemePreference.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, true);
        }
    }

    public static void NavigateTo(Type pageType) => Navigation?.Navigate(pageType);

    /// <summary>Opens a tool, optionally handing it files (e.g. "Continue with…" or files dropped on Home).</summary>
    public static void OpenTool(string toolId, IEnumerable<string>? files = null)
    {
        if (files != null) PendingFiles[toolId] = files.ToList();
        var page = ToolPages.TypeFor(toolId);
        if (Navigation?.Navigate(page) != true && Navigation != null)
            Navigation.NavigateWithHierarchy(page);
    }

    public static List<string>? TakePendingFiles(string toolId)
    {
        if (!PendingFiles.Remove(toolId, out var files)) return null;
        return files;
    }

    public static string OutputDirectoryFor(string? firstInput)
    {
        switch (Settings.Output)
        {
            case OutputLocation.Folder when !string.IsNullOrWhiteSpace(Settings.OutputFolder):
                return Settings.OutputFolder;
            case OutputLocation.Ask when _askedFolder != null:
                return _askedFolder;
        }
        var dir = firstInput != null ? Path.GetDirectoryName(Path.GetFullPath(firstInput)) : null;
        if (dir == null || !CanWrite(dir)) dir = Settings.OutputFolder;
        return dir;
    }

    public static bool AskOutputFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save the results", Multiselect = false };
        if (dialog.ShowDialog(Window) != true) return false;
        _askedFolder = dialog.FolderName;
        return true;
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

    public static string[] PickFiles(ToolDescriptor tool)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"{tool.Title}: choose {(tool.MultipleFiles ? "files" : "a file")}",
            Filter = tool.FileFilter,
            Multiselect = tool.MultipleFiles,
        };
        return dialog.ShowDialog(Window) == true ? dialog.FileNames : [];
    }

    public static string? PickFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        return dialog.ShowDialog(Window) == true ? dialog.FileName : null;
    }

    public static void Notify(string title, string message, ControlAppearance appearance = ControlAppearance.Secondary, SymbolRegular icon = SymbolRegular.Info24)
    {
        if (Snackbars == null) return;
        var snackbar = new Snackbar(Snackbars)
        {
            Title = title,
            Content = message,
            Appearance = appearance,
            Icon = new SymbolIcon(icon),
            Timeout = TimeSpan.FromSeconds(5),
        };
        _ = snackbar.ShowAsync();
    }

    public static async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        if (DialogHost == null) return ContentDialogResult.None;
        dialog.DialogHostEx = DialogHost;
        return await dialog.ShowAsync(CancellationToken.None);
    }

    public static async Task<bool> ConfirmAsync(string title, string message, string confirm = "OK", string cancel = "Cancel")
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
            PrimaryButtonText = confirm,
            CloseButtonText = cancel,
            DefaultButton = ContentDialogButton.Primary,
        };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>Asks for a document password; returns null when the user cancels.</summary>
    public static async Task<string?> AskPasswordAsync(string fileName, bool wrongBefore)
    {
        var box = new Wpf.Ui.Controls.PasswordBox { PlaceholderText = "Password", MinWidth = 320 };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = wrongBefore ? $"That password didn't work for \"{fileName}\". Try again." : $"\"{fileName}\" is protected. Enter its password to continue.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 380,
            Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = "Password required",
            Content = panel,
            PrimaryButtonText = "Unlock",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Loaded += (_, _) => box.Focus();
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? box.Password : null;
    }

    public static void OpenFile(string path) => OpenUri(path);

    public static void ShowInFolder(string path)
    {
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LogError(ex);
        }
    }

    public static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("Could not open", ex.Message, ControlAppearance.Danger, SymbolRegular.ErrorCircle24);
        }
    }

    public static string LogFile => Path.Combine(AppSettings.Folder, "pagesmith.log");

    public static void LogError(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.AppendAllText(LogFile, $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch
        {
            // Logging must never throw.
        }
    }
}
