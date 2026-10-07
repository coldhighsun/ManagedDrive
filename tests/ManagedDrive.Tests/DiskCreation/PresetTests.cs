namespace ManagedDrive.Tests;

public sealed class EnvRedirectPolicyTests
{
    [Theory]
    [InlineData("npm_config_cache", "npm-cache")]
    [InlineData("NUGET_PACKAGES", "nuget")]
    [InlineData("X", @"a\b\c")]
    [InlineData("X", "a/b")]
    public void Validate_AcceptableRedirect_ReturnsNone(string variable, string subPath)
    {
        var result = EnvRedirectPolicy.Validate(new() { Variable = variable, SubPath = subPath });

        Assert.Equal(EnvRedirectError.None, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1ABC")]
    [InlineData("has space")]
    [InlineData("a=b")]
    [InlineData("a%b")]
    public void Validate_BadVariableName_ReturnsBadVariable(string variable)
    {
        var result = EnvRedirectPolicy.Validate(new() { Variable = variable, SubPath = "x" });

        Assert.Equal(EnvRedirectError.BadVariable, result);
    }

    [Theory]
    [InlineData("Path")]
    [InlineData("PATH")]
    [InlineData("temp")]
    [InlineData("TMP")]
    [InlineData("USERPROFILE")]
    [InlineData("SystemRoot")]
    public void Validate_ReservedVariable_ReturnsReservedVariable(string variable)
    {
        var result = EnvRedirectPolicy.Validate(new() { Variable = variable, SubPath = "x" });

        Assert.Equal(EnvRedirectError.ReservedVariable, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"C:\x")]
    [InlineData(@"\\server\share")]
    [InlineData(@"..\x")]
    [InlineData(@"a\..\..\b")]
    [InlineData("a:b")]
    [InlineData("a*")]
    [InlineData("a?")]
    public void IsValidSubPath_PathOutsideTheDisk_ReturnsFalse(string subPath)
    {
        Assert.False(EnvRedirectPolicy.IsValidSubPath(subPath));
    }

    [Fact]
    public void IsValidSubPath_Null_ReturnsFalse()
    {
        Assert.False(EnvRedirectPolicy.IsValidSubPath(null));
    }

    [Theory]
    [InlineData("R:", "cache", @"R:\cache")]
    [InlineData(@"R:\", "a/b", @"R:\a\b")]
    [InlineData(@"D:\mount", @"x\", @"D:\mount\x")]
    public void Resolve_BuildsAbsolutePathOnTheMountPoint(string mountPoint, string subPath, string expected)
    {
        Assert.Equal(expected, EnvRedirectPolicy.Resolve(mountPoint, subPath));
    }
}

public sealed class PresetComposerTests
{
    [Fact]
    public void Merge_NoPresets_ReturnsEmptyComposition()
    {
        var result = PresetComposer.Merge([]);

        Assert.Equal(0UL, result.CapacityBytes);
        Assert.Null(result.VolumeLabel);
        Assert.Empty(result.Folders);
        Assert.Empty(result.EnvRedirects);
        Assert.False(result.SetAsTemp);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Merge_SumsCapacityAndUnitesFoldersAndVariables()
    {
        var result = PresetComposer.Merge([BuiltInPresets.Node, BuiltInPresets.NuGet, BuiltInPresets.Python]);

        Assert.Equal(BuiltInPresets.Node.CapacityBytes + BuiltInPresets.NuGet.CapacityBytes + BuiltInPresets.Python.CapacityBytes, result.CapacityBytes);
        Assert.Equal("Node Cache", result.VolumeLabel);
        Assert.Contains("npm-cache", result.Folders);
        Assert.Contains("nuget", result.Folders);
        Assert.Contains("pip-cache", result.Folders);
        Assert.Contains(result.EnvRedirects, r => r.Variable == "NUGET_PACKAGES");
        Assert.Contains(result.EnvRedirects, r => r.Variable == "PIP_CACHE_DIR");
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Merge_SamePresetTwice_DoesNotDuplicateEntries()
    {
        var result = PresetComposer.Merge([BuiltInPresets.NuGet, BuiltInPresets.NuGet]);

        Assert.Single(result.Folders);
        Assert.Single(result.EnvRedirects);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Merge_SameVariableDifferentFolder_ReportsConflictAndKeepsTheFirst()
    {
        var other = new DiskPreset
        {
            Id = "other",
            EnvRedirects = [new() { Variable = "nuget_packages", SubPath = "elsewhere" }],
        };

        var result = PresetComposer.Merge([BuiltInPresets.NuGet, other]);

        Assert.Equal(["nuget_packages"], result.Conflicts);
        var kept = Assert.Single(result.EnvRedirects);
        Assert.Equal("nuget", kept.SubPath);
    }

    [Fact]
    public void Merge_TempPresetCombinesWithCaches()
    {
        var result = PresetComposer.Merge([BuiltInPresets.Temp, BuiltInPresets.Node]);

        Assert.True(result.SetAsTemp);
        Assert.Contains("Temp", result.Folders);
        Assert.Contains("npm-cache", result.Folders);
    }

    [Fact]
    public void Merge_TakesTheFirstLabelAndCompressionThatAreSet()
    {
        var first = new DiskPreset { Id = "a", CapacityBytes = 1 };
        var second = new DiskPreset { Id = "b", CapacityBytes = 1, VolumeLabel = "B", CompressionLevel = ImageCompressionLevel.Optimal };
        var third = new DiskPreset { Id = "c", CapacityBytes = 1, VolumeLabel = "C", CompressionLevel = ImageCompressionLevel.None };

        var result = PresetComposer.Merge([first, second, third]);

        Assert.Equal("B", result.VolumeLabel);
        Assert.Equal(ImageCompressionLevel.Optimal, result.CompressionLevel);
    }

    [Fact]
    public void Merge_CapacityOverflow_SaturatesInsteadOfWrapping()
    {
        var huge = new DiskPreset { Id = "a", CapacityBytes = ulong.MaxValue - 1 };

        var result = PresetComposer.Merge([huge, huge]);

        Assert.Equal(ulong.MaxValue, result.CapacityBytes);
    }
}

public sealed class BuiltInPresetsTests
{
    [Fact]
    public void All_IdsAreUniqueAndCapacitiesPositive()
    {
        Assert.Equal(BuiltInPresets.All.Count, BuiltInPresets.All.Select(p => p.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(BuiltInPresets.All, preset => Assert.True(preset.CapacityBytes > 0));
    }

    [Fact]
    public void All_EveryRedirectAndFolderPassesThePolicy()
    {
        foreach (var preset in BuiltInPresets.All)
        {
            Assert.All(preset.EnvRedirects, redirect => Assert.Equal(EnvRedirectError.None, EnvRedirectPolicy.Validate(redirect)));
            Assert.All(preset.Folders, folder => Assert.True(EnvRedirectPolicy.IsValidSubPath(folder)));
        }
    }

    [Fact]
    public void All_EveryRedirectTargetIsAlsoACreatedFolder()
    {
        foreach (var preset in BuiltInPresets.All)
        {
            Assert.All(preset.EnvRedirects, redirect => Assert.Contains(redirect.SubPath, preset.Folders));
        }
    }

    [Fact]
    public void All_CanAllBeCombinedWithoutConflict()
    {
        var result = PresetComposer.Merge(BuiltInPresets.All);

        Assert.Empty(result.Conflicts);
    }
}
