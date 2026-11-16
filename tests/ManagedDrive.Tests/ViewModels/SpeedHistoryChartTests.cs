namespace ManagedDrive.Tests;

public sealed class SpeedHistoryChartTests
{
    [Fact]
    public void NormalizeMirroredPoints_EmptyValues_ReturnsEmptyCollection()
    {
        var points = SpeedHistoryChart.NormalizeMirroredPoints(Array.Empty<double>(), width: 100, height: 50, scaleMax: 0, upward: true);

        Assert.Empty(points);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NormalizeMirroredPoints_AllZero_LiesOnZeroLine(bool upward)
    {
        var points = SpeedHistoryChart.NormalizeMirroredPoints([0, 0, 0], width: 100, height: 50, scaleMax: 0, upward);

        Assert.All(points, p => Assert.Equal(25, p.Y));
    }

    [Fact]
    public void NormalizeMirroredPoints_UpwardMaxValue_TouchesTop()
    {
        var points = SpeedHistoryChart.NormalizeMirroredPoints([0, 100], width: 100, height: 50, scaleMax: 100, upward: true);

        Assert.Equal(25, points[0].Y);
        Assert.Equal(0, points[1].Y);
    }

    [Fact]
    public void NormalizeMirroredPoints_DownwardMaxValue_TouchesBottom()
    {
        var points = SpeedHistoryChart.NormalizeMirroredPoints([0, 100], width: 100, height: 50, scaleMax: 100, upward: false);

        Assert.Equal(25, points[0].Y);
        Assert.Equal(50, points[1].Y);
    }

    [Fact]
    public void NormalizeMirroredPoints_ValueAboveScale_IsClampedToEdge()
    {
        var points = SpeedHistoryChart.NormalizeMirroredPoints([200], width: 100, height: 50, scaleMax: 100, upward: true);

        Assert.Equal(0, points[0].Y);
    }

    [Fact]
    public void NormalizeMirroredPoints_SinglePoint_DoesNotDivideByZeroWidth()
    {
        var points = SpeedHistoryChart.NormalizeMirroredPoints([42], width: 100, height: 50, scaleMax: 42, upward: true);

        Assert.Single(points);
        Assert.Equal(0, points[0].X);
    }

    [Fact]
    public void NormalizeMirroredPoints_SpansFullWidth()
    {
        var points = SpeedHistoryChart.NormalizeMirroredPoints([0, 0, 0, 0], width: 90, height: 50, scaleMax: 0, upward: false);

        Assert.Equal(0, points[0].X);
        Assert.Equal(90, points[3].X);
    }

    [Fact]
    public void ToAreaPoints_Line_ClosesDownToBaseline()
    {
        var line = SpeedHistoryChart.NormalizeMirroredPoints([50, 100], width: 100, height: 50, scaleMax: 100, upward: true);

        var area = SpeedHistoryChart.ToAreaPoints(line, baseline: 25);

        Assert.Equal(4, area.Count);
        Assert.Equal(new System.Windows.Point(100, 25), area[2]);
        Assert.Equal(new System.Windows.Point(0, 25), area[3]);
    }

    [Fact]
    public void ToAreaPoints_EmptyLine_ReturnsEmptyArea()
    {
        var area = SpeedHistoryChart.ToAreaPoints([], baseline: 25);

        Assert.Empty(area);
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
