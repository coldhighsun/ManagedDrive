namespace ManagedDrive.App.Services;

/// <summary>
/// Tracks whether the tray icon is visible only so balloon tips could be shown (it is otherwise
/// hidden while the main window is open) and decides when it can be hidden again. Counts the
/// balloons still open instead of hiding on the first close: a balloon shown while another is up
/// makes Windows close the earlier one, and hiding the icon on that close would take the new
/// balloon down with it.
/// </summary>
internal sealed class BalloonOnlyIconTracker
{
    /// <summary>
    /// The balloons shown on an icon made visible only for them that have not closed yet.
    /// </summary>
    private int _openBalloons;

    /// <summary>
    /// Records a balloon about to be shown.
    /// </summary>
    /// <param name="iconVisible">Whether the tray icon is currently visible.</param>
    /// <returns>Whether the icon must be made visible for the balloon.</returns>
    public bool OnBalloonShowing(bool iconVisible)
    {
        if (!iconVisible)
        {
            _openBalloons = 1;
            return true;
        }

        // An icon that is visible for its own sake (window hidden to the tray) stays untracked.
        if (_openBalloons > 0)
        {
            _openBalloons++;
        }

        return false;
    }

    /// <summary>
    /// Records that a balloon closed (timed out, was dismissed or replaced, or was clicked).
    /// </summary>
    /// <returns>Whether that was the last open balloon, so the icon should be hidden again.</returns>
    public bool OnBalloonClosed()
    {
        if (_openBalloons == 0)
        {
            return false;
        }

        _openBalloons--;
        return _openBalloons == 0;
    }

    /// <summary>
    /// Stops tracking, e.g. when the icon's visibility is set explicitly or the main window comes
    /// back into view before every balloon's close notification arrived.
    /// </summary>
    /// <returns>Whether the icon was visible only for balloons and should be hidden.</returns>
    public bool Reset()
    {
        var wasShownForBalloonsOnly = _openBalloons > 0;
        _openBalloons = 0;
        return wasShownForBalloonsOnly;
    }
}
