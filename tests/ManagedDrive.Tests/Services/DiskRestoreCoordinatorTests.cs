using ManagedDrive.App.Models;
using ManagedDrive.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace ManagedDrive.Tests;

public sealed class DiskRestoreCoordinatorTests
{
    /// <summary>
    /// The mount point of the disk most tests use.
    /// </summary>
    private const string Mount = "R:";


    /// <summary>
    /// The fake user environment the redirector reads.
    /// </summary>
    private readonly FakeUserEnvironment _env = new();


    /// <summary>
    /// Builds a coordinator over the fake environment; preset names show as their ids in angle brackets.
    /// </summary>
    /// <returns>The coordinator.</returns>
    private DiskRestoreCoordinator CreateCoordinator() => new(
        new UserEnvironmentRedirector(_env, () => [], _ => true),
        id => $"<{id}>",
        NullLogger.Instance);


    /// <summary>
    /// Builds the options of a disk with the Node.js and NuGet presets.
    /// </summary>
    /// <returns>The options.</returns>
    private static DiskOptions NodeDisk() => new()
    {
        MountPoint = Mount,
        CapacityBytes = 1024 * 1024,
        Folders = [.. BuiltInPresets.Node.Folders, .. BuiltInPresets.NuGet.Folders],
        EnvRedirects = [.. BuiltInPresets.Node.EnvRedirects, .. BuiltInPresets.NuGet.EnvRedirects],
    };

    /// <summary>
    /// Makes the given redirects of a disk point into it, as the redirector does when it applies them.
    /// </summary>
    /// <param name="options">The disk's options.</param>
    /// <param name="variables">The variables to point into the disk.</param>
    private void PointIntoDisk(DiskOptions options, params string[] variables)
    {
        foreach (var redirect in (options.EnvRedirects ?? []).Where(r => variables.Contains(r.Variable)))
        {
            _env.Values[redirect.Variable] = new(EnvRedirectPolicy.Resolve(options.MountPoint, redirect.SubPath), RegistryValueKind.String);
        }
    }

    /// <summary>
    /// Puts the given variables back, so they no longer point into any disk.
    /// </summary>
    /// <param name="variables">The variable names.</param>
    private void Restore(params string[] variables)
    {
        foreach (var variable in variables)
        {
            _env.Values.Remove(variable);
        }
    }

    /// <summary>
    /// Only a disk with a variable pointing into it is planned.
    /// </summary>
    [Fact]
    public void Plan_DiskWithPointingVariable_IsPlannedAndIdleDiskIsNot()
    {
        var options = NodeDisk();
        PointIntoDisk(options, "NUGET_PACKAGES");
        var pointing = new FakeDisk(options);
        var idle = new FakeDisk(options with { MountPoint = "S:" });

        var planned = CreateCoordinator().Plan([pointing, idle]);

        var entry = Assert.Single(planned);
        Assert.Same(pointing, entry.Disk);
        Assert.Equal(["nuget"], entry.Candidates.Presets.Select(p => p.Id));
    }

    /// <summary>
    /// A fully restored preset is applied to the disk without it, and the disk is refreshed.
    /// </summary>
    [Fact]
    public async Task TrimAsync_PresetRestored_DiskLosesItAndIsRefreshed()
    {
        var options = NodeDisk();
        PointIntoDisk(options, "npm_config_cache", "YARN_CACHE_FOLDER", "npm_config_store_dir", "NUGET_PACKAGES");
        var disk = new FakeDisk(options);
        var coordinator = CreateCoordinator();
        var planned = coordinator.Plan([disk]);
        Restore("npm_config_cache", "YARN_CACHE_FOLDER", "npm_config_store_dir");

        var outcome = await coordinator.TrimAsync(planned);

        Assert.Equal(["R: (<node>)"], outcome.ReleasedFrom);
        Assert.Empty(outcome.NotUpdated);
        var applied = Assert.Single(disk.Applied);
        Assert.Equal(BuiltInPresets.NuGet.EnvRedirects, applied.EnvRedirects);
        Assert.Equal(BuiltInPresets.NuGet.Folders, applied.Folders);
        Assert.Equal(1, disk.Refreshes);
    }

    /// <summary>
    /// A preset with a variable that was not restored stays on the disk.
    /// </summary>
    [Fact]
    public async Task TrimAsync_OneVariableStillPointsIntoTheDisk_AppliesNothing()
    {
        var options = NodeDisk();
        PointIntoDisk(options, "npm_config_cache", "YARN_CACHE_FOLDER", "npm_config_store_dir");
        var disk = new FakeDisk(options);
        var coordinator = CreateCoordinator();
        var planned = coordinator.Plan([disk]);
        Restore("npm_config_cache", "npm_config_store_dir");

        var outcome = await coordinator.TrimAsync(planned);

        Assert.Empty(outcome.ReleasedFrom);
        Assert.Empty(outcome.NotUpdated);
        Assert.Empty(disk.Applied);
    }

