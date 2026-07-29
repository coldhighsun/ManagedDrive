using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.App.Controls;

/// <summary>
/// Renders a fixed-size read/write speed history as two overlaid polylines. Intended for use
/// inside a hover-triggered <see cref="System.Windows.Controls.Primitives.Popup"/> — it is not
/// rendered while off-screen, so no visibility/throttling logic lives here.
/// </summary>
public partial class SpeedHistoryChart
{
    /// <summary>Identifies the <see cref="ReadHistory"/> dependency property.</summary>
    public static readonly DependencyProperty ReadHistoryProperty = DependencyProperty.Register(
        nameof(ReadHistory), typeof(IReadOnlyList<double>), typeof(SpeedHistoryChart),
        new PropertyMetadata(null, OnHistoryChanged));

    /// <summary>Identifies the <see cref="WriteHistory"/> dependency property.</summary>
    public static readonly DependencyProperty WriteHistoryProperty = DependencyProperty.Register(
        nameof(WriteHistory), typeof(IReadOnlyList<double>), typeof(SpeedHistoryChart),
        new PropertyMetadata(null, OnHistoryChanged));

    /// <summary>
    /// Initializes a new instance of <see cref="SpeedHistoryChart"/>.
    /// </summary>
    public SpeedHistoryChart()
    {
        InitializeComponent();
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
    /// Maps a sequence of values onto polyline points that fit within
    /// <paramref name="width"/> x <paramref name="height"/>, scaled by the largest value across
    /// both series (<paramref name="scaleMax"/>) so the read and write lines share one scale.
    /// A flat, near-baseline line is returned when every value is zero, rather than an empty
    /// point list, so the chart never looks unloaded.
    /// </summary>
    public static PointCollection NormalizePoints(IReadOnlyList<double> values, double width, double height, double scaleMax)
    {
        var points = new PointCollection();
        if (values.Count == 0)
        {
            return points;
        }

        var stepX = values.Count > 1 ? width / (values.Count - 1) : 0;
        for (var i = 0; i < values.Count; i++)
        {
            var x = i * stepX;
            var y = scaleMax > 0
                ? height - (values[i] / scaleMax * height)
                : height;
            points.Add(new(x, y));
        }

        return points;
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

        // Both charts share one Y-axis scale (derived from the larger of the two series) so
        // their heights are directly comparable at a glance, rather than each auto-scaling to
        // its own max independently.
        var scaleMax = RoundUpToNiceMax(Math.Max(
            read.Count > 0 ? read.Max() : 0.0,
            write.Count > 0 ? write.Max() : 0.0));

        RedrawSeries(read, scaleMax, ReadChartArea, ReadLine, ReadTopGridLine, ReadMidGridLine, ReadBottomGridLine,
            ReadAxisMaxLabel, ReadAxisMidLabel, ReadAxisZeroLabel);
        RedrawSeries(write, scaleMax, WriteChartArea, WriteLine, WriteTopGridLine, WriteMidGridLine, WriteBottomGridLine,
            WriteAxisMaxLabel, WriteAxisMidLabel, WriteAxisZeroLabel);

        var pointCount = read.Count > 0 ? read.Count : write.Count;
        var timeLabels = BuildTimeAxisLabels(pointCount);
        TimeAxisLabel0.Text = timeLabels[0];
        TimeAxisLabel1.Text = timeLabels[1];
        TimeAxisLabel2.Text = timeLabels[2];
        TimeAxisLabel3.Text = timeLabels[3];
    }

    private static void RedrawSeries(
        IReadOnlyList<double> values,
        double scaleMax,
        Grid chartArea,
        Polyline line,
        Line topGridLine,
        Line midGridLine,
        Line bottomGridLine,
        TextBlock axisMaxLabel,
        TextBlock axisMidLabel,
        TextBlock axisZeroLabel)
    {
        var width = chartArea.ActualWidth;
        var height = chartArea.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        line.Points = NormalizePoints(values, width, height, scaleMax);

        topGridLine.X1 = 0;
        topGridLine.Y1 = 0;
        topGridLine.X2 = width;
        topGridLine.Y2 = 0;

        midGridLine.X1 = 0;
        midGridLine.Y1 = height / 2;
        midGridLine.X2 = width;
        midGridLine.Y2 = height / 2;

        bottomGridLine.X1 = 0;
        bottomGridLine.Y1 = height;
        bottomGridLine.X2 = width;
        bottomGridLine.Y2 = height;

        axisMaxLabel.Text = scaleMax > 0 ? ByteFormatter.FormatRate(scaleMax) : string.Empty;
        axisMidLabel.Text = scaleMax > 0 ? ByteFormatter.FormatRate(scaleMax / 2) : string.Empty;
        axisZeroLabel.Text = "0 B/s";
    }
}
