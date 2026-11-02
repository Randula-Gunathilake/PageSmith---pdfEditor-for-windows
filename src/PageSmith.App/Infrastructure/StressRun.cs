using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using PageSmith.App.Documents;

namespace PageSmith.App.Infrastructure;

/// <summary>
/// Development aid for --snapshot --stress: drives the reader through common work on a document and
/// records, for each step, how long it took and the longest time the window stopped responding (the
/// UI thread busy). Windows shows "Not responding" after about five seconds of that.
/// </summary>
internal sealed class StressRun(MainWindow window, string reportPath)
{
    private readonly List<string> _lines = [];

    /// <summary>Measures how late the UI thread runs a tiny job queued every 20 ms.</summary>
    private sealed class StallMonitor : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Thread _thread;
        private volatile bool _stop;
        private long _maxStallMs;

        public StallMonitor(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _thread = new Thread(Watch) { IsBackground = true, Name = "Stall monitor" };
            _thread.Start();
        }

        public long TakeMaxStall() => Interlocked.Exchange(ref _maxStallMs, 0);

        private void Watch()
        {
            while (!_stop)
            {
                var sw = Stopwatch.StartNew();
                var op = _dispatcher.BeginInvoke(DispatcherPriority.Input, () => { });
                op.Wait(TimeSpan.FromSeconds(30));
                var waited = sw.ElapsedMilliseconds;
                long current;
                while (waited > (current = Interlocked.Read(ref _maxStallMs)) && Interlocked.CompareExchange(ref _maxStallMs, waited, current) != current) { }
                Thread.Sleep(20);
            }
        }

        public void Dispose() => _stop = true;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    public async Task RunAsync(IReadOnlyList<string> files)
    {
        using var monitor = new StallMonitor(window.Dispatcher);
        var process = Process.GetCurrentProcess();
        async Task Step(string name, Func<Task> action)
        {
            monitor.TakeMaxStall();
            var cpu = process.TotalProcessorTime;
            var sw = Stopwatch.StartNew();
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                _lines.Add($"{name}: FAILED {ex.GetType().Name}: {ex.Message}");
            }
            // Let queued rendering finish so its cost counts for this step.
            await Task.Delay(400);
            process.Refresh();
            _lines.Add($"{name,-28} {sw.ElapsedMilliseconds,6} ms   longest freeze {monitor.TakeMaxStall(),5} ms   CPU {(process.TotalProcessorTime - cpu).TotalMilliseconds,6:0} ms   memory {process.PrivateMemorySize64 / 1048576,4} MB");
        }

        var uiThread = GetCurrentThreadId();
        Dictionary<int, TimeSpan> ThreadTimes()
        {
            process.Refresh();
            var times = new Dictionary<int, TimeSpan>();
            foreach (ProcessThread t in process.Threads)
                try { times[t.Id] = t.TotalProcessorTime; } catch { /* exited */ }
            return times;
        }
        // Which threads the CPU went to in a step: the UI thread, and the busiest others.
        void Threads(string name, Dictionary<int, TimeSpan> before)
        {
            var after = ThreadTimes();
            var used = after.Select(kv => (kv.Key, Ms: (kv.Value - before.GetValueOrDefault(kv.Key)).TotalMilliseconds))
                .OrderByDescending(t => t.Ms).Take(6)
                .Select(t => $"{(t.Key == uiThread ? "UI" : t.Key.ToString())}={t.Ms:0}");
            _lines.Add($"  {name} threads (ms): {string.Join("  ", used)}");
        }

