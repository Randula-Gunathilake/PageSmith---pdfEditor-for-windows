using System.Windows;

namespace PageSmith.App.Infrastructure;

/// <summary>Where the main window was and how big, saved between runs. Values are in device-independent pixels.</summary>
public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool Maximized);

/// <summary>
/// Opens the main window at a size that suits the screen it is on, whatever its resolution and scaling,
/// and reopens it where the user left it.
/// </summary>
public static class WindowSizing
{
    /// <summary>Largest size used on big screens; beyond this the layout only gets emptier.</summary>
    public const double PreferredWidth = 1440, PreferredHeight = 940;

    /// <summary>Below this work area the window opens maximized: a smaller window would cramp the tools.</summary>
    public const double SmallScreenWidth = 1280, SmallScreenHeight = 800;

    /// <summary>Most of the work area, centred, capped at the preferred size; small screens get a maximized window.</summary>
    public static WindowPlacement Default(Rect workArea)
    {
        if (workArea.Width < SmallScreenWidth || workArea.Height < SmallScreenHeight)
            return new WindowPlacement(workArea.Left, workArea.Top, workArea.Width, workArea.Height, Maximized: true);
        var width = Math.Min(PreferredWidth, Math.Round(workArea.Width * 0.85));
        var height = Math.Min(PreferredHeight, Math.Round(workArea.Height * 0.9));
        return new WindowPlacement(
            Math.Round(workArea.Left + (workArea.Width - width) / 2),
            Math.Round(workArea.Top + (workArea.Height - height) / 2),
            width, height, Maximized: false);
    }

    /// <summary>
    /// A saved placement, shrunk to fit the screens there are now and moved back if its title bar would be off
    /// screen (after a monitor was unplugged or the resolution changed). Null when it can't be used at all.
    /// </summary>
    public static WindowPlacement? Fit(WindowPlacement saved, Rect virtualScreen, Rect workArea, double minWidth, double minHeight)
    {
        if (saved.Width <= 0 || saved.Height <= 0 || double.IsNaN(saved.Left) || double.IsNaN(saved.Top)) return null;
        // On the main screen the window must fit its work area; elsewhere, at least within all screens together.
        // Where the title bar starts decides which screen the window is on.
        var anchor = new Point(saved.Left + Math.Min(60, saved.Width / 2), saved.Top + 16);
        var limit = workArea.Contains(anchor) ? workArea : virtualScreen;
        var width = Math.Max(Math.Min(minWidth, limit.Width), Math.Min(saved.Width, limit.Width));
        var height = Math.Max(Math.Min(minHeight, limit.Height), Math.Min(saved.Height, limit.Height));
        // The title bar must be reachable: at least 120 × 32 of its top strip on some screen.
        var titleBar = new Rect(saved.Left, saved.Top, width, 32);
        titleBar.Intersect(virtualScreen);
        if (titleBar.IsEmpty || titleBar.Width < 120 || titleBar.Height < 16) return null;
        // Keep the shrunk window inside the area it was limited to.
        var left = Math.Clamp(saved.Left, limit.Left, Math.Max(limit.Left, limit.Right - width));
        var top = Math.Clamp(saved.Top, limit.Top, Math.Max(limit.Top, limit.Bottom - height));
        return saved with { Left = left, Top = top, Width = width, Height = height };
    }

    /// <summary>Sizes and positions the window before it is shown.</summary>
    public static void Apply(Window window, WindowPlacement? saved)
    {
        var work = SystemParameters.WorkArea;
        var virtualScreen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // Never ask for more room than the screen has.
        window.MinWidth = Math.Min(window.MinWidth, work.Width);
        window.MinHeight = Math.Min(window.MinHeight, work.Height);

        var placement = (saved != null ? Fit(saved, virtualScreen, work, window.MinWidth, window.MinHeight) : null) ?? Default(work);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = placement.Left;
        window.Top = placement.Top;
        window.Width = placement.Width;
        window.Height = placement.Height;
        window.WindowState = placement.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    /// <summary>The window's normal (not maximized) bounds and whether it is maximized, for saving.</summary>
    public static WindowPlacement Capture(Window window)
    {
        var bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        return new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, window.WindowState == WindowState.Maximized);
    }
}
