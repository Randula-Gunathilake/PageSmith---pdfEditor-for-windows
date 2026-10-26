using System.Globalization;
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
    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = CommandLine.Parse(e.Args);
        AppServices.IsSnapshotRun = args.Snapshot != null;
        // One window per user: starting PageSmith again (a PDF opened from Explorer) hands the files to it.
        // Snapshot runs are separate throwaway processes.
        if (!AppServices.IsSnapshotRun)
        {
            _instance = SingleInstance.TryClaim();
            if (_instance == null && SingleInstance.Forward(args.Files))
            {
                Shutdown();
                return;
            }
        }

        PdfDocs.EnsureInitialized();
        DispatcherUnhandledException += OnUnhandled;
        TaskScheduler.UnobservedTaskException += (_, unobserved) =>
        {
            AppServices.LogError(unobserved.Exception);
            unobserved.SetObserved();
        };
        if (args.Theme != null) AppServices.Settings.Theme = args.Theme.Value;
        if (args.Panel != null) AppServices.Settings.ReaderPanel = args.Panel;
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
            window.WindowState = WindowState.Normal;
            window.Width = args.Width;
            window.Height = args.Height;
        }
        // A snapshot run must not take keyboard focus from whatever the user is doing.
        if (args.Snapshot != null) window.ShowActivated = false;
        window.Show();
        _instance?.Listen(files => window.Dispatcher.InvokeAsync(async () =>
        {
            BringToFront(window);
            if (files.Count > 0) await AppServices.Documents.OpenAsync(files);
        }), AppServices.LogError);

        window.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(300);
            if (args.Page != null) OpenPage(args.Page, args.Files);
            else if (args.Files.Count > 0) await AppServices.Documents.OpenAsync(args.Files);
            if (args.Snapshot != null) await SnapshotAsync(window, args);
        }, DispatcherPriority.ApplicationIdle);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static void BringToFront(Window window)
    {
        // Restoring goes back to maximized if the window was maximized before it was minimized.
        if (window.WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(window);
        window.Activate();
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
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

    /// <summary>Development aid: renders the window to a PNG (optionally after running the tool), then exits.</summary>
    private async Task SnapshotAsync(MainWindow window, CommandLine args)
    {
        await Task.Delay(args.DelayMs);
        if (window.ActiveView is { } reader)
        {
            // Reader checks: go to a page, select text, search, or open a tool on the document.
            if (args.GoTo > 0) reader.PageView.GoToPage(args.GoTo - 1);
            if (args.SelectAll) reader.PageView.SelectAll();
            if (args.Drag is { Length: 4 } drag)
            {
                await reader.Session.GetTextAsync();
                reader.PageView.SimulateDrag(new Point(drag[0], drag[1]), new Point(drag[2], drag[3]));
            }
            if (args.Properties) _ = reader.ShowPropertiesAsync();
            if (args.Find != null)
            {
                reader.Find(args.Find);
                await Task.Delay(1500);
            }
            if (args.DocumentTool != null)
            {
                await reader.OpenToolAsync(args.DocumentTool);
                await Task.Delay(1500);
            }
            await Task.Delay(500);
        }
        if (args.Demo && FindTool(window) is { } demo)
        {
            await demo.SeedDemoAsync();
            if (args.DemoAlternative) demo.ShowDemoAlternative();
            await Task.Delay(args.DemoAlternative ? 4000 : 1500);
        }
        if (args.Run && FindTool(window) is { } tool)
        {
            await tool.RunCommand.ExecuteAsync(null);
            await Task.Delay(1500);
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

    /// <summary>The tool on screen: one running on the document in front, else the tool page shown on Home.</summary>
    private static Tools.ToolController? FindTool(MainWindow window) =>
        window.ActiveView != null
            ? window.ActiveView.ActiveTool
            : window.PageProvider.Created.OfType<ToolPage>().FirstOrDefault(p => p.IsLoaded)?.Controller;

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
    public bool DemoAlternative { get; private set; }
    public string? Find { get; private set; }
    public string? DocumentTool { get; private set; }
    public int GoTo { get; private set; }
    public bool SelectAll { get; private set; }

    /// <summary>The reader's side panel for this run ("pages", "bookmarks" or "none").</summary>
    public string? Panel { get; private set; }

    /// <summary>A mouse drag in the reader, "x1,y1,x2,y2" in the page view's coordinates.</summary>
    public double[]? Drag { get; private set; }

    public bool Properties { get; private set; }

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
                case "--demo-alt": c.Demo = c.DemoAlternative = true; break;
                case "--files": c.Files.AddRange(Next().Split(';', StringSplitOptions.RemoveEmptyEntries)); break;
                case "--find": c.Find = Next(); break;
                case "--doc-tool": c.DocumentTool = Next(); break;
                case "--goto": c.GoTo = int.TryParse(Next(), out var g) ? g : 0; break;
                case "--select-all": c.SelectAll = true; break;
                case "--panel": c.Panel = Next(); break;
                case "--drag": c.Drag = Next().Split(',').Select(v => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0).ToArray(); break;
                case "--properties": c.Properties = true; break;
                default:
                    if (File.Exists(args[i])) c.Files.Add(Path.GetFullPath(args[i]));
                    break;
            }
        }
        return c;
    }
}
