using System.Reflection;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for <see cref="DiskOptionsMerge.MergeEdit"/>.
/// </summary>
public sealed class DiskOptionsMergeTests
{
    /// <summary>
    /// The options an edit starts from.
    /// </summary>
    private static readonly DiskOptions Baseline = new()
    {
        MountPoint = "R:",
        CapacityBytes = 1024 * 1024,
        VolumeLabel = "Data",
        PersistImagePath = @"C:\a.mdr",
        AutoSaveIntervalMinutes = 5,
        Folders = ["npm-cache"],
        EnvRedirects = [new() { Variable = "npm_config_cache", SubPath = "npm-cache" }],
    };

    /// <summary>
    /// A field the edit did not touch keeps what another change set meanwhile.
    /// </summary>
    [Fact]
    public void MergeEdit_FieldUntouchedByEdit_KeepsCurrentValue()
    {
        var edited = Baseline with { VolumeLabel = "Edited" };
        var current = Baseline with { CapacityBytes = 8 * 1024 * 1024, AutoSaveIntervalMinutes = 10 };

        var merged = DiskOptionsMerge.MergeEdit(Baseline, edited, current);

        Assert.Equal("Edited", merged.VolumeLabel);
        Assert.Equal(8UL * 1024 * 1024, merged.CapacityBytes);
        Assert.Equal(10u, merged.AutoSaveIntervalMinutes);
    }

    /// <summary>
    /// A field the edit changed wins over a concurrent change of the same field.
    /// </summary>
    [Fact]
    public void MergeEdit_FieldChangedByEditAndMeanwhile_TakesEditedValue()
    {
        var edited = Baseline with { CapacityBytes = 2 * 1024 * 1024 };
        var current = Baseline with { CapacityBytes = 8 * 1024 * 1024 };

        var merged = DiskOptionsMerge.MergeEdit(Baseline, edited, current);

        Assert.Equal(2UL * 1024 * 1024, merged.CapacityBytes);
    }

    /// <summary>
    /// Lists are compared by content: an edit that rebuilt an equal list does not replace the
    /// folders another change added meanwhile.
    /// </summary>
    [Fact]
    public void MergeEdit_EqualListRebuiltByEdit_KeepsCurrentList()
    {
        var edited = Baseline with
        {
            Folders = [.. Baseline.Folders!],
            EnvRedirects = [.. Baseline.EnvRedirects!],
        };
        var current = Baseline with { Folders = ["npm-cache", "pip-cache"] };

        var merged = DiskOptionsMerge.MergeEdit(Baseline, edited, current);

        Assert.Equal(["npm-cache", "pip-cache"], merged.Folders);
    }

    /// <summary>
    /// A folder the edit only renamed by case counts as changed: the current entry, matched
    /// ignoring case, is replaced by the edit's spelling, and other current entries stay.
    /// </summary>
    [Fact]
    public void MergeEdit_FolderRenamedByCaseOnly_TakesEditedSpelling()
    {
        var edited = Baseline with { Folders = ["NPM-Cache"] };
        var current = Baseline with { Folders = ["npm-cache", "pip-cache"] };

        var merged = DiskOptionsMerge.MergeEdit(Baseline, edited, current);

        Assert.Equal(["pip-cache", "NPM-Cache"], merged.Folders);
    }

    /// <summary>
    /// <c>null</c> and an empty list count as the same, unchanged list.
    /// </summary>
    [Fact]
    public void MergeEdit_NullVersusEmptyList_TreatedAsUnchanged()
    {
        var baseline = Baseline with { Folders = null };
        var edited = baseline with { Folders = [] };
        var current = baseline with { Folders = ["pip-cache"] };

        var merged = DiskOptionsMerge.MergeEdit(baseline, edited, current);

        Assert.Equal(["pip-cache"], merged.Folders);
    }

