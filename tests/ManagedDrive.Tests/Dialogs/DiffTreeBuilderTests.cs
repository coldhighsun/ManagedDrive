using ManagedDrive.App.Views;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests for the folder tree <see cref="DiffTreeBuilder"/> builds for the snapshot diff dialog.
/// </summary>
public sealed class DiffTreeBuilderTests
{
    /// <summary>
    /// Creates a changed file row.
    /// </summary>
    private static DiffRow File(DiffKind kind, string path) => DiffRow.Create(kind, path, isDirectory: false);

    /// <summary>
    /// Creates a changed folder row.
    /// </summary>
    private static DiffRow Folder(DiffKind kind, string path) => DiffRow.Create(kind, path, isDirectory: true);

    /// <summary>
    /// A nested path gets a container node for every unchanged parent folder.
    /// </summary>
    [Fact]
    public void Build_NestedPath_CreatesContainerParents()
    {
        var roots = DiffTreeBuilder.Build([File(DiffKind.Restored, @"\A\B\c.txt")]);

        var a = Assert.Single(roots);
        Assert.Equal("A", a.Name);
        Assert.True(a.IsDirectory);
        Assert.Null(a.Kind);
        var b = Assert.Single(a.Children);
        Assert.Equal(@"\A\B", b.FullPath);
        Assert.Null(b.Kind);
        var file = Assert.Single(b.Children);
        Assert.Equal(DiffKind.Restored, file.Kind);
        Assert.False(file.IsDirectory);
        Assert.Equal(2, file.Depth);
    }

    /// <summary>
    /// A folder that is changed itself keeps its kind even when a child path was added first.
    /// </summary>
    [Fact]
    public void Build_ChangedFolderAfterChild_KeepsFolderKind()
    {
        var roots = DiffTreeBuilder.Build(
        [
            File(DiffKind.Removed, @"\A\x.txt"),
            Folder(DiffKind.Removed, @"\A\"),
        ]);

        var a = Assert.Single(roots);
        Assert.Equal(DiffKind.Removed, a.Kind);
        Assert.True(a.IsDirectory);
    }

    /// <summary>
    /// Siblings are sorted folders first, then by name ignoring case.
    /// </summary>
    [Fact]
    public void Build_Siblings_SortsFoldersFirstThenByName()
    {
        var roots = DiffTreeBuilder.Build(
        [
            File(DiffKind.Restored, @"\b.txt"),
            File(DiffKind.Restored, @"\Zed\f.txt"),
            File(DiffKind.Restored, @"\a.txt"),
            File(DiffKind.Restored, @"\alpha\f.txt"),
        ]);

        Assert.Equal(["alpha", "Zed", "a.txt", "b.txt"], roots.Select(r => r.Name));
    }

    /// <summary>
    /// With every folder collapsed, only the top level is listed.
    /// </summary>
    [Fact]
    public void Flatten_AllCollapsed_ReturnsTopLevelOnly()
    {
        var roots = DiffTreeBuilder.Build(
        [
            File(DiffKind.Restored, @"\A\B\c.txt"),
            File(DiffKind.Reverted, @"\root.txt"),
        ]);

        var flat = DiffTreeBuilder.Flatten(roots, filter: null);

        Assert.Equal(["A", "root.txt"], flat.Select(n => n.Name));
        Assert.True(flat[0].CanExpand);
        Assert.False(flat[1].CanExpand);
    }

    /// <summary>
    /// Expanding a folder lists its children after it, and nested expansion is honoured.
    /// </summary>
    [Fact]
    public void Flatten_ExpandedFolders_ListsChildrenInOrder()
    {
        var roots = DiffTreeBuilder.Build(
        [
            File(DiffKind.Restored, @"\A\B\c.txt"),
            File(DiffKind.Restored, @"\A\d.txt"),
        ]);
        roots[0].IsExpanded = true;
        roots[0].Children[0].IsExpanded = true;

        var flat = DiffTreeBuilder.Flatten(roots, filter: null);

        Assert.Equal(["A", "B", "c.txt", "d.txt"], flat.Select(n => n.Name));
    }

    /// <summary>
    /// A folder's count is the number of changed files below it; folders themselves are not counted.
    /// </summary>
    [Fact]
    public void Flatten_Folder_CountsChangedFilesBelow()
    {
        var roots = DiffTreeBuilder.Build(
        [
            Folder(DiffKind.Removed, @"\A\"),
            File(DiffKind.Removed, @"\A\x.txt"),
            File(DiffKind.Removed, @"\A\B\y.txt"),
        ]);

        DiffTreeBuilder.Flatten(roots, filter: null);

        Assert.Equal(2, roots[0].ChangeCount);
        Assert.Equal("(2)", roots[0].CountText);
    }

    /// <summary>
    /// A filter keeps the ancestors of matching nodes and hides containers with nothing matching.
    /// </summary>
    [Fact]
    public void Flatten_Filter_KeepsAncestorsAndHidesEmptyContainers()
    {
        var roots = DiffTreeBuilder.Build(
        [
            File(DiffKind.Restored, @"\A\x.txt"),
            File(DiffKind.Reverted, @"\B\y.txt"),
        ]);
        foreach (var root in roots)
        {
            root.IsExpanded = true;
        }

        var flat = DiffTreeBuilder.Flatten(roots, DiffKind.Reverted);

        Assert.Equal(["B", "y.txt"], flat.Select(n => n.Name));
        Assert.Equal(1, flat[0].ChangeCount);
    }

    /// <summary>
    /// Under a filter an expanded folder lists only its matching children.
    /// </summary>
    [Fact]
    public void Flatten_ExpandedFolderWithFilter_ListsMatchingChildrenOnly()
    {
        var roots = DiffTreeBuilder.Build(
        [
            File(DiffKind.Restored, @"\A\x.txt"),
            File(DiffKind.Reverted, @"\A\y.txt"),
        ]);
        roots[0].IsExpanded = true;

        var flat = DiffTreeBuilder.Flatten(roots, DiffKind.Restored);

        Assert.Equal(["A", "x.txt"], flat.Select(n => n.Name));
    }
}
