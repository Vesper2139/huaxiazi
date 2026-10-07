using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class StartupWindowPolicyTests
{
    [Fact]
    public void ManualLaunch_ShowsMainWindowByDefault()
    {
        Assert.True(StartupWindowPolicy.ShouldShowMainWindow([]));
    }

    [Fact]
    public void BackgroundLaunch_KeepsTheMainWindowHidden()
    {
        Assert.False(StartupWindowPolicy.ShouldShowMainWindow(["--background"]));
    }

    [Fact]
    public void ExplicitShowMain_OverridesBackgroundLaunch()
    {
        Assert.True(StartupWindowPolicy.ShouldShowMainWindow(["--background", "--show-main"]));
    }

    [Fact]
    public void WindowsStartupCommand_UsesBackgroundMode()
    {
        Assert.Equal("\"C:\\Program Files\\Huaxiazi\\Huaxiazi.exe\" --background",
            StartupService.BuildBackgroundLaunchCommand("C:\\Program Files\\Huaxiazi\\Huaxiazi.exe"));
    }
}
