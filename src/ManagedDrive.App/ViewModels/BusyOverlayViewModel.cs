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
            if (field == value)
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
    /// Updates the current progress fraction, clamped to [0, 1].
    /// </summary>
    /// <param name="value">Progress fraction to report.</param>
    public void Report(double value) => Progress = Math.Clamp(value, 0.0, 1.0);

    /// <summary>
    /// Shows the overlay with a fresh <paramref name="statusText"/> and resets progress to zero.
    /// </summary>
    /// <param name="statusText">Status text to display above the progress bar.</param>
    /// <param name="indeterminate">Whether the operation has no computable total.</param>
    public void Start(string statusText, bool indeterminate = false)
    {
        StatusText = statusText;
        IsIndeterminate = indeterminate;
        Progress = 0;
        IsBusy = true;
    }

    /// <summary>
    /// Hides the overlay.
    /// </summary>
    public void Stop() => IsBusy = false;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new(propertyName));
}