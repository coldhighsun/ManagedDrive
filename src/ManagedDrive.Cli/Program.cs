using ManagedDrive.Cli.Core;
using System.Diagnostics;

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

    // Relative path arguments need no rewriting here: CliPipeClient sends this process's working
    // directory along with the args, and the app resolves paths against it.
    public static async Task<int> Main(string[] args)
    {
        if (CliPipeClient.TrySend(args, out var response))
        {
            return CliOutputRenderer.Render(response);
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

            if (CliPipeClient.TrySend(args, out response))
            {
                return CliOutputRenderer.Render(response);
            }
        }

        await Console.Error.WriteLineAsync("Timed out waiting for ManagedDrive to start.");
        return 1;
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
