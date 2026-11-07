namespace ManagedDrive.App.Services;

/// <summary>
/// Remembers where a click on the current balloon tip leads. A click carries no balloon id, so it
/// can only be matched to the latest balloon. There is deliberately no way to clear the target when
/// a balloon closes: the close of a replaced balloon can arrive after its successor was shown, and
/// would wipe that one's target.
/// </summary>
internal sealed class BalloonClickTargetTracker
{
    /// <summary>
    /// The latest balloon's target, until it is clicked.
    /// </summary>
    private NotificationClickTarget? _target;

    /// <summary>
    /// Records a balloon about to be shown. A balloon without a target replaces the previous
    /// target too, or a click on it would run the previous balloon's action.
    /// </summary>
    /// <param name="target">Where clicking the balloon leads; <c>null</c> when it only informs.</param>
    public void OnBalloonShowing(NotificationClickTarget? target) => _target = target;

    /// <summary>
    /// Takes the target for a click, so that a second click does not run it again.
    /// </summary>
    /// <returns>The target, or <c>null</c> if the click leads nowhere.</returns>
    public NotificationClickTarget? TakeForClick()
    {
        var target = _target;
        _target = null;
        return target;
    }
}
