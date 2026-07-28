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
}
