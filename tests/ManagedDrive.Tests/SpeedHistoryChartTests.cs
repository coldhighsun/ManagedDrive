namespace ManagedDrive.Tests;

public sealed class SpeedHistoryChartTests
{
    [Fact]
    public void NormalizePoints_EmptyValues_ReturnsEmptyCollection()
    {
        var points = SpeedHistoryChart.NormalizePoints(Array.Empty<double>(), width: 100, height: 50, scaleMax: 0);

        Assert.Empty(points);
    }

    [Fact]
    public void NormalizePoints_AllZero_ReturnsFlatLineAtBaseline()
    {
        var points = SpeedHistoryChart.NormalizePoints([0, 0, 0], width: 100, height: 50, scaleMax: 0);

        Assert.All(points, p => Assert.Equal(50, p.Y));
    }

    [Fact]
    public void NormalizePoints_MaxValue_TouchesTop()
    {
        var points = SpeedHistoryChart.NormalizePoints([0, 100], width: 100, height: 50, scaleMax: 100);

        Assert.Equal(50, points[0].Y);
        Assert.Equal(0, points[1].Y);
    }

    [Fact]
    public void NormalizePoints_SinglePoint_DoesNotDivideByZeroWidth()
    {
        var points = SpeedHistoryChart.NormalizePoints([42], width: 100, height: 50, scaleMax: 42);

        Assert.Single(points);
        Assert.Equal(0, points[0].X);
    }

    [Fact]
    public void NormalizePoints_SpansFullWidth()
    {
        var points = SpeedHistoryChart.NormalizePoints([0, 0, 0, 0], width: 90, height: 50, scaleMax: 0);

        Assert.Equal(0, points[0].X);
        Assert.Equal(90, points[3].X);
    }

    [Fact]
    public void RoundUpToNiceMax_Zero_ReturnsZero()
    {
        Assert.Equal(0, SpeedHistoryChart.RoundUpToNiceMax(0));
    }

    [Fact]
    public void RoundUpToNiceMax_BelowTenInUnit_RoundsUpToTen()
    {
        var result = SpeedHistoryChart.RoundUpToNiceMax(3 * 1024.0 * 1024);

        Assert.Equal(10 * 1024.0 * 1024, result);
    }

    [Fact]
    public void RoundUpToNiceMax_AlreadyMultipleOfTen_StaysUnchanged()
    {
        var result = SpeedHistoryChart.RoundUpToNiceMax(20 * 1024.0 * 1024);

        Assert.Equal(20 * 1024.0 * 1024, result);
    }

    [Fact]
    public void RoundUpToNiceMax_NearUnitBoundary_StaysInSameUnitTier()
    {
        var result = SpeedHistoryChart.RoundUpToNiceMax(903 * 1024.0);

        Assert.Equal(910 * 1024.0, result);
    }
}
