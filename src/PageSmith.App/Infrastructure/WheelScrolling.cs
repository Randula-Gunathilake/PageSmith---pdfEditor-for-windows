using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PageSmith.App.Infrastructure;

/// <summary>
/// Mouse wheel and touchpad scrolling for every scroll view in the app. It fixes two WPF behaviours:
/// <list type="bullet">
/// <item>WPF scrolls three lines per wheel message whatever its size, and a touchpad sends many small
/// messages, so pages raced away under two fingers. Here a message scrolls in proportion to its delta
/// (a mouse notch, 120, still scrolls three lines).</item>
/// <item>A scroll view that can't move (one as tall as its content, nested in another) still swallowed
/// every wheel message, so the page around it never scrolled: Home, Settings and Workflows sit in the
/// navigation view's scroll view like that. Here the message goes on to the scroll view around it.</item>
/// </list>
/// </summary>
public static class WheelScrolling
{
    /// <summary>WPF's own scroll distance for a line.</summary>
    private const double LinePixels = 16;

    private sealed class State
    {
        /// <summary>Offset asked for but not applied yet: scroll views move at their next layout pass.</summary>
        public double? Target;

        /// <summary>Wheel delta not yet turned into whole lines (lists that scroll by item).</summary>
        public int Remainder;

        /// <summary>Whether offsets are pixels; worked out on first use.</summary>
        public bool? ByPixel;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();

    public static void Register()
    {
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel));
        EventManager.RegisterClassHandler(typeof(ScrollViewer), ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Scroll events bubble up from nested scroll views; only this view's own movement counts.
        if (ReferenceEquals(e.OriginalSource, sender) && States.TryGetValue((ScrollViewer)sender, out var state)) state.Target = null;
    }

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ctrl and Shift combinations (zoom, sideways) stay with the controls that use them.
        if (e.Handled || sender is not ScrollViewer view || Keyboard.Modifiers != ModifierKeys.None) return;
        var state = States.GetOrCreateValue(view);
        if (!CanScroll(view, state, e.Delta))
        {
            PassToParent(view, e);
            return;
        }
        var lines = SystemParameters.WheelScrollLines;
        state.ByPixel ??= ScrollsByPixel(view);
        if (state.ByPixel.Value)
        {
            var notch = lines > 0 ? lines * LinePixels : view.ViewportHeight;
            var from = state.Target ?? view.VerticalOffset;
            var to = Math.Clamp(from - e.Delta / 120.0 * notch, 0, view.ScrollableHeight);
            state.Target = to;
            view.ScrollToVerticalOffset(to);
        }
        else
        {
            state.Remainder += e.Delta;
            var step = 120 / Math.Max(1, lines);
            for (; state.Remainder >= step; state.Remainder -= step) view.LineUp();
            for (; state.Remainder <= -step; state.Remainder += step) view.LineDown();
        }
        e.Handled = true;
    }

    /// <summary>Whether the view can move the way the wheel turned (up for a positive delta).</summary>
    private static bool CanScroll(ScrollViewer view, State state, int delta)
    {
        if (view.ScrollableHeight <= 0) return false;
        var offset = state.Target ?? view.VerticalOffset;
        return delta > 0 ? offset > 0.5 : offset < view.ScrollableHeight - 0.5;
    }

    /// <summary>Sends the wheel message on to the nearest element around the view, where an outer scroll view can take it.</summary>
    private static void PassToParent(ScrollViewer view, MouseWheelEventArgs e)
    {
        var parent = VisualTreeHelper.GetParent(view);
        while (parent != null && parent is not UIElement) parent = VisualTreeHelper.GetParent(parent);
        // Marked as handled here either way, so WPF's own handling doesn't swallow it.
        e.Handled = true;
        if (parent is UIElement element)
            element.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = view });
    }

    /// <summary>
    /// Whether offsets are pixels rather than items. A list whose items sit in a stack panel scrolls by
    /// item unless it virtualizes by pixel; everything else (pages, panels, wrapping lists) by pixel.
    /// </summary>
    private static bool ScrollsByPixel(ScrollViewer view)
    {
        if (!view.CanContentScroll || view.Content is not ItemsPresenter presenter) return true;
        return FindItemsHost(presenter, 0) switch
        {
            VirtualizingStackPanel host => ItemsControl.GetItemsOwner(host) is { } owner && VirtualizingPanel.GetScrollUnit(owner) == ScrollUnit.Pixel,
            StackPanel => false,
            _ => true,
        };
    }

    private static Panel? FindItemsHost(DependencyObject parent, int depth)
    {
        if (depth > 4) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Panel { IsItemsHost: true } host) return host;
            if (FindItemsHost(child, depth + 1) is { } found) return found;
        }
        return null;
    }
}
