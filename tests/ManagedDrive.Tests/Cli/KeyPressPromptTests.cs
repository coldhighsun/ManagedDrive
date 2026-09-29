using ManagedDrive.WingetExtension;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="KeyPressPrompt"/>.
/// </summary>
public sealed class KeyPressPromptTests
{
    /// <summary>
    /// With console input, the prompt is written and a key is read.
    /// </summary>
    [Fact]
    public void Wait_ConsoleInput_PromptsAndReadsKey()
    {
        var output = new StringWriter();
        var keyRead = false;

        KeyPressPrompt.Wait(isInputRedirected: false, output, () => keyRead = true);

        Assert.Equal($"Press any key to continue...{Environment.NewLine}", output.ToString());
        Assert.True(keyRead);
    }

    /// <summary>
    /// With redirected input there is no key to read, so nothing is prompted or read.
    /// </summary>
    [Fact]
    public void Wait_RedirectedInput_DoesNotPromptOrReadKey()
    {
        var output = new StringWriter();
        var keyRead = false;

        KeyPressPrompt.Wait(isInputRedirected: true, output, () => keyRead = true);

        Assert.Empty(output.ToString());
        Assert.False(keyRead);
    }

    /// <summary>
    /// A process without a console can't read a key; the wait ends instead of throwing, since the
    /// request has already been handled.
    /// </summary>
    [Fact]
    public void Wait_NoConsoleToReadFrom_DoesNotThrow()
    {
        var output = new StringWriter();

        var exception = Record.Exception(() =>
            KeyPressPrompt.Wait(isInputRedirected: false, output, () => throw new InvalidOperationException()));

        Assert.Null(exception);
    }
}
