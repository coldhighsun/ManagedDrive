using Fsp;

namespace ManagedDrive.Core;

/// <summary>
/// Represents a single mounted RAM disk. Wraps a <see cref="MemoryFileSystem"/> and a
/// <see cref="FileSystemHost"/> and manages the mount/unmount lifecycle.
/// Dispose to unmount and free all resources.
/// </summary>
public sealed class RamDisk : IDisposable
{
    private const uint ShcneDriveadd = 0x00000100;
    private const uint ShcnfFlush = 0x1000;
    private const uint ShcnfPath = 0x0005;
    private readonly MemoryFileSystem _fs;
    private readonly FileSystemHost _host;
    private bool _disposed;

    private RamDisk(MemoryFileSystem fs, FileSystemHost host, DiskOptions options)
    {
        _fs = fs;
        _host = host;
        Options = options;
    }

    /// <summary>
    /// Gets the number of bytes currently available on this RAM disk.
    /// </summary>
    public ulong FreeBytes => TotalBytes > UsedBytes ? TotalBytes - UsedBytes : 0;

    /// <summary>
    /// Gets the mount point string as reported by WinFsp after a successful mount.
    /// </summary>
    public string MountPoint => _host.MountPoint() ?? Options.MountPoint;

    /// <summary>
    /// Gets the configuration used to create this disk.
    /// </summary>
    public DiskOptions Options
    {
        get;
    }

    /// <summary>
    /// Gets the total configured capacity of this RAM disk in bytes.
    /// </summary>
    public ulong TotalBytes => Options.CapacityBytes;

    /// <summary>
    /// Gets the number of bytes currently allocated by files on this RAM disk.
    /// </summary>
    public ulong UsedBytes => _fs.NodeMap.GetTotalAllocated();

    /// <summary>
    /// Creates and mounts a new RAM disk according to the supplied options.
    /// If <see cref="DiskOptions.PersistImagePath"/> points to an existing file,
    /// its contents are restored into the new disk.
    /// </summary>
    /// <param name="options">Mount configuration.</param>
    /// <returns>
    /// A fully mounted <see cref="RamDisk"/> instance.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when WinFsp returns a non-zero NTSTATUS from <c>Mount</c>.
    /// </exception>
    public static RamDisk Create(DiskOptions options)
    {
        MemoryFileSystem fs;

        if (options.PersistImagePath != null &&
            File.Exists(options.PersistImagePath))
        {
            var nodeMap = DiskImageSerializer.Load(
                options.PersistImagePath,
                out var savedCapacity,
                out var savedLabel);

            var capacity = savedCapacity > 0 ? savedCapacity : options.CapacityBytes;
            var label = string.IsNullOrEmpty(savedLabel) ? options.VolumeLabel : savedLabel;
            fs = new MemoryFileSystem(capacity, label, nodeMap, options.ReadOnly);
        }
        else
        {
            fs = new MemoryFileSystem(options.CapacityBytes, options.VolumeLabel, options.ReadOnly);
        }

        var host = new FileSystemHost(fs);
        ConfigureHost(host);

        var status = host.Mount(options.MountPoint);
        if (status != FileSystemBase.STATUS_SUCCESS)
        {
            host.Dispose();
            throw new InvalidOperationException(
                $"WinFsp Mount failed for '{options.MountPoint}'. NTSTATUS: 0x{(uint)status:X8}");
        }

        // host.Mount with Synchronized=false starts the WinFsp dispatcher in a background
        // thread and returns immediately.  The drive letter is only visible in the OS once
        // that thread has completed the FspVolumeMount IOCTL.  Poll until it appears.
        if (IsDriveLetter(options.MountPoint) && !WaitForDriveVisible(options.MountPoint))
        {
            host.Dispose();
            throw new InvalidOperationException(
                $"WinFsp did not expose drive '{options.MountPoint}' within 2.5 s. " +
                "Verify that the WinFsp kernel driver is loaded and that the drive letter is not already in use.");
        }

        // Notify Windows Shell (Explorer) so the drive appears immediately.
        if (IsDriveLetter(options.MountPoint))
        {
            NotifyShellDriveAdded(options.MountPoint);
        }

        return new RamDisk(fs, host, options);
    }

    /// <summary>
    /// Unmounts the disk and releases all resources. After disposal the disk is no longer
    /// accessible from the Windows shell.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _host.Unmount();
            _host.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Serializes the current disk contents to <see cref="DiskOptions.PersistImagePath"/>.
    /// Does nothing if <see cref="DiskOptions.PersistImagePath"/> is <c>null</c>.
    /// </summary>
    public void SaveToImage()
    {
        if (Options.PersistImagePath == null)
        {
            return;
        }

        DiskImageSerializer.Save(
            _fs.NodeMap,
            Options.CapacityBytes,
            Options.VolumeLabel,
            Options.PersistImagePath);
    }

    private static void ConfigureHost(FileSystemHost host)
    {
        host.SectorSize = (ushort)FileNode.AllocationUnit;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileSystemName = "MemFS";
        host.CasePreservedNames = true;
        host.CaseSensitiveSearch = false;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.FileInfoTimeout = 1000;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = (uint)new Random().Next(int.MaxValue / 2);
    }

    /// <summary>
    /// Returns <c>true</c> when <paramref name="mountPoint"/> is a Windows drive-letter path
    /// of the form <c>X:</c> (single letter followed by a colon).
    /// </summary>
    private static bool IsDriveLetter(string mountPoint) =>
        mountPoint.Length == 2 && char.IsLetter(mountPoint[0]) && mountPoint[1] == ':';

    /// <summary>
    /// Broadcasts a <c>SHCNE_DRIVEADD</c> Shell change notification so that Windows Explorer
    /// immediately refreshes and shows the newly mounted drive letter.
    /// </summary>
    /// <param name="mountPoint">Drive-letter mount point in the form <c>X:</c>.</param>
    private static void NotifyShellDriveAdded(string mountPoint)
    {
        // Shell expects the path to end with a backslash.
        var path = mountPoint.TrimEnd('\\') + '\\';
        SHChangeNotify(ShcneDriveadd, ShcnfPath | ShcnfFlush, path, null);
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    private static extern void SHChangeNotify(uint wEventId, uint uFlags, string? dwItem1, string? dwItem2);

    /// <summary>
    /// Polls <see cref="System.IO.DriveInfo.GetDrives"/> until the drive letter described by
    /// <paramref name="mountPoint"/> is reported by the OS, or a 2.5-second timeout elapses.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the drive became visible; <c>false</c> when the timeout expired.
    /// </returns>
    private static bool WaitForDriveVisible(string mountPoint)
    {
        for (var attempt = 0; attempt < 25; attempt++)
        {
            Thread.Sleep(100);

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (string.Equals(
                    drive.Name.TrimEnd('\\'),
                    mountPoint,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}