using ManagedDrive.App.Services;

namespace ManagedDrive.Tests;

public sealed class EnvRestoreMenuEntryTests
{
    private static readonly EnvRestoreGroup Node = new("node", ["npm_config_cache"]);
    private static readonly EnvRestoreGroup Python = new("python", ["PIP_CACHE_DIR"]);

    [Fact]
    public void Build_NoGroups_ListsOnlyTheDisabledNote()
    {
        var entries = EnvRestoreMenuEntry.Build([]);

        Assert.Equal([EnvRestoreMenuEntryKind.Nothing], entries.Select(e => e.Kind));
    }

    [Fact]
    public void Build_OneGroup_ListsJustThatGroup()
    {
        var entries = EnvRestoreMenuEntry.Build([Node]);

        var entry = Assert.Single(entries);
        Assert.Equal(EnvRestoreMenuEntryKind.Group, entry.Kind);
        Assert.Same(Node, entry.Group);
    }

    [Fact]
    public void Build_SeveralGroups_AddsSeparatorAndRestoreAllAtTheEnd()
    {
        var entries = EnvRestoreMenuEntry.Build([Node, Python]);

        Assert.Equal(
            [
                EnvRestoreMenuEntryKind.Group,
                EnvRestoreMenuEntryKind.Group,
                EnvRestoreMenuEntryKind.Separator,
                EnvRestoreMenuEntryKind.All,
            ],
            entries.Select(e => e.Kind));
        Assert.Null(entries[^1].Group);
    }

    /// <summary>
    /// A failed read is shown as such and not as an empty list.
    /// </summary>
    [Fact]
    public void Build_ReadFailed_ListsOnlyTheUnreadableNote()
    {
        var entries = EnvRestoreMenuEntry.Build([Node, Python], readFailed: true);

        Assert.Equal([EnvRestoreMenuEntryKind.Unreadable], entries.Select(e => e.Kind));
    }
}
