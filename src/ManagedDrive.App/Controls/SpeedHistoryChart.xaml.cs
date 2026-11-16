using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.App.Controls;

/// <summary>
/// Renders a fixed-size read/write speed history as one mirrored plot (read above the zero line, write below it, each
/// with a translucent area under its line). Intended for use
/// inside a hover-triggered <see cref="System.Windows.Controls.Primitives.Popup"/> — it is not
/// rendered while off-screen, so no visibility/throttling logic lives here.
/// </summary>
public partial class SpeedHistoryChart
{
    /// <summary>Identifies the <see cref="ReadHistory"/> dependency property.</summary>
    public static readonly DependencyProperty ReadHistoryProperty = DependencyProperty.Register(
        nameof(ReadHistory), typeof(IReadOnlyList<double>), typeof(SpeedHistoryChart),
        new(null, OnHistoryChanged));

    /// <summary>Identifies the <see cref="WriteHistory"/> dependency property.</summary>
    public static readonly DependencyProperty WriteHistoryProperty = DependencyProperty.Register(
        nameof(WriteHistory), typeof(IReadOnlyList<double>), typeof(SpeedHistoryChart),
        new(null, OnHistoryChanged));

    /// <summary>Identifies the <see cref="ShowWrite"/> dependency property.</summary>
    public static readonly DependencyProperty ShowWriteProperty = DependencyProperty.Register(
        nameof(ShowWrite), typeof(bool), typeof(SpeedHistoryChart),
        new(true, OnHistoryChanged));

    /// <summary>
    /// Initializes a new instance of <see cref="SpeedHistoryChart"/>.
    /// </summary>
    public SpeedHistoryChart()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets or sets whether the write series is drawn. When it is not (a read-only disk never writes), the read series
    /// uses the whole height of the plot instead of the upper half of the mirrored one.
    /// </summary>
    public bool ShowWrite
    {
        get => (bool)GetValue(ShowWriteProperty);
        set => SetValue(ShowWriteProperty, value);
    }

    /// <summary>
    /// Gets or sets the oldest-first read-speed history (bytes/sec) to plot.
    /// </summary>
    public IReadOnlyList<double>? ReadHistory
    {
        get => (IReadOnlyList<double>?)GetValue(ReadHistoryProperty);
        set => SetValue(ReadHistoryProperty, value);
    }

    /// <summary>
    /// Gets or sets the oldest-first write-speed history (bytes/sec) to plot.
    /// </summary>
    public IReadOnlyList<double>? WriteHistory
    {
        get => (IReadOnlyList<double>?)GetValue(WriteHistoryProperty);
        set => SetValue(WriteHistoryProperty, value);
    }

    /// <summary>
    /// Maps a sequence of values onto polyline points of a mirrored chart: the zero line is the middle of
    /// <paramref name="height"/>, an upward series rises above it and a downward one falls below it, and
    /// <paramref name="scaleMax"/> reaches the top or bottom edge. With a scale of zero every point lies on the zero line.
    /// </summary>
    public static PointCollection NormalizeMirroredPoints(
        IReadOnlyList<double> values, double width, double height, double scaleMax, bool upward)
    {
        var points = new PointCollection();
        if (values.Count == 0)
        {
            return points;
        }

        var baseline = height / 2;
        var sign = upward ? -1.0 : 1.0;
        var stepX = values.Count > 1 ? width / (values.Count - 1) : 0;
        for (var i = 0; i < values.Count; i++)
        {
            var offset = scaleMax > 0 ? Math.Min(values[i] / scaleMax, 1.0) * baseline : 0;
            points.Add(new(i * stepX, baseline + sign * offset));
        }

        return points;
    }

    /// <summary>
    /// Maps a sequence of values onto polyline points of a single-series chart: the zero line is the bottom of
    /// <paramref name="height"/> and <paramref name="scaleMax"/> reaches the top edge.
    /// </summary>
    public static PointCollection NormalizeUpwardPoints(
        IReadOnlyList<double> values, double width, double height, double scaleMax) =>
        NormalizeMirroredPoints(values, width, height * 2, scaleMax, upward: true);

    /// <summary>
    /// Closes a line's points into a polygon that reaches down (or up) to the zero line, for the translucent area
    /// under a series. An empty line gives an empty polygon.
    /// </summary>
    public static PointCollection ToAreaPoints(PointCollection linePoints, double baseline)
    {
        var area = new PointCollection(linePoints);
        if (linePoints.Count == 0)
        {
            return area;
        }

        area.Add(new(linePoints[^1].X, baseline));
        area.Add(new(linePoints[0].X, baseline));
        return area;
    }

