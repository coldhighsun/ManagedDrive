using ManagedDrive.Cli.Core;

namespace ManagedDrive.App.ViewModels;

/// <summary>
/// Backing state for the app-wide busy/progress overlay shown during long-running disk
/// operations (save, archive import, export). Supports both determinate (known fraction) and
/// indeterminate (unknown total, e.g. importing an archive with no computable byte total) modes.
/// </summary>
public sealed class BusyOverlayViewModel : INotifyPropertyChanged
{
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
    public void Start(string statusText, bool indeterminate = false, ulong? totalBytes = null)
    {
        StatusText = statusText;
        IsIndeterminate = indeterminate;
        Progress = 0;
        _totalBytes = totalBytes;
        DetailText = totalBytes is { } total ? FormatDetail(0, total) : string.Empty;
        IsBusy = true;
    }

    private static string FormatDetail(ulong soFar, ulong total) =>
        Loc.Format("Busy.ByteProgress", ByteFormatter.Format(soFar), ByteFormatter.Format(total));

    /// <summary>
    /// Hides the overlay.
    /// </summary>
    public void Stop() => IsBusy = false;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}