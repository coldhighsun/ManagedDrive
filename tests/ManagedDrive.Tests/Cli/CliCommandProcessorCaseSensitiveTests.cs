using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the <c>--case-sensitive</c> flag of <c>mount</c> and <c>create</c> and for the
/// "Case-sensitive" line of <c>info</c>.
/// </summary>
public sealed class CliCommandProcessorCaseSensitiveTests
{
    /// <summary>
    /// <c>mount --case-sensitive</c> reaches the controller as an override.
    /// </summary>
    [Fact]
    public async Task Mount_CaseSensitiveFlag_ForwardsOverrideToController()
    {
        var imagePath = CreateTempImageFile();
        try
        {
            var controller = new Controller();

            var outcome = await CliCommandProcessor.ExecuteAsync(["mount", imagePath, "r", "--case-sensitive"], controller);

            Assert.True(outcome.Success);
            Assert.True(controller.LastOverrides?.CaseSensitive);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    /// <summary>
    /// Without the flag the override stays unset, so the saved profile's value is kept.
    /// </summary>
    [Fact]
    public async Task Mount_NoCaseSensitiveFlag_LeavesOverrideUnset()
    {
        var imagePath = CreateTempImageFile();
        try
        {
            var controller = new Controller();

            await CliCommandProcessor.ExecuteAsync(["mount", imagePath, "r"], controller);

            Assert.Null(controller.LastOverrides?.CaseSensitive);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    /// <summary>
    /// <c>mount --case-sensitive false</c> turns an option the profile has on off again.
    /// </summary>
    [Fact]
    public async Task Mount_CaseSensitiveFalse_ForwardsFalseOverride()
    {
        var imagePath = CreateTempImageFile();
        try
        {
            var controller = new Controller();

            await CliCommandProcessor.ExecuteAsync(["mount", imagePath, "r", "--case-sensitive", "false"], controller);

            Assert.False(controller.LastOverrides?.CaseSensitive);
        }
        finally
        {
            File.Delete(imagePath);
        }
    }

    /// <summary>
    /// <c>create --case-sensitive</c> goes through the case-sensitive create and not the plain one.
    /// </summary>
    [Fact]
    public async Task Create_CaseSensitiveFlag_UsesCaseSensitiveCreate()
    {
        var controller = new Controller();

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["create", "r", "--capacity-mb", "64", "--case-sensitive"], controller);

        Assert.True(outcome.Success);
        Assert.Equal("R:", controller.CaseSensitiveMountPoint);
        Assert.Equal(64UL * 1024 * 1024, controller.CaseSensitiveCapacity);
        Assert.False(controller.PlainCreateCalled);
    }

    /// <summary>
    /// Without the flag <c>create</c> keeps calling the plain create, so controllers that predate
    /// the option behave as before.
    /// </summary>
    [Fact]
    public async Task Create_NoCaseSensitiveFlag_UsesPlainCreate()
    {
        var controller = new Controller();

        await CliCommandProcessor.ExecuteAsync(["create", "r", "--capacity-mb", "64"], controller);

        Assert.True(controller.PlainCreateCalled);
        Assert.Null(controller.CaseSensitiveMountPoint);
    }

    /// <summary>
    /// A controller that has no case-sensitive create refuses the flag with its message.
    /// </summary>
    [Fact]
    public async Task Create_CaseSensitiveFlagOnOldController_Fails()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["create", "r", "--capacity-mb", "64", "--case-sensitive"], new OldController());

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("not supported", outcome.Message);
    }

    /// <summary>
    /// <c>info</c> says whether the disk is case-sensitive.
    /// </summary>
    /// <param name="caseSensitive">The disk's setting.</param>
    /// <param name="expected">The text expected after the label.</param>
    [Theory]
    [InlineData(true, "yes")]
    [InlineData(false, "no")]
    public async Task Info_CaseSensitiveDisk_ShowsTheSetting(bool caseSensitive, string expected)
    {
        var details = new CliDiskDetails(
            "R:", "Scratch", 1024UL, 64UL * 1024 * 1024, ReadOnly: false,
            ImagePath: null, SourceArchivePath: null, PasswordProtected: false,
            AutoSaveIntervalMinutes: null, CompressionLevel: "1",
            MaxSnapshotCount: null, MaxSnapshotSizeBytes: null, SnapshotCount: null, LastSaveTime: null,
            CaseSensitive: caseSensitive);

        var outcome = await CliCommandProcessor.ExecuteAsync(["info", "r"], new Controller { Details = details });

        Assert.Matches($@"Case-sensitive:\s+{expected}", outcome.Message);
    }

    /// <summary>
    /// Creates an empty file the mount command accepts as an image path.
    /// </summary>
    /// <returns>The file's path.</returns>
    private static string CreateTempImageFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.mdr");
        File.WriteAllBytes(path, []);
        return path;
    }

    /// <summary>
    /// A controller that has the case-sensitive create and records what it is asked.
    /// </summary>
    private sealed class Controller : StubCliDiskController
    {
        /// <summary>
        /// Gets the overrides of the last mount.
        /// </summary>
        public CliMountOverrides? LastOverrides
        {
            get; private set;
        }

        /// <summary>
        /// Gets or sets what <c>info</c> returns.
        /// </summary>
        public CliDiskDetails? Details
        {
            get; set;
        }

        /// <summary>
        /// Gets the mount point of the last case-sensitive create.
        /// </summary>
        public string? CaseSensitiveMountPoint
        {
            get; private set;
        }

        /// <summary>
        /// Gets the capacity of the last case-sensitive create.
        /// </summary>
        public ulong? CaseSensitiveCapacity
        {
            get; private set;
        }

        /// <summary>
        /// Gets a value indicating whether the plain create ran.
        /// </summary>
        public bool PlainCreateCalled
        {
            get; private set;
        }

        /// <inheritdoc />
        public override Task<(bool Success, string Message)> MountImageAsync(string imagePath, string mountPoint, CliMountOverrides overrides)
        {
            LastOverrides = overrides;
            return Task.FromResult((true, "Mounted."));
        }

        /// <inheritdoc />
        public override Task<(bool Success, string Message)> CreateAsync(
            string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password)
        {
            PlainCreateCalled = true;
            return Task.FromResult((true, "Created."));
        }

        /// <inheritdoc />
        public override Task<(bool Success, string Message)> CreateCaseSensitiveAsync(
            string mountPoint, ulong? capacityBytes, string? volumeLabel, string? imagePath, string? password, IReadOnlyList<string> presets)
        {
            CaseSensitiveMountPoint = mountPoint;
            CaseSensitiveCapacity = capacityBytes;
            return Task.FromResult((true, "Created."));
        }

        /// <inheritdoc />
        public override Task<(bool Success, string Message, CliDiskDetails? Details)> GetDiskInfoAsync(string mountPoint) =>
            Task.FromResult((Details is not null, string.Empty, Details));
    }

    /// <summary>
    /// A controller that does not override the case-sensitive create, as one that predates it.
    /// </summary>
    private sealed class OldController : StubCliDiskController
    {
    }
}
