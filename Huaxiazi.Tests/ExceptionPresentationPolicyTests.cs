using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class ExceptionPresentationPolicyTests
{
    [Theory]
    [InlineData(UnhandledExceptionOrigin.UiDispatcher, false)]
    [InlineData(UnhandledExceptionOrigin.UnobservedTask, false)]
    [InlineData(UnhandledExceptionOrigin.AppDomain, true)]
    public void ShouldShowFatalDialog_OnlyProcessLevelFailuresAreModal(
        UnhandledExceptionOrigin origin,
        bool expected)
    {
        Assert.Equal(expected, ExceptionPresentationPolicy.ShouldShowFatalDialog(origin));
    }
}