        var workspace = AppServices.Documents;
        await Step("open", () => workspace.OpenAsync(files));
        var view = window.ActiveView ?? throw new InvalidOperationException("No document opened.");
        var viewer = view.PageView;
        await Step("first pages drawn", () => Task.Delay(1500));
        var threads = ThreadTimes();
        double slowestCall = 0, slowestWait = 0;
        await Step("scroll 40 pages", async () =>
        {
            for (var i = 1; i <= 40; i++)
            {
                var sw = Stopwatch.StartNew();
                viewer.GoToPage(i);
                slowestCall = Math.Max(slowestCall, sw.Elapsed.TotalMilliseconds);
                sw.Restart();
                await Task.Delay(30);
                slowestWait = Math.Max(slowestWait, sw.Elapsed.TotalMilliseconds);
            }
        });
        _lines.Add($"  slowest GoToPage {slowestCall:0} ms, slowest 30 ms delay {slowestWait:0} ms");
        Threads("scroll", threads);
        await Step("thumbnails follow scrolling", async () =>
        {
            viewer.GoToPage(0);
            await Task.Delay(600);
            var list = view.Thumbs;
            if (Descendant<System.Windows.Controls.ScrollViewer>(list) is not { } thumbs)
            {
                _lines.Add("  thumbnails are hidden");
                return;
            }
            // Scrolling the pages down steadily should only ever move the thumbnails down too.
            int reversals = 0, direction = 0, jumps = 0;
            var last = thumbs.VerticalOffset;
            for (var i = 0; i < 160; i++)
            {
                viewer.ScrollBy(60);
                await Task.Delay(16);
                var now = thumbs.VerticalOffset;
                var moved = Math.Sign(Math.Round(now - last));
                if (moved != 0)
                {
                    if (direction != 0 && moved != direction) reversals++;
                    direction = moved;
                }
                if (Math.Abs(now - last) > thumbs.ViewportHeight) jumps++;
                last = now;
            }
            await Task.Delay(600);
            _lines.Add($"  thumbnails turned back {reversals} times, jumped {jumps} times; page {viewer.CurrentPage + 1}'s thumbnail is {(ThumbnailShown(list, thumbs, viewer.CurrentPage) ? "in view" : "OUT OF VIEW")}");

            // Picking a thumbnail far away goes to that page and keeps it picked.
            var target = Math.Min(viewer.PageCount - 1, viewer.CurrentPage + 37);
            list.SelectedItem = list.Items[target];
            await Task.Delay(600);
            _lines.Add($"  picked page {target + 1}: viewer shows page {viewer.CurrentPage + 1}, thumbnail {(ThumbnailShown(list, thumbs, target) ? "in view" : "OUT OF VIEW")}");
        });
        await Step("jump to the end", async () =>
        {
            viewer.GoToPage(viewer.PageCount - 1);
            await Task.Delay(800);
        });
        await Step("pinch zoom in and out", async () =>
        {
            for (var i = 0; i < 30; i++)
            {
                viewer.SetZoom(viewer.Zoom * (i < 15 ? 1.06 : 1 / 1.06));
                await Task.Delay(16);
            }
            viewer.SetMode(Controls.ZoomMode.FitWidth);
            await Task.Delay(800);
        });
        await Step("find in all pages", async () =>
        {
            view.Find("Americas");
            await Task.Delay(3000);
        });
        await Step("select all text", async () =>
        {
            viewer.SelectAll();
            await viewer.GetSelectedTextAsync();
        });
        await Step("rotate a page", () => PageActions.RotateAsync(view.Session, [0], 90));
        await Step("undo", () => view.Session.UndoAsync());
        await Step("open a tool on it", async () =>
        {
            await view.OpenToolAsync("compress");
            await Task.Delay(1000);
            await view.CloseToolAsync();
        });
        await Step("idle", () => Task.Delay(3000));
        await Step("close the tab", () => workspace.CloseAsync(view.Session));
        await Step("idle after closing", () => Task.Delay(3000));
        File.WriteAllLines(reportPath, _lines);
    }

    private static bool ThumbnailShown(System.Windows.Controls.ListBox list, System.Windows.Controls.ScrollViewer thumbs, int page)
    {
        if (list.ItemContainerGenerator.ContainerFromIndex(page) is not FrameworkElement item || !item.IsVisible) return false;
        var top = item.TranslatePoint(new Point(0, 0), thumbs).Y;
        return top >= -1 && top + item.ActualHeight <= thumbs.ViewportHeight + 1;
    }

    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (Descendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }
}
