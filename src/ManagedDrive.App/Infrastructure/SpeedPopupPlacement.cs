using System.Windows.Controls.Primitives;

namespace ManagedDrive.App.Infrastructure;

/// <summary>
/// Decides on which side of its speed row the speed-history popup opens.
/// </summary>
public static class SpeedPopupPlacement
{
    /// <summary>
    /// Chooses below the row when the popup fits there, otherwise above it when it fits there, otherwise the side
    /// with more room.
    /// </summary>
    /// <param name="spaceBelow">The room below the row inside the window.</param>
    /// <param name="spaceAbove">The room above the row inside the window.</param>
    /// <param name="popupHeight">The height the popup needs.</param>
    /// <returns><see cref="PlacementMode.Bottom"/> or <see cref="PlacementMode.Top"/>.</returns>
    public static PlacementMode Choose(double spaceBelow, double spaceAbove, double popupHeight)
    {
        if (spaceBelow >= popupHeight)
        {
            return PlacementMode.Bottom;
        }

        if (spaceAbove >= popupHeight)
        {
            return PlacementMode.Top;
        }

        return spaceAbove > spaceBelow ? PlacementMode.Top : PlacementMode.Bottom;
    }
}
