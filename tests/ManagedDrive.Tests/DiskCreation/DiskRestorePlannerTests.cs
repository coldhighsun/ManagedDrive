namespace ManagedDrive.Tests;

public sealed class DiskRestorePlannerTests
{
    private static readonly EnvRedirect Custom = new() { Variable = "My_Cache", SubPath = "mine" };

    [Fact]
    public void Plan_PresetWithPointingVariables_IsACandidate()
    {
        var plan = DiskRestorePlanner.Plan(
            BuiltInPresets.All, BuiltInPresets.Node.EnvRedirects, _ => true);

        Assert.Equal(["node"], plan.Presets.Select(p => p.Id));
        Assert.Empty(plan.CustomVariables);
    }

    [Fact]
    public void Plan_PresetWithoutAnyPointingVariable_IsNotACandidate()
    {
        var plan = DiskRestorePlanner.Plan(BuiltInPresets.All, BuiltInPresets.Node.EnvRedirects, r => r.Variable == "YARN_CACHE_FOLDER");

        // One pointing variable is enough to take the preset whole.
        Assert.Equal(["node"], plan.Presets.Select(p => p.Id));
        Assert.Empty(plan.CustomVariables);
    }

    [Fact]
    public void Plan_NothingPointsIntoTheDisk_IsEmpty()
    {
        var plan = DiskRestorePlanner.Plan(BuiltInPresets.All, BuiltInPresets.Node.EnvRedirects, _ => false);

        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Plan_CustomRedirect_IsACandidateOnlyWhileItPointsIntoTheDisk()
    {
        var pointing = DiskRestorePlanner.Plan(BuiltInPresets.All, [Custom], _ => true);
        var elsewhere = DiskRestorePlanner.Plan(BuiltInPresets.All, [Custom], _ => false);

        Assert.Equal(["My_Cache"], pointing.CustomVariables);
        Assert.Empty(elsewhere.CustomVariables);
    }

    [Fact]
    public void Plan_PresetVariablesAreNeverCustom()
    {
        var plan = DiskRestorePlanner.Plan(BuiltInPresets.All, BuiltInPresets.NuGet.EnvRedirects, _ => true);

        Assert.Empty(plan.CustomVariables);
    }

    [Fact]
    public void Settle_VariablesRestored_KeepsEverythingPlanned()
    {
        EnvRedirect[] redirects = [.. BuiltInPresets.Node.EnvRedirects, Custom];
        var planned = DiskRestorePlanner.Plan(BuiltInPresets.All, redirects, _ => true);

        var settled = DiskRestorePlanner.Settle(planned, redirects, _ => false);

        Assert.Equal(["node"], settled.Presets.Select(p => p.Id));
        Assert.Equal(["My_Cache"], settled.CustomVariables);
    }

    [Fact]
    public void Settle_PresetVariableStillPointsIntoTheDisk_KeepsThePreset()
    {
        var planned = DiskRestorePlanner.Plan(BuiltInPresets.All, BuiltInPresets.Node.EnvRedirects, _ => true);

        // One of node's three variables was not restored (the restore failed for it).
        var settled = DiskRestorePlanner.Settle(
            planned, BuiltInPresets.Node.EnvRedirects, redirect => redirect.Variable == "YARN_CACHE_FOLDER");

        Assert.Empty(settled.Presets);
    }

    [Fact]
    public void Settle_OnlyOnePresetRestored_TakesAwayThatPresetAndNotTheOther()
    {
        EnvRedirect[] redirects = [.. BuiltInPresets.Node.EnvRedirects, .. BuiltInPresets.NuGet.EnvRedirects];
        var planned = DiskRestorePlanner.Plan(BuiltInPresets.All, redirects, _ => true);

        var settled = DiskRestorePlanner.Settle(planned, redirects, redirect => redirect.Variable == "NUGET_PACKAGES");

        Assert.Equal(["node"], settled.Presets.Select(p => p.Id));
    }

    [Fact]
    public void Settle_CustomVariableNotRestored_StaysOnTheDisk()
    {
        var planned = DiskRestorePlanner.Plan(BuiltInPresets.All, [Custom], _ => true);

        var settled = DiskRestorePlanner.Settle(planned, [Custom], _ => true);

        Assert.Empty(settled.CustomVariables);
        Assert.True(settled.IsEmpty);
    }

    [Fact]
    public void Plan_PresetVariableRedirectedToAnotherFolder_StillMakesThePresetACandidate()
    {
        EnvRedirect[] redirects = [new() { Variable = "nuget_packages", SubPath = "pkgs" }];

        var plan = DiskRestorePlanner.Plan(BuiltInPresets.All, redirects, _ => true);

        Assert.Equal(["nuget"], plan.Presets.Select(p => p.Id));
        Assert.Empty(plan.CustomVariables);
    }

    [Fact]
    public void Apply_RestoredPreset_IsTakenAwayWithItsFoldersAndVariables()
    {
        var settled = new DiskRestoreCandidates([BuiltInPresets.Node], []);

        var result = DiskRestorePlanner.Apply(BuiltInPresets.All, BuiltInPresets.Node.Folders, BuiltInPresets.Node.EnvRedirects, settled);

        Assert.Equal(["node"], result.ReleasedPresetIds);
        Assert.Empty(result.Folders);
        Assert.Empty(result.EnvRedirects);
    }

    [Fact]
    public void Apply_OnlyOnePreset_LeavesTheOtherPresetOnTheDisk()
    {
        string[] folders = [.. BuiltInPresets.Node.Folders, .. BuiltInPresets.NuGet.Folders];
        EnvRedirect[] redirects = [.. BuiltInPresets.Node.EnvRedirects, .. BuiltInPresets.NuGet.EnvRedirects];

        var result = DiskRestorePlanner.Apply(BuiltInPresets.All, folders, redirects, new([BuiltInPresets.Node], []));

        Assert.Equal(BuiltInPresets.NuGet.Folders, result.Folders);
        Assert.Equal(BuiltInPresets.NuGet.EnvRedirects, result.EnvRedirects);
    }

    [Fact]
    public void Apply_CustomVariable_IsRemovedIgnoringCaseAndPresetsStay()
    {
        EnvRedirect[] redirects = [.. BuiltInPresets.NuGet.EnvRedirects, Custom];

        var result = DiskRestorePlanner.Apply(BuiltInPresets.All, BuiltInPresets.NuGet.Folders, redirects, new([], ["MY_CACHE"]));

        Assert.Equal(BuiltInPresets.NuGet.EnvRedirects, result.EnvRedirects);
        Assert.Equal(BuiltInPresets.NuGet.Folders, result.Folders);
    }

    [Fact]
    public void Apply_PresetTheDiskDoesNotList_ChangesNothing()
    {
        var result = DiskRestorePlanner.Apply(
            BuiltInPresets.All, BuiltInPresets.NuGet.Folders, BuiltInPresets.NuGet.EnvRedirects, new([BuiltInPresets.Node], []));

        Assert.True(result.IsEmpty);
        Assert.Equal(BuiltInPresets.NuGet.Folders, result.Folders);
        Assert.Equal(BuiltInPresets.NuGet.EnvRedirects, result.EnvRedirects);
    }
}
