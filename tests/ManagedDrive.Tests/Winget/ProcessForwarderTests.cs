using ManagedDrive.WingetExtension;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="ProcessForwarder"/>.
/// </summary>
public sealed class ProcessForwarderTests
{
    /// <summary>
    /// Arguments are quoted so that <c>CommandLineToArgvW</c> parses them back unchanged; this
    /// matters for the elevated retry, which passes one command-line string.
    /// </summary>
    /// <param name="argument">The argument to quote.</param>
    /// <param name="expected">The quoted form.</param>
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("with space", "\"with space\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\my dir\", "\"C:\\my dir\\\\\"")]
    [InlineData(@"C:\nospace\", @"C:\nospace\")]
    public void QuoteArgument_VariousInputs_QuotesForCommandLineParsing(string argument, string expected)
    {
        Assert.Equal(expected, ProcessForwarder.QuoteArgument(argument));
    }

    /// <summary>
    /// Several arguments are joined with spaces, each quoted as needed.
    /// </summary>
    [Fact]
    public void JoinArguments_SeveralArguments_JoinsQuotedArguments()
    {
        Assert.Equal("/i \"C:\\a b\\x.msi\" /qn", ProcessForwarder.JoinArguments(["/i", @"C:\a b\x.msi", "/qn"]));
    }

    /// <summary>
    /// A program's output and exit code are captured without inheriting the console.
    /// </summary>
    [Fact]
    public void RunCapture_ProgramWritingOutput_ReturnsExitCodeAndOutput()
    {
        var exitCode = ProcessForwarder.RunCapture("cmd.exe", ["/c", "echo captured-text"], TimeSpan.FromSeconds(30), out var output);

        Assert.Equal(0, exitCode);
        Assert.Contains("captured-text", output);
    }

    /// <summary>
    /// A program that doesn't exist yields <c>null</c> instead of throwing.
    /// </summary>
    [Fact]
    public void RunCapture_MissingProgram_ReturnsNull()
    {
        var exitCode = ProcessForwarder.RunCapture("definitely-not-a-real-program.exe", [], TimeSpan.FromSeconds(5), out _);

        Assert.Null(exitCode);
    }

    /// <summary>
    /// A program that can't be started is reported with a failing exit code, not an unhandled
    /// exception.
    /// </summary>
    [Fact]
    public void Run_MissingProgram_ReturnsFailureExitCode()
    {
        var originalError = Console.Error;
        Console.SetError(TextWriter.Null);
        try
        {
            Assert.Equal(1, ProcessForwarder.Run("definitely-not-a-real-program.exe", []));
        }
        finally
        {
            Console.SetError(originalError);
        }
    }
}
