using ManagedDrive.Cli.Core;
using System.Collections.Specialized;

namespace ManagedDrive.App.Cli;

/// <summary>
/// Reports mount, unmount, save and high-usage events of the disks in
/// <see cref="MainViewModel.Disks"/> to <c>mdrive watch</c>, by listening to the same view-model
/// events <see cref="Services.DiskNotificationService"/> turns into tray notifications.
/// </summary>
internal sealed class MainViewModelCliEventSource(MainViewModel mainViewModel) : ICliEventSource
{
    /// <inheritdoc />
    public async Task<IDisposable> SubscribeAsync(Action<CliEvent> handler)
    {
        var subscription = new Subscription(mainViewModel, handler);

        // The collection and the disks' events belong to the UI thread; attaching there also means
        // no mount or unmount can slip in between listing the disks and listening for changes.
        await Application.Current.Dispatcher.InvokeAsync(subscription.Attach);
        return subscription;
    }

    /// <summary>
    /// One watcher's listeners on the disk collection and on each disk in it.
    /// </summary>
    /// <param name="mainViewModel">The view model owning the disk collection.</param>
    /// <param name="handler">Receives each event.</param>
    private sealed class Subscription(MainViewModel mainViewModel, Action<CliEvent> handler) : IDisposable
    {
        /// <summary>
        /// Set once the listeners are removed (or being removed), so an event already in flight is
        /// not delivered to a watcher that has gone. Written on the UI thread, read from whichever thread raises an event.
        /// </summary>
        private volatile bool _detached;

        /// <summary>
        /// The save listener added to each hooked disk, kept so exactly that one is removed again.
        /// Only used on the UI thread.
        /// </summary>
        private readonly Dictionary<DiskViewModel, EventHandler> _saveHandlers = [];

        /// <summary>
        /// Starts listening to the disks present now and to those added or removed later. Must run
        /// on the UI thread.
        /// </summary>
        public void Attach()
        {
            foreach (var vm in mainViewModel.Disks)
            {
                Hook(vm);
            }

            mainViewModel.Disks.CollectionChanged += OnDisksChanged;
        }

        /// <summary>
        /// Stops listening. Safe to call from any thread and more than once.
        /// </summary>
        public void Dispose()
        {
            // The application may already be shutting down, in which case there is nothing left to
            // detach from.
            if (Application.Current?.Dispatcher is { HasShutdownStarted: false } dispatcher)
            {
                dispatcher.BeginInvoke(Detach);
            }
        }

        /// <summary>
        /// Removes every listener this subscription added. Must run on the UI thread.
        /// </summary>
        private void Detach()
        {
            if (_detached)
            {
                return;
            }

            _detached = true;
            mainViewModel.Disks.CollectionChanged -= OnDisksChanged;
            foreach (var vm in mainViewModel.Disks)
            {
                Unhook(vm);
            }
        }

        /// <summary>
        /// Starts listening to one disk's save and usage events.
        /// </summary>
        /// <param name="vm">The disk.</param>
        private void Hook(DiskViewModel vm)
        {
            EventHandler onSaved = (_, _) => Raise(CliEventNames.SaveCompleted, vm);
            _saveHandlers[vm] = onSaved;
            vm.Disk.ImageSaved += onSaved;
            vm.SaveFailed += OnSaveFailed;
            vm.HighUsageWarning += OnHighUsageWarning;
        }

        /// <summary>
        /// Stops listening to one disk.
        /// </summary>
        /// <param name="vm">The disk.</param>
        private void Unhook(DiskViewModel vm)
        {
            if (_saveHandlers.Remove(vm, out var onSaved))
            {
                vm.Disk.ImageSaved -= onSaved;
            }

            vm.SaveFailed -= OnSaveFailed;
            vm.HighUsageWarning -= OnHighUsageWarning;
        }

        /// <summary>
        /// Reports disks being mounted (added) and unmounted (removed), and follows them.
        /// </summary>
        /// <param name="sender">The disk collection.</param>
        /// <param name="e">What changed.</param>
        private void OnDisksChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (DiskViewModel vm in e.OldItems)
                {
                    Unhook(vm);
                    Raise(CliEventNames.Unmounted, vm);
                }
            }

            if (e.NewItems != null)
            {
                foreach (DiskViewModel vm in e.NewItems)
                {
                    Hook(vm);
                    Raise(CliEventNames.Mounted, vm, vm.VolumeLabel);
                }
            }
        }

        /// <summary>
        /// Reports a failed image save with the reason.
        /// </summary>
        /// <param name="sender">The disk.</param>
        /// <param name="ex">Why the save failed.</param>
        private void OnSaveFailed(object? sender, Exception ex) =>
            Raise(CliEventNames.SaveFailed, (DiskViewModel)sender!, ex.Message);

        /// <summary>
        /// Reports a disk crossing its high-usage threshold.
        /// </summary>
        /// <param name="sender">The disk.</param>
        /// <param name="e">Unused.</param>
        private void OnHighUsageWarning(object? sender, EventArgs e)
        {
            var vm = (DiskViewModel)sender!;
            Raise(CliEventNames.HighUsage, vm, $"{vm.UsedPercent:0.#}% used");
        }

        /// <summary>
        /// Delivers an event to the watcher unless it has already detached.
        /// </summary>
        /// <param name="name">One of <see cref="CliEventNames"/>.</param>
        /// <param name="vm">The disk concerned.</param>
        /// <param name="message">Detail for the event, if any.</param>
        private void Raise(string name, DiskViewModel vm, string? message = null)
        {
            if (!_detached)
            {
                handler(new(name, vm.MountPoint, DateTimeOffset.Now, message));
            }
        }
    }
}
