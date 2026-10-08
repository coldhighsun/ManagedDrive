namespace ManagedDrive.Core.Diagnostics;

/// <summary>
/// One drawn rectangle of a treemap.
/// </summary>
/// <param name="Node">The file or directory shown, or <c>null</c> for a block that stands for several small items.</param>
/// <param name="Rect">Where it is drawn.</param>
/// <param name="MergedCount">For a block of small items, how many it stands for; 0 otherwise.</param>
/// <param name="MergedBytes">For a block of small items, the memory they occupy together; 0 otherwise.</param>
public sealed record TreemapCell(
    SpaceNode? Node,
    TreemapRect Rect,
    int MergedCount,
    ulong MergedBytes);

/// <summary>
/// Tuning of <see cref="TreemapBuilder"/>; all sizes are in the same units as the bounds.
/// </summary>
/// <param name="MinSide">Items whose shorter side would be smaller than this are merged into one block.</param>
/// <param name="MaxItems">Most cells the directory shows before the rest is merged.</param>
public sealed record TreemapOptions(
    double MinSide = 4,
    int MaxItems = 150);

/// <summary>
/// Turns the direct children of a <see cref="SpaceNode"/> into the rectangles a treemap draws:
/// squarified, one directory level only (a subdirectory is a single block), with tiny items merged
/// so the number of cells stays bounded.
/// </summary>
public static class TreemapBuilder
{
    /// <summary>
    /// Lays out the direct children of <paramref name="directory"/>.
    /// </summary>
    /// <param name="directory">The directory to show; its own rectangle is not part of the result.</param>
    /// <param name="bounds">The area to fill.</param>
    /// <param name="options">Tuning, or <c>null</c> for the defaults.</param>
    /// <returns>The cells, largest first, with the merged block (if any) last.</returns>
    public static IReadOnlyList<TreemapCell> Build(SpaceNode directory, TreemapRect bounds, TreemapOptions? options = null)
    {
        options ??= new();
        var children = directory.Children.TakeWhile(child => child.Allocated > 0).ToList();
        if (children.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return [];
        }

        // Shown items are a prefix of the size-sorted children; the tail becomes one merged block.
        var shown = Math.Min(children.Count, options.MaxItems);
        var rects = TreemapLayout.Squarify(children.Select(child => (double)child.Allocated).ToList(), bounds);
        for (var i = 0; i < shown; i++)
        {
            if (rects[i].MinSide < options.MinSide)
            {
                shown = i;
                break;
            }
        }

        var mergedCount = children.Count - shown;
        var mergedBytes = 0UL;
        if (mergedCount > 0)
        {
            mergedBytes = children.Skip(shown).Aggregate(0UL, (sum, child) => sum + child.Allocated);
            var weights = children.Take(shown).Select(child => (double)child.Allocated).Append(mergedBytes).ToList();
            rects = TreemapLayout.Squarify(weights, bounds);
        }

        var cells = new List<TreemapCell>(shown + 1);
        for (var i = 0; i < shown; i++)
        {
            cells.Add(new(children[i], rects[i], 0, 0));
        }

        if (mergedCount > 0)
        {
            cells.Add(new(null, rects[shown], mergedCount, mergedBytes));
        }

        return cells;
    }
}
