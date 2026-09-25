using ManagedDrive.HelperProtocol;

namespace ManagedDrive.App.Services;

/// <summary>
/// Bridges each disk's "is this the current TEMP directory?" state to the SYSTEM helper service,
/// which publishes or removes a global (<c>\GLOBAL??</c>) DOS-device symlink for the drive so the
/// disk is reachable from other sessions (e.g. installers that access TEMP via the global device
/// namespace and otherwise fail with <c>0x800704b3</c>).
///
/// The trigger is <see cref="DiskViewModel.IsCurrentTempDir"/>: whatever code path flips it
/// (interactive toggle, unmount auto-reset, tray reset, CLI) funnels through the same
/// <see cref="INotifyPropertyChanged"/> notification, so this one observer covers them all.
/// Every call is best-effort — if the helper service is not installed/running, the disk still
/// works, just without cross-session visibility.
///
/// Only drive letters are published (see <see cref="IsPublishable"/>): a directory mount point is
/// a reparse point on the host volume that targets the WinFsp volume device directly, so it
/// already resolves the same way from every session, and the helper rejects anything but a drive
/// letter anyway.
/// </summary>
public sealed class GlobalMountCoordinator
{
    /// <summary>
    /// Logs the outcome of each helper-service request.
    /// </summary>
    private readonly ILogger<GlobalMountCoordinator> _logger;

    /// <summary>
    /// Sends the helper-service requests in the order the TEMP state changed.
    /// </summary>
    private readonly GlobalMountRequestQueue _requests;

    /// <summary>
    /// Initializes a new instance of the <see cref="GlobalMountCoordinator"/> class and starts
    /// observing the TEMP state of every disk in <paramref name="mainViewModel"/>.
    /// </summary>
    /// <param name="mainViewModel">The view model whose disks to observe.</param>
    /// <param name="logger">The logger.</param>
    public GlobalMountCoordinator(MainViewModel mainViewModel, ILogger<GlobalMountCoordinator> logger)
    {
        _logger = logger;
        _requests = new(logger);

        mainViewModel.Disks.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (DiskViewModel vm in e.NewItems)
                {
                    vm.PropertyChanged += OnDiskPropertyChanged;

                    // A disk auto-mounted at startup may already be the TEMP target.
                    if (vm.IsCurrentTempDir)
                    {
                        EnqueuePublish(vm);
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (DiskViewModel vm in e.OldItems)
                {
                    vm.PropertyChanged -= OnDiskPropertyChanged;
                    EnqueueUnpublish(vm.MountPoint);
                }
            }
        };
    }

    /// <summary>
    /// Publishes or unpublishes a disk when its <see cref="DiskViewModel.IsCurrentTempDir"/>
    /// state changes.
    /// </summary>
    /// <param name="sender">The disk view model.</param>
    /// <param name="e">The event data.</param>
    private void OnDiskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DiskViewModel.IsCurrentTempDir) || sender is not DiskViewModel vm)
        {
            return;
        }

        if (vm.IsCurrentTempDir)
        {
            EnqueuePublish(vm);
        }
        else
        {
            EnqueueUnpublish(vm.MountPoint);
        }
    }

    /// <summary>
    /// Returns whether a disk's mount point gets a global DOS-device symlink: only drive letters
    /// do, since a directory mount point is already reachable from other sessions.
    /// </summary>
    /// <param name="mountPoint">The disk's mount point.</param>
    /// <returns><c>true</c> if <paramref name="mountPoint"/> is a drive letter.</returns>
    internal static bool IsPublishable(string mountPoint) => MountPointValidator.IsDriveLetter(mountPoint);

    /// <summary>
    /// Queues a request asking the helper service to publish <paramref name="vm"/>'s drive
    /// letter globally; does nothing for a directory mount point or a disk whose volume device
    /// path is unknown.
    /// </summary>
    /// <param name="vm">The disk that became the TEMP target.</param>
    private void EnqueuePublish(DiskViewModel vm)
    {
        if (!IsPublishable(vm.MountPoint) ||
            !vm.Disk.TryGetVolumeDevicePath(out var devicePath) ||
            devicePath == null)
        {
            return;
        }

        var letter = vm.MountPoint;

        // Pipe I/O blocks briefly; the property change fires on the UI thread, so offload it.
        _requests.Enqueue(() =>
        {
            if (HelperPipeClient.TryPublish(letter, devicePath, out var response))
            {
                _logger.LogInformation("[GlobalMount] publish {Letter}: {Success} — {Message}", letter, response.Success, response.Message);
            }
            else
            {
                _logger.LogWarning("[GlobalMount] publish {Letter}: helper service unavailable (degraded).", letter);
            }
        });
    }

    /// <summary>
    /// Queues a request asking the helper service to remove the global symlink for
    /// <paramref name="letter"/>; does nothing for a directory mount point.
    /// </summary>
    /// <param name="letter">The mount point of the disk that stopped being the TEMP target.</param>
    private void EnqueueUnpublish(string letter)
    {
        if (!IsPublishable(letter))
        {
            return;
        }

        _requests.Enqueue(() =>
        {
            if (HelperPipeClient.TryUnpublish(letter, out var response))
            {
                _logger.LogInformation("[GlobalMount] unpublish {Letter}: {Success} — {Message}", letter, response.Success, response.Message);
            }
        });
    }
}
