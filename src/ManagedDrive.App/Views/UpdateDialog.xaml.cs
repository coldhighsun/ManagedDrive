using GitHubReleaseUpdater;
using GitHubReleaseUpdater.Download;
using ManagedDrive.Cli.Core;

namespace ManagedDrive.App.Views;

/// <summary>
/// Tells the user a newer ManagedDrive release exists and, if they agree, downloads its installer
/// (with progress) and starts it. Cycles through three states: asking, downloading, and showing a failure.
/// </summary>
public partial class UpdateDialog
{
    /// <summary>
    /// Longest run of release notes shown, so one oversized release body cannot make the dialog unwieldy.
    /// </summary>
    private const int MaxNotesLength = 4000;

    /// <summary>
    /// What the dialog is currently showing.
    /// </summary>
    private enum DialogState
    {
        /// <summary>Asking whether to update.</summary>
        Prompt,

        /// <summary>Downloading the installer.</summary>
        Downloading,

        /// <summary>The last attempt failed or was declined; the user can retry.</summary>
        Failed,
    }

    /// <summary>
    /// Runs the installation and records skipped versions.
    /// </summary>
    private readonly UpdateCheckService _service;

    /// <summary>
    /// The update this dialog offers.
    /// </summary>
    private readonly AvailableUpdate _update;

    /// <summary>
    /// Cancels the in-flight download, or <see langword="null"/> when none is running.
    /// </summary>
    private CancellationTokenSource? _downloadCts;

    /// <summary>
    /// What the dialog is showing now.
    /// </summary>
    private DialogState _state;

    /// <summary>
    /// Set once the window has closed, so a download that finishes late does not touch it.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// Hands the controller's progress reports, which may come from a pool thread, to the UI thread at background priority.
    /// </summary>
    private readonly CoalescingSignal _progressSignal;

    /// <summary>
    /// Initializes the dialog for <paramref name="update"/>.
    /// </summary>
    /// <param name="service">Runs the installation and records skipped versions.</param>
    /// <param name="update">The update to offer.</param>
    private UpdateDialog(UpdateCheckService service, AvailableUpdate update)
    {
        InitializeComponent();
        _service = service;
        _update = update;
        _progressSignal = new(ShowLatestProgress, action => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, action));

        HeaderText.Text = Loc.Format("Update.Header", update.Version.ToString(includeMetadata: false));
        CurrentVersionText.Text = Loc.Format("Update.CurrentVersion", UpdateCheckService.GetRunningVersion());
        ShowReleaseNotes(update.Release.Body);
        InfoText.Text = BuildInfoText();
        SetState(DialogState.Prompt);

