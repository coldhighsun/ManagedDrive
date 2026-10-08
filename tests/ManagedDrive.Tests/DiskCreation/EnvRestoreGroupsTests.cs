namespace ManagedDrive.Tests;

public sealed class EnvRestoreGroupsTests
{
    [Fact]
    public void Build_NothingRedirected_ReturnsNoGroups()
    {
        var groups = EnvRestoreGroups.Build([]);

        Assert.Empty(groups);
    }

    [Fact]
    public void Build_PresetVariables_AreGroupedByPresetInDisplayOrder()
    {
        var groups = EnvRestoreGroups.Build(["PIP_CACHE_DIR", "npm_config_cache", "TEMP", "TMP"]);

        Assert.Equal(["temp", "node", "python"], groups.Select(g => g.Id));
        Assert.Equal(["TEMP", "TMP"], groups[0].Variables);
        Assert.Equal(["npm_config_cache"], groups[1].Variables);
    }

    [Fact]
    public void Build_ListsOnlyTheVariablesThatAreRedirected()
    {
        var groups = EnvRestoreGroups.Build(["YARN_CACHE_FOLDER"]);

        var group = Assert.Single(groups);
        Assert.Equal("node", group.Id);
        Assert.Equal(["YARN_CACHE_FOLDER"], group.Variables);
    }

    [Fact]
    public void Build_NamesAreComparedIgnoringCase()
    {
        var groups = EnvRestoreGroups.Build(["nuget_packages", "Temp"]);

        Assert.Equal(["temp", "nuget"], groups.Select(g => g.Id));
    }

    [Fact]
    public void Build_VariableNoPresetOwns_GoesIntoTheOtherGroupLast()
    {
        var groups = EnvRestoreGroups.Build(["ZED_CACHE", "NUGET_PACKAGES", "ALPHA_CACHE"]);

        Assert.Equal(["nuget", EnvRestoreGroups.OtherId], groups.Select(g => g.Id));
        Assert.Equal(["ALPHA_CACHE", "ZED_CACHE"], groups[1].Variables);
    }

    [Fact]
    public void Build_DuplicateNames_AppearOnce()
    {
        var groups = EnvRestoreGroups.Build(["TEMP", "temp", "CUSTOM", "custom"]);

        Assert.Equal(["TEMP"], groups[0].Variables);
        Assert.Single(groups[1].Variables);
    }
}
