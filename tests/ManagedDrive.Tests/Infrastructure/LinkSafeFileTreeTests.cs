using System.Diagnostics;

namespace ManagedDrive.Tests;

/// <summary>
/// Tests that <see cref="LinkSafeFileTree"/> never counts or deletes what lies behind a junction.
/// </summary>
public sealed class LinkSafeFileTreeTests : IDisposable
{
    /// <summary>
    /// Per-test temporary directory.
    /// </summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ManagedDrive.Tests." + Guid.NewGuid());

    /// <summary>
    /// Removes the temporary directory, taking care to drop junctions before their targets.
    /// </summary>
    public void Dispose()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                         .Where(LinkSafeFileTree.IsLink))
            {
                Directory.Delete(dir);
            }

            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    /// <summary>
    /// Creates a junction at <paramref name="link"/> pointing to <paramref name="target"/>.
    /// </summary>
    /// <param name="link">The junction path.</param>
    /// <param name="target">The directory it points to.</param>
    private static void CreateJunction(string link, string target)
    {
        var startInfo = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(startInfo)!;

        // Read both streams while waiting, so a full pipe cannot stall the process; the timeout keeps
        // a hung cmd.exe from blocking the whole test run.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(JunctionTimeout))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"mklink did not finish within {JunctionTimeout}.");
        }

        // WaitForExit(TimeSpan) returns before the redirected streams are fully drained.
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink failed ({process.ExitCode}): {output.Result} {error.Result}");
    }

    /// <summary>
    /// How long creating a junction may take before the test fails instead of hanging.
    /// </summary>
    private static readonly TimeSpan JunctionTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates a directory outside the tree under test holding one file that must survive.
    /// </summary>
    /// <returns>The directory and the file's path.</returns>
    private (string Directory, string File) CreateOutsideDirectory()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside")).FullName;
        var precious = Path.Combine(outside, "precious.txt");
        File.WriteAllText(precious, "keep");
        return (outside, precious);
    }

    /// <summary>
    /// A plain tree, hidden files included, is counted completely.
    /// </summary>
    [Fact]
    public void CountFiles_PlainTreeWithHiddenFile_CountsAll()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "plain", "sub")).Parent!.FullName;
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        File.WriteAllText(Path.Combine(dir, "sub", "b.txt"), "b");
        var hidden = Path.Combine(dir, "sub", "h.txt");
        File.WriteAllText(hidden, "h");
        File.SetAttributes(hidden, FileAttributes.Hidden);

        Assert.Equal(3, LinkSafeFileTree.CountFiles(dir));
    }

    /// <summary>
    /// A junction nested in the tree adds nothing to the count: it is not entered.
    /// </summary>
    [Fact]
    public void CountFiles_NestedJunction_DoesNotCountItsTarget()
    {
        var (outside, _) = CreateOutsideDirectory();
        var dir = Directory.CreateDirectory(Path.Combine(_root, "disk", "folder")).FullName;
        File.WriteAllText(Path.Combine(dir, "own.txt"), "own");
        CreateJunction(Path.Combine(dir, "link"), outside);

        Assert.Equal(1, LinkSafeFileTree.CountFiles(dir));
    }

    /// <summary>
    /// A junction given as the start directory counts as empty, instead of as its target's files.
    /// </summary>
    [Fact]
    public void CountFiles_StartDirectoryIsJunction_ReturnsZero()
    {
        var (outside, _) = CreateOutsideDirectory();
        var link = Path.Combine(_root, "link");
        CreateJunction(link, outside);

        Assert.Equal(0, LinkSafeFileTree.CountFiles(link));
    }

    /// <summary>
    /// Deleting a tree that holds a nested junction removes the tree and the junction, reports each
    /// file, and leaves the junction target's files in place.
    /// </summary>
    [Fact]
    public void DeleteDirectory_NestedJunction_LeavesTargetIntact()
    {
        var (outside, precious) = CreateOutsideDirectory();
        var dir = Directory.CreateDirectory(Path.Combine(_root, "disk", "folder", "sub")).Parent!.FullName;
        File.WriteAllText(Path.Combine(dir, "own.txt"), "own");
        File.WriteAllText(Path.Combine(dir, "sub", "own2.txt"), "own2");
        CreateJunction(Path.Combine(dir, "link"), outside);
        var deleted = 0;

        LinkSafeFileTree.DeleteDirectory(dir, () => deleted++, CancellationToken.None);

        Assert.False(Directory.Exists(dir));
        Assert.Equal(2, deleted);
        Assert.True(File.Exists(precious));
    }

    /// <summary>
    /// Deleting a junction itself removes only the junction, not the files it points to.
    /// </summary>
    [Fact]
    public void DeleteDirectory_PathIsJunction_RemovesOnlyTheLink()
    {
        var (outside, precious) = CreateOutsideDirectory();
        var link = Path.Combine(_root, "link");
        CreateJunction(link, outside);

        LinkSafeFileTree.DeleteDirectory(link, onFileDeleted: null, CancellationToken.None);

        Assert.False(Directory.Exists(link));
        Assert.True(File.Exists(precious));
    }

    /// <summary>
    /// A cancelled token stops the delete before the next file is touched.
    /// </summary>
    [Fact]
    public void DeleteDirectory_TokenAlreadyCancelled_ThrowsAndKeepsFiles()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "plain")).FullName;
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => LinkSafeFileTree.DeleteDirectory(dir, onFileDeleted: null, cts.Token));
        Assert.True(File.Exists(file));
    }

    /// <summary>
    /// Hidden and system files, and a hidden directory holding one, are deleted along with the rest
    /// and counted, so the final directory delete does not fail on leftovers.
    /// </summary>
    [Fact]
    public void DeleteDirectory_HiddenAndSystemEntries_DeletesAndCountsThem()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "plain", "hiddenDir")).Parent!.FullName;
        var hiddenFile = Path.Combine(dir, "h.txt");
        var systemFile = Path.Combine(dir, "s.sys");
        var nestedFile = Path.Combine(dir, "hiddenDir", "n.txt");
        File.WriteAllText(hiddenFile, "h");
        File.WriteAllText(systemFile, "s");
        File.WriteAllText(nestedFile, "n");
        File.SetAttributes(hiddenFile, FileAttributes.Hidden);
        File.SetAttributes(systemFile, FileAttributes.Hidden | FileAttributes.System);
        File.SetAttributes(Path.Combine(dir, "hiddenDir"), FileAttributes.Hidden | FileAttributes.Directory);
        var deleted = 0;

        Assert.Equal(3, LinkSafeFileTree.CountFiles(dir));
        LinkSafeFileTree.DeleteDirectory(dir, () => deleted++, CancellationToken.None);

        Assert.False(Directory.Exists(dir));
        Assert.Equal(3, deleted);
    }

    /// <summary>
    /// Read-only files and read-only directories, at any depth, are deleted like any other entry
    /// instead of stopping the delete part-way, and each file is reported.
    /// </summary>
    [Fact]
    public void DeleteDirectory_ReadOnlyFilesAndDirectories_AreDeleted()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "plain", "roDir")).Parent!.FullName;
        var topFile = Path.Combine(dir, "ro.txt");
        var nestedFile = Path.Combine(dir, "roDir", "nested.txt");
        File.WriteAllText(Path.Combine(dir, "a.txt"), "a");
        File.WriteAllText(topFile, "ro");
        File.WriteAllText(nestedFile, "nested");
        File.SetAttributes(topFile, FileAttributes.ReadOnly);
        File.SetAttributes(nestedFile, FileAttributes.ReadOnly | FileAttributes.Hidden);
        File.SetAttributes(Path.Combine(dir, "roDir"), FileAttributes.Directory | FileAttributes.ReadOnly);
        File.SetAttributes(dir, FileAttributes.Directory | FileAttributes.ReadOnly);
        var deleted = 0;

        LinkSafeFileTree.DeleteDirectory(dir, () => deleted++, CancellationToken.None);

        Assert.False(Directory.Exists(dir));
        Assert.Equal(3, deleted);
    }

    /// <summary>
    /// The read-only attribute of files behind a junction is not touched: the junction is removed as
    /// a link and its target is never entered or modified.
    /// </summary>
    [Fact]
    public void DeleteDirectory_ReadOnlyFileBehindJunction_KeepsItAndItsAttribute()
    {
        var (outside, precious) = CreateOutsideDirectory();
        File.SetAttributes(precious, FileAttributes.ReadOnly);
        var dir = Directory.CreateDirectory(Path.Combine(_root, "disk", "folder")).FullName;
        File.WriteAllText(Path.Combine(dir, "own.txt"), "own");
        CreateJunction(Path.Combine(dir, "link"), outside);

        try
        {
            LinkSafeFileTree.DeleteDirectory(dir, onFileDeleted: null, CancellationToken.None);

            Assert.False(Directory.Exists(dir));
            Assert.True(File.Exists(precious));
            Assert.True(File.GetAttributes(precious).HasFlag(FileAttributes.ReadOnly));
        }
        finally
        {
            File.SetAttributes(precious, FileAttributes.Normal);
        }
    }

    /// <summary>
    /// <c>IsLink</c> reports junctions, but not plain directories or missing paths.
    /// </summary>
    [Fact]
    public void IsLink_JunctionPlainDirectoryAndMissingPath_ReportsOnlyTheJunction()
    {
        var (outside, _) = CreateOutsideDirectory();
        var link = Path.Combine(_root, "link");
        CreateJunction(link, outside);

        Assert.True(LinkSafeFileTree.IsLink(link));
        Assert.False(LinkSafeFileTree.IsLink(outside));
        Assert.False(LinkSafeFileTree.IsLink(Path.Combine(_root, "missing")));
    }
}
