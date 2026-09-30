using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace ManagedDrive.WingetExtension;

// Runs a child process with inherited stdin/stdout/stderr and returns its exit code.
// Machine-scope installers (msiexec, some Nullsoft/Inno installers) can require elevation;
// if the non-elevated launch fails with ERROR_ELEVATION_REQUIRED, retry once via ShellExecute+runas.
internal static class ProcessForwarder
{
    private const int ErrorElevationRequired = 740;

    public static int Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var resolvedWorkingDirectory = workingDirectory ?? Environment.CurrentDirectory;

        try
        {
            return RunCore(fileName, arguments, resolvedWorkingDirectory, elevate: false);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            try
            {
                return RunCore(fileName, arguments, resolvedWorkingDirectory, elevate: true);
            }
            catch (Win32Exception retryEx)
            {
                // Most likely the user declined the UAC prompt.
                return ReportStartFailure(fileName, retryEx);
            }
        }
        catch (Win32Exception ex)
        {
            // e.g. winget.exe or msiexec.exe missing, or the downloaded installer not runnable:
            // a one-line message and a failing exit code instead of an unhandled exception dump.
            return ReportStartFailure(fileName, ex);
        }
    }

    /// <summary>
    /// Runs a child process without inheriting the console and captures what it wrote to stdout
    /// and stderr, for probes whose output is inspected rather than shown.
    /// </summary>
    /// <param name="fileName">The program to run.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="timeout">How long to wait before killing it.</param>
    /// <param name="output">The captured stdout and stderr.</param>
    /// <returns>
    /// The exit code, or <c>null</c> if the process could not be started or did not finish within
    /// <paramref name="timeout"/>.
    /// </returns>
    public static int? RunCapture(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, out string output)
    {
        output = string.Empty;

        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // Both streams are drained concurrently so a full pipe buffer can't stall the child.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeout))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Joins arguments into one command-line string, quoted the way <c>CommandLineToArgvW</c> (and
    /// so msiexec, winget and installers) parses it back. Needed for the elevated retry, where
    /// ShellExecute takes a single string and ignores <see cref="ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    /// <param name="arguments">The arguments to join.</param>
    /// <returns>The command-line string.</returns>
    internal static string JoinArguments(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(QuoteArgument));

    /// <summary>
    /// Quotes one argument for a command line, escaping embedded quotes and the backslashes
    /// before them; returns it unchanged when no quoting is needed.
    /// </summary>
    /// <param name="argument">The argument to quote.</param>
    /// <returns>The argument as it should appear on the command line.</returns>
    internal static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;

        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                // Backslashes before a quote are doubled, and the quote itself escaped.
                quoted.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                quoted.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        // Trailing backslashes would otherwise escape the closing quote.
        quoted.Append('\\', backslashes * 2).Append('"');
        return quoted.ToString();
    }

    /// <summary>
    /// Writes a one-line error for a program that could not be started.
    /// </summary>
    /// <param name="fileName">The program that failed to start.</param>
    /// <param name="exception">Why it failed.</param>
    /// <returns>The failing exit code, 1.</returns>
    private static int ReportStartFailure(string fileName, Win32Exception exception)
    {
        Console.Error.WriteLine($"wingetx: could not start '{fileName}': {exception.Message}");
        return 1;
    }

    private static int RunCore(string fileName, IReadOnlyList<string> arguments, string workingDirectory, bool elevate)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = elevate,
            Verb = elevate ? "runas" : string.Empty,
            WorkingDirectory = workingDirectory,
        };

        if (elevate)
        {
            // ShellExecute silently ignores ArgumentList, which would run the elevated retry
            // with no arguments at all.
            startInfo.Arguments = JoinArguments(arguments);
        }
        else
        {
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        process.WaitForExit();
        return process.ExitCode;
    }
}
