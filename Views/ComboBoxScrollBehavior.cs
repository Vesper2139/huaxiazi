using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Huaxiazi.Views;

/// <summary>
/// 附加行为：ComboBox 下拉关闭时，把鼠标滚轮转发给最近的外层 ScrollViewer，
/// 使页面像普通软件一样正常滚动；下拉展开时则交回原生列表滚动。
/// 通过 GlassComboBox 样式全局启用（SetBlockWheelOnClosed="True"）。
/// </summary>
public static class ComboBoxScrollBehavior
{
    public static readonly DependencyProperty BlockWheelOnClosedProperty = DependencyProperty.RegisterAttached(
        "BlockWheelOnClosed", typeof(bool), typeof(ComboBoxScrollBehavior),
        new PropertyMetadata(false, OnBlockWheelOnClosedChanged));

    public static bool GetBlockWheelOnClosed(ComboBox element) =>
        (bool)element.GetValue(BlockWheelOnClosedProperty);

    public static void SetBlockWheelOnClosed(ComboBox element, bool value) =>
        element.SetValue(BlockWheelOnClosedProperty, value);

    private static void OnBlockWheelOnClosedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;
        if (e.OldValue is true) combo.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true) combo.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // 下拉展开时交回原生列表滚动；关闭时把滚轮转发给外层滚动容器。
        if (sender is not ComboBox { IsDropDownOpen: false } combo) return;

        var parent = FindParentScrollViewer(combo);
        if (parent is null || parent.ScrollableHeight <= 0) return;

        var lines = Math.Max(1, SystemParameters.WheelScrollLines);
        var step = e.Delta / 120d * lines * 16d;
        parent.ScrollToVerticalOffset(parent.VerticalOffset - step);
        e.Handled = true;
    }

    private static ScrollViewer? FindParentScrollViewer(DependencyObject source)
    {
        for (var current = VisualTreeHelper.GetParent(source); current is not null; current = VisualTreeHelper.GetParent(current))
            if (current is ScrollViewer viewer) return viewer;
        return null;
    }
}
