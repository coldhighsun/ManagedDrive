using System.Diagnostics;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.Cli;

/// <summary>
/// Console-subsystem entry point for the <c>mdrive</c> CLI. Unlike <c>ManagedDrive.exe</c>
/// (a <c>WinExe</c>), this is a real console-subsystem executable, so the invoking shell
/// naturally blocks until it exits — CLI output can never race with the shell prompt returning.
/// </summary>
public static class Program
{
    private const int LaunchWaitTimeoutMs = 10_000;
    private const int RetryIntervalMs = 200;

    private static readonly string[] PathArgCommands = ["mount", "mount-archive"];

    public static async Task<int> Main(string[] args)
    {
        args = ResolvePathArgument(args);

        if (CliPipeClient.TrySend(args, out var output, out var exitCode))
        {
            return Print(output, exitCode);
        }

        if (!TryLaunchApp())
        {
            await Console.Error.WriteLineAsync("Could not find or start ManagedDrive.exe.");
            return 1;
        }

        var deadline = Environment.TickCount64 + LaunchWaitTimeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(RetryIntervalMs);

            if (CliPipeClient.TrySend(args, out output, out exitCode))
            {
                return Print(output, exitCode);
            }
        }

        await Console.Error.WriteLineAsync("Timed out waiting for ManagedDrive to start.");
        return 1;
    }

    /// <summary>
    /// Resolves the image/archive path argument of a <c>mount</c>/<c>mount-archive</c> command to
    /// an absolute path before it's sent over the named pipe. <paramref name="args"/> is parsed
    /// by <c>System.CommandLine</c> inside the <c>ManagedDrive.exe</c> process, so a relative
    /// path would otherwise resolve against that process's working directory instead of the
    /// shell the user actually invoked <c>mdrive</c> from.
    /// </summary>
    private static string[] ResolvePathArgument(string[] args)
    {
        if (args.Length < 2 || !PathArgCommands.Contains(args[0]) || args[1].StartsWith('-'))
        {
            return args;
        }

        var resolved = (string[])args.Clone();
        resolved[1] = Path.GetFullPath(args[1]);
        return resolved;
    }

    private static int Print(string output, int exitCode)
    {
        if (exitCode == 0)
        {
            Console.WriteLine(output);
        }
        else
        {
            Console.Error.WriteLine(output);
        }

        return exitCode;
    }

    private static bool TryLaunchApp()
    {
        var exePath = Path.Combine(AppContext.BaseDirectory, "ManagedDrive.exe");
        if (!File.Exists(exePath))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = false });
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}