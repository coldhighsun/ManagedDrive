using ManagedDrive.App.Views;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for how <see cref="DiskContentDialog"/> drops selected nodes that lie inside another
/// selected directory.
/// </summary>
public sealed class DiskContentSelectionTests
{
    /// <summary>
    /// Creates a node at <paramref name="fullPath"/> without children.
    /// </summary>
    private static DiskContentNode Node(string fullPath, bool isDirectory) =>
        new(fullPath[(fullPath.LastIndexOf('\\') + 1)..], isDirectory, 0, fullPath, []);

    /// <summary>
    /// A file inside a selected directory is dropped, as the directory's recursive delete covers it.
    /// </summary>
    [Fact]
    public void ExcludeDescendantsOfSelectedDirectories_FileInsideSelectedDirectory_IsDropped()
    {
        var folder = Node(@"\Dir", isDirectory: true);
        var inside = Node(@"\Dir\a.txt", isDirectory: false);

        var result = DiskContentDialog.ExcludeDescendantsOfSelectedDirectories([folder, inside], StringComparison.OrdinalIgnoreCase);

        Assert.Equal([folder], result);
    }

    /// <summary>
    /// On a case-insensitive disk <c>\dir</c> is the same folder as <c>\Dir</c>, so its file is a descendant.
    /// </summary>
    [Fact]
    public void ExcludeDescendantsOfSelectedDirectories_CaseInsensitive_TreatsDifferentCaseAsSameFolder()
    {
        var folder = Node(@"\Dir", isDirectory: true);
        var inside = Node(@"\dir\a.txt", isDirectory: false);

        var result = DiskContentDialog.ExcludeDescendantsOfSelectedDirectories([folder, inside], StringComparison.OrdinalIgnoreCase);

        Assert.Equal([folder], result);
    }

    /// <summary>
    /// On a case-sensitive disk <c>\dir</c> is another folder than <c>\Dir</c>, so a file in it
    /// stays selected and is still deleted.
    /// </summary>
    [Fact]
    public void ExcludeDescendantsOfSelectedDirectories_CaseSensitive_KeepsFileOfCaseVariantFolder()
    {
        var folder = Node(@"\Dir", isDirectory: true);
        var other = Node(@"\dir\a.txt", isDirectory: false);

        var result = DiskContentDialog.ExcludeDescendantsOfSelectedDirectories([folder, other], StringComparison.Ordinal);

        Assert.Equal([folder, other], result);
    }
}
