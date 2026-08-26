using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PromptFloat.Views;
using Xunit;

namespace PromptFloat.Tests;

public sealed class ScrollBehaviorTests
{
    [Theory]
    [InlineData(0, 900, 300, 120, 0, false)]
    [InlineData(600, 900, 300, -120, 600, false)]
    [InlineData(300, 900, 300, 120, 284, true)]
    public void ScrollOffsetCalculation_IdentifiesBoundaryAndMovesWithinRange(
        double offset, double extent, double viewport, int delta, double expected, bool shouldMove)
    {
        var actual = NestedScrollBehavior.CalculateNextOffset(offset, extent, viewport, delta, 1);

        Assert.Equal(shouldMove, actual.HasValue);
        if (shouldMove) Assert.Equal(expected, actual!.Value, 3);
    }

    [Fact]
    public void ComboBox_ClosedForwardsWheelToParentScroller()
    {
        RunSta(() =>
        {
            var combo = new ComboBox();
            var content = new StackPanel();
            content.Children.Add(combo);
            content.Children.Add(new Border { Height = 1000 });
            var scroll = new ScrollViewer { Height = 100, Content = content };
            var window = new Window { Height = 150, Width = 200, Content = scroll };
            window.Show();
            window.UpdateLayout();

            ComboBoxScrollBehavior.SetBlockWheelOnClosed(combo, true);
            scroll.ScrollToVerticalOffset(300);
            PumpEvents();

            // 下拉关闭时，滚轮应转发给外层滚动容器并标记为已处理。
            var raised = RaiseWheel(combo, 120);
            PumpEvents();
            Assert.True(raised.Handled);
            var expectedStep = SystemParameters.WheelScrollLines * 16d;
            Assert.Equal(300 - expectedStep, scroll.VerticalOffset, 1);

            // 关闭行为后，事件处理器被移除，滚轮不再被拦截或转发。
            ComboBoxScrollBehavior.SetBlockWheelOnClosed(combo, false);
            var raisedAfter = RaiseWheel(combo, 120);
            Assert.False(raisedAfter.Handled);
            window.Close();
        });
    }

    [Fact]
    public void NestedWheel_UsesSystemLineScaleInsteadOfRawPixelDelta()
    {
        RunSta(() =>
        {
            var nested = new Border { Height = 20 };
            NestedScrollBehavior.SetForwardMouseWheelToParent(nested, true);
            var content = new StackPanel { Height = 1000 };
            content.Children.Add(nested);
            var scroll = new ScrollViewer { Height = 100, Content = content };
            var window = new Window { Height = 150, Width = 200, Content = scroll };
            window.Show();
            window.UpdateLayout();
            scroll.ScrollToVerticalOffset(300);
            PumpEvents();

            RaiseWheel(nested, 120);
            PumpEvents();

            var expectedStep = SystemParameters.WheelScrollLines * 16d;
            Assert.Equal(300 - expectedStep, scroll.VerticalOffset, 1);
            window.Close();
        });
    }

    private static MouseWheelEventArgs RaiseWheel(UIElement element, int delta)
    {
        var args = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = element
        };
        element.RaiseEvent(args);
        return args;
    }

    private static void PumpEvents()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                TestHelpers.EnsureWpfApplication();
                action();
            }
            catch (Exception exception) { failure = exception; }
            finally { TestHelpers.ResetWpfApplication(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
