namespace ManagedDrive.App.Services;

/// <summary>
/// The tray's "Restore environment variables" action: confirms with the user, restores the chosen
/// group and reports the outcome in a balloon tip.
/// </summary>
/// <param name="trayIconController">Used to show the result balloon tip.</param>
/// <param name="ownerWindowProvider">Supplies the confirm dialog's owner window, or <c>null</c> if none is loaded.</param>
/// <param name="restoreEnv">
/// Restores a group (all groups for <c>null</c>); returns <c>null</c> when nothing was attempted
/// because another operation is running.
/// </param>
public sealed class EnvRestoreTrayAction(
    TrayIconController trayIconController,
    Func<Window?> ownerWindowProvider,
    Func<EnvRestoreGroup?, Task<EnvRestoreReport?>> restoreEnv)
{
    /// <summary>
    /// Runs the action for one group.
    /// </summary>
    /// <param name="group">The group to restore; <c>null</c> restores every group.</param>
    /// <returns>A task completing when the user has been told the outcome.</returns>
    public async Task RunAsync(EnvRestoreGroup? group)
    {
        var confirm = new ConfirmDialog(
            Loc.Get("Msg.RestoreEnvConfirmTitle"),
            EnvRestoreGroupText.GetConfirmBody(group));

        if (ownerWindowProvider() is { } owner)
        {
            confirm.Owner = owner;
        }

        if (confirm.ShowDialog() != true)
        {
            return;
        }

        if (await restoreEnv(group) is not { } report)
        {
            // Another operation is running; the status bar already says so.
            return;
        }

        trayIconController.ShowBalloonTip(
            "ManagedDrive",
            EnvRestoreGroupText.GetResultMessage(report),
            report.Result == EnvRestoreResult.Failed ? System.Windows.Forms.ToolTipIcon.Warning : System.Windows.Forms.ToolTipIcon.Info);
    }
}
