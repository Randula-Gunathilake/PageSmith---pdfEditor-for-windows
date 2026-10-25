using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PageSmith.App.Infrastructure;
using PageSmith.App.Pages;
using PageSmith.Core.Common;

namespace PageSmith.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        PdfDocs.EnsureInitialized();
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppServices.LogError(args.Exception);
            args.SetObserved();
        };

        var args = CommandLine.Parse(e.Args);
        if (args.Theme != null) AppServices.Settings.Theme = args.Theme.Value;
        if (args.Snapshot != null)
        {
            // Never leave a snapshot run hanging, even if something fails.
            var guard = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(args.DelayMs + 90_000) };
            guard.Tick += (_, _) => Environment.Exit(2);
            guard.Start();
        }

        var window = new MainWindow();
        MainWindow = window;
        if (args.Width > 0 && args.Height > 0)
        {
            window.Width = args.Width;
            window.Height = args.Height;
        }
        // A snapshot run must not take keyboard focus from whatever the user is doing.
        if (args.Snapshot != null) window.ShowActivated = false;
        window.Show();

        window.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(300);
            if (args.Page != null) OpenPage(args.Page, args.Files);
            else if (args.Files.Count > 0) OpenFilesFromShell(args.Files);
            if (args.Snapshot != null) await SnapshotAsync(window, args);
        }, DispatcherPriority.ApplicationIdle);
    }

    private static void OpenPage(string page, IReadOnlyList<string> files)
    {
        switch (page)
        {
            case "home": AppServices.NavigateTo(typeof(HomePage)); break;
            case "settings": AppServices.NavigateTo(typeof(SettingsPage)); break;
            case "about": AppServices.NavigateTo(typeof(AboutPage)); break;
            case "workflows": AppServices.NavigateTo(typeof(WorkflowsPage)); break;
            default: AppServices.OpenTool(page, files.Count > 0 ? files : null); break;
        }
    }

    /// <summary>Files passed by "Open with" or a shortcut: open the obvious tool for them.</summary>
    private static void OpenFilesFromShell(IReadOnlyList<string> files)
    {
        var ext = Path.GetExtension(files[0]).ToLowerInvariant();
        var tool = ext switch
        {
            ".pdf" when files.Count > 1 => "merge",
            ".pdf" => "organize",
            _ when Core.Imaging.ImageIO.IsSupported(files[0]) => "jpg-to-pdf",
            _ => ToolRegistry.All.FirstOrDefault(t => t.Accepts(files[0]))?.Id,
        };
        if (tool != null) AppServices.OpenTool(tool, files);
    }

    /// <summary>Development aid: renders the window to a PNG (optionally after running the tool), then exits.</summary>
    private async Task SnapshotAsync(MainWindow window, CommandLine args)
    {
        await Task.Delay(args.DelayMs);
        if (args.Demo && FindToolPage(window)?.Controller is Tools.Editor.EditorController editor)
        {
            await editor.SeedDemoAsync();
            if (args.DemoDialog) editor.ShowDemoDialog();
            await Task.Delay(1500);
        }
        if (args.Run)
        {
            var page = FindToolPage(window);
            if (page != null)
            {
                await page.Controller.RunCommand.ExecuteAsync(null);
                await Task.Delay(800);
            }
        }
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap(
            (int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        // Mica is drawn by Windows, not WPF: paint the theme background so the snapshot is opaque.
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)FindResource("ApplicationBackgroundBrush"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args.Snapshot!))!);
        await using (var fs = File.Create(args.Snapshot!)) encoder.Save(fs);
        // A snapshot run is a throwaway process: exit at once rather than wait for background work to wind down.
        Environment.Exit(0);
    }

    private static ToolPage? FindToolPage(MainWindow window)
    {
        return window.PageProvider.Created.OfType<ToolPage>().FirstOrDefault(p => p.IsLoaded);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppServices.LogError(e.Exception);
        AppServices.Notify("Something went wrong", e.Exception.Message, Wpf.Ui.Controls.ControlAppearance.Danger, Wpf.Ui.Controls.SymbolRegular.ErrorCircle24);
        e.Handled = true;
    }
}

/// <summary>Command-line arguments: files to open, and developer options for screenshots.</summary>
public sealed class CommandLine
{
    public List<string> Files { get; } = [];
    public string? Page { get; private set; }
    public string? Snapshot { get; private set; }
    public ThemePreference? Theme { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int DelayMs { get; private set; } = 2500;
    public bool Run { get; private set; }
    public bool Demo { get; private set; }
    public bool DemoDialog { get; private set; }

    public static CommandLine Parse(string[] args)
    {
        var c = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i].ToLowerInvariant())
            {
                case "--tool" or "--page": c.Page = Next(); break;
                case "--snapshot": c.Snapshot = Next(); break;
                case "--theme": c.Theme = Enum.TryParse<ThemePreference>(Next(), true, out var t) ? t : null; break;
                case "--width": c.Width = int.TryParse(Next(), out var w) ? w : 0; break;
                case "--height": c.Height = int.TryParse(Next(), out var h) ? h : 0; break;
                case "--delay": c.DelayMs = int.TryParse(Next(), out var d) ? d : c.DelayMs; break;
                case "--run": c.Run = true; break;
                case "--demo": c.Demo = true; break;
                case "--demo-dialog": c.Demo = c.DemoDialog = true; break;
                case "--files": c.Files.AddRange(Next().Split(';', StringSplitOptions.RemoveEmptyEntries)); break;
                default:
                    if (File.Exists(args[i])) c.Files.Add(Path.GetFullPath(args[i]));
                    break;
            }
        }
        return c;
    }
}
