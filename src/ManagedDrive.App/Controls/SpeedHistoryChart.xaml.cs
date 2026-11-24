using System.Windows.Controls;
using System.Windows.Input;
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

        var stepX = values.Count > 1 ? width / (values.Count - 1) : 0;
        for (var i = 0; i < values.Count; i++)
        {
            points.Add(new(i * stepX, ValueToY(values[i], height, scaleMax, upward, mirrored: true)));
        }

        return points;
    }

    /// <summary>
    /// Maps one value onto the Y coordinate it has on the plot. With <paramref name="mirrored"/> the zero line is the
    /// middle of <paramref name="height"/> (an upward series rises above it, a downward one falls below it);
    /// otherwise the zero line is the bottom edge and the series uses the whole height. <paramref name="scaleMax"/>
    /// reaches the edge, larger values are clamped, and with a scale of zero the point lies on the zero line.
    /// </summary>
    public static double ValueToY(double value, double height, double scaleMax, bool upward, bool mirrored)
    {
        var plotHeight = mirrored ? height : height * 2;
        var baseline = plotHeight / 2;
        var sign = upward ? -1.0 : 1.0;
        var offset = scaleMax > 0 ? Math.Min(value / scaleMax, 1.0) * baseline : 0;
        return baseline + sign * offset;
    }

    /// <summary>
    /// Finds the sample under a horizontal position: the samples span <paramref name="width"/> evenly, the nearest one
    /// wins and positions outside the plot are clamped to the first or last sample. Returns -1 when there are no samples.
    /// </summary>
    public static int HitTestIndex(double x, double width, int count)
    {
        if (count <= 0)
        {
            return -1;
        }

        if (count == 1 || width <= 0)
        {
            return 0;
        }

        var index = (int)Math.Round(x / (width / (count - 1)));
        return Math.Clamp(index, 0, count - 1);
    }

    /// <summary>
    /// Describes when a sample was taken for the hover box: the wall-clock time followed by how long ago it was, e.g.
    /// <c>12:30:05 (-3m 20s)</c>. The newest sample (the last of <paramref name="count"/>) counts as <paramref name="now"/>.
    /// </summary>
    public static string FormatSampleTime(
        DateTimeOffset now, int count, int index, double secondsPerPoint = SecondsPerPoint)
    {
        var age = TimeSpan.FromSeconds((count - 1 - index) * secondsPerPoint);
        var time = now - age;
        var ago = age.TotalMinutes >= 1
            ? $"-{(int)age.TotalMinutes}m {age.Seconds}s"
            : $"-{(int)age.TotalSeconds}s";
        return $"{time.ToLocalTime():HH:mm:ss} ({ago})";
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

    /// <summary>
    /// Gap in pixels between the hovered sample and the info box.
    /// </summary>
    private const double HoverInfoGap = 12;

    /// <summary>
    /// The read history drawn by the last <see cref="Redraw"/>, so the hover box shows what is on screen.
    /// </summary>
    private IReadOnlyList<double> _drawnRead = Array.Empty<double>();

    /// <summary>
    /// The write history drawn by the last <see cref="Redraw"/>.
    /// </summary>
    private IReadOnlyList<double> _drawnWrite = Array.Empty<double>();

    /// <summary>
    /// The Y-axis maximum used by the last <see cref="Redraw"/>.
    /// </summary>
    private double _drawnScaleMax;

    /// <summary>
    /// The last mouse position over the plot, or <see langword="null"/> while the mouse is outside it.
    /// </summary>
    private System.Windows.Point? _hoverPoint;

    private static void OnHistoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SpeedHistoryChart)d).Redraw();

    private void ChartArea_SizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    /// <summary>
    /// Tracks the mouse over the plot and shows the crosshair and info box for the sample under it.
    /// </summary>
    private void ChartArea_MouseMove(object sender, MouseEventArgs e)
    {
        _hoverPoint = e.GetPosition(ChartArea);
        UpdateHover();
    }

    /// <summary>
    /// Hides the crosshair and info box when the mouse leaves the plot.
    /// </summary>
    private void ChartArea_MouseLeave(object sender, MouseEventArgs e)
    {
        _hoverPoint = null;
        UpdateHover();
    }

    /// <summary>
    /// Positions the crosshair (a vertical dashed line at the hovered sample, a horizontal one and a dot at each
    /// series' value) and the info box from the last mouse position and the data of the last redraw; hides them all
    /// when the mouse is outside the plot or there is nothing to show.
    /// </summary>
    private void UpdateHover()
    {
        var read = _drawnRead;
        var write = _drawnWrite;
        var count = read.Count > 0 ? read.Count : write.Count;
        var width = ChartArea.ActualWidth;
        var height = ChartArea.ActualHeight;
        if (_hoverPoint is not { } mouse || count == 0 || width <= 0 || height <= 0)
        {
            SetHoverVisibility(Visibility.Collapsed, Visibility.Collapsed);
            return;
        }

        var index = HitTestIndex(mouse.X, width, count);
        var x = count > 1 ? index * (width / (count - 1)) : 0;
        var readValue = index < read.Count ? read[index] : 0;
        var writeValue = index < write.Count ? write[index] : 0;
        var showWrite = ShowWrite;
        var readY = ValueToY(readValue, height, _drawnScaleMax, upward: true, mirrored: showWrite);
        var writeY = ValueToY(writeValue, height, _drawnScaleMax, upward: false, mirrored: true);

        HoverVLine.X1 = x;
        HoverVLine.X2 = x;
        HoverVLine.Y1 = 0;
        HoverVLine.Y2 = height;

        HoverReadLine.X1 = 0;
        HoverReadLine.X2 = width;
        HoverReadLine.Y1 = readY;
        HoverReadLine.Y2 = readY;
        HoverReadDot.Margin = new(x - HoverReadDot.Width / 2, readY - HoverReadDot.Height / 2, 0, 0);

        HoverWriteLine.X1 = 0;
        HoverWriteLine.X2 = width;
        HoverWriteLine.Y1 = writeY;
        HoverWriteLine.Y2 = writeY;
        HoverWriteDot.Margin = new(x - HoverWriteDot.Width / 2, writeY - HoverWriteDot.Height / 2, 0, 0);

        HoverTimeText.Text = FormatSampleTime(DateTimeOffset.Now, count, index);
        HoverReadText.Text = $"{FindLabel("Card.ReadSpeed")}: {ByteFormatter.FormatRate(readValue)}";
        HoverWriteText.Text = $"{FindLabel("Card.WriteSpeed")}: {ByteFormatter.FormatRate(writeValue)}";
        HoverWriteRow.Visibility = showWrite ? Visibility.Visible : Visibility.Collapsed;

        SetHoverVisibility(Visibility.Visible, showWrite ? Visibility.Visible : Visibility.Collapsed);

        // The box sits right of the hovered sample, flips to its left near the right edge and stays inside the plot.
        HoverInfo.Measure(new(double.PositiveInfinity, double.PositiveInfinity));
        var size = HoverInfo.DesiredSize;
        var left = x + HoverInfoGap + size.Width <= width ? x + HoverInfoGap : x - HoverInfoGap - size.Width;
        var top = mouse.Y - size.Height / 2;
        HoverInfo.Margin = new(
            Math.Clamp(left, 0, Math.Max(0, width - size.Width)),
            Math.Clamp(top, 0, Math.Max(0, height - size.Height)),
            0,
            0);
    }

    /// <summary>
    /// Shows or hides the hover elements: the shared ones (vertical line, read line and dot, info box) and the
    /// write line and dot, which a read-only disk never shows.
    /// </summary>
    private void SetHoverVisibility(Visibility shared, Visibility write)
    {
        HoverVLine.Visibility = shared;
        HoverReadLine.Visibility = shared;
        HoverReadDot.Visibility = shared;
        HoverInfo.Visibility = shared;
        HoverWriteLine.Visibility = write;
        HoverWriteDot.Visibility = write;
    }

    /// <summary>
    /// Looks up a localized string resource, falling back to its key when it is missing.
    /// </summary>
    private string FindLabel(string key) => TryFindResource(key) as string ?? key;

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

        _drawnRead = read;
        _drawnWrite = write;
        _drawnScaleMax = scaleMax;

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

        // The data moved under a stationary mouse, so the hover box has to follow it.
        UpdateHover();
    }

}
