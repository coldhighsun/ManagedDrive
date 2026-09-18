using ManagedDrive.App.Infrastructure;
using ManagedDrive.Cli.Core;

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
    /// Initializes the overlay's <see cref="CancelCommand"/>.
    /// </summary>
    public BusyOverlayViewModel() => CancelCommand = new(_ => Cancel(), _ => CanCancel && !IsCancellationRequested);

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Gets whether the overlay should be visible.
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
        }
    }

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
    public void Start(string statusText, bool indeterminate = false, ulong? totalBytes = null, CancellationTokenSource? cancellationSource = null)
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
