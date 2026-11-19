using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests the timing rules of <see cref="TrayIconStateMachine"/>.
/// </summary>
public sealed class TrayIconStateMachineTests
{
    /// <summary>
    /// A fixed point in time the tests count from.
    /// </summary>
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies that a fresh machine shows the plain icon and has nothing scheduled.
    /// </summary>
    [Fact]
    public void GetVisual_NoInput_IsIdleWithNothingScheduled()
    {
        // Arrange
        var machine = new TrayIconStateMachine();

        // Act
        var visual = machine.GetVisual(Start);

        // Assert
        Assert.Equal(TrayIconVisual.Idle, visual);
        Assert.Null(machine.GetNextChange(Start));
    }

    /// <summary>
    /// Verifies that a read and a write in the same poll light both lights.
    /// </summary>
    [Fact]
    public void OnActivity_ReadAndWrite_LightsBothLights()
    {
        // Arrange
        var machine = new TrayIconStateMachine();

        // Act
        machine.OnActivity(hadRead: true, hadWrite: true, Start);

        // Assert
        Assert.Equal(new TrayIconVisual(true, true, TrayAlertLevel.None), machine.GetVisual(Start));
    }

    /// <summary>
    /// Verifies that a write does not put out a read light that is still on.
    /// </summary>
    [Fact]
    public void OnActivity_WriteAfterRead_KeepsTheReadLight()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        machine.OnActivity(hadRead: true, hadWrite: false, Start);

        // Act
        machine.OnActivity(hadRead: false, hadWrite: true, Start + TimeSpan.FromMilliseconds(150));

        // Assert
        Assert.Equal(new TrayIconVisual(true, true, TrayAlertLevel.None), machine.GetVisual(Start + TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>
    /// Verifies that a light goes out once the hold time has passed and that the change is scheduled for exactly then.
    /// </summary>
    [Fact]
    public void GetVisual_AfterTheHold_LightGoesOut()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        machine.OnActivity(hadRead: true, hadWrite: false, Start);

        // Act
        var next = machine.GetNextChange(Start);
        var after = machine.GetVisual(Start + TrayIconStateMachine.ActivityHold);

        // Assert
        Assert.Equal(TrayIconStateMachine.ActivityHold, next);
        Assert.Equal(TrayIconVisual.Idle, after);
        Assert.Null(machine.GetNextChange(Start + TrayIconStateMachine.ActivityHold));
    }

    /// <summary>
    /// Verifies that activity reported every 150 ms (the poll interval) keeps the light on without a gap.
    /// </summary>
    [Fact]
    public void OnActivity_SteadyPolls_LightStaysOn()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        var poll = TimeSpan.FromMilliseconds(150);

        for (var i = 0; i < 10; i++)
        {
            // Act
            machine.OnActivity(hadRead: false, hadWrite: true, Start + poll * i);

            // Assert
            Assert.True(machine.GetVisual(Start + poll * i + TimeSpan.FromMilliseconds(149)).Write);
        }
    }

    /// <summary>
    /// Verifies that a warning badge is steady and schedules nothing.
    /// </summary>
    [Fact]
    public void SetAlertLevel_Warning_SteadyBadge()
    {
        // Arrange
        var machine = new TrayIconStateMachine();

        // Act
        machine.SetAlertLevel(TrayAlertLevel.Warning, Start);

        // Assert
        Assert.Equal(TrayAlertLevel.Warning, machine.GetVisual(Start + TimeSpan.FromMinutes(5)).Badge);
        Assert.Null(machine.GetNextChange(Start));
    }

    /// <summary>
    /// Verifies that a critical badge starts on, turns off in the second phase and on again in the third.
    /// </summary>
    [Fact]
    public void GetVisual_Critical_PulsesOnOff()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        var phase = TrayIconStateMachine.PulsePhase;

        // Act
        machine.SetAlertLevel(TrayAlertLevel.Critical, Start);

