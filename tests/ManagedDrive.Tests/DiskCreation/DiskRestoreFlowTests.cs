namespace ManagedDrive.Tests;

/// <summary>
/// Runs plan, restore, settle and apply in sequence against a fake environment, the way the view
/// model does, so the cases that only show up across the steps are covered.
/// </summary>
public sealed class DiskRestoreFlowTests
{
    private static readonly EnvRedirect Custom = new() { Variable = "MY_CACHE", SubPath = "mine" };

    /// <summary>
    /// Plays a restore on a disk.
    /// </summary>
    /// <param name="folders">The folders the disk lists.</param>
    /// <param name="redirects">The redirections the disk lists.</param>
    /// <param name="pointing">The variables that point into the disk before the restore.</param>
    /// <param name="restored">The variables the restore really put back.</param>
    /// <returns>What the disk keeps and the ids of the presets it lost.</returns>
    private static PresetReleaseResult Run(
        IReadOnlyList<string> folders, IReadOnlyList<EnvRedirect> redirects, string[] pointing, string[] restored)
    {
        var environment = pointing.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var planned = DiskRestorePlanner.Plan(BuiltInPresets.All, redirects, r => environment.Contains(r.Variable));
        environment.ExceptWith(restored);
        var settled = DiskRestorePlanner.Settle(planned, redirects, r => environment.Contains(r.Variable));
        return DiskRestorePlanner.Apply(BuiltInPresets.All, folders, redirects, settled);
    }

    [Fact]
    public void PlanSettleApply_WholePresetRestored_DiskLosesItAndKeepsTheOther()
    {
        string[] folders = [.. BuiltInPresets.Node.Folders, .. BuiltInPresets.NuGet.Folders];
        EnvRedirect[] redirects = [.. BuiltInPresets.Node.EnvRedirects, .. BuiltInPresets.NuGet.EnvRedirects];
        string[] node = [.. BuiltInPresets.Node.EnvRedirects.Select(r => r.Variable)];

        var result = Run(folders, redirects, [.. node, "NUGET_PACKAGES"], node);

        Assert.Equal(["node"], result.ReleasedPresetIds);
        Assert.Equal(BuiltInPresets.NuGet.Folders, result.Folders);
        Assert.Equal(BuiltInPresets.NuGet.EnvRedirects, result.EnvRedirects);
    }

    [Fact]
    public void PlanSettleApply_OneVariableOfThePresetFailedToRestore_DiskKeepsTheWholePreset()
    {
        var redirects = BuiltInPresets.Node.EnvRedirects;
        string[] all = [.. redirects.Select(r => r.Variable)];

        var result = Run(BuiltInPresets.Node.Folders, redirects, all, ["npm_config_cache", "npm_config_store_dir"]);

        Assert.True(result.IsEmpty);
        Assert.Equal(BuiltInPresets.Node.Folders, result.Folders);
        Assert.Equal(redirects, result.EnvRedirects);
    }

    [Fact]
    public void PlanSettleApply_PresetBackedOnlyInPart_IsTakenWholeIncludingTheUnbackedVariable()
    {
        var redirects = BuiltInPresets.Node.EnvRedirects;

        // YARN_CACHE_FOLDER belongs to another disk, so only two variables point into this one.
        var result = Run(BuiltInPresets.Node.Folders, redirects, ["npm_config_cache", "npm_config_store_dir"], ["npm_config_cache", "npm_config_store_dir"]);

        Assert.Equal(["node"], result.ReleasedPresetIds);
        Assert.Empty(result.Folders);
        Assert.Empty(result.EnvRedirects);
    }

    [Fact]
    public void PlanSettleApply_PresetVariableWithACustomSubfolder_IsTakenWhenRestored()
    {
        EnvRedirect[] redirects = [new() { Variable = "NUGET_PACKAGES", SubPath = "pkgs" }];

        var result = Run([], redirects, ["NUGET_PACKAGES"], ["NUGET_PACKAGES"]);

        Assert.Empty(result.EnvRedirects);
    }

    [Fact]
    public void PlanSettleApply_CustomVariableNotRestored_StaysAndRestoredOneGoes()
    {
        EnvRedirect[] redirects = [Custom, new() { Variable = "OTHER_CACHE", SubPath = "o" }];

        var result = Run([], redirects, ["MY_CACHE", "OTHER_CACHE"], ["OTHER_CACHE"]);

        Assert.Equal([Custom], result.EnvRedirects);
    }

    [Fact]
    public void PlanSettleApply_NothingRestored_ChangesNothing()
    {
        var redirects = BuiltInPresets.NuGet.EnvRedirects;

        var result = Run(BuiltInPresets.NuGet.Folders, redirects, ["NUGET_PACKAGES"], []);

        Assert.True(result.IsEmpty);
        Assert.Equal(redirects, result.EnvRedirects);
    }
}
