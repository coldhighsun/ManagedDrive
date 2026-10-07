using ManagedDrive.Cli.Core;

namespace ManagedDrive.Tests;

public class CliCommandProcessorPresetTests
{
    [Fact]
    public async Task Create_WithPresetAndNoCapacity_ForwardsPresetsAndNullCapacity()
    {
        var controller = new PresetController();

        var outcome = await CliCommandProcessor.ExecuteAsync(["create", "r", "--preset", "node"], controller);

        Assert.True(outcome.Success);
        Assert.Equal("R:", controller.MountPoint);
        Assert.Null(controller.Capacity);
        Assert.Equal(["node"], controller.Presets);
        Assert.False(controller.PlainCreateCalled);
    }

    [Fact]
    public async Task Create_RepeatedPresetAndExplicitCapacity_ForwardsBoth()
    {
        var controller = new PresetController();

        var outcome = await CliCommandProcessor.ExecuteAsync(
            ["create", "R:", "--preset", "node", "--preset", "nuget", "--capacity-mb", "512", "--label", "Dev"], controller);

        Assert.True(outcome.Success);
        Assert.Equal(["node", "nuget"], controller.Presets);
        Assert.Equal(512UL * 1024 * 1024, controller.Capacity);
        Assert.Equal("Dev", controller.Label);
    }

    [Fact]
    public async Task Create_WithoutCapacityOrPreset_FailsWithoutCallingController()
    {
        var controller = new PresetController();

        var outcome = await CliCommandProcessor.ExecuteAsync(["create", "R:"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("--capacity-mb", outcome.Message);
        Assert.Null(controller.MountPoint);
        Assert.False(controller.PlainCreateCalled);
    }

    [Fact]
    public async Task Create_CapacityOnly_UsesThePlainCreate()
    {
        var controller = new PresetController();

        var outcome = await CliCommandProcessor.ExecuteAsync(["create", "R:", "--capacity-mb", "64"], controller);

        Assert.True(outcome.Success);
        Assert.True(controller.PlainCreateCalled);
        Assert.Null(controller.Presets);
    }

    [Fact]
    public async Task Create_ControllerRejectsPreset_ReportsItsMessage()
    {
        var controller = new PresetController { Fail = "Unknown preset \"x\"." };

        var outcome = await CliCommandProcessor.ExecuteAsync(["create", "R:", "--preset", "x"], controller);

        Assert.False(outcome.Success);
        Assert.Equal(1, outcome.ExitCode);
        Assert.Contains("Unknown preset", outcome.Message);
    }

    [Fact]
    public async Task PresetList_PrintsIdNameCapacityAndVariables()
    {
        var controller = new PresetController();

        var outcome = await CliCommandProcessor.ExecuteAsync(["preset", "list"], controller);

        Assert.True(outcome.Success);
        Assert.Contains("node", outcome.Message);
        Assert.Contains("Node.js caches", outcome.Message);
        Assert.Contains("npm_config_cache=npm-cache", outcome.Message);
        Assert.Contains("temp", outcome.Message);
        Assert.Contains("(temp directory)", outcome.Message);
    }

    [Fact]
    public async Task PresetList_NoPresets_SaysSo()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["preset", "list"], new EmptyController());

        Assert.True(outcome.Success);
        Assert.Contains("No presets", outcome.Message);
    }

    [Fact]
    public async Task PresetList_Json_ReturnsTheListAsData()
    {
        var outcome = await CliCommandProcessor.ExecuteAsync(["--json", "preset", "list"], new PresetController());

        Assert.True(outcome.Success);
        Assert.Contains("\"id\"", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("npm_config_cache=npm-cache", outcome.Message);
    }

    /// <summary>
    /// A controller that records what create received and lists two presets.
    /// </summary>
    private sealed class PresetController : StubCliDiskController
    {
        /// <summary>Gets the mount point received.</summary>
        public string? MountPoint { get; private set; }

        /// <summary>Gets the capacity received.</summary>
        public ulong? Capacity { get; private set; }

        /// <summary>Gets the label received.</summary>
        public string? Label { get; private set; }

        /// <summary>Gets the presets received, or <c>null</c> if the preset create was not called.</summary>
        public IReadOnlyList<string>? Presets { get; private set; }

        /// <summary>Gets a value indicating whether the plain create was called.</summary>
        public bool PlainCreateCalled { get; private set; }

        /// <summary>Gets or sets a failure message to return from the preset create.</summary>
        public string? Fail { get; init; }

        /// <inheritdoc />
        public override Task<(bool Success, string Message)> CreateAsync(
            string mountPoint, ulong capacityBytes, string? volumeLabel, string? imagePath, string? password)
        {
            PlainCreateCalled = true;
            MountPoint = mountPoint;
            return Task.FromResult((true, "ok"));
        }

        /// <inheritdoc />
        public override Task<(bool Success, string Message)> CreateWithPresetsAsync(
            string mountPoint, ulong? capacityBytes, string? volumeLabel, string? imagePath, string? password, IReadOnlyList<string> presets)
        {
            MountPoint = mountPoint;
            Capacity = capacityBytes;
            Label = volumeLabel;
            Presets = presets;
            return Task.FromResult(Fail is null ? (true, "ok") : (false, Fail));
        }

        /// <inheritdoc />
        public override Task<IReadOnlyList<CliPreset>> GetPresetsAsync() => Task.FromResult<IReadOnlyList<CliPreset>>(
        [
            new("node", "Node.js caches", true, 2UL * 1024 * 1024 * 1024, ["npm-cache"], ["npm_config_cache=npm-cache"], false),
            new("temp", "Temp directory", true, 4UL * 1024 * 1024 * 1024, ["Temp"], [], true),
        ]);
    }

    /// <summary>
    /// A controller that does not know presets, relying on the interface defaults.
    /// </summary>
    private sealed class EmptyController : StubCliDiskController
    {
    }
}
