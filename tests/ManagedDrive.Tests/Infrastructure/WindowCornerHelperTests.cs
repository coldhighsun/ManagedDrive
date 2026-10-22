using System.Windows;
using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="WindowCornerHelper"/>: the decision whether the running Windows build rounds
/// window corners and draws the border of a borderless window itself, and how the corners are given
/// to and taken from a window.
/// </summary>
public sealed class WindowCornerHelperTests
{
    /// <summary>
    /// Verifies that the Windows 10 builds, which do not know the corner attribute, are not supported.
    /// </summary>
    [Theory]
    [InlineData(10240)]
    [InlineData(19045)]
    [InlineData(21999)]
    public void IsSupported_BuildBeforeWindows11_ReturnsFalse(int build)
    {
        // Act
        var supported = WindowCornerHelper.IsSupported(build);

        // Assert
        Assert.False(supported);
    }

    /// <summary>
    /// Verifies that Windows 11 builds, from the first one (22000) on, are supported.
    /// </summary>
    [Theory]
    [InlineData(22000)]
    [InlineData(22631)]
    [InlineData(26300)]
    public void IsSupported_Windows11OrLater_ReturnsTrue(int build)
    {
        // Act
        var supported = WindowCornerHelper.IsSupported(build);

        // Assert
        Assert.True(supported);
    }

    /// <summary>
    /// Verifies that where DWM cannot round the corners (Windows 10) the window gets a 1 px outline
    /// of its own instead.
    /// </summary>
    [Fact]
    public void Apply_CornersNotSupported_GivesWindowAnOutline()
    {
        StaThread.Run(() =>
        {
            // Arrange
            var window = new Window();

            // Act
            WindowCornerHelper.Apply(window, nativeCornersSupported: false);

            // Assert
            Assert.Equal(new Thickness(1), window.BorderThickness);
        });
    }

    /// <summary>
    /// Verifies that the outline goes away again when the corners are taken from the window.
    /// </summary>
    [Fact]
    public void Remove_CornersNotSupported_RemovesTheOutline()
    {
        StaThread.Run(() =>
        {
            // Arrange
            var window = new Window();
            WindowCornerHelper.Apply(window, nativeCornersSupported: false);

            // Act
            WindowCornerHelper.Remove(window, nativeCornersSupported: false);

            // Assert
            Assert.Equal(new Thickness(0), window.BorderThickness);
        });
    }

    /// <summary>
    /// Verifies that where DWM rounds the corners no outline is drawn by the window, and that a window
    /// without a native handle yet is left alone by both calls.
    /// </summary>
    [Fact]
    public void ApplyAndRemove_CornersSupportedAndNoHandle_LeaveTheBorderAlone()
    {
        StaThread.Run(() =>
        {
            // Arrange
            var window = new Window();

            // Act
            WindowCornerHelper.Apply(window, nativeCornersSupported: true);
            WindowCornerHelper.Remove(window, nativeCornersSupported: true);

            // Assert
            Assert.Equal(new Thickness(0), window.BorderThickness);
        });
    }

    /// <summary>
    /// Verifies that the attached property drives the corners on this Windows version: switching it on
    /// gives the window the outline exactly where DWM cannot round the corners, and switching it off
    /// takes it away again.
    /// </summary>
    [Fact]
    public void SetUseNativeCorners_OnThenOff_AppliesAndRemovesTheCorners()
    {
        StaThread.Run(() =>
        {
            // Arrange
            var window = new Window();
            var outline = WindowCornerHelper.IsSupportedByThisOs ? new Thickness(0) : new Thickness(1);

            // Act
            WindowCornerHelper.SetUseNativeCorners(window, true);
            var whileOn = window.BorderThickness;
            WindowCornerHelper.SetUseNativeCorners(window, false);
            var afterOff = window.BorderThickness;

            // Assert
            Assert.Equal(outline, whileOn);
            Assert.Equal(new Thickness(0), afterOff);
        });
    }

    /// <summary>
    /// Verifies that a style with a setter for the property switches it on for a window that takes the
    /// style, which is how <c>BorderlessWindowStyle</c> gives its windows the corners without any call.
    /// </summary>
    [Fact]
    public void Style_SetterOnTheProperty_TurnsTheCornersOn()
    {
        StaThread.Run(() =>
        {
            // Arrange
            var style = new Style(typeof(Window));
            style.Setters.Add(new Setter(WindowCornerHelper.UseNativeCornersProperty, true));
            var window = new Window { Style = style };

            // Act
            window.ApplyTemplate();

            // Assert
            Assert.True(WindowCornerHelper.GetUseNativeCorners(window));
        });
    }
}
