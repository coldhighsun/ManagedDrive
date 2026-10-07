namespace ManagedDrive.Core.Diagnostics;

/// <summary>
/// A rectangle in the treemap's coordinate space (device independent; the origin is top left).
/// </summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    /// <summary>
    /// The right edge.
    /// </summary>
    public double Right => X + Width;

    /// <summary>
    /// The bottom edge.
    /// </summary>
    public double Bottom => Y + Height;

    /// <summary>
    /// The area.
    /// </summary>
    public double Area => Width * Height;

    /// <summary>
    /// The shorter side.
    /// </summary>
    public double MinSide => Math.Min(Width, Height);

    /// <summary>
    /// Whether a point lies inside, the left and top edges included.
    /// </summary>
    /// <param name="x">Horizontal position.</param>
    /// <param name="y">Vertical position.</param>
    /// <returns><c>true</c> if the point is inside.</returns>
    public bool Contains(double x, double y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>
    /// Shrinks the rectangle by the same amount on every side, never below zero size.
    /// </summary>
    /// <param name="amount">How much to take off each side.</param>
    /// <returns>The smaller rectangle.</returns>
    public TreemapRect Inset(double amount) =>
        new(X + amount, Y + amount, Math.Max(0, Width - (2 * amount)), Math.Max(0, Height - (2 * amount)));
}

/// <summary>
/// Squarified treemap layout (Bruls, Huizing, van Wijk): splits a rectangle into cells whose areas
/// are proportional to the weights while keeping the cells close to square.
/// </summary>
public static class TreemapLayout
{
    /// <summary>
    /// Lays the weights out inside <paramref name="bounds"/>.
    /// </summary>
    /// <param name="weights">The size of each item, largest first for the best shapes (any order is laid out correctly).</param>
    /// <param name="bounds">The area to fill.</param>
    /// <returns>
    /// One rectangle per weight, in the same order. Together they cover <paramref name="bounds"/>
    /// exactly and do not overlap; an item with a weight of zero or less gets an empty rectangle.
    /// </returns>
    public static TreemapRect[] Squarify(IReadOnlyList<double> weights, TreemapRect bounds)
    {
        var result = new TreemapRect[weights.Count];
        var total = 0.0;
        foreach (var weight in weights)
        {
            if (weight > 0)
            {
                total += weight;
            }
        }

        if (total <= 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return result;
        }

        var scale = bounds.Area / total;
        var indices = new List<int>(weights.Count);
        for (var i = 0; i < weights.Count; i++)
        {
            if (weights[i] > 0)
            {
                indices.Add(i);
            }
        }

        var remaining = bounds;
        var rowStart = 0;
        var rowSum = 0.0;
        var rowMin = double.MaxValue;
        var rowMax = 0.0;

        for (var position = 0; position < indices.Count; position++)
        {
            var area = weights[indices[position]] * scale;
            var side = remaining.MinSide;
            var inRow = position - rowStart;

            if (inRow > 0 && Worst(rowSum + area, Math.Min(rowMin, area), Math.Max(rowMax, area), side) > Worst(rowSum, rowMin, rowMax, side))
            {
                remaining = PlaceRow(indices, weights, scale, rowStart, position, rowSum, remaining, result);
                rowStart = position;
                rowSum = 0;
                rowMin = double.MaxValue;
                rowMax = 0;
            }

            rowSum += area;
            rowMin = Math.Min(rowMin, area);
            rowMax = Math.Max(rowMax, area);
        }

        PlaceRow(indices, weights, scale, rowStart, indices.Count, rowSum, remaining, result);
        return result;
    }

    /// <summary>
    /// The worst aspect ratio a row would have when laid along a side of the given length.
    /// </summary>
    /// <param name="sum">Total area of the row.</param>
    /// <param name="min">Smallest area in the row.</param>
    /// <param name="max">Largest area in the row.</param>
    /// <param name="side">Length of the side the row is laid along.</param>
    /// <returns>The ratio, 1 being perfectly square and larger being worse.</returns>
    private static double Worst(double sum, double min, double max, double side)
    {
        var squared = side * side;
        var total = sum * sum;
        return Math.Max(squared * max / total, total / (squared * min));
    }

    /// <summary>
    /// Writes the rectangles of one row and returns what is left of the area. A wide area gets a
    /// column down its left edge, a tall one a strip along its top.
    /// </summary>
    /// <param name="indices">Positions in the weight list of the items with a positive weight.</param>
    /// <param name="weights">The weights.</param>
    /// <param name="scale">Area per unit of weight.</param>
    /// <param name="start">First entry of <paramref name="indices"/> in the row.</param>
    /// <param name="end">One past the last entry in the row.</param>
    /// <param name="rowSum">Total area of the row.</param>
    /// <param name="area">The area still to fill.</param>
    /// <param name="result">Receives the rectangles.</param>
    /// <returns>The area left after the row.</returns>
    private static TreemapRect PlaceRow(
        List<int> indices, IReadOnlyList<double> weights, double scale, int start, int end, double rowSum,
        TreemapRect area, TreemapRect[] result)
    {
        var vertical = area.Width >= area.Height;
        var thickness = vertical ? rowSum / area.Height : rowSum / area.Width;
        var offset = vertical ? area.Y : area.X;

        for (var i = start; i < end; i++)
        {
            var length = weights[indices[i]] * scale / thickness;
            result[indices[i]] = vertical
                ? new(area.X, offset, thickness, length)
                : new(offset, area.Y, length, thickness);
            offset += length;
        }

        return vertical
            ? new(area.X + thickness, area.Y, Math.Max(0, area.Width - thickness), area.Height)
            : new(area.X, area.Y + thickness, area.Width, Math.Max(0, area.Height - thickness));
    }
}
