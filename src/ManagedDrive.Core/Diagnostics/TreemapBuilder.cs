namespace ManagedDrive.Core.Diagnostics;

/// <summary>
/// One drawn rectangle of a treemap.
/// </summary>
/// <param name="Node">The file or directory shown, or <c>null</c> for a block that stands for several small items.</param>
/// <param name="Rect">Where it is drawn.</param>
/// <param name="Depth">0 for the items directly inside the shown directory, 1 for what is inside those, and so on.</param>
/// <param name="IsContainer">Whether the cell is a directory whose children are drawn inside it (the first <see cref="TreemapOptions.HeaderHeight"/> of it is a title bar when it is tall enough).</param>
/// <param name="HasHeader">Whether a title bar is reserved at the top of a container.</param>
/// <param name="MergedCount">For a block of small items, how many it stands for; 0 otherwise.</param>
/// <param name="MergedBytes">For a block of small items, the memory they occupy together; 0 otherwise.</param>
public sealed record TreemapCell(
    SpaceNode? Node,
    TreemapRect Rect,
    int Depth,
    bool IsContainer,
    bool HasHeader,
    int MergedCount,
    ulong MergedBytes)
{
    /// <summary>
    /// Whether this cell stands for several small items rather than one node.
    /// </summary>
    public bool IsMerged => Node is null;
}

/// <summary>
/// Tuning of <see cref="TreemapBuilder"/>; all sizes are in the same units as the bounds.
/// </summary>
/// <param name="MinSide">Items whose shorter side would be smaller than this are merged into one block.</param>
/// <param name="HeaderHeight">Height of a directory's title bar.</param>
/// <param name="Padding">Gap kept inside a directory around its children.</param>
/// <param name="MaxDepth">How many levels of directories are opened inside the shown one.</param>
/// <param name="MaxItemsPerLevel">Most cells one directory shows before the rest is merged.</param>
/// <param name="MaxCells">Total number of cells after which directories are no longer opened.</param>
public sealed record TreemapOptions(
    double MinSide = 4,
    double HeaderHeight = 16,
    double Padding = 2,
    int MaxDepth = 2,
    int MaxItemsPerLevel = 150,
    int MaxCells = 1500);

/// <summary>
/// Turns a <see cref="SpaceNode"/> tree into the rectangles a treemap draws: squarified, with
/// directories opened a few levels deep and tiny items merged so the number of cells stays bounded.
/// </summary>
public static class TreemapBuilder
{
    /// <summary>
    /// Lays out the contents of <paramref name="directory"/>.
    /// </summary>
    /// <param name="directory">The directory to show; its own rectangle is not part of the result.</param>
    /// <param name="bounds">The area to fill.</param>
    /// <param name="options">Tuning, or <c>null</c> for the defaults.</param>
    /// <returns>
    /// The cells, each directory before the cells inside it, so the last cell containing a point is
    /// the innermost one.
    /// </returns>
    public static IReadOnlyList<TreemapCell> Build(SpaceNode directory, TreemapRect bounds, TreemapOptions? options = null)
    {
        var cells = new List<TreemapCell>();
        Fill(directory, bounds, 0, options ?? new(), cells);
        return cells;
    }

    /// <summary>
    /// Adds the cells for the children of <paramref name="directory"/> to <paramref name="cells"/>.
    /// </summary>
    /// <param name="directory">The directory whose children are laid out.</param>
    /// <param name="area">The area they share.</param>
    /// <param name="depth">Depth of the cells being added.</param>
    /// <param name="options">Tuning.</param>
    /// <param name="cells">Receives the cells.</param>
    private static void Fill(SpaceNode directory, TreemapRect area, int depth, TreemapOptions options, List<TreemapCell> cells)
    {
        var children = directory.Children.TakeWhile(child => child.Allocated > 0).ToList();
        if (children.Count == 0 || area.Width <= 0 || area.Height <= 0)
        {
            return;
        }

        // Shown items are a prefix of the size-sorted children; the tail becomes one merged block.
        var shown = Math.Min(children.Count, options.MaxItemsPerLevel);
        var rects = TreemapLayout.Squarify(children.Select(child => (double)child.Allocated).ToList(), area);
        for (var i = 0; i < shown; i++)
        {
            if (rects[i].MinSide < options.MinSide)
            {
                shown = i;
                break;
            }
        }

        var mergedCount = children.Count - shown;
        if (mergedCount > 0)
        {
            var mergedBytes = children.Skip(shown).Aggregate(0UL, (sum, child) => sum + child.Allocated);
            var weights = children.Take(shown).Select(child => (double)child.Allocated).Append(mergedBytes).ToList();
            rects = TreemapLayout.Squarify(weights, area);
            for (var i = 0; i < shown; i++)
            {
                AddCell(children[i], rects[i], depth, options, cells);
            }

            cells.Add(new(null, rects[shown], depth, false, false, mergedCount, mergedBytes));
            return;
        }

        for (var i = 0; i < shown; i++)
        {
            AddCell(children[i], rects[i], depth, options, cells);
        }
    }

    /// <summary>
    /// Adds the cell of one node and, if it is a directory with room, the cells inside it.
    /// </summary>
    /// <param name="node">The file or directory.</param>
    /// <param name="rect">Its rectangle.</param>
    /// <param name="depth">Its depth.</param>
    /// <param name="options">Tuning.</param>
    /// <param name="cells">Receives the cells.</param>
    private static void AddCell(SpaceNode node, TreemapRect rect, int depth, TreemapOptions options, List<TreemapCell> cells)
    {
        var inner = rect.Inset(options.Padding);
        var hasHeader = node.IsDirectory && inner.Height >= options.HeaderHeight + (2 * options.MinSide);
        if (hasHeader)
        {
            inner = new(inner.X, inner.Y + options.HeaderHeight, inner.Width, inner.Height - options.HeaderHeight);
        }

        var open = node.IsDirectory
            && node.Children.Count > 0
            && depth < options.MaxDepth
            && cells.Count < options.MaxCells
            && inner.Width >= 2 * options.MinSide
            && inner.Height >= 2 * options.MinSide;

        cells.Add(new(node, rect, depth, open, open && hasHeader, 0, 0));
        if (open)
        {
            Fill(node, inner, depth + 1, options, cells);
        }
    }
}
