namespace ManagedDrive.Tests;

public sealed class PresetSelectionTests
{
    [Fact]
    public void Detect_DiskWithAPresetsFoldersAndVariables_FindsThePreset()
    {
        var detected = PresetSelection.Detect(
            BuiltInPresets.All, BuiltInPresets.NuGet.Folders, BuiltInPresets.NuGet.EnvRedirects);

        Assert.Equal(["nuget"], detected);
    }

    [Fact]
    public void Detect_PresetOnlyPartlyPresent_IsNotFound()
    {
        var detected = PresetSelection.Detect(
            BuiltInPresets.All, ["npm-cache", "yarn-cache"], [BuiltInPresets.Node.EnvRedirects[0]]);

        Assert.DoesNotContain("node", detected);
    }

    [Fact]
    public void Detect_IgnoresCaseAndSurroundingSlashes()
    {
        var detected = PresetSelection.Detect(
            BuiltInPresets.All, [@"\NUGET\"], [new() { Variable = "nuget_packages", SubPath = "/nuget/" }]);

        Assert.Equal(["nuget"], detected);
    }

    [Fact]
    public void Detect_EmptyDisk_FindsNothing()
    {
        Assert.Empty(PresetSelection.Detect(BuiltInPresets.All, [], []));
    }

    /// <summary>
    /// Only the Temp preset is reported when just its variables point into the disk.
    /// </summary>
    [Fact]
    public void DetectActive_OnlyTempPointsIntoDisk_ReportsTemp()
    {
        var active = PresetSelection.DetectActive(
            BuiltInPresets.All, [], redirect => BuiltInPresets.Temp.EnvRedirects.Contains(redirect));

        Assert.Equal(["temp"], active.PresetIds);
        Assert.Empty(active.CustomVariables);
        Assert.False(active.IsEmpty);
    }

    /// <summary>
    /// Every preset whose variables all point into the disk is reported.
    /// </summary>
    [Fact]
    public void DetectActive_TempAndNuGetPointIntoDisk_ReportsBoth()
    {
        var active = PresetSelection.DetectActive(
            BuiltInPresets.All,
            [],
            redirect => BuiltInPresets.Temp.EnvRedirects.Concat(BuiltInPresets.NuGet.EnvRedirects).Contains(redirect));

        Assert.Equal(["temp", "nuget"], active.PresetIds);
    }

    /// <summary>
    /// A preset with only some of its variables pointing into the disk is not reported.
    /// </summary>
    [Fact]
    public void DetectActive_PresetOnlyPartlyRedirected_IsNotReported()
    {
        var active = PresetSelection.DetectActive(
            BuiltInPresets.All, [], redirect => redirect == BuiltInPresets.Node.EnvRedirects[0]);

        Assert.True(active.IsEmpty);
    }

    /// <summary>
    /// A folder-only preset is never reported, since the environment cannot tell whether it is in use.
    /// </summary>
    [Fact]
    public void DetectActive_FolderOnlyBrowserPreset_IsNeverReported()
    {
        var active = PresetSelection.DetectActive(BuiltInPresets.All, [], _ => true);

        Assert.DoesNotContain("browser", active.PresetIds);
    }

    /// <summary>
    /// A variable no preset owns is reported as custom when it points into the disk.
    /// </summary>
    [Fact]
    public void DetectActive_CustomVariablePointingIntoDisk_IsReportedAsCustom()
    {
        var custom = new EnvRedirect { Variable = "GOPATH", SubPath = "go" };
        var other = new EnvRedirect { Variable = "CARGO_HOME", SubPath = "cargo" };

        var active = PresetSelection.DetectActive(
            BuiltInPresets.All, [custom, other, BuiltInPresets.NuGet.EnvRedirects[0]], redirect => redirect == custom);

        Assert.Empty(active.PresetIds);
        Assert.Equal(["GOPATH"], active.CustomVariables);
    }

    /// <summary>
    /// The Temp preset is reported when only TEMP points into the disk, as the card has always flagged it.
    /// </summary>
    [Fact]
    public void DetectActive_OnlyTempVariablePointsIntoDisk_ReportsTemp()
    {
        var active = PresetSelection.DetectActive(
            BuiltInPresets.All, [], redirect => redirect.Variable == "TEMP");

        Assert.Equal(["temp"], active.PresetIds);
    }

    /// <summary>
    /// Reconcile treats a disk whose TEMP alone points into it as having the Temp preset, like the badge does.
    /// </summary>
    [Fact]
    public void Reconcile_OnlyTempVariablePointsIntoDisk_AddsTheTempPreset()
    {
        var result = PresetSelection.Reconcile(
            BuiltInPresets.All, [], [], redirect => redirect.Variable == "TEMP");

        Assert.Equal(BuiltInPresets.Temp.Folders, result.Folders);
        Assert.Equal(BuiltInPresets.Temp.EnvRedirects, result.EnvRedirects);
    }

    /// <summary>
    /// A disk whose TEMP alone points into it is stored as the whole Temp preset, so TEMP and TMP end up in sync.
    /// </summary>
    [Fact]
    public void Split_AfterReconcileOfTempOnlyDisk_StoresTheTempPresetId()
    {
        var reconciled = PresetSelection.Reconcile(
            BuiltInPresets.All, [], [], redirect => redirect.Variable == "TEMP");

        var stored = PresetSelection.Split(BuiltInPresets.All, reconciled.Folders, reconciled.EnvRedirects);

        Assert.Equal(["temp"], stored.PresetIds);
        Assert.Empty(stored.EnvRedirects);
    }

    /// <summary>
    /// Taking the Temp preset away from a TEMP-only disk releases TEMP and TMP together.
    /// </summary>
    [Fact]
    public void Release_TempPresetOfTempOnlyDisk_ReleasesBothVariables()
    {
        var reconciled = PresetSelection.Reconcile(
            BuiltInPresets.All, [], [], redirect => redirect.Variable == "TEMP");

        var released = PresetSelection.Release(
            BuiltInPresets.All, reconciled.Folders, reconciled.EnvRedirects, [BuiltInPresets.Temp]);

        Assert.Equal(["temp"], released.ReleasedPresetIds);
        Assert.Equal(["TEMP", "TMP"], released.ReleasedVariables.Order().ToList());
        Assert.Empty(released.EnvRedirects);
    }

    /// <summary>
    /// A disk the user's TMP alone points into is not the temp disk.
    /// </summary>
    [Fact]
    public void TempPointsIntoDisk_OnlyTmpPointsIntoDisk_ReturnsFalse()
    {
        Assert.False(PresetSelection.TempPointsIntoDisk(redirect => redirect.Variable == "TMP"));
        Assert.True(PresetSelection.TempPointsIntoDisk(redirect => redirect.Variable == "TEMP"));
    }

    /// <summary>
    /// Nothing is reported when no variable points into the disk.
    /// </summary>
    [Fact]
    public void DetectActive_NothingPointsIntoDisk_IsEmpty()
    {
        Assert.True(PresetSelection.DetectActive(BuiltInPresets.All, [], _ => false).IsEmpty);
    }

    [Fact]
    public void Apply_NewDisk_EqualsTheMergedPresets()
    {
        var result = PresetSelection.Apply(BuiltInPresets.All, [BuiltInPresets.Node, BuiltInPresets.NuGet], [], []);

        var merged = PresetComposer.Merge([BuiltInPresets.Node, BuiltInPresets.NuGet]);
        Assert.Equal(merged.Folders, result.Folders);
        Assert.Equal(merged.EnvRedirects, result.EnvRedirects);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void Apply_UntickingAPreset_DropsItsFoldersAndVariablesOnly()
    {
        var both = PresetComposer.Merge([BuiltInPresets.Node, BuiltInPresets.NuGet]);

        var result = PresetSelection.Apply(BuiltInPresets.All, [BuiltInPresets.NuGet], both.Folders, both.EnvRedirects);

        Assert.Equal(BuiltInPresets.NuGet.Folders, result.Folders);
        Assert.Equal(BuiltInPresets.NuGet.EnvRedirects, result.EnvRedirects);
    }

    [Fact]
    public void Apply_ThingsNoPresetOwns_AreKept()
    {
        var customRedirect = new EnvRedirect { Variable = "MY_CACHE", SubPath = "mine" };

        var result = PresetSelection.Apply(BuiltInPresets.All, [BuiltInPresets.Python], ["mine"], [customRedirect]);

        Assert.Equal(["mine", "pip-cache"], result.Folders);
        Assert.Equal([customRedirect, BuiltInPresets.Python.EnvRedirects[0]], result.EnvRedirects);
    }

    [Fact]
    public void Apply_NothingTicked_KeepsOnlyWhatNoPresetOwns()
    {
        var both = PresetComposer.Merge([BuiltInPresets.Node, BuiltInPresets.Python]);

        var result = PresetSelection.Apply(BuiltInPresets.All, [], [.. both.Folders, "mine"], both.EnvRedirects);

        Assert.Equal(["mine"], result.Folders);
        Assert.Empty(result.EnvRedirects);
    }

    [Fact]
    public void Apply_CustomVariableClashesWithATickedPreset_ReportsConflictAndKeepsTheCustomOne()
    {
        var custom = new EnvRedirect { Variable = "PIP_CACHE_DIR", SubPath = "elsewhere" };

        var result = PresetSelection.Apply(BuiltInPresets.All, [BuiltInPresets.Python], ["elsewhere"], [custom]);

        Assert.Equal(["PIP_CACHE_DIR"], result.Conflicts);
        Assert.Equal([custom], result.EnvRedirects);
    }

    [Fact]
    public void Reconcile_EnvironmentPointsIntoDiskButSettingsLackIt_AddsThePreset()
    {
        var result = PresetSelection.Reconcile(
            BuiltInPresets.All, [], [], redirect => BuiltInPresets.Temp.EnvRedirects.Contains(redirect));

        Assert.Equal(BuiltInPresets.Temp.Folders, result.Folders);
        Assert.Equal(BuiltInPresets.Temp.EnvRedirects, result.EnvRedirects);
    }

    [Fact]
    public void Reconcile_SettingsListAPresetTheEnvironmentDoesNotBack_DropsIt()
    {
        var result = PresetSelection.Reconcile(
            BuiltInPresets.All, BuiltInPresets.Node.Folders, BuiltInPresets.Node.EnvRedirects, _ => false);

        Assert.Empty(result.Folders);
        Assert.Empty(result.EnvRedirects);
    }

    [Fact]
    public void Reconcile_PresetOnlyPartlyBackedByTheEnvironment_IsNotActive()
    {
        var backed = BuiltInPresets.Node.EnvRedirects[0];

        var result = PresetSelection.Reconcile(
            BuiltInPresets.All, BuiltInPresets.Node.Folders, BuiltInPresets.Node.EnvRedirects, redirect => redirect == backed);

        Assert.Empty(result.EnvRedirects);
    }

    [Fact]
    public void Reconcile_FolderOnlyPresetsAndCustomThingsAreLeftAlone()
    {
        var custom = new EnvRedirect { Variable = "MY_CACHE", SubPath = "mine" };

        var result = PresetSelection.Reconcile(
            BuiltInPresets.All, [.. BuiltInPresets.Browser.Folders, "mine"], [custom], _ => false);

        Assert.Equal([.. BuiltInPresets.Browser.Folders, "mine"], result.Folders);
        Assert.Equal([custom], result.EnvRedirects);
    }

    [Fact]
    public void Reconcile_ResultIsDetectedExactlyForTheActivePresets()
    {
        var result = PresetSelection.Reconcile(
            BuiltInPresets.All, BuiltInPresets.Node.Folders, BuiltInPresets.Node.EnvRedirects,
            redirect => BuiltInPresets.NuGet.EnvRedirects.Contains(redirect));

        Assert.Equal(["nuget"], PresetSelection.Detect(BuiltInPresets.All, result.Folders, result.EnvRedirects));
    }

    [Fact]
    public void Split_VariableRedirectingPreset_IsKeptAsAnIdOnly()
    {
        var custom = new EnvRedirect { Variable = "MY_CACHE", SubPath = "mine" };
        var node = PresetComposer.Merge([BuiltInPresets.Node, BuiltInPresets.Browser]);

        var stored = PresetSelection.Split(BuiltInPresets.All, [.. node.Folders, "mine"], [.. node.EnvRedirects, custom]);

        Assert.Equal(["node"], stored.PresetIds);
        Assert.Equal([.. BuiltInPresets.Browser.Folders, "mine"], stored.Folders);
        Assert.Equal([custom], stored.EnvRedirects);
    }

    [Fact]
    public void Split_NoVariableRedirectingPreset_ReturnsTheListsUnchanged()
    {
        IReadOnlyList<string> folders = [.. BuiltInPresets.Browser.Folders, "mine"];
        IReadOnlyList<EnvRedirect> redirects = [BuiltInPresets.Node.EnvRedirects[0]];

        var stored = PresetSelection.Split(BuiltInPresets.All, folders, redirects);

        Assert.Empty(stored.PresetIds);
        Assert.Same(folders, stored.Folders);
        Assert.Same(redirects, stored.EnvRedirects);
    }

    [Fact]
    public void Expand_ThenSplit_RoundTrips()
    {
        var expanded = PresetSelection.Expand(
            BuiltInPresets.All, ["node", "temp", "nope"], ["mine"], [new() { Variable = "MY_CACHE", SubPath = "mine" }]);

        var stored = PresetSelection.Split(BuiltInPresets.All, expanded.Folders, expanded.EnvRedirects);

        Assert.Equal(["node", "temp"], expanded.PresetIds);
        Assert.Contains("npm-cache", expanded.Folders);
        Assert.Contains(expanded.EnvRedirects, r => r is { Variable: "TEMP" });
        Assert.Equal(["temp", "node"], stored.PresetIds);
        Assert.Equal(["mine"], stored.Folders);
        Assert.Equal("MY_CACHE", Assert.Single(stored.EnvRedirects).Variable);
    }

    [Fact]
    public void Expand_PresetAlreadyListedByHand_IsNotDuplicated()
    {
        var expanded = PresetSelection.Expand(
            BuiltInPresets.All, ["nuget"], BuiltInPresets.NuGet.Folders, BuiltInPresets.NuGet.EnvRedirects);

        Assert.Equal(BuiltInPresets.NuGet.Folders, expanded.Folders);
        Assert.Equal(BuiltInPresets.NuGet.EnvRedirects, expanded.EnvRedirects);
    }

    [Fact]
    public void IsExclusive_OnlyPresetsThatRedirectVariables()
    {
        Assert.True(PresetSelection.IsExclusive(BuiltInPresets.Node));
        Assert.True(PresetSelection.IsExclusive(BuiltInPresets.Temp));
        Assert.False(PresetSelection.IsExclusive(BuiltInPresets.Browser));
    }

    [Fact]
    public void Release_ClaimedPresetOnTheDisk_IsTakenAwayWithItsFoldersAndVariables()
    {
        var both = PresetComposer.Merge([BuiltInPresets.Node, BuiltInPresets.Python]);

        var result = PresetSelection.Release(
            BuiltInPresets.All, both.Folders, both.EnvRedirects, [BuiltInPresets.Node]);

        Assert.Equal(["node"], result.ReleasedPresetIds);
        Assert.Equal(["npm_config_cache", "YARN_CACHE_FOLDER", "npm_config_store_dir"], result.ReleasedVariables);
        Assert.Equal(BuiltInPresets.Python.Folders, result.Folders);
        Assert.Equal(BuiltInPresets.Python.EnvRedirects, result.EnvRedirects);
        Assert.False(result.IsEmpty);
    }

    [Fact]
    public void Release_ClaimedPresetNotOnTheDisk_ChangesNothing()
    {
        var result = PresetSelection.Release(
            BuiltInPresets.All, BuiltInPresets.Python.Folders, BuiltInPresets.Python.EnvRedirects, [BuiltInPresets.Node]);

        Assert.True(result.IsEmpty);
        Assert.Equal(BuiltInPresets.Python.Folders, result.Folders);
        Assert.Equal(BuiltInPresets.Python.EnvRedirects, result.EnvRedirects);
    }

    [Fact]
    public void Release_BrowserPreset_IsNeverTaken()
    {
        var result = PresetSelection.Release(
            BuiltInPresets.All, BuiltInPresets.Browser.Folders, [], [BuiltInPresets.Browser]);

        Assert.True(result.IsEmpty);
        Assert.Equal(BuiltInPresets.Browser.Folders, result.Folders);
    }

    [Fact]
    public void Release_PresetOnlyPartlyThere_IsStillTakenByItsVariable()
    {
        var result = PresetSelection.Release(
            BuiltInPresets.All, ["mine"], [new() { Variable = "nuget_packages", SubPath = "nuget" }], [BuiltInPresets.NuGet]);

        Assert.Equal(["nuget"], result.ReleasedPresetIds);
        Assert.Equal(["mine"], result.Folders);
        Assert.Empty(result.EnvRedirects);
    }

    [Fact]
    public void Release_KeepsWhatNoReleasedPresetOwns()
    {
        var custom = new EnvRedirect { Variable = "MY_CACHE", SubPath = "mine" };

        var result = PresetSelection.Release(
            BuiltInPresets.All,
            [.. BuiltInPresets.NuGet.Folders, "mine"],
            [.. BuiltInPresets.NuGet.EnvRedirects, custom],
            [BuiltInPresets.NuGet]);

        Assert.Equal(["mine"], result.Folders);
        Assert.Equal([custom], result.EnvRedirects);
    }
}

public sealed class DiskEffectsDiffTests
{
    [Fact]
    public void Compute_NothingChanged_IsEmpty()
    {
        var change = DiskEffectsDiff.Compute(["a"], [new() { Variable = "X", SubPath = "a" }], ["A"], [new() { Variable = "x", SubPath = "a" }]);

        Assert.True(change.IsEmpty);
    }

    [Fact]
    public void Compute_NullLists_AreTreatedAsEmpty()
    {
        Assert.True(DiskEffectsDiff.Compute(null, null, null, null).IsEmpty);
    }

    [Fact]
    public void Compute_AddedPreset_ListsNewFoldersAndVariables()
    {
        var change = DiskEffectsDiff.Compute(null, null, BuiltInPresets.Node.Folders, BuiltInPresets.Node.EnvRedirects);

        Assert.Equal(BuiltInPresets.Node.Folders, change.AddedFolders);
        Assert.Equal(BuiltInPresets.Node.EnvRedirects, change.AddedRedirects);
        Assert.Empty(change.RemovedVariables);
    }

    [Fact]
    public void Compute_RemovedPreset_ListsVariablesToRestoreButNoFolders()
    {
        var change = DiskEffectsDiff.Compute(BuiltInPresets.Temp.Folders, BuiltInPresets.Temp.EnvRedirects, [], []);

        Assert.Empty(change.AddedFolders);
        Assert.Empty(change.AddedRedirects);
        Assert.Equal(["TEMP", "TMP"], change.RemovedVariables);
    }

    [Fact]
    public void Compute_VariableMovedToAnotherFolder_IsAddedAgainNotRemoved()
    {
        var change = DiskEffectsDiff.Compute(
            ["a"], [new() { Variable = "X", SubPath = "a" }], ["b"], [new() { Variable = "X", SubPath = "b" }]);

        Assert.Equal(["b"], change.AddedFolders);
        Assert.Equal([new EnvRedirect { Variable = "X", SubPath = "b" }], change.AddedRedirects);
        Assert.Empty(change.RemovedVariables);
    }
}

public sealed class PresetSummaryTests
{
    [Fact]
    public void Describe_VariablesPointingAtTheSameFolder_ShareOneLine()
    {
        var lines = PresetSummary.Describe(BuiltInPresets.Temp.Folders, BuiltInPresets.Temp.EnvRedirects);

        Assert.Equal([@"TEMP, TMP → Temp\"], lines);
    }

    [Fact]
    public void Describe_FolderWithoutAVariable_IsListedOnItsOwn()
    {
        var lines = PresetSummary.Describe(BuiltInPresets.Browser.Folders, BuiltInPresets.Browser.EnvRedirects);

        Assert.Equal([@"BrowserCache\"], lines);
    }

    [Fact]
    public void Describe_NodePreset_ListsOneLinePerFolder()
    {
        var lines = PresetSummary.Describe(BuiltInPresets.Node.Folders, BuiltInPresets.Node.EnvRedirects);

        Assert.Equal(
            [@"npm_config_cache → npm-cache\", @"YARN_CACHE_FOLDER → yarn-cache\", @"npm_config_store_dir → pnpm-store\"],
            lines);
    }

    [Fact]
    public void Describe_IgnoresCaseAndSlashesWhenMatchingFoldersToVariables()
    {
        var lines = PresetSummary.Describe(["/Cache/", "extra"], [new() { Variable = "X", SubPath = @"cache\" }]);

        Assert.Equal([@"X → cache\", @"extra\"], lines);
    }

    [Fact]
    public void Describe_Nothing_IsEmpty()
    {
        Assert.Empty(PresetSummary.Describe([], []));
    }
}