    /// <summary>
    /// Rounds <paramref name="bytesPerSecond"/> up to the nearest multiple of 10 within the same
    /// unit tier (B/KB/MB/GB) that <see cref="ByteFormatter.Format"/> would display it in, so
    /// axis labels read as clean numbers (e.g. 23.4 MB/s becomes 30 MB/s) without jumping to a
    /// coarser unit (e.g. 900 KB/s stays in KB, rounding to 10 KB rather than jumping to GB).
    /// </summary>
    public static double RoundUpToNiceMax(double bytesPerSecond)
    {
        if (bytesPerSecond <= 0)
        {
            return 0;
        }

        var unitSize = bytesPerSecond >= 1024.0 * 1024 * 1024 ? 1024.0 * 1024 * 1024
            : bytesPerSecond >= 1024.0 * 1024 ? 1024.0 * 1024
            : bytesPerSecond >= 1024.0 ? 1024.0
            : 1.0;

        var valueInUnit = bytesPerSecond / unitSize;
        return Math.Ceiling(valueInUnit / 10.0) * 10.0 * unitSize;
    }

    /// <summary>
    /// Number of seconds each point in <see cref="ReadHistory"/>/<see cref="WriteHistory"/>
    /// represents, matching <c>DiskViewModel</c>'s speed-history sampling interval.
    /// </summary>
    private const double SecondsPerPoint = 2.0;

    private static void OnHistoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SpeedHistoryChart)d).Redraw();

    private void ChartArea_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    /// <summary>
    /// Builds the four relative-time axis labels (oldest edge, two quarter-points, "now") for a
    /// history buffer of <paramref name="pointCount"/> samples spaced <see cref="SecondsPerPoint"/>
    /// seconds apart, e.g. <c>["-30m", "-20m", "-10m", "0m"]</c> for a 900-point/30-minute buffer.
    /// </summary>
    public static string[] BuildTimeAxisLabels(int pointCount, double secondsPerPoint = SecondsPerPoint)
    {
        var totalMinutes = pointCount * secondsPerPoint / 60.0;
        return
        [
            FormatMinutesAgo(totalMinutes),
            FormatMinutesAgo(totalMinutes * 2 / 3),
            FormatMinutesAgo(totalMinutes / 3),
            "0m",
        ];
    }

    private static string FormatMinutesAgo(double minutes) => $"-{Math.Round(minutes):0}m";

    private void Redraw()
    {
        var read = ReadHistory ?? Array.Empty<double>();
        var write = WriteHistory ?? Array.Empty<double>();

        // Both lines share the plot area and one Y-axis scale (derived from the larger of the two
        // series), so their heights are directly comparable at a glance.
        var scaleMax = RoundUpToNiceMax(Math.Max(
            read.Count > 0 ? read.Max() : 0.0,
            write.Count > 0 ? write.Max() : 0.0));

        var width = ChartArea.ActualWidth;
        var height = ChartArea.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var showWrite = ShowWrite;
        var baseline = showWrite ? height / 2 : height;
        var readPoints = showWrite
            ? NormalizeMirroredPoints(read, width, height, scaleMax, upward: true)
            : NormalizeUpwardPoints(read, width, height, scaleMax);
        var writePoints = showWrite
            ? NormalizeMirroredPoints(write, width, height, scaleMax, upward: false)
            : new PointCollection();
        ReadLine.Points = readPoints;
        WriteLine.Points = writePoints;
        ReadArea.Points = ToAreaPoints(readPoints, baseline);
        WriteArea.Points = ToAreaPoints(writePoints, baseline);

        var writeVisibility = showWrite ? Visibility.Visible : Visibility.Collapsed;
        WriteLegend.Visibility = writeVisibility;
        AxisBottomLabel.Visibility = writeVisibility;
        BottomGridLine.Visibility = writeVisibility;
        AxisZeroLabel.VerticalAlignment = showWrite ? VerticalAlignment.Center : VerticalAlignment.Bottom;

        TopGridLine.X1 = 0;
        TopGridLine.Y1 = 0;
        TopGridLine.X2 = width;
        TopGridLine.Y2 = 0;

        MidGridLine.X1 = 0;
        MidGridLine.Y1 = baseline;
        MidGridLine.X2 = width;
        MidGridLine.Y2 = baseline;

        BottomGridLine.X1 = 0;
        BottomGridLine.Y1 = height;
        BottomGridLine.X2 = width;
        BottomGridLine.Y2 = height;

        var maxText = scaleMax > 0 ? ByteFormatter.FormatRate(scaleMax) : string.Empty;
        AxisTopLabel.Text = maxText;
        AxisZeroLabel.Text = "0 B/s";
        AxisBottomLabel.Text = maxText;

        var pointCount = read.Count > 0 ? read.Count : write.Count;
        var timeLabels = BuildTimeAxisLabels(pointCount);
        TimeAxisLabel0.Text = timeLabels[0];
        TimeAxisLabel1.Text = timeLabels[1];
        TimeAxisLabel2.Text = timeLabels[2];
        TimeAxisLabel3.Text = timeLabels[3];
    }

}
