using ManagedDrive.App.Infrastructure;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests <see cref="UrlLauncher"/>, which keeps anything but web addresses away from the shell.
/// </summary>
public sealed class UrlLauncherTests
{
    /// <summary>
    /// Checks that http and https addresses are accepted.
    /// </summary>
    /// <param name="url">The address to check.</param>
    [Theory]
    [InlineData("https://github.com/sponsors/coldhighsun")]
    [InlineData("http://example.com/")]
    public void IsAllowed_WebUrl_ReturnsTrue(string url)
    {
        // Act
        var result = UrlLauncher.IsAllowed(url);

        // Assert
        Assert.True(result);
    }

    /// <summary>
    /// Checks that empty, relative and non-web addresses are refused.
    /// </summary>
    /// <param name="url">The address to check.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("github.com/sponsors")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ms-settings:network")]
    public void IsAllowed_NonWebOrInvalidUrl_ReturnsFalse(string? url)
    {
        // Act
        var result = UrlLauncher.IsAllowed(url);

        // Assert
        Assert.False(result);
    }

    /// <summary>
    /// Checks that a refused address makes <see cref="UrlLauncher.TryOpen"/> return false before anything is started.
    /// </summary>
    /// <param name="url">The address to open.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("ms-settings:network")]
    public void TryOpen_RefusedUrl_ReturnsFalse(string? url)
    {
        // Act
        var result = UrlLauncher.TryOpen(url);

        // Assert
        Assert.False(result);
    }
}
