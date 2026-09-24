using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using ThrottledLogging;

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

    /// <summary>
    /// Publishes a global symlink <paramref name="letter"/> → <paramref name="devicePath"/> on
    /// behalf of <paramref name="callerSid"/> and records it for later cleanup. Refuses a letter
    /// that is already defined system-wide or published by someone else (see <see cref="GlobalMountPolicy"/>).
    /// </summary>
    /// <param name="letter">The drive letter, in <c>"X:"</c> form.</param>
    /// <param name="devicePath">The <c>\Device\Volume{GUID}</c> path to point it at.</param>
    /// <param name="callerSid">SID of the user requesting the publication.</param>
    /// <returns>Whether the letter is now published, and a diagnostic message.</returns>
    public (bool Success, string Message) Publish(string letter, string devicePath, string callerSid)
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
            // A record whose device has gone away is only waiting for the next reconciliation
            // sweep; clear it now so it neither blocks nor is mistaken for the caller's own.
            var recorded = ReadRegistryEntry(letter);
            if (recorded != null && !NativeMethods.DeviceExists(recorded.DevicePath))
            {
                NativeMethods.RemoveGlobalSymlink(letter, recorded.DevicePath);
                DeleteRegistryEntry(letter);
                recorded = null;
            }

            var decision = GlobalMountPolicy.DecidePublish(
                devicePath,
                callerSid,
                recorded,
                NativeMethods.DeviceExists(devicePath),
                NativeMethods.QueryDosDeviceTargets(letter));

            switch (decision)
            {
                case PublishDecision.AlreadyPublished:
                    WriteRegistryEntry(letter, new(devicePath, callerSid));
                    return (true, $"{letter} -> {devicePath} is already published.");

                case PublishDecision.RejectDeviceMissing:
                    return (false, $"Device '{devicePath}' does not exist.");

                case PublishDecision.RejectLetterInUse:
                    logger.LogWarning(
                        "Refused to publish {Letter} -> {Device} for {Sid}: letter already defined globally",
                        letter, devicePath, callerSid);
                    return (false, $"{letter} is already in use system-wide.");

                case PublishDecision.RejectPublishedByOther:
                    logger.LogWarning(
                        "Refused to publish {Letter} -> {Device} for {Sid}: letter published by another user or for another device",
                        letter, devicePath, callerSid);
                    return (false, $"{letter} is already published for another disk.");
            }

            if (!NativeMethods.CreateGlobalSymlink(letter, devicePath))
            {
                var error = Marshal.GetLastWin32Error();
                logger.LogError("DefineDosDevice create failed for {Letter} -> {Device}. Win32Error={Error}",
                    letter, devicePath, error);
                return (false, $"DefineDosDevice failed. Win32Error={error}");
            }

            if (!GlobalMountPolicy.IsSoleDefinition(NativeMethods.QueryDosDeviceTargets(letter), devicePath))
            {
                // Someone else defined the letter between the check above and our definition.
                NativeMethods.RemoveGlobalSymlink(letter, devicePath);
                logger.LogWarning(
                    "Withdrew {Letter} -> {Device} for {Sid}: letter was defined concurrently by someone else",
                    letter, devicePath, callerSid);
                return (false, $"{letter} is already in use system-wide.");
            }

            WriteRegistryEntry(letter, new(devicePath, callerSid));
            logger.LogInformation("Published global symlink {Letter} -> {Device} for {Sid}", letter, devicePath, callerSid);
            return (true, $"Published {letter} -> {devicePath}");
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
            foreach (var (letter, (devicePath, _)) in ReadAllRegistryEntries())
            {
                if (NativeMethods.DeviceExists(devicePath))
                {
                    continue;
                }

                NativeMethods.RemoveGlobalSymlink(letter, devicePath);
                DeleteRegistryEntry(letter);
                logger.LogInformationThrottled(
                    $"reconciled-stale:{letter}", TimeSpan.FromMinutes(10),
                    "Reconciled stale symlink {Letter} -> {Device} (device gone)", letter, devicePath);
            }
        }
    }

    /// <summary>
    /// Removes the global symlink previously published for <paramref name="letter"/>, using the
    /// device path recorded at publish time for an exact-match removal. Only the user who
    /// published it may remove it.
    /// </summary>
    /// <param name="letter">The drive letter, in <c>"X:"</c> form.</param>
    /// <param name="callerSid">SID of the user requesting the removal.</param>
    /// <returns>Whether the letter is no longer published, and a diagnostic message.</returns>
    public (bool Success, string Message) Unpublish(string letter, string callerSid)
    {
        if (!DriveLetterRegex().IsMatch(letter))
        {
            return (false, $"Rejected drive letter '{letter}'.");
        }

        lock (_lock)
        {
            var recorded = ReadRegistryEntry(letter);
            switch (GlobalMountPolicy.DecideUnpublish(callerSid, recorded))
            {
                case UnpublishDecision.NothingRecorded:
                    // Nothing recorded — treat as already gone rather than an error.
                    return (true, $"No published symlink recorded for {letter}.");

                case UnpublishDecision.RejectNotOwner:
                    logger.LogWarning("Refused to unpublish {Letter} for {Sid}: published by another user", letter, callerSid);
                    return (false, $"{letter} was published by another user.");
            }

            var devicePath = recorded!.DevicePath;
            var removed = NativeMethods.RemoveGlobalSymlink(letter, devicePath);
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

    private static void DeleteRegistryEntry(string letter)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: true);
        key?.DeleteValue(letter, throwOnMissingValue: false);
    }

    // WinFsp volumes surface as \Device\Volume{GUID}. Constrain hard: this service hands SYSTEM's
    // ability to create arbitrary global symlinks, so only well-formed volume device paths pass.
    [GeneratedRegex(@"^\\Device\\Volume\{[0-9A-Fa-f-]+\}$")]
    private static partial Regex DevicePathRegex();

    [GeneratedRegex(@"^[A-Za-z]:$")]
    private static partial Regex DriveLetterRegex();

    /// <summary>
    /// Decodes a registry value written by <see cref="WriteRegistryEntry"/>: a
    /// <c>REG_MULTI_SZ</c> of device path and owner SID, or a plain <c>REG_SZ</c> device path
    /// written before owners were recorded.
    /// </summary>
    /// <param name="value">The raw registry value.</param>
    /// <returns>The recorded publication, or <c>null</c> if the value is in neither format.</returns>
    internal static PublishedMount? ParseRegistryValue(object? value) => value switch
    {
        string devicePath => new(devicePath, null),
        string[] { Length: >= 2 } parts => new(parts[0], string.IsNullOrEmpty(parts[1]) ? null : parts[1]),
        string[] { Length: 1 } parts => new(parts[0], null),
        _ => null,
    };

    /// <summary>
    /// Reads every recorded publication.
    /// </summary>
    /// <returns>The recorded letters and their publications.</returns>
    private static IReadOnlyList<(string Letter, PublishedMount Mount)> ReadAllRegistryEntries()
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: false);
        if (key == null)
        {
            return [];
        }

        var entries = new List<(string, PublishedMount)>();
        foreach (var name in key.GetValueNames())
        {
            if (ParseRegistryValue(key.GetValue(name)) is { } mount)
            {
                entries.Add((name, mount));
            }
        }

        return entries;
    }

    /// <summary>
    /// Reads the recorded publication for <paramref name="letter"/>.
    /// </summary>
    /// <param name="letter">The drive letter, in <c>"X:"</c> form.</param>
    /// <returns>The recorded publication, or <c>null</c> if none.</returns>
    private static PublishedMount? ReadRegistryEntry(string letter)
    {
        using var key = Registry.LocalMachine.OpenSubKey(RegistryKeyPath, writable: false);
        return ParseRegistryValue(key?.GetValue(letter));
    }

    /// <summary>
    /// Records <paramref name="mount"/> as published for <paramref name="letter"/>.
    /// </summary>
    /// <param name="letter">The drive letter, in <c>"X:"</c> form.</param>
    /// <param name="mount">The publication to record.</param>
    private static void WriteRegistryEntry(string letter, PublishedMount mount)
    {
        using var key = Registry.LocalMachine.CreateSubKey(RegistryKeyPath, writable: true);
        key.SetValue(letter, new[] { mount.DevicePath, mount.OwnerSid ?? string.Empty }, RegistryValueKind.MultiString);
    }
}
