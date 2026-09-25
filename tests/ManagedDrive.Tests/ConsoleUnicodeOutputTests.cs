using ManagedDrive.Cli;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="ConsoleUnicodeOutput"/>.
/// </summary>
public sealed class ConsoleUnicodeOutputTests
{
    /// <summary>
    /// Output is switched to UTF-16 only when standard output goes to the console, since a
    /// redirected standard output would receive raw UTF-16 bytes.
    /// </summary>
    /// <param name="isOutputRedirected">Whether standard output is redirected.</param>
    /// <param name="expected">Whether to switch to UTF-16.</param>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void ShouldEnable_OutputRedirectionState_EnablesOnlyForConsoleOutput(bool isOutputRedirected, bool expected)
    {
        var shouldEnable = ConsoleUnicodeOutput.ShouldEnable(isOutputRedirected);

        Assert.Equal(expected, shouldEnable);
    }
}