        service.Controller.ProgressChanged += OnProgressChanged;
        Loaded += (_, _) => DialogPlacement.BringToFrontWhenUnowned(this);
        Closed += (_, _) =>
        {
            _closed = true;
            _progressSignal.Complete();
            service.Controller.ProgressChanged -= OnProgressChanged;
            _downloadCts?.Cancel();
        };
    }

    /// <summary>
    /// Whether an update dialog is open. Only one is shown at a time, so the startup prompt and the
    /// About dialog cannot stack two of them.
    /// </summary>
    public static bool IsShowing { get; private set; }

    /// <summary>
    /// Shows the dialog for the update the service has found, modally unless one is already open.
    /// </summary>
    /// <param name="service">Runs the installation and records skipped versions.</param>
    /// <param name="owner">The window to centre on, or <see langword="null"/> when none is visible.</param>
    /// <returns><see langword="true"/> when the installer was started.</returns>
    public static bool ShowFor(UpdateCheckService service, Window? owner)
    {
        if (IsShowing || service.Controller.Available is not { } update)
        {
            return false;
        }

        IsShowing = true;
        try
        {
            var dialog = new UpdateDialog(service, update);
            if (owner is { IsLoaded: true })
            {
                dialog.Owner = owner;
            }

            return dialog.ShowDialog() == true;
        }
        finally
        {
            IsShowing = false;
        }
    }

    /// <summary>
    /// Fills the release-notes box, hiding it when the release has none.
    /// </summary>
    private void ShowReleaseNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return;
        }

        var trimmed = notes.Trim();
        NotesText.Text = trimmed.Length > MaxNotesLength ? trimmed[..MaxNotesLength] + "…" : trimmed;
        NotesPanel.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Describes what updating will do, or why it cannot be done automatically.
    /// </summary>
    private string BuildInfoText()
    {
        if (!_service.Controller.HasInstaller)
        {
            return Loc.Get("Update.NoInstaller");
        }

        var text = Loc.Get("Update.InstallNote");
        return _service.IsTempOnRamDisk() ? text + "\n\n" + Loc.Get("Msg.ExitTempDirWillBeReset") : text;
    }

    /// <summary>
    /// Shows the controls that belong to <paramref name="state"/> and hides the rest.
    /// </summary>
    private void SetState(DialogState state)
    {
        _state = state;
        var prompt = state == DialogState.Prompt;
        var downloading = state == DialogState.Downloading;
        var failed = state == DialogState.Failed;

        InfoText.Visibility = downloading || failed ? Visibility.Collapsed : Visibility.Visible;
        ProgressPanel.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = failed ? Visibility.Visible : Visibility.Collapsed;

        UpdateButton.Visibility = !downloading && _service.Controller.HasInstaller ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Content = Loc.Get(failed ? "Update.Retry" : "Update.UpdateNow");
        SkipButton.Visibility = prompt ? Visibility.Visible : Visibility.Collapsed;
        LaterButton.Visibility = downloading ? Visibility.Collapsed : Visibility.Visible;
        LaterButton.Content = Loc.Get(failed ? "Btn.Close" : "Update.RemindLater");
        ViewReleaseButton.Visibility = !downloading && (failed || !_service.Controller.HasInstaller) ? Visibility.Visible : Visibility.Collapsed;
        CancelDownloadButton.Visibility = downloading ? Visibility.Visible : Visibility.Collapsed;

        if (downloading)
        {
            DownloadProgressBar.IsIndeterminate = true;
            ProgressText.Text = Loc.Get("Update.Starting");
        }
    }

    /// <summary>
    /// Switches to the failed state showing <paramref name="message"/>.
    /// </summary>
    private void ShowFailure(string message)
    {
        ErrorText.Text = message;
        SetState(DialogState.Failed);
    }

    /// <summary>
    /// Shows the controller's latest download progress.
    /// </summary>
    private void OnProgressChanged(object? sender, EventArgs e) => _progressSignal.Notify();

    /// <summary>
    /// Applies the latest progress on the UI thread; a report that arrives after the download ended is dropped.
    /// </summary>
    private void ShowLatestProgress()
    {
        if (!_closed && _state == DialogState.Downloading && _service.Controller.Progress is { } progress)
        {
            OnDownloadProgress(progress);
        }
    }

    /// <summary>
    /// Reflects a download progress report in the progress bar and its caption.
    /// </summary>
    private void OnDownloadProgress(DownloadProgress progress)
    {
        if (_closed)
        {
            return;
        }

        var received = ByteFormatter.Format((ulong)progress.BytesReceived);
        var speed = ByteFormatter.Format((ulong)progress.BytesPerSecond);
        if (progress is { Percentage: { } percentage, TotalBytes: { } total })
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = percentage / 100;
            ProgressText.Text = Loc.Format("Update.Progress", received, ByteFormatter.Format((ulong)total), speed);
        }
        else
        {
            ProgressText.Text = Loc.Format("Update.ProgressUnknownTotal", received, speed);
        }
    }

    /// <summary>
    /// Downloads and starts the installer, then closes the dialog so the installer can ask the app to exit.
    /// </summary>
    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        SetState(DialogState.Downloading);
        using var cts = _downloadCts = new CancellationTokenSource();

        try
        {
            var launched = await _service.InstallAsync(cts.Token);
            if (_closed)
            {
                return;
            }

            if (launched)
            {
                DialogResult = true;
                return;
            }

            // Without a recorded failure the user declined the elevation prompt.
            ShowFailure(_service.LastInstallError is { } error
                ? Loc.Format("Update.Failed", error.Message)
                : Loc.Get("Update.UacDeclined"));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (!_closed)
            {
                SetState(DialogState.Prompt);
            }
        }
        catch (Exception ex)
        {
            // Any other failure ends up here too, so the dialog never stays stuck in the downloading state
            // (this is an async void handler: an escaping exception would bypass the UI state entirely).
            if (!_closed)
            {
                ShowFailure(Loc.Format("Update.Failed", ex.Message));
            }
        }
        finally
        {
            _downloadCts = null;
        }
    }

    /// <summary>
    /// Stops the running download and returns to the question.
    /// </summary>
    private void CancelDownloadButton_Click(object sender, RoutedEventArgs e) => _downloadCts?.Cancel();

    /// <summary>
    /// Remembers that this version should not be offered again, then closes.
    /// </summary>
    private async void SkipButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _service.Controller.SkipVersionAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not being able to persist the choice only means the user is asked again next time.
        }

        DialogResult = false;
    }

    /// <summary>
    /// Opens the release page in the browser, for a manual download.
    /// </summary>
    private void ViewReleaseButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_update.Release.HtmlUrl ?? UpdateCheckService.ReleasesUrl) { UseShellExecute = true });
}
