using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Huaxiazi.Views;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class DecimalInputBehaviorTests
{
    [Theory]
    [InlineData("3.5", 0, 2)]
    [InlineData("letters", 0, 1)]
    public void InvalidDecimalDraft_ExposesImmediateRangeFeedback(string draft, double minimum, double maximum)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var box = new TextBox();
                DecimalInputBehavior.SetMinimum(box, minimum);
                DecimalInputBehavior.SetMaximum(box, maximum);

                box.Text = draft;

                Assert.True(DecimalInputBehavior.GetIsOutOfRange(box));
                Assert.Contains($"{minimum:0.##}–{maximum:0.##}", AutomationProperties.GetHelpText(box));
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(failure);
    }
}