    /// <summary>
    /// A disk unmounted during the restore is skipped without a report.
    /// </summary>
    [Fact]
    public async Task TrimAsync_DiskUnmountedMeanwhile_IsSkippedSilently()
    {
        var options = NodeDisk();
        PointIntoDisk(options, "NUGET_PACKAGES");
        var disk = new FakeDisk(options);
        var coordinator = CreateCoordinator();
        var planned = coordinator.Plan([disk]);
        Restore("NUGET_PACKAGES");
        disk.IsMounted = false;

        var outcome = await coordinator.TrimAsync(planned);

        Assert.Empty(outcome.ReleasedFrom);
        Assert.Empty(outcome.NotUpdated);
        Assert.Empty(disk.Applied);
    }

    /// <summary>
    /// A disk that is remounting cannot take the change and is reported.
    /// </summary>
    [Fact]
    public async Task TrimAsync_DiskRemounting_IsReportedAsNotUpdated()
    {
        var options = NodeDisk();
        PointIntoDisk(options, "NUGET_PACKAGES");
        var disk = new FakeDisk(options);
        var coordinator = CreateCoordinator();
        var planned = coordinator.Plan([disk]);
        Restore("NUGET_PACKAGES");
        disk.IsRemounting = true;

        var outcome = await coordinator.TrimAsync(planned);

        Assert.Equal(["R:"], outcome.NotUpdated);
        Assert.Empty(disk.Applied);
    }

    /// <summary>
    /// A disk that refuses the new options is reported and not refreshed.
    /// </summary>
    [Fact]
    public async Task TrimAsync_DiskRefusesTheOptions_IsReportedAsNotUpdated()
    {
        var options = NodeDisk();
        PointIntoDisk(options, "NUGET_PACKAGES");
        var disk = new FakeDisk(options) { Error = "refused" };
        var coordinator = CreateCoordinator();
        var planned = coordinator.Plan([disk]);
        Restore("NUGET_PACKAGES");

        var outcome = await coordinator.TrimAsync(planned);

        Assert.Empty(outcome.ReleasedFrom);
        Assert.Equal(["R:"], outcome.NotUpdated);
        Assert.Equal(0, disk.Refreshes);
    }

    /// <summary>
    /// An exception from one disk is reported and the next disk is still updated.
    /// </summary>
    [Fact]
    public async Task TrimAsync_ApplyThrows_IsReportedAndTheNextDiskStillRuns()
    {
        var first = new DiskOptions
        {
            MountPoint = Mount,
            CapacityBytes = 1024 * 1024,
            Folders = BuiltInPresets.Node.Folders,
            EnvRedirects = BuiltInPresets.Node.EnvRedirects,
        };
        var second = new DiskOptions
        {
            MountPoint = "S:",
            CapacityBytes = 1024 * 1024,
            Folders = BuiltInPresets.NuGet.Folders,
            EnvRedirects = BuiltInPresets.NuGet.EnvRedirects,
        };
        PointIntoDisk(first, "npm_config_cache", "YARN_CACHE_FOLDER", "npm_config_store_dir");
        PointIntoDisk(second, "NUGET_PACKAGES");
        var coordinator = CreateCoordinator();
        var planned = coordinator.Plan([new FakeDisk(first) { Throw = true }, new FakeDisk(second)]);
        Restore("npm_config_cache", "YARN_CACHE_FOLDER", "npm_config_store_dir", "NUGET_PACKAGES");

        var outcome = await coordinator.TrimAsync(planned);

        Assert.Equal(["R:"], outcome.NotUpdated);
        Assert.Equal(["S: (<nuget>)"], outcome.ReleasedFrom);
    }

    /// <summary>
    /// A disk whose options no longer list what was planned is left alone.
    /// </summary>
    [Fact]
    public async Task TrimAsync_OptionsChangedSincePlan_AppliesNothing()
    {
        var options = NodeDisk();
        PointIntoDisk(options, "NUGET_PACKAGES");
        var disk = new FakeDisk(options);
        var coordinator = CreateCoordinator();
        var planned = coordinator.Plan([disk]);
        Restore("NUGET_PACKAGES");
        disk.Options = options with { Folders = null, EnvRedirects = null };

        var outcome = await coordinator.TrimAsync(planned);

        Assert.Empty(outcome.ReleasedFrom);
        Assert.Empty(outcome.NotUpdated);
        Assert.Empty(disk.Applied);
    }

