using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls;

namespace PromptFloat.Views;

/// <summary>
/// Gives a nested, non-scrolling selector one clear scroll owner. WPF ListBox handles
/// mouse-wheel input before the containing page can see it, even when its own content
/// does not scroll; this behavior deliberately forwards that input to the page scroller.
/// </summary>
public static class NestedScrollBehavior
{
    public static readonly DependencyProperty ForwardMouseWheelToParentProperty =
        DependencyProperty.RegisterAttached(
            "ForwardMouseWheelToParent",
            typeof(bool),
            typeof(NestedScrollBehavior),
            new PropertyMetadata(false, OnForwardMouseWheelToParentChanged));

    public static void SetForwardMouseWheelToParent(DependencyObject element, bool value) =>
        element.SetValue(ForwardMouseWheelToParentProperty, value);

    public static bool GetForwardMouseWheelToParent(DependencyObject element) =>
        (bool)element.GetValue(ForwardMouseWheelToParentProperty);

    private static void OnForwardMouseWheelToParentChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is not FrameworkElement element) return;
        if ((bool)args.OldValue)
        {
            element.PreviewMouseWheel -= ForwardMouseWheel;
            element.RequestBringIntoView -= KeepSelectionInsideNestedSurface;
        }
        if ((bool)args.NewValue)
        {
            element.PreviewMouseWheel += ForwardMouseWheel;
            element.RequestBringIntoView += KeepSelectionInsideNestedSurface;
        }
    }

    private static void ForwardMouseWheel(object sender, MouseWheelEventArgs args)
    {
        if (args.Handled || sender is not DependencyObject source) return;
        var current = FindOwnedScrollViewer(source);
        if (current is not null && TryScroll(current, args.Delta))
        {
            args.Handled = true;
            return;
        }

        // 内层滚动容器到达边界后，把同一滚轮动作交给外层容器，避免“滚到底就卡住”。
        var parent = current is null ? FindParentScrollViewer(source) : FindParentScrollViewer(current);
        if (parent is not null && TryScroll(parent, args.Delta)) args.Handled = true;
    }

    /// <summary>计算一次滚轮动作后的偏移；已在边界或内容不足以滚动时返回 null。</summary>
    public static double? CalculateNextOffset(double offset, double extent, double viewport, int delta, double lineScale)
    {
        if (delta == 0 || extent <= viewport) return null;
        var maximum = Math.Max(0, extent - viewport);
        var step = delta / 120d * Math.Max(1d, lineScale) * 16d;
        var next = Math.Clamp(offset - step, 0, maximum);
        return Math.Abs(next - offset) < 0.01 ? null : next;
    }

    private static bool TryScroll(ScrollViewer viewer, int delta)
    {
        var next = CalculateNextOffset(
            viewer.VerticalOffset,
            viewer.ExtentHeight,
            viewer.ViewportHeight,
            delta,
            SystemParameters.WheelScrollLines);
        if (!next.HasValue) return false;
        viewer.ScrollToVerticalOffset(next.Value);
        return true;
    }

    private static void KeepSelectionInsideNestedSurface(object sender, RequestBringIntoViewEventArgs args)
    {
        // Every preset is already materialized in the fixed three-column grid. Letting a
        // selected ListBoxItem bubble this request would make the outer settings page jump.
        if (!ReferenceEquals(sender, args.OriginalSource)) args.Handled = true;
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject source)
    {
        for (var current = VisualTreeHelper.GetParent(source); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ScrollViewer scrollViewer) return scrollViewer;
        return null;
    }

    private static ScrollViewer? FindOwnedScrollViewer(DependencyObject source)
    {
        if (source is ScrollViewer own) return own;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(source); index++)
        {
            var child = VisualTreeHelper.GetChild(source, index);
            if (child is ScrollViewer viewer) return viewer;
            var nested = FindOwnedScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