        // Assert
        Assert.Equal(TrayAlertLevel.Critical, machine.GetVisual(Start).Badge);
        Assert.Equal(TrayAlertLevel.None, machine.GetVisual(Start + phase).Badge);
        Assert.Equal(TrayAlertLevel.Critical, machine.GetVisual(Start + phase * 2).Badge);
        Assert.Equal(phase, machine.GetNextChange(Start));
    }

    /// <summary>
    /// Verifies that the pulse ends after its duration and the critical badge then stays on.
    /// </summary>
    [Fact]
    public void GetVisual_CriticalAfterThePulse_StaysOn()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        machine.SetAlertLevel(TrayAlertLevel.Critical, Start);
        var later = Start + TrayIconStateMachine.CriticalPulseDuration;

        // Act
        var visuals = Enumerable.Range(0, 5).Select(i => machine.GetVisual(later + TrayIconStateMachine.PulsePhase * i)).ToList();

        // Assert
        Assert.All(visuals, v => Assert.Equal(TrayAlertLevel.Critical, v.Badge));
        Assert.Null(machine.GetNextChange(later));
    }

    /// <summary>
    /// Verifies that falling back to a warning ends the pulse at once.
    /// </summary>
    [Fact]
    public void SetAlertLevel_CriticalToWarning_StopsThePulse()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        machine.SetAlertLevel(TrayAlertLevel.Critical, Start);

        // Act
        machine.SetAlertLevel(TrayAlertLevel.Warning, Start + TrayIconStateMachine.PulsePhase);

        // Assert
        Assert.Equal(TrayAlertLevel.Warning, machine.GetVisual(Start + TrayIconStateMachine.PulsePhase).Badge);
        Assert.Null(machine.GetNextChange(Start + TrayIconStateMachine.PulsePhase));
    }

    /// <summary>
    /// Verifies that setting critical again while already critical does not restart the pulse.
    /// </summary>
    [Fact]
    public void SetAlertLevel_CriticalAgain_DoesNotRestartThePulse()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        machine.SetAlertLevel(TrayAlertLevel.Critical, Start);
        var later = Start + TrayIconStateMachine.CriticalPulseDuration;

        // Act
        machine.SetAlertLevel(TrayAlertLevel.Critical, later);

        // Assert
        Assert.Null(machine.GetNextChange(later));
    }

    /// <summary>
    /// Verifies that a new failure while the badge has gone steady makes it pulse again.
    /// </summary>
    [Fact]
    public void RestartPulse_WhileSteadyCritical_PulsesAgain()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        machine.SetAlertLevel(TrayAlertLevel.Critical, Start);
        var later = Start + TimeSpan.FromMinutes(5);

        // Act
        machine.RestartPulse(later);

        // Assert
        Assert.Equal(TrayAlertLevel.None, machine.GetVisual(later + TrayIconStateMachine.PulsePhase).Badge);
        Assert.Equal(TrayIconStateMachine.PulsePhase, machine.GetNextChange(later));
    }

    /// <summary>
    /// Verifies that restarting the pulse below the critical level changes nothing.
    /// </summary>
    [Fact]
    public void RestartPulse_AtWarning_StaysSteady()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        machine.SetAlertLevel(TrayAlertLevel.Warning, Start);

        // Act
        machine.RestartPulse(Start);

        // Assert
        Assert.Null(machine.GetNextChange(Start));
    }

    /// <summary>
    /// Verifies that a critical level reached again after it was cleared pulses again.
    /// </summary>
    [Fact]
    public void SetAlertLevel_CriticalAfterClear_PulsesAgain()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        machine.SetAlertLevel(TrayAlertLevel.Critical, Start);
        var later = Start + TimeSpan.FromMinutes(1);
        machine.SetAlertLevel(TrayAlertLevel.None, later);

        // Act
        machine.SetAlertLevel(TrayAlertLevel.Critical, later);

        // Assert
        Assert.Equal(TrayAlertLevel.None, machine.GetVisual(later + TrayIconStateMachine.PulsePhase).Badge);
    }

    /// <summary>
    /// Verifies that the next change is the earlier of a light going out and a pulse phase ending.
    /// </summary>
    [Fact]
    public void GetNextChange_LightAndPulse_TakesTheEarlier()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        machine.SetAlertLevel(TrayAlertLevel.Critical, Start);
        machine.OnActivity(hadRead: true, hadWrite: false, Start + TimeSpan.FromMilliseconds(400));

        // Act
        var next = machine.GetNextChange(Start + TimeSpan.FromMilliseconds(400));

        // Assert: the phase ends at 500 ms, the light only at 750 ms.
        Assert.Equal(TimeSpan.FromMilliseconds(100), next);
    }

    /// <summary>
    /// Verifies that the visual combines lights and badge.
    /// </summary>
    [Fact]
    public void GetVisual_ActivityDuringWarning_ShowsBoth()
    {
        // Arrange
        var machine = new TrayIconStateMachine();
        machine.SetAlertLevel(TrayAlertLevel.Warning, Start);

        // Act
        machine.OnActivity(hadRead: false, hadWrite: true, Start);

        // Assert
        Assert.Equal(new TrayIconVisual(false, true, TrayAlertLevel.Warning), machine.GetVisual(Start));
    }
}
