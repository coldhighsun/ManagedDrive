namespace ManagedDrive.Tests;

public sealed class DirectoryEnumerationTests
{
    [Fact]
    public void Build_IncludesDotAndDotDotFirst()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\file.txt", MakeFile());

        var names = Enumerate(map, "\\", pattern: null, marker: null);

        Assert.Equal(new[] { ".", "..", "file.txt" }, names);
    }

    [Fact]
    public void Build_ListsOnlyDirectChildren()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\Sub", MakeDir());
        map.Add("\\Sub\\nested.txt", MakeFile());
        map.Add("\\top.txt", MakeFile());

        var names = Enumerate(map, "\\", pattern: null, marker: null);

        Assert.Equal(new[] { ".", "..", "Sub", "top.txt" }, names);
    }

    [Fact]
    public void Build_AppliesGlobPatternToChildren()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\a.txt", MakeFile());
        map.Add("\\b.doc", MakeFile());
        map.Add("\\c.txt", MakeFile());

        var names = Enumerate(map, "\\", pattern: "*.txt", marker: null);

        Assert.Equal(new[] { ".", "..", "a.txt", "c.txt" }, names);
    }

    [Fact]
    public void Build_MarkerSkipsDotAndDotDot()
    {
        var map = new FileNodeMap();
        map.Add("\\", MakeDir());
        map.Add("\\file.txt", MakeFile());

        var names = Enumerate(map, "\\", pattern: null, marker: "..");

        Assert.Equal(new[] { "file.txt" }, names);
    }

    private static List<string> Enumerate(FileNodeMap map, string dirPath, string? pattern, string? marker)
    {
        map.TryGet(dirPath, out var dir);
        var context = DirectoryEnumeration.Build(map, dir!, pattern, marker);

        var names = new List<string>();
        while (context.TryNext(out var name, out _))
        {
            names.Add(name!);
        }

        return names;
    }

    private static FileNode MakeDir() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Directory },
    };

    private static FileNode MakeFile() => new()
    {
        FileInfo = { FileAttributes = (uint)FileAttributes.Normal },
    };
}
