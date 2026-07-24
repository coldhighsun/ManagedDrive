using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ManagedDrive.Service;

/// <summary>
/// Owns the set of global DOS-device symlinks the service has published, persists them to
/// <c>HKLM\SOFTWARE\ManagedDrive\GlobalMounts</c> so they survive a service restart, and
/// reconciles that set against reality (removing symlinks whose backing device has gone away).
/// All operations are serialized under a single lock — the pipe server may call in concurrently
/// with the periodic reconciliation sweep.
/// </summary>
public sealed partial class GlobalMountManager(ILogger<GlobalMountManager> logger)
{
    private const string RegistryKeyPath = @"SOFTWARE\ManagedDrive\GlobalMounts";

    private readonly Lock _lock = new();

    [GeneratedRegex(@"^[A-Za-z]:$")]
    private static partial Regex DriveLetterRegex();

    // WinFsp volumes surface as \Device\Volume{GUID}. Constrain hard: this service hands SYSTEM's
    // ability to create arbitrary global symlinks, so only well-formed volume device paths pass.
    [GeneratedRegex(@"^\\Device\\Volume\{[0-9A-Fa-f-]+\}$")]
    private static partial Regex DevicePathRegex();

    /// <summary>
    /// Publishes a global symlink <paramref name="letter"/> → <paramref name="devicePath"/> and
    /// records it for later cleanup.
    /// </summary>
    public (bool Success, string Message) Publish(string letter, string devicePath)
    {
        if (!DriveLetterRegex().IsMatch(letter))
        {
            return (false, $"Rejected drive letter '{letter}'.");
        }

        if (!DevicePathRegex().IsMatch(devicePath))
        {
            return (false, $"Rejected device path '{devicePath}'.");
        }

        lock (_lock)
        {
            if (!NativeMethods.CreateGlobalSymlink(letter, devicePath))
            {
                var error = Marshal.GetLastWin32Error();
                logger.LogError("DefineDosDevice create failed for {Letter} -> {Device}. Win32Error={Error}",
                    letter, devicePath, error);
                return (false, $"DefineDosDevice failed. Win32Error={error}");
            }

            WriteRegistryEntry(letter, devicePath);
            logger.LogInformation("Published global symlink {Letter} -> {Device}", letter, devicePath);
            return (true, $"Published {letter} -> {devicePath}");
        }
    }

    /// <summary>
    /// Removes the global symlink previously published for <paramref name="letter"/>, using the
    /// device path recorded at publish time for an exact-match removal.
    /// </summary>
    public (bool Success, string Message) Unpublish(string letter)
    {
        if (!DriveLetterRegex().IsMatch(letter))
        {
            return (false, $"Rejected drive letter '{letter}'.");
        }

        lock (_lock)
        {
            var devicePath = ReadRegistryEntry(letter);
            if (devicePath == null)
            {
                // Nothing recorded — treat as already gone rather than an error.
                return (true, $"No published symlink recorded for {letter}.");
            }

            bool removed = NativeMethods.RemoveGlobalSymlink(letter, devicePath);
            if (!removed)
            {
                logger.LogWarning("DefineDosDevice remove failed for {Letter} -> {Device}. Win32Error={Error}",
                    letter, devicePath, Marshal.GetLastWin32Error());
            }

            DeleteRegistryEntry(letter);
            logger.LogInformation("Unpublished global symlink {Letter}", letter);
            return (true, $"Unpublished {letter}");
        }
    }

    /// <summary>
    /// Removes every recorded symlink whose backing device is no longer present. Called once at
    /// startup (after a reboot all WinFsp devices are gone, so every stale entry is purged) and
    /// periodically thereafter to reclaim letters leaked by an app that crashed without
    /// unpublishing.
    /// </summary>
    public void Reconcile()
    {
        lock (_lock)
        {
            foreach (var (letter, devicePath) in ReadAllRegistryEntries())
            {
                if (NativeMethods.DeviceExists(devicePath))
                {
                    continue;
                }

                NativeMethods.RemoveGlobalSymlink(letter, devicePath);
                DeleteRegistryEntry(letter);
                logger.LogInformation("Reconciled stale symlink {Letter} -> {Device} (device gone)",
                    letter, devicePath);
            }
        }
    }

    private static void WriteRegistryEntry(string letter, string devicePath)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryKeyPath, writable: true);
        key.SetValue(letter, devicePath, RegistryValueKind.String);
    }

    private static string? ReadRegistryEntry(string letter)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: false);
        return key?.GetValue(letter) as string;
    }

    private static void DeleteRegistryEntry(string letter)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: true);
        key?.DeleteValue(letter, throwOnMissingValue: false);
    }

    private static IReadOnlyList<(string Letter, string DevicePath)> ReadAllRegistryEntries()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: false);
        if (key == null)
        {
            return [];
        }

        var entries = new List<(string, string)>();
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is string devicePath)
            {
                entries.Add((name, devicePath));
            }
        }

        return entries;
    }
}
