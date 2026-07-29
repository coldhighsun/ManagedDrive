using System.Windows.Media;
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

    private static void OnHistoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SpeedHistoryChart)d).Redraw();

    private void ChartArea_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        var width = ChartArea.ActualWidth;
        var height = ChartArea.ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var read = ReadHistory ?? Array.Empty<double>();
        var write = WriteHistory ?? Array.Empty<double>();
        var rawMax = Math.Max(
            read.Count > 0 ? read.Max() : 0.0,
            write.Count > 0 ? write.Max() : 0.0);
        var scaleMax = RoundUpToNiceMax(rawMax);

        ReadLine.Points = NormalizePoints(read, width, height, scaleMax);
        WriteLine.Points = NormalizePoints(write, width, height, scaleMax);

        TopGridLine.X1 = 0;
        TopGridLine.Y1 = 0;
        TopGridLine.X2 = width;
        TopGridLine.Y2 = 0;

        MidGridLine.X1 = 0;
        MidGridLine.Y1 = height / 2;
        MidGridLine.X2 = width;
        MidGridLine.Y2 = height / 2;

        BottomGridLine.X1 = 0;
        BottomGridLine.Y1 = height;
        BottomGridLine.X2 = width;
        BottomGridLine.Y2 = height;

        AxisMaxLabel.Text = scaleMax > 0 ? ByteFormatter.FormatRate(scaleMax) : string.Empty;
        AxisMidLabel.Text = scaleMax > 0 ? ByteFormatter.FormatRate(scaleMax / 2) : string.Empty;
        AxisZeroLabel.Text = "0 B/s";
    }
}
