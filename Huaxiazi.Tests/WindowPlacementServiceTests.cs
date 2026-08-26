using System.Windows;
using Huaxiazi.Services;
using Xunit;

namespace Huaxiazi.Tests;

public sealed class WindowPlacementServiceTests
{
    [Fact]
    public void ClampRect_BottomRightOverflow_KeepsSizeAndMovesInsideWorkArea()
    {
        var result = WindowPlacementService.ClampRect(
            new Rect(1900, 1000, 420, 650),
            new Rect(0, 0, 1920, 1040));

        Assert.Equal(new Rect(1500, 390, 420, 650), result);
    }

    [Fact]
    public void ClampRect_NegativeSecondaryMonitor_UsesItsVirtualCoordinates()
    {
        var result = WindowPlacementService.ClampRect(
            new Rect(-1400, -20, 420, 650),
            new Rect(-1280, 0, 1280, 1024));

        Assert.Equal(new Rect(-1280, 0, 420, 650), result);
    }

    [Fact]
    public void ExpansionPlacement_FromBottomRightBall_UsesAnInsideCandidateWithoutShrinking()
    {
        var result = WindowPlacementService.CalculateExpansionPlacement(
            new Rect(1868, 988, 40, 40),
            new Size(520, 176),
            new Rect(0, 0, 1920, 1040),
            safeMargin: 8);

        Assert.Equal(new Size(520, 176), result.Bounds.Size);
        Assert.True(new Rect(8, 8, 1904, 1024).Contains(result.Bounds));
        Assert.Equal(ExpansionDirection.UpLeft, result.Direction);
        Assert.False(result.WasReduced);
    }

    [Fact]
    public void ExpansionPlacement_OnNegativeSecondaryMonitor_RemainsFullyInsideThatWorkArea()
    {
        var result = WindowPlacementService.CalculateExpansionPlacement(
            new Rect(-1274, 8, 40, 40),
            new Size(520, 176),
            new Rect(-1280, 0, 1280, 1024),
            safeMargin: 8);

        Assert.True(new Rect(-1272, 8, 1264, 1008).Contains(result.Bounds));
        Assert.Equal(ExpansionDirection.DownRight, result.Direction);
    }

    [Fact]
    public void ExpansionPlacement_WhenMonitorIsSmaller_ReducesOnlyWindowBoundsToSafeArea()
    {
        var result = WindowPlacementService.CalculateExpansionPlacement(
            new Rect(220, 140, 40, 40),
            new Size(520, 176),
            new Rect(0, 0, 320, 180),
            safeMargin: 8);

        Assert.Equal(new Size(304, 164), result.Bounds.Size);
        Assert.Equal(new Point(8, 8), result.Bounds.TopLeft);
        Assert.True(result.WasReduced);
    }

    [Theory]
    [InlineData(2, 300, 6, 300)]
    [InlineData(1878, 300, 1874, 300)]
    [InlineData(700, 1, 700, 6)]
    [InlineData(700, 1002, 700, 994)]
    public void SnapBallOrigin_NearAnyEdge_SnapsWithVisibleInset(
        double left, double top, double expectedLeft, double expectedTop)
    {
        var result = WindowPlacementService.SnapBallOrigin(
            new Point(left, top),
            new Size(40, 40),
            new Rect(0, 0, 1920, 1040),
            threshold: 20,
            inset: 6);

        Assert.Equal(new Point(expectedLeft, expectedTop), result);
    }

    [Fact]
    public void SnapBallOrigin_OutsideSnapThreshold_OnlyClampsToWorkArea()
    {
        var result = WindowPlacementService.SnapBallOrigin(
            new Point(400, 300), new Size(40, 40), new Rect(0, 0, 1920, 1040), 20, 6);

        Assert.Equal(new Point(400, 300), result);
    }

    [Fact]
    public void DockedCompanionOrigin_PlacesSettingsBesideFloatingWindow()
    {
        var result = WindowPlacementService.GetDockedCompanionOrigin(
            new Rect(100, 200, 490, 170), new Size(760, 560), 10, new Rect(0, 0, 1920, 1080));

        Assert.Equal(new Point(600, 200), result);
    }

    [Fact]
    public void DockedCompanionOrigin_WhenRightSideIsFull_UsesLeftSide()
    {
        var result = WindowPlacementService.GetDockedCompanionOrigin(
            new Rect(1300, 200, 490, 170), new Size(760, 560), 10, new Rect(0, 0, 1920, 1080));

        Assert.Equal(new Point(530, 200), result);
    }

    [Fact]
    public void RestoreBallOrigin_IgnoresClampedExpandedWindowLocation()
    {
        var originalBall = new Point(1870, 980);
        var clampedExpanded = new Point(1200, 500);

        var result = WindowPlacementService.GetRestoredBallOrigin(originalBall, clampedExpanded);

        Assert.Equal(originalBall, result);
    }
}
