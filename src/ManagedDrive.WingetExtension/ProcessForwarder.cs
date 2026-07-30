using System.ComponentModel;
using System.Diagnostics;

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
            return RunCore(fileName, arguments, resolvedWorkingDirectory, elevate: true);
        }
    }

    private static int RunCore(string fileName, IReadOnlyList<string> arguments, string workingDirectory, bool elevate)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = elevate,
            Verb = elevate ? "runas" : string.Empty,
            WorkingDirectory = workingDirectory,
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