    /// <summary>
    /// An edit that changed every field gets every field, whatever the current options hold.
    /// </summary>
    [Fact]
    public void MergeEdit_EveryFieldChanged_ReturnsEdited()
    {
        var edited = new DiskOptions
        {
            MountPoint = "S:",
            CapacityBytes = 4 * 1024 * 1024,
            VolumeLabel = "Other",
            ReadOnly = true,
            PersistImagePath = @"C:\b.mdr",
            SourceArchivePath = @"C:\b.zip",
            AutoMount = true,
            AutoSaveIntervalMinutes = 30,
            CompressionLevel = 19,
            MaxSnapshotCount = 3,
            MaxSnapshotSizeBytes = 4096,
            HighUsageWarnPercent = 75,
            SaveImageOnExit = false,
            Folders = ["pip-cache"],
            EnvRedirects = [new() { Variable = "PIP_CACHE_DIR", SubPath = "pip-cache" }],
        };
        var current = Baseline with { CapacityBytes = 8 * 1024 * 1024, VolumeLabel = "Meanwhile" };

        var merged = DiskOptionsMerge.MergeEdit(Baseline, edited, current);

        Assert.Equal(edited, merged with { Folders = edited.Folders, EnvRedirects = edited.EnvRedirects });
        Assert.Equal(edited.Folders, merged.Folders);
        Assert.Equal(edited.EnvRedirects, merged.EnvRedirects);
    }

    /// <summary>
    /// An entry the edit added goes in, and an entry another change took away meanwhile stays out:
    /// the edit's stale copy of the list does not bring it back.
    /// </summary>
    [Fact]
    public void MergeEdit_RedirectRemovedMeanwhileAndOtherAddedByEdit_KeepsRemovalAndAddsEntry()
    {
        var pip = new EnvRedirect { Variable = "PIP_CACHE_DIR", SubPath = "pip-cache" };
        var edited = Baseline with
        {
            Folders = ["npm-cache", "pip-cache"],
            EnvRedirects = [.. Baseline.EnvRedirects!, pip],
        };
        var current = Baseline with { EnvRedirects = null };

        var merged = DiskOptionsMerge.MergeEdit(Baseline, edited, current);

        Assert.Equal([pip], merged.EnvRedirects);
        Assert.Equal(["npm-cache", "pip-cache"], merged.Folders);
    }

    /// <summary>
    /// An entry the edit removed is taken out, while one another change added meanwhile is kept.
    /// </summary>
    [Fact]
    public void MergeEdit_EntryRemovedByEditAndOtherAddedMeanwhile_KeepsOtherEntry()
    {
        var edited = Baseline with { Folders = null };
        var current = Baseline with { Folders = ["npm-cache", "pip-cache"] };

        var merged = DiskOptionsMerge.MergeEdit(Baseline, edited, current);

        Assert.Equal(["pip-cache"], merged.Folders);
    }

    /// <summary>
    /// A redirect the edit added replaces a current redirect of the same variable, so a variable
    /// never ends up pointing at two folders.
    /// </summary>
    [Fact]
    public void MergeEdit_RedirectAddedForVariableChangedMeanwhile_ReplacesCurrentRedirect()
    {
        var editedRedirect = new EnvRedirect { Variable = "npm_config_cache", SubPath = "edited" };
        var currentRedirect = new EnvRedirect { Variable = "NPM_CONFIG_CACHE", SubPath = "meanwhile" };
        var edited = Baseline with { EnvRedirects = [editedRedirect] };
        var current = Baseline with { EnvRedirects = [currentRedirect] };

        var merged = DiskOptionsMerge.MergeEdit(Baseline, edited, current);

        Assert.Equal([editedRedirect], merged.EnvRedirects);
    }

    /// <summary>
    /// A list the edit emptied comes out as <c>null</c>, as the options store an empty list.
    /// </summary>
    [Fact]
    public void MergeEdit_EditRemovesEveryEntry_ReturnsNull()
    {
        var edited = Baseline with { Folders = [], EnvRedirects = [] };

        var merged = DiskOptionsMerge.MergeEdit(Baseline, edited, Baseline);

        Assert.Null(merged.Folders);
        Assert.Null(merged.EnvRedirects);
    }

    /// <summary>
    /// Guards against a new <see cref="DiskOptions"/> property that <see cref="DiskOptionsMerge.MergeEdit"/>
    /// does not merge: it would silently always keep the current value, dropping the user's edit.
    /// </summary>
    [Fact]
    public void MergeEdit_DiskOptionsProperties_AllHandled()
    {
        var properties = typeof(DiskOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Order()
            .ToList();

        Assert.Equal(
            [
                "AutoMount", "AutoSaveIntervalMinutes", "CapacityBytes", "CompressionLevel", "EnvRedirects",
                "Folders", "HighUsageWarnPercent", "MaxSnapshotCount", "MaxSnapshotSizeBytes", "MountPoint",
                "PersistImagePath", "ReadOnly", "SaveImageOnExit", "SourceArchivePath", "VolumeLabel",
            ],
            properties);
    }
}