    /// <summary>
    /// A restore that succeeds is followed by the trim, and the outcome reaches the callback.
    /// </summary>
    [Fact]
    public async Task RunAsync_RestoreSucceeds_TrimsDisksAndReportsOutcome()
    {
        var options = NodeDisk();
        PointIntoDisk(options, "NUGET_PACKAGES");
        var disk = new FakeDisk(options);
        DiskTrimOutcome? reported = null;

        var run = await CreateCoordinator().RunAsync(
            [disk],
            () =>
            {
                Restore("NUGET_PACKAGES");
                return Task.FromResult(42);
            },
            outcome => reported = outcome);

        Assert.Equal(42, run.Result);
        Assert.Equal(["R: (<nuget>)"], run.Outcome.ReleasedFrom);
        Assert.Same(run.Outcome, reported);
        Assert.Single(disk.Applied);
    }

    /// <summary>
    /// A restore that throws midway still gets the disks updated for what it put back, and the exception is rethrown.
    /// </summary>
    [Fact]
    public async Task RunAsync_RestoreThrowsAfterRestoringSome_StillTrimsAndRethrows()
    {
        var options = NodeDisk();
        PointIntoDisk(options, "NUGET_PACKAGES");
        var disk = new FakeDisk(options);
        DiskTrimOutcome? reported = null;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => CreateCoordinator().RunAsync<int>(
            [disk],
            () =>
            {
                Restore("NUGET_PACKAGES");
                throw new InvalidOperationException("second group failed");
            },
            outcome => reported = outcome));

        Assert.Equal("second group failed", thrown.Message);
        Assert.Equal(["R: (<nuget>)"], reported?.ReleasedFrom);
        Assert.Single(disk.Applied);
    }

    /// <summary>
    /// A failure while reporting the disk update after a failed restore is thrown together with the restore's exception.
    /// </summary>
    [Fact]
    public async Task RunAsync_CallbackThrowsAfterFailedRestore_ThrowsBothExceptions()
    {
        var disk = new FakeDisk(NodeDisk());

        var thrown = await Assert.ThrowsAsync<AggregateException>(() => CreateCoordinator().RunAsync<int>(
            [disk],
            () => throw new InvalidOperationException("restore failed"),
            _ => throw new IOException("settings locked")));

        Assert.Collection(
            thrown.InnerExceptions,
            first => Assert.Equal("restore failed", first.Message),
            second => Assert.Equal("settings locked", second.Message));
    }

    /// <summary>
    /// A callback failure after a successful restore is not hidden.
    /// </summary>
    [Fact]
    public async Task RunAsync_CallbackThrowsAfterSuccessfulRestore_Propagates()
    {
        var disk = new FakeDisk(NodeDisk());

        await Assert.ThrowsAsync<IOException>(() => CreateCoordinator().RunAsync(
            [disk],
            () => Task.FromResult(1),
            _ => throw new IOException("settings locked")));
    }

    /// <summary>
    /// A disk that records what is applied to it.
    /// </summary>
    /// <param name="options">The options the disk starts with.</param>
    private sealed class FakeDisk(DiskOptions options) : IMountedDisk
    {
        /// <summary>Gets the options passed to <see cref="ApplyOptionsAsync"/>.</summary>
        public List<DiskOptions> Applied { get; } = [];

        /// <summary>Gets the number of refreshes.</summary>
        public int Refreshes { get; private set; }

        /// <summary>Gets or sets the reason to refuse options, or <c>null</c> to accept them.</summary>
        public string? Error { get; init; }

        /// <summary>Gets a value indicating whether applying throws.</summary>
        public bool Throw { get; init; }

        /// <inheritdoc />
        public string MountPoint => Options.MountPoint;

        /// <inheritdoc />
        public bool IsMounted { get; set; } = true;

        /// <inheritdoc />
        public bool IsRemounting { get; set; }

        /// <inheritdoc />
        public DiskOptions Options { get; set; } = options;

        /// <inheritdoc />
        public Task<string?> ApplyOptionsAsync(DiskOptions newOptions)
        {
            if (Throw)
            {
                throw new InvalidOperationException("disk is gone");
            }

            if (Error is null)
            {
                Applied.Add(newOptions);
            }

            return Task.FromResult(Error);
        }

        /// <inheritdoc />
        public void Refresh() => Refreshes++;
    }
}
