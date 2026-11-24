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
    public void NormalizeUpwardPoints_ZeroAndMax_SpanBottomToTop()
    {
        var points = SpeedHistoryChart.NormalizeUpwardPoints([0, 100], width: 100, height: 50, scaleMax: 100);

        Assert.Equal(50, points[0].Y);
        Assert.Equal(0, points[1].Y);
    }

    [Fact]
    public void ToAreaPoints_EmptyLine_ReturnsEmptyArea()
    {
        var area = SpeedHistoryChart.ToAreaPoints([], baseline: 25);

        Assert.Empty(area);
    }

    [Theory]
    [InlineData(0, true, 25)]
    [InlineData(100, true, 0)]
    [InlineData(100, false, 50)]
    [InlineData(500, true, 0)]
    [InlineData(50, true, 12.5)]
    public void ValueToY_Mirrored_MapsAroundMiddleLine(double value, bool upward, double expected)
    {
        var y = SpeedHistoryChart.ValueToY(value, height: 50, scaleMax: 100, upward, mirrored: true);

        Assert.Equal(expected, y);
    }

    [Fact]
    public void ValueToY_ZeroScale_LiesOnZeroLine()
    {
        var y = SpeedHistoryChart.ValueToY(80, height: 50, scaleMax: 0, upward: true, mirrored: true);

        Assert.Equal(25, y);
    }

    [Fact]
    public void ValueToY_NotMirrored_UsesWholeHeightFromBottom()
    {
        Assert.Equal(50, SpeedHistoryChart.ValueToY(0, height: 50, scaleMax: 100, upward: true, mirrored: false));
        Assert.Equal(0, SpeedHistoryChart.ValueToY(100, height: 50, scaleMax: 100, upward: true, mirrored: false));
    }

    [Fact]
    public void ValueToY_SameValues_MatchesNormalizedPoints()
    {
        double[] values = [0, 30, 100];

        var points = SpeedHistoryChart.NormalizeMirroredPoints(values, width: 100, height: 50, scaleMax: 100, upward: false);

        for (var i = 0; i < values.Length; i++)
        {
            Assert.Equal(points[i].Y, SpeedHistoryChart.ValueToY(values[i], 50, 100, upward: false, mirrored: true));
        }
    }

    [Fact]
    public void HitTestIndex_NoSamples_ReturnsMinusOne()
    {
        Assert.Equal(-1, SpeedHistoryChart.HitTestIndex(10, width: 100, count: 0));
    }

    [Fact]
    public void HitTestIndex_SingleSample_ReturnsZero()
    {
        Assert.Equal(0, SpeedHistoryChart.HitTestIndex(80, width: 100, count: 1));
    }

    [Theory]
    [InlineData(-20, 0)]
    [InlineData(0, 0)]
    [InlineData(100, 10)]
    [InlineData(250, 10)]
    public void HitTestIndex_OutsideOrOnEdge_IsClampedToFirstOrLast(double x, int expected)
    {
        Assert.Equal(expected, SpeedHistoryChart.HitTestIndex(x, width: 100, count: 11));
    }

    [Theory]
    [InlineData(14, 1)]
    [InlineData(16, 2)]
    [InlineData(50, 5)]
    public void HitTestIndex_BetweenSamples_SnapsToNearest(double x, int expected)
    {
        // 11 samples over 100 px: one every 10 px.
        Assert.Equal(expected, SpeedHistoryChart.HitTestIndex(x, width: 100, count: 11));
    }

    [Fact]
    public void FormatSampleTime_NewestSample_IsNow()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 30, 5, TimeSpan.Zero);

        var text = SpeedHistoryChart.FormatSampleTime(now, count: 900, index: 899);

        Assert.Equal($"{now.ToLocalTime():HH:mm:ss} (-0s)", text);
    }

    [Fact]
    public void FormatSampleTime_OldestSample_ShowsThirtyMinutesAgo()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero);

        var text = SpeedHistoryChart.FormatSampleTime(now, count: 900, index: 0);

        Assert.Equal($"{now.AddSeconds(-1798).ToLocalTime():HH:mm:ss} (-29m 58s)", text);
    }

    [Fact]
    public void FormatSampleTime_UnderOneMinute_ShowsSecondsOnly()
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.Zero);

        var text = SpeedHistoryChart.FormatSampleTime(now, count: 900, index: 890);

        Assert.EndsWith("(-18s)", text);
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
