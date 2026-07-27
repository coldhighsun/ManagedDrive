using System.Diagnostics;

namespace ManagedDrive.WingetExtension;

// Runs a child process with inherited stdin/stdout/stderr and returns its exit code.
internal static class ProcessForwarder
{
    public static int Run(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        process.WaitForExit();
        return process.ExitCode;
    }
}
