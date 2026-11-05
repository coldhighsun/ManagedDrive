using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="NotificationClickRunner"/>.
/// </summary>
public sealed class NotificationClickRunnerTests
{
    /// <summary>
    /// The mount point of the disk the recording runner knows.
    /// </summary>
    private const string KnownDisk = "R:";

    /// <summary>
    /// The calls the runner made, in order.
    /// </summary>
    private readonly List<string> _calls = [];

    /// <summary>
    /// A plain window target shows the window and nothing else.
    /// </summary>
    [Fact]
    public void Run_ShowWindowTarget_OnlyShowsWindow()
    {
        var runner = CreateRunner();

        runner.Run(NotificationClickTarget.ShowWindow);

        Assert.Equal(["show"], _calls);
    }

    /// <summary>
    /// A disk target selects the disk after showing the window, without opening a dialog.
    /// </summary>
    [Fact]
    public void Run_DiskTarget_ShowsWindowThenSelectsDisk()
    {
        var runner = CreateRunner();

        runner.Run(NotificationClickTarget.Disk(KnownDisk));

        Assert.Equal(["show", $"select {KnownDisk}"], _calls);
    }

    /// <summary>
    /// An edit target opens the edit dialog on the selected disk.
    /// </summary>
    [Fact]
    public void Run_EditDiskTarget_SelectsDiskThenOpensEditDialog()
    {
        var runner = CreateRunner();

        runner.Run(NotificationClickTarget.EditDisk(KnownDisk));

        Assert.Equal(["show", $"select {KnownDisk}", "edit"], _calls);
    }

    /// <summary>
    /// A disk unmounted since the balloon was shown leaves just the window: the edit dialog would
    /// otherwise open on whatever disk happens to be selected.
    /// </summary>
    [Fact]
    public void Run_EditDiskTargetDiskGone_DoesNotOpenEditDialog()
    {
        var runner = CreateRunner();

        runner.Run(NotificationClickTarget.EditDisk("Z:"));

        Assert.Equal(["show", "select Z:"], _calls);
    }

    /// <summary>
    /// With a dialog in the way the disk is still selected, but no second dialog is stacked.
    /// </summary>
    [Fact]
    public void Run_EditDiskTargetDialogBlocked_OnlySelectsDisk()
    {
        var runner = CreateRunner(canOpenDialog: false);

        runner.Run(NotificationClickTarget.EditDisk(KnownDisk));

        Assert.Equal(["show", $"select {KnownDisk}"], _calls);
    }

    /// <summary>
    /// An update target opens the update dialog.
    /// </summary>
    [Fact]
    public void Run_UpdateTarget_OpensUpdateDialog()
    {
        var runner = CreateRunner();

        runner.Run(NotificationClickTarget.Update);

        Assert.Equal(["show", "update"], _calls);
    }

    /// <summary>
    /// With a dialog in the way an update target only shows the window.
    /// </summary>
    [Fact]
    public void Run_UpdateTargetDialogBlocked_OnlyShowsWindow()
    {
        var runner = CreateRunner(canOpenDialog: false);

        runner.Run(NotificationClickTarget.Update);

        Assert.Equal(["show"], _calls);
    }

    /// <summary>
    /// Creates a runner that records its calls in <see cref="_calls"/>.
    /// </summary>
    /// <param name="canOpenDialog">Whether a dialog can be opened.</param>
    /// <returns>The runner.</returns>
    private NotificationClickRunner CreateRunner(bool canOpenDialog = true) => new(
        () => _calls.Add("show"),
        mountPoint =>
        {
            _calls.Add($"select {mountPoint}");
            return mountPoint == KnownDisk;
        },
        () => canOpenDialog,
        () => _calls.Add("edit"),
        () => _calls.Add("update"));
}
