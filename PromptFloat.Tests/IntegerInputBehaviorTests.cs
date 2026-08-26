using System;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using PromptFloat.Views;
using Xunit;

namespace PromptFloat.Tests;

public sealed class IntegerInputBehaviorTests
{
    [Fact]
    public void OutOfRangeDraft_ShowsImmediateAccessibleFeedbackWithoutChangingSource()
    {
        RunSta(() =>
        {
            var source = new IntegerSource { Value = 10 };
            var box = BoundBox(source, 1, 100);
            var window = new Window { Content = box };
            window.Show();
            box.Focus();

            box.Text = "999";
            PumpEvents();

            Assert.Contains("1–100", AutomationProperties.GetHelpText(box));
            Assert.Equal(10, source.Value);
            window.Close();
        });
    }

    [Fact]
    public void EmptyDraft_OnBlurCommitsMinimumInsteadOfRestoringTheOldValue()
    {
        RunSta(() =>
        {
            var source = new IntegerSource { Value = 10 };
            var box = BoundBox(source, 1, 100);
            var next = new Button { Content = "next" };
            var panel = new StackPanel();
            panel.Children.Add(box);
            panel.Children.Add(next);
            var window = new Window { Content = panel };
            window.Show();
            box.Focus();
            box.Text = string.Empty;

            box.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
            PumpEvents();

            Assert.Equal("1", box.Text);
            Assert.Equal(1, source.Value);
            window.Close();
        });
    }

    private static TextBox BoundBox(IntegerSource source, int minimum, int maximum)
    {
        var box = new TextBox();
        box.SetBinding(TextBox.TextProperty, new Binding(nameof(IntegerSource.Value))
        {
            Source = source,
            Mode = BindingMode.TwoWay,
            UpdateSourceTrigger = UpdateSourceTrigger.LostFocus
        });
        IntegerInputBehavior.SetMinimum(box, minimum);
        IntegerInputBehavior.SetMaximum(box, maximum);
        return box;
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

    private sealed class IntegerSource
    {
        public int Value { get; set; }
    }
}
