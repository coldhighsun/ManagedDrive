namespace ManagedDrive.Tests;

public sealed class MemoryFileSystemVolumeLabelTests
{
    [Fact]
    public void VolumeLabel_AfterSetVolumeLabelFromExplorer_ReturnsNewLabelAndMarksDirty()
    {
        var fs = new MemoryFileSystem(1024, "Old");
        var dirtyBefore = fs.IsDirty;

        var status = fs.SetVolumeLabel("New", out _);

        Assert.Equal(0, status);
        Assert.Equal("New", fs.VolumeLabel);
        Assert.True(fs.IsDirty);
        Assert.False(dirtyBefore);
    }

    [Fact]
    public void VolumeLabel_AfterUpdateVolumeLabel_ReturnsNewLabel()
    {
        var fs = new MemoryFileSystem(1024, "Old");

        fs.UpdateVolumeLabel("Edited");

        Assert.Equal("Edited", fs.VolumeLabel);
    }
}
