namespace ManagedDrive.App.Services;

/// <summary>
/// What the tray icon shows at one moment: which activity lights are on and which badge is drawn.
/// Also the key of a rendered icon, so there is one icon per distinct value.
/// </summary>
/// <param name="Read">Whether the read light (green, bottom left) is on.</param>
/// <param name="Write">Whether the write light (orange, bottom right) is on.</param>
/// <param name="Badge">
/// The badge drawn in the top-right corner; <see cref="TrayAlertLevel.None"/> also during the off
/// phase of a critical pulse.
/// </param>
public readonly record struct TrayIconVisual(bool Read, bool Write, TrayAlertLevel Badge)
{
    /// <summary>
    /// Gets the plain icon: no light, no badge.
    /// </summary>
    public static TrayIconVisual Idle => default;
}

/// <summary>
/// Decides what the tray icon shows over time, like the activity lights of a hard disk plus an
/// alert badge. Pure state machine: the caller supplies the time and drives one timer from
/// <see cref="GetNextChange"/>.
/// <list type="bullet">
/// <item>
/// <description>
/// The read and write lights are independent, so both are on while a disk is read and written at once.
/// A light stays on for <see cref="ActivityHold"/> after the last access it saw, which is longer than
/// <see cref="MountManager"/>'s 150 ms poll, so steady I/O keeps it lit instead of flickering.
/// </description>
/// </item>
/// <item>
/// <description>
/// A warning badge is steady. A critical badge pulses for <see cref="CriticalPulseDuration"/> after
/// the level rises to critical, then stays steady, so a lasting failure does not blink forever.
/// </description>
/// </item>
/// </list>
/// </summary>
public sealed class TrayIconStateMachine
{
    /// <summary>
    /// How long a light stays on after the last access reported for it.
    /// </summary>
    public static readonly TimeSpan ActivityHold = TimeSpan.FromMilliseconds(350);

    /// <summary>
    /// How long a critical badge pulses before it stays on.
    /// </summary>
    public static readonly TimeSpan CriticalPulseDuration = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long each on and each off phase of the critical pulse lasts.
    /// </summary>
    public static readonly TimeSpan PulsePhase = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Until when the read light is on; <c>null</c> once nothing was read.
    /// </summary>
    private DateTimeOffset? _readUntil;

    /// <summary>
    /// Until when the write light is on; <c>null</c> once nothing was written.
    /// </summary>
    private DateTimeOffset? _writeUntil;

    /// <summary>
    /// When the current critical pulse began; <c>null</c> when there is none.
    /// </summary>
    private DateTimeOffset? _pulseStart;

    /// <summary>
    /// Gets the alert level last set with <see cref="SetAlertLevel"/>.
    /// </summary>
    public TrayAlertLevel AlertLevel { get; private set; }

    /// <summary>
    /// Records an access seen by the activity poll.
    /// </summary>
    /// <param name="hadRead">Whether anything was read.</param>
    /// <param name="hadWrite">Whether anything was written.</param>
    /// <param name="now">The current time.</param>
    public void OnActivity(bool hadRead, bool hadWrite, DateTimeOffset now)
    {
        if (hadRead)
        {
            _readUntil = now + ActivityHold;
        }

        if (hadWrite)
        {
            _writeUntil = now + ActivityHold;
        }
    }

    /// <summary>
    /// Sets the alert level. Rising to <see cref="TrayAlertLevel.Critical"/> starts a new pulse;
    /// any other change ends the pulse.
    /// </summary>
    /// <param name="level">The new level.</param>
    /// <param name="now">The current time.</param>
    public void SetAlertLevel(TrayAlertLevel level, DateTimeOffset now)
    {
        if (level == AlertLevel)
        {
            return;
        }

        _pulseStart = level == TrayAlertLevel.Critical ? now : null;
        AlertLevel = level;
    }

    /// <summary>
    /// Starts the pulse again for a new failure while the level already is critical, so it is not
    /// lost in a badge that has gone steady. Does nothing below critical.
    /// </summary>
    /// <param name="now">The current time.</param>
    public void RestartPulse(DateTimeOffset now)
    {
        if (AlertLevel == TrayAlertLevel.Critical)
        {
            _pulseStart = now;
        }
    }

    /// <summary>
    /// Gets what the icon shows at <paramref name="now"/>.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The lights and the badge.</returns>
    public TrayIconVisual GetVisual(DateTimeOffset now)
    {
        var badge = AlertLevel;
        if (IsPulsing(now) && IsOffPhase(now))
        {
            badge = TrayAlertLevel.None;
        }

        return new(_readUntil > now, _writeUntil > now, badge);
    }

    /// <summary>
    /// Gets how long until <see cref="GetVisual"/> next changes by itself: a light going out or a
    /// pulse phase ending.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns>The delay, at least one millisecond; <c>null</c> when nothing changes until the next input.</returns>
    public TimeSpan? GetNextChange(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        Consider(_readUntil);
        Consider(_writeUntil);

        if (IsPulsing(now) && _pulseStart is { } start)
        {
            var phases = (long)((now - start) / PulsePhase) + 1;
            var boundary = start + PulsePhase * phases;
            var pulseEnd = start + CriticalPulseDuration;
            Consider(boundary < pulseEnd ? boundary : pulseEnd);
        }

        return next is { } at ? TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerMillisecond, (at - now).Ticks)) : null;

        void Consider(DateTimeOffset? candidate)
        {
            if (candidate > now && (next is null || candidate < next))
            {
                next = candidate;
            }
        }
    }

    /// <summary>
    /// Gets whether the critical badge is still pulsing.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c> during the first <see cref="CriticalPulseDuration"/> of a critical level.</returns>
    private bool IsPulsing(DateTimeOffset now) =>
        AlertLevel == TrayAlertLevel.Critical && _pulseStart is { } start && now - start < CriticalPulseDuration;

    /// <summary>
    /// Gets whether the pulse is in an off phase; the pulse starts with an on phase.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c> in every second phase.</returns>
    private bool IsOffPhase(DateTimeOffset now) =>
        _pulseStart is { } start && (long)((now - start) / PulsePhase) % 2 == 1;
}
