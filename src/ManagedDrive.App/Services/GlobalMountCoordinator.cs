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
/// </summary>
public sealed class GlobalMountCoordinator
{
    public GlobalMountCoordinator(MainViewModel mainViewModel)
    {
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
                        PublishAsync(vm);
                    }
                }
            }

            if (e.OldItems != null)
            {
                foreach (DiskViewModel vm in e.OldItems)
                {
                    vm.PropertyChanged -= OnDiskPropertyChanged;
                    UnpublishAsync(vm.MountPoint);
                }
            }
        };
    }

    private void OnDiskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DiskViewModel.IsCurrentTempDir) || sender is not DiskViewModel vm)
        {
            return;
        }

        if (vm.IsCurrentTempDir)
        {
            PublishAsync(vm);
        }
        else
        {
            UnpublishAsync(vm.MountPoint);
        }
    }

    private static void PublishAsync(DiskViewModel vm)
    {
        if (!vm.Disk.TryGetVolumeDevicePath(out var devicePath) || devicePath == null)
        {
            return;
        }

        var letter = vm.MountPoint;

        // Pipe I/O blocks briefly; the property change fires on the UI thread, so offload it.
        Task.Run(() =>
        {
            if (HelperPipeClient.TryPublish(letter, devicePath, out var response))
            {
                Debug.WriteLine($"[GlobalMount] publish {letter}: {response.Success} — {response.Message}");
            }
            else
            {
                Debug.WriteLine($"[GlobalMount] publish {letter}: helper service unavailable (degraded).");
            }
        });
    }

    private static void UnpublishAsync(string letter)
    {
        Task.Run(() =>
        {
            if (HelperPipeClient.TryUnpublish(letter, out var response))
            {
                Debug.WriteLine($"[GlobalMount] unpublish {letter}: {response.Success} — {response.Message}");
            }
        });
    }
}
