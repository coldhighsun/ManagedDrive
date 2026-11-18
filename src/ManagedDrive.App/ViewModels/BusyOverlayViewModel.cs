using ManagedDrive.Cli.Core;
using System.Windows.Input;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// Backing state for the app-wide busy/progress overlay shown during long-running disk
/// operations (save, archive import, export). Supports both determinate (known fraction) and
/// indeterminate (unknown total, e.g. importing an archive with no computable byte total) modes.
/// </summary>
public sealed class BusyOverlayViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// The <see cref="CancellationTokenSource"/> for the operation currently shown by the
    /// overlay, or <see langword="null"/> when <see cref="Start"/> wasn't given one (that
    /// operation doesn't support cancellation, so <see cref="CanCancel"/> stays <see langword="false"/>
    /// and <see cref="CancelCommand"/> has nothing to do).
    /// </summary>
    private CancellationTokenSource? _cancellationSource;

    /// <summary>
    /// How long an operation has to run before the overlay is shown.
    /// </summary>
    private readonly TimeSpan _showDelay;

    /// <summary>
    /// How long the overlay stays up at least once it is shown.
    /// </summary>
    private readonly TimeSpan _minimumVisible;

    /// <summary>
    /// Waits for a delay; replaced in tests so they control when the delay ends.
    /// </summary>
    private readonly Func<TimeSpan, Task> _delay;

    /// <summary>
    /// Clock used to measure how long the overlay has been visible.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Counts <see cref="Start"/> and <see cref="Stop"/> calls. A pending show or hide only acts if
    /// no call has happened since it was scheduled, so a stale delay can never change a newer state.
    /// </summary>
    private int _generation;

    /// <summary>
    /// Timestamp (see <see cref="TimeProvider.GetTimestamp"/>) at which the overlay became visible.
    /// </summary>
    private long _shownAtTimestamp;

    /// <summary>
    /// Initializes the overlay's <see cref="CancelCommand"/> and its show/hide timing.
    /// </summary>
    /// <param name="showDelay">
    /// How long an operation has to run before the overlay is shown, or <see langword="null"/> for
    /// <see cref="BusyOverlayPolicy.ShowDelay"/>.
    /// </param>
    /// <param name="minimumVisible">
    /// How long the overlay stays up at least once shown, or <see langword="null"/> for
    /// <see cref="BusyOverlayPolicy.MinimumVisible"/>.
    /// </param>
    /// <param name="delay">Waits for a delay, or <see langword="null"/> for <see cref="Task.Delay(TimeSpan)"/>.</param>
    /// <param name="timeProvider">The clock, or <see langword="null"/> for <see cref="TimeProvider.System"/>.</param>
    internal BusyOverlayViewModel(
        TimeSpan? showDelay = null,
        TimeSpan? minimumVisible = null,
        Func<TimeSpan, Task>? delay = null,
        TimeProvider? timeProvider = null)
    {
        _showDelay = showDelay ?? BusyOverlayPolicy.ShowDelay;
        _minimumVisible = minimumVisible ?? BusyOverlayPolicy.MinimumVisible;
        _delay = delay ?? (static timeout => Task.Delay(timeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
        CancelCommand = new(_ => Cancel(), _ => CanCancel && !IsCancellationRequested);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets whether an operation is running. This is the logical state, which blocks other
    /// operations at once; the overlay itself follows it through <see cref="IsOverlayVisible"/>,
    /// after a short delay.
    /// </summary>
    public bool IsBusy
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(IsInputBlocked));

            // Commands that start an operation of their own are disabled while one runs.
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Gets whether the overlay is on screen. It follows <see cref="IsBusy"/> with a delay: it
    /// appears only once an operation has run for <see cref="BusyOverlayPolicy.ShowDelay"/> (a quick
    /// one never shows it), and stays at least <see cref="BusyOverlayPolicy.MinimumVisible"/>, so
    /// the screen does not flash.
    /// </summary>
    public bool IsOverlayVisible
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(IsOverlayVisible));
            OnPropertyChanged(nameof(IsInputBlocked));
        }
    }

    /// <summary>
    /// Gets whether the window must ignore mouse input: while an operation runs (even before the
    /// overlay is shown, so a click cannot slip through in the first moments) and while the overlay
    /// is still up.
    /// </summary>
    public bool IsInputBlocked => IsBusy || IsOverlayVisible;

    /// <summary>
    /// Gets whether the operation currently shown by the overlay can be cancelled, i.e.
    /// <see cref="Start"/> was given a <see cref="CancellationTokenSource"/>.
    /// </summary>
    public bool CanCancel
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(CanCancel));
            CancelCommand.Refresh();
        }
    }

    /// <summary>
    /// Gets whether <see cref="CancelCommand"/> has already been invoked for the operation
    /// currently shown by the overlay, so the button can disable itself instead of firing
    /// <see cref="CancellationTokenSource.Cancel()"/> a second time while the caller is still
    /// unwinding from the first.
    /// </summary>
    public bool IsCancellationRequested
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(IsCancellationRequested));
            CancelCommand.Refresh();
        }
    }

    /// <summary>
    /// Command bound to the overlay's Cancel button; requests cancellation of the operation
    /// currently shown, if it supports cancellation.
    /// </summary>
    public RelayCommand CancelCommand { get; }

    /// <summary>
    /// Gets whether the operation has no computable total, so the progress bar should render
    /// in indeterminate mode instead of showing <see cref="Progress"/>.
    /// </summary>
    public bool IsIndeterminate
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(IsIndeterminate));
        }
    }

    /// <summary>
    /// Gets the current progress fraction in [0, 1]. Meaningful only when
    /// <see cref="IsIndeterminate"/> is <c>false</c>.
    /// </summary>
    public double Progress
    {
        get;
        private set
        {
            // The epsilon check below only suppresses redundant PropertyChanged notifications for
            // near-identical intermediate ticks; it must never suppress storing the terminal value
            // itself, or a final Report(1.0) that lands within epsilon of the last-stored value
            // would leave `field` stuck just short of 1.0 forever (the bar visibly stops early).
            if (value < 1.0 && Math.Abs(field - value) < 0.0001)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(Progress));
        }
    }

    /// <summary>
    /// Gets the status text shown above the progress bar.
    /// </summary>
    public string StatusText
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(StatusText));
        }
    } = string.Empty;

    /// <summary>
    /// Gets the "bytes so far / total bytes" detail text shown below <see cref="StatusText"/>, or
    /// an empty string when <see cref="Start"/> wasn't given a total byte count.
    /// </summary>
    public string DetailText
    {
        get;
        private set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(DetailText));
        }
    } = string.Empty;

    private ulong? _totalBytes;

    /// <summary>
    /// Updates the current progress fraction, clamped to [0, 1], and — when <see cref="Start"/>
    /// was given a total byte count — recomputes <see cref="DetailText"/> from it.
    /// </summary>
    /// <param name="value">Progress fraction to report.</param>
    public void Report(double value)
    {
        Progress = Math.Clamp(value, 0.0, 1.0);

        if (_totalBytes is { } total)
        {
            DetailText = FormatDetail((ulong)(total * Progress), total);
        }
    }

    /// <summary>
    /// Shows the overlay with a fresh <paramref name="statusText"/> and resets progress to zero.
    /// </summary>
    /// <param name="statusText">Status text to display above the progress bar.</param>
    /// <param name="indeterminate">Whether the operation has no computable total.</param>
    /// <param name="totalBytes">
    /// Total byte count for the operation, used to populate <see cref="DetailText"/> as progress
    /// advances, or <see langword="null"/> to leave <see cref="DetailText"/> empty.
    /// </param>
    /// <param name="cancellationSource">
    /// The <see cref="CancellationTokenSource"/> the caller's operation observes, or
    /// <see langword="null"/> when that operation doesn't support cancellation — the overlay then
    /// shows no Cancel button for it. Owned by the caller: <see cref="Stop"/> does not dispose it.
    /// </param>
    /// <param name="showImmediately">
    /// Whether to draw the overlay at once instead of after <see cref="BusyOverlayPolicy.ShowDelay"/>,
    /// for an operation that always takes a while and whose window would otherwise show uncovered
    /// (the startup auto-mount).
    /// </param>
    public void Start(
        string statusText,
        bool indeterminate = false,
        ulong? totalBytes = null,
        CancellationTokenSource? cancellationSource = null,
        bool showImmediately = false)
    {
        StatusText = statusText;
        IsIndeterminate = indeterminate;
        Progress = 0;
        _totalBytes = totalBytes;
        DetailText = totalBytes is { } total ? FormatDetail(0, total) : string.Empty;
        _cancellationSource = cancellationSource;
        CanCancel = cancellationSource is not null;
        IsCancellationRequested = false;
        IsBusy = true;
        ScheduleShow(showImmediately);
    }

    /// <summary>
    /// Replaces the status text of the operation being shown without touching its progress, for
    /// an operation whose text changes as it goes (e.g. "2 of 5 disks loaded").
    /// </summary>
    /// <param name="statusText">The new status text.</param>
    public void UpdateStatusText(string statusText) => StatusText = statusText;

    /// <summary>
    /// Shows the overlay for a new operation like <see cref="Start"/>, unless another operation is
    /// already showing it — the overlay tracks a single operation, so a second one would take over
    /// its progress and Cancel button and hide the overlay when it finished first.
    /// </summary>
    /// <param name="statusText">Status text to display above the progress bar.</param>
    /// <param name="indeterminate">Whether the operation has no computable total.</param>
    /// <param name="totalBytes">Total byte count for the operation; see <see cref="Start"/>.</param>
    /// <param name="cancellationSource">The operation's cancellation source; see <see cref="Start"/>.</param>
    /// <returns>
    /// <see langword="true"/> if the overlay now shows this operation, which must then call
    /// <see cref="Stop"/> when done; <see langword="false"/> if another operation is running.
    /// </returns>
    public bool TryStart(string statusText, bool indeterminate = false, ulong? totalBytes = null, CancellationTokenSource? cancellationSource = null)
    {
        if (IsBusy)
        {
            return false;
        }

        Start(statusText, indeterminate, totalBytes, cancellationSource);
        return true;
    }

    private static string FormatDetail(ulong soFar, ulong total) =>
        Loc.Format("Busy.ByteProgress", ByteFormatter.Format(soFar), ByteFormatter.Format(total));

    /// <summary>
    /// Hides the overlay.
    /// </summary>
    public void Stop()
    {
        IsBusy = false;
        _cancellationSource = null;
        CanCancel = false;
        ScheduleHide();
    }

    /// <summary>
    /// Shows the overlay once the operation has run for the show delay, unless it has stopped (or a
    /// newer operation has started) by then. When the overlay is still up from the previous
    /// operation, it simply stays.
    /// </summary>
    /// <param name="showImmediately">Whether to skip the show delay.</param>
    private void ScheduleShow(bool showImmediately)
    {
        var generation = ++_generation;
        if (IsOverlayVisible)
        {
            return;
        }

        if (showImmediately || _showDelay <= TimeSpan.Zero)
        {
            ShowOverlay();
            return;
        }

        _ = ShowAfterDelayAsync(generation);
    }

    /// <summary>
    /// Waits for the show delay and shows the overlay if nothing has started or stopped since.
    /// </summary>
    /// <param name="generation">The <see cref="_generation"/> this show was scheduled in.</param>
    private async Task ShowAfterDelayAsync(int generation)
    {
        await _delay(_showDelay);
        if (generation == _generation && IsBusy)
        {
            ShowOverlay();
        }
    }

    /// <summary>
    /// Hides the overlay, but not before it has been up for the minimum visible time. An operation
    /// that stopped before the overlay was shown leaves it hidden.
    /// </summary>
    private void ScheduleHide()
    {
        var generation = ++_generation;
        if (!IsOverlayVisible)
        {
            return;
        }

        var remaining = BusyOverlayPolicy.RemainingVisible(
            _timeProvider.GetElapsedTime(_shownAtTimestamp), _minimumVisible);
        if (remaining <= TimeSpan.Zero)
        {
            IsOverlayVisible = false;
            return;
        }

        _ = HideAfterDelayAsync(generation, remaining);
    }

    /// <summary>
    /// Waits out the rest of the minimum visible time and hides the overlay if nothing has started
    /// or stopped since.
    /// </summary>
    /// <param name="generation">The <see cref="_generation"/> this hide was scheduled in.</param>
    /// <param name="remaining">How long the overlay still has to stay up.</param>
    private async Task HideAfterDelayAsync(int generation, TimeSpan remaining)
    {
        await _delay(remaining);
        if (generation == _generation)
        {
            IsOverlayVisible = false;
        }
    }

    /// <summary>
    /// Makes the overlay visible and notes when, for the minimum visible time.
    /// </summary>
    private void ShowOverlay()
    {
        _shownAtTimestamp = _timeProvider.GetTimestamp();
        IsOverlayVisible = true;
    }

    /// <summary>
    /// Requests cancellation of the operation currently shown, via the
    /// <see cref="CancellationTokenSource"/> passed to <see cref="Start"/>. Does nothing if
    /// <see cref="Start"/> wasn't given one, or if this was already called for the current
    /// operation.
    /// </summary>
    private void Cancel()
    {
        if (_cancellationSource is null || IsCancellationRequested)
        {
            return;
        }

        IsCancellationRequested = true;
        _cancellationSource.Cancel();
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}
